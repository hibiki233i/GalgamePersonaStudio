using Microsoft.Win32;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using System.Collections.ObjectModel;
using System.ClientModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace GalgamePersonaStudio;

public partial class MainWindow : Window
{
    private const string AppCacheName = "GalgamePersonaExtractor";
    private const string SettingsFileName = "wpf_studio_settings.json";
    private const string OllamaChatEndpoint = "http://localhost:11434/v1/chat/completions";
    private const string OllamaEmbeddingEndpoint = "http://localhost:11434/v1/embeddings";
    private const string DefaultChatModel = "gpt-5.4";
    private const string DefaultEmbeddingModel = "text-embedding-3-large";
    private const string DefaultVisionEndpoint = "https://api.openai.com/v1/chat/completions";
    private const string DefaultVisionModel = "gpt-4o-mini";
    private const string AppVersion = "1.0.1";
    private const string RepoOwner = "hibiki233i";
    private const string RepoName = "GalgamePersonaStudio";

    private static readonly Regex TokenRegex = new(@"[\u3040-\u30ff\u3400-\u9fffA-Za-z0-9ー]{2,}", RegexOptions.Compiled);
    private static readonly Regex SpeakerPrefixRegex = new(@"^([^:：「」\n]{1,24})[:：]\s*(.+)$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex EmotionKanjiRegex = new(@"[嬉怒哀楽泣笑驚悩悲]", RegexOptions.Compiled);

    private readonly ObservableCollection<string> _characters = [];
    private readonly ObservableCollection<string> _ragPresets = [];
    private readonly List<ScriptEntry> _entries = [];
    private readonly Dictionary<string, int> _characterCounts = [];
    private readonly HashSet<string> _knownRecordHashes = [];
    private AppSettings _settings = new();
    private System.Windows.Threading.DispatcherTimer? _captureTimer;
    private bool _isLoadingSettings = true;
    private bool _isSidebarCollapsed;
    private System.Windows.Threading.DispatcherTimer? _settingsSaveTimer;
    private readonly string? _logFilePath = TryCreateLogFilePath();
    private AutoAdvanceManager? _autoAdvance;
    private string _lastRecordedName = "";
    private string _lastRecordedMessage = "";

    private static string DetectGameName(List<ScriptEntry> entries)
    {
        try
        {
            var gameDir = entries
                .Select(e => e.SourceFile)
                .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
            if (gameDir is null) return "";
            var dir = Path.GetDirectoryName(gameDir);
            while (dir is not null)
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent is null || parent.Length < 4) break;
                var parentName = Path.GetFileName(parent);
                if (string.Equals(parentName, "galgame", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
                dir = parent;
            }
            return Path.GetFileName(Path.GetDirectoryName(gameDir) ?? "") ?? "";
        }
        catch { return ""; }
    }

    private static readonly Dictionary<string, string> RagPresetPrompts = new()
    {
        ["综合人格画像"] = "核心人格、价值观、日常语气、对亲密对象和陌生人的态度差异",
        ["恋爱与亲密关系"] = "害羞、撒娇、吃醋、依赖、试探、主动靠近、保持距离、对喜欢的人的态度",
        ["冲突与生气反应"] = "愤怒、反驳、威胁、受伤、防御、争吵、被误解时的反应",
        ["日常吐槽与喜剧节奏"] = "吐槽、玩笑、轻松互动、打趣、无奈、夸张反应、节奏感",
        ["弱点与不安"] = "害怕、犹豫、自卑、秘密、逃避、脆弱、需要安慰的时刻",
        ["信念与行动动机"] = "目标、原则、正义感、执念、选择理由、关键剧情中的判断",
        ["口癖与说话风格"] = "口癖、语尾、称呼、短句、长句、礼貌程度、常用词和表达习惯",
        ["预设对话素材"] = "适合做角色卡开场白、示例对话、代表性回应、可复用互动片段"
    };

    public MainWindow()
    {
        InitializeComponent();
        CharacterList.ItemsSource = _characters;
        RagPresetList.ItemsSource = _ragPresets;
        foreach (var preset in RagPresetPrompts.Keys)
        {
            _ragPresets.Add(preset);
        }
        LoadSettings();
        ApplySettingsToUi();
        UpdateApiVisibility();
        UpdateCaptureVisibility();
        HookAutosave();
        _isLoadingSettings = false;
        Log($"Galgame Persona Studio v{AppVersion}");
        _ = CheckForUpdateAsync();
    }

    private static string CacheDir()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, AppCacheName);
    }

    private static string SettingsPath() => Path.Combine(CacheDir(), SettingsFileName);

    private static string? TryCreateLogFilePath()
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "log");
            Directory.CreateDirectory(logDir);
            return Path.Combine(logDir, $"studio-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            return null;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath()))
            {
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath())) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings) return;
        ReadUiIntoSettings();
        Directory.CreateDirectory(CacheDir());
        File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(_settings, JsonOptions()));
    }

    private void ScheduleSettingsSave()
    {
        if (_isLoadingSettings) return;
        _settingsSaveTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _settingsSaveTimer.Tick -= SettingsSaveTimer_Tick;
        _settingsSaveTimer.Tick += SettingsSaveTimer_Tick;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SettingsSaveTimer_Tick(object? sender, EventArgs e)
    {
        _settingsSaveTimer?.Stop();
        SaveSettings();
    }

    private void HookAutosave()
    {
        Closing += (_, _) => SaveSettings();
        foreach (var textBox in FindVisualChildren<System.Windows.Controls.TextBox>(this))
        {
            if (textBox.Name.EndsWith("RevealBox", StringComparison.Ordinal)) continue;
            textBox.TextChanged += (_, _) => ScheduleSettingsSave();
        }
        foreach (var passwordBox in FindVisualChildren<PasswordBox>(this))
            passwordBox.PasswordChanged += (_, _) => ScheduleSettingsSave();
        foreach (var comboBox in FindVisualChildren<System.Windows.Controls.ComboBox>(this))
            comboBox.SelectionChanged += (_, _) => ScheduleSettingsSave();
        foreach (var checkBox in FindVisualChildren<System.Windows.Controls.CheckBox>(this))
        {
            checkBox.Checked += (_, _) => ScheduleSettingsSave();
            checkBox.Unchecked += (_, _) => ScheduleSettingsSave();
        }
        RagPresetList.SelectionChanged += (_, _) => ScheduleSettingsSave();
    }

    private void ApplySettingsToUi()
    {
        var root = Directory.GetParent(AppContext.BaseDirectory)?.Parent?.Parent?.Parent?.Parent?.FullName ?? @"E:\code";
        ScriptPathBox.Text = string.IsNullOrWhiteSpace(_settings.ScriptPath) ? Path.Combine(root, "out") : _settings.ScriptPath;
        OutputPathBox.Text = string.IsNullOrWhiteSpace(_settings.OutputPath) ? Path.Combine(root, "persona_outputs_wpf") : _settings.OutputPath;
        MinCountBox.Text = _settings.MinCount <= 0 ? "20" : _settings.MinCount.ToString();
        MaxEvidenceBox.Text = _settings.MaxEvidence <= 0 ? "180" : _settings.MaxEvidence.ToString();
        SelectCombo(ProviderCombo, string.IsNullOrWhiteSpace(_settings.Provider) ? "本地规则" : _settings.Provider);
        EndpointBox.Text = string.IsNullOrWhiteSpace(_settings.Endpoint) ? OllamaChatEndpoint : _settings.Endpoint;
        ModelBox.Text = string.IsNullOrWhiteSpace(_settings.Model) ? DefaultChatModel : _settings.Model;
        SelectCombo(EmbeddingModeCombo, string.IsNullOrWhiteSpace(_settings.EmbeddingMode) ? "不使用嵌入" : _settings.EmbeddingMode);
        EmbeddingCandidateBox.Text = _settings.EmbeddingCandidateLimit <= 0 ? "800" : _settings.EmbeddingCandidateLimit.ToString();
        EmbeddingBatchSizeBox.Text = _settings.EmbeddingBatchSize <= 0 ? "0" : _settings.EmbeddingBatchSize.ToString();
        EmbeddingEndpointBox.Text = string.IsNullOrWhiteSpace(_settings.EmbeddingEndpoint) ? OllamaEmbeddingEndpoint : _settings.EmbeddingEndpoint;
        EmbeddingModelBox.Text = string.IsNullOrWhiteSpace(_settings.EmbeddingModel) ? DefaultEmbeddingModel : _settings.EmbeddingModel;
        ApiKeyBox.Password = _settings.ApiKey ?? "";
        EmbeddingApiKeyBox.Password = _settings.EmbeddingApiKey ?? "";
        VisionApiKeyBox.Password = _settings.VisionApiKey ?? "";
        ExportSoulCheck.IsChecked = _settings.ExportSoul;
        FilterHSceneCheck.IsChecked = _settings.FilterHScene;
        RagEnabledCheck.IsChecked = _settings.RagEnabled;
        RagTopKBox.Text = _settings.RagTopK <= 0 ? "40" : _settings.RagTopK.ToString();
        foreach (var label in _settings.RagPresets.Count > 0 ? _settings.RagPresets : ["综合人格画像", "口癖与说话风格"])
        {
            RagPresetList.SelectedItems.Add(label);
        }
        RagCustomBox.Text = _settings.RagCustomDirections ?? "";
        VersionText.Text = $"v{AppVersion}";

        ProcessNameBox.Text = string.IsNullOrWhiteSpace(_settings.LastProcessName) ? "unknown" : _settings.LastProcessName;
        WindowTitleBox.Text = _settings.LastWindowTitle ?? "";
        RecordOutputBox.Text = string.IsNullOrWhiteSpace(_settings.RecordOutputPath) ? Path.Combine(root, "ocr_records_wpf") : _settings.RecordOutputPath;
        SelectCombo(CaptureSourceCombo, string.IsNullOrWhiteSpace(_settings.CaptureSource) ? "Clipboard / 外部剪贴板" : _settings.CaptureSource);
        CaptureIntervalBox.Text = _settings.CaptureIntervalSeconds <= 0 ? "1.2" : _settings.CaptureIntervalSeconds.ToString("0.###");
        DialogRegionBox.Text = _settings.DialogRegion ?? "";
        NameRegionBox.Text = _settings.NameRegion ?? "";
        VisionEndpointBox.Text = string.IsNullOrWhiteSpace(_settings.VisionEndpoint) ? DefaultVisionEndpoint : _settings.VisionEndpoint;
        VisionModelBox.Text = string.IsNullOrWhiteSpace(_settings.VisionModel) ? DefaultVisionModel : _settings.VisionModel;
        UmiOcrEndpointBox.Text = string.IsNullOrWhiteSpace(_settings.UmiOcrEndpoint) ? "http://127.0.0.1:1224" : _settings.UmiOcrEndpoint;
        OnlyNamedNowCheck.IsChecked = _settings.OnlyNamedNow;
        OnlyAfterBox.Text = _settings.OnlyAfterCount <= 0 ? "200" : _settings.OnlyAfterCount.ToString();
        AllowedNamesBox.Text = _settings.AllowedNames ?? "";
        ExampleCharFilterBox.Text = _settings.ExampleCharFilter ?? "";
        AutoAdvanceCheck.IsChecked = _settings.AutoAdvanceEnabled;
        AutoAdvanceConfigPanel.IsEnabled = _settings.AutoAdvanceEnabled;
        AdvanceClickBox.Text = _settings.AdvanceClickPoint ?? "";
        PostClickDelayBox.Text = _settings.PostClickDelaySeconds <= 0 ? "1.2" : _settings.PostClickDelaySeconds.ToString("0.###");
        StuckThresholdBox.Text = _settings.StuckThreshold <= 0 ? "5" : _settings.StuckThreshold.ToString();
        ChoiceDetectionCheck.IsChecked = _settings.ChoiceDetectionEnabled;
        ChoiceConfigPanel.IsEnabled = _settings.ChoiceDetectionEnabled;
        SelectCombo(ChoiceModeCombo, string.IsNullOrWhiteSpace(_settings.ChoiceHandleMode) ? "弹窗手动选择" : _settings.ChoiceHandleMode);
        SelectCombo(ChoiceAutoRuleCombo, string.IsNullOrWhiteSpace(_settings.ChoiceAutoRule) ? "选择第一个" : _settings.ChoiceAutoRule);
        ChoiceModeCombo_SelectionChanged(null!, null!); // sync auto-rule visibility
    }

    private void ReadUiIntoSettings()
    {
        _settings.ScriptPath = ScriptPathBox.Text.Trim();
        _settings.OutputPath = OutputPathBox.Text.Trim();
        _settings.MinCount = ParseInt(MinCountBox.Text, 20);
        _settings.MaxEvidence = ParseInt(MaxEvidenceBox.Text, 180);
        _settings.Provider = ComboText(ProviderCombo);
        _settings.Endpoint = EndpointBox.Text.Trim();
        _settings.Model = ModelBox.Text.Trim();
        _settings.EmbeddingMode = ComboText(EmbeddingModeCombo);
        _settings.EmbeddingEndpoint = EmbeddingEndpointBox.Text.Trim();
        _settings.EmbeddingModel = EmbeddingModelBox.Text.Trim();
        _settings.EmbeddingCandidateLimit = ParseInt(EmbeddingCandidateBox.Text, 800);
        _settings.EmbeddingBatchSize = ParseInt(EmbeddingBatchSizeBox.Text, 0);
        _settings.RememberApiKeys = true;
        _settings.ApiKey = ApiKeyBox.Password;
        _settings.EmbeddingApiKey = EmbeddingApiKeyBox.Password;
        _settings.VisionApiKey = VisionApiKeyBox.Password;
        _settings.ExportSoul = ExportSoulCheck.IsChecked == true;
        _settings.FilterHScene = FilterHSceneCheck.IsChecked == true;
        _settings.RagEnabled = RagEnabledCheck.IsChecked == true;
        _settings.RagTopK = ParseInt(RagTopKBox.Text, 40);
        _settings.RagPresets = RagPresetList.SelectedItems.Cast<string>().ToList();
        _settings.RagCustomDirections = RagCustomBox.Text.Trim();
        _settings.LastProcessName = ProcessNameBox.Text.Trim();
        _settings.LastWindowTitle = WindowTitleBox.Text.Trim();
        _settings.RecordOutputPath = RecordOutputBox.Text.Trim();
        _settings.CaptureSource = ComboText(CaptureSourceCombo);
        _settings.CaptureIntervalSeconds = ParseDouble(CaptureIntervalBox.Text, 1.2);
        _settings.DialogRegion = DialogRegionBox.Text.Trim();
        _settings.NameRegion = NameRegionBox.Text.Trim();
        _settings.VisionEndpoint = VisionEndpointBox.Text.Trim();
        _settings.VisionModel = VisionModelBox.Text.Trim();
        _settings.UmiOcrEndpoint = UmiOcrEndpointBox.Text.Trim();
        _settings.OnlyNamedNow = OnlyNamedNowCheck.IsChecked == true;
        _settings.OnlyAfterCount = ParseInt(OnlyAfterBox.Text, 200);
        _settings.AllowedNames = AllowedNamesBox.Text.Trim();
        _settings.ExampleCharFilter = ExampleCharFilterBox.Text.Trim();
        _settings.AutoAdvanceEnabled = AutoAdvanceCheck.IsChecked == true;
        _settings.AdvanceClickPoint = AdvanceClickBox.Text.Trim();
        _settings.PostClickDelaySeconds = ParseDouble(PostClickDelayBox.Text, 1.2);
        _settings.StuckThreshold = ParseInt(StuckThresholdBox.Text, 5);
        _settings.ChoiceDetectionEnabled = ChoiceDetectionCheck.IsChecked == true;
        _settings.ChoiceHandleMode = ComboText(ChoiceModeCombo);
        _settings.ChoiceAutoRule = ComboText(ChoiceAutoRuleCombo);
    }

    private void ChooseScriptFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true) ScriptPathBox.Text = dialog.FileName;
    }

    private void ChooseScriptFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == Forms.DialogResult.OK) ScriptPathBox.Text = dialog.SelectedPath;
    }

    private void ChooseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == Forms.DialogResult.OK) OutputPathBox.Text = dialog.SelectedPath;
    }

    private void ChooseRecordOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == Forms.DialogResult.OK) RecordOutputBox.Text = dialog.SelectedPath;
    }

    private void ScanCharacters_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _entries.Clear();
            _entries.AddRange(LoadScriptEntries(ScriptPathBox.Text.Trim()));
            _characterCounts.Clear();
            foreach (var group in _entries.Where(x => !string.IsNullOrWhiteSpace(x.Name)).GroupBy(x => x.Name))
                _characterCounts[group.Key] = group.Count();
            PopulateCharacters();
            Log($"读取 {_entries.Count} 条文本，识别到 {_characterCounts.Count} 个角色。");
        }
        catch (Exception ex)
        {
            Log($"扫描失败: {ex.Message}");
        }
    }

    private void FilterCharacters_Click(object sender, RoutedEventArgs e) => PopulateCharacters();

    private void SidebarNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag) return;
        PersonaPanel.Visibility = tag == "persona" ? Visibility.Visible : Visibility.Collapsed;
        CharacterPanel.Visibility = tag == "character" ? Visibility.Visible : Visibility.Collapsed;
        RagPanel.Visibility = tag == "rag" ? Visibility.Visible : Visibility.Collapsed;
        RecordPanel.Visibility = tag == "record" ? Visibility.Visible : Visibility.Collapsed;
        LogPanel.Visibility = tag == "log" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _isSidebarCollapsed = !_isSidebarCollapsed;
        if (_isSidebarCollapsed)
        {
            SidebarBorder.Width = 56;
            ExpandedSidebar.Visibility = Visibility.Collapsed;
            CollapsedSidebar.Visibility = Visibility.Visible;
            ToggleIcon.Text = "▶";
        }
        else
        {
            SidebarBorder.Width = 260;
            ExpandedSidebar.Visibility = Visibility.Visible;
            CollapsedSidebar.Visibility = Visibility.Collapsed;
            ToggleIcon.Text = "◀";
        }
    }

    private void PopulateCharacters()
    {
        _characters.Clear();
        var min = ParseInt(MinCountBox.Text, 1);
        foreach (var pair in _characterCounts.OrderByDescending(x => x.Value).Where(x => x.Value >= min))
            _characters.Add($"{pair.Key}  ({pair.Value})");
    }

    private async void GeneratePersona_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) ScanCharacters_Click(sender, e);
        var names = CharacterList.SelectedItems.Cast<string>().Select(x => Regex.Replace(x, @"\s+\(\d+\)$", "")).ToList();
        if (names.Count == 0)
        {
            Log("请先选择至少一个角色。");
            return;
        }
        SaveSettings();
        foreach (var character in names)
        {
            try
            {
                await GenerateForCharacter(character);
            }
            catch (Exception ex)
            {
                Log($"{character}: 生成失败: {ex.Message}");
            }
        }
        Log("人格生成完成。");
    }

    private async Task GenerateForCharacter(string character)
    {
        var charEntries = Dedup(_entries.Where(x => x.Name == character)).ToList();
        var maxEvidence = ParseInt(MaxEvidenceBox.Text, 180);
        var evidenceLimit = RagEnabledCheck.IsChecked == true ? ParseInt(RagTopKBox.Text, 40) : maxEvidence;
        var embeddingMode = ComboText(EmbeddingModeCombo);
        Log($"{character}: 开始生成，角色去重台词 {charEntries.Count} 条，LLM={ComboText(ProviderCombo)}，Embedding={embeddingMode}。");
        if (RagEnabledCheck.IsChecked == true && embeddingMode == "不使用嵌入")
        {
            embeddingMode = "本地哈希嵌入";
            Log("RAG 需要嵌入前置，本次自动使用本地哈希嵌入。");
        }

        EvidenceResult evidence = RagEnabledCheck.IsChecked == true
            ? await SelectRagEvidence(charEntries, SelectedRagDirections(), evidenceLimit, embeddingMode)
            : await SelectEvidence(charEntries, evidenceLimit, embeddingMode);
        Log($"{character}: 证据选择完成，方式={evidence.Metadata.GetValueOrDefault("evidence_selection")}，证据 {evidence.Entries.Count} 条。");

        var persona = BuildLocalPersona(character, charEntries, evidence);
        if (ComboText(ProviderCombo) != "本地规则")
        {
            var endpoint = ComboText(ProviderCombo).StartsWith("Ollama") ? EndpointBox.Text.Trim() : EndpointBox.Text.Trim();
            var model = string.IsNullOrWhiteSpace(ModelBox.Text) ? DefaultChatModel : ModelBox.Text.Trim();
            var prompt = BuildLlmPrompt(character, evidence, persona);
            Log($"{character}: 调用 LLM，endpoint={endpoint}，model={model}，prompt 字符数={prompt.Length}。");
            var promptFile = Path.Combine(OutputPathBox.Text.Trim(), SafeName(character), "llm_prompt.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(promptFile)!);
            File.WriteAllText(promptFile, prompt, Encoding.UTF8);
            Log($"{character}: 已保存完整提示词到 llm_prompt.txt。提示词预览:\n{Truncate(prompt, 600)}");
            var raw = await CallChatCompletion(endpoint, model, ApiKeyBox.Password, prompt);
            Log($"{character}: LLM 返回 {raw.Length} 字符，开始解析 JSON。");
            var merged = TryParseObject(raw);
            if (merged is not null)
            {
                foreach (var prop in merged) persona[prop.Key] = prop.Value?.DeepClone();
                persona["evidence_metadata"] = JsonSerializer.SerializeToNode(evidence.Metadata, JsonOptions());
                Log($"{character}: LLM JSON 解析成功。");
            }
            else
            {
                Log($"{character}: LLM 返回不是可解析 JSON，保留本地规则结果。返回摘要: {Truncate(raw, 600)}");
            }
        }

        WritePersonaOutputs(character, charEntries, persona, evidence);
        if (persona.TryGetPropertyValue("example_exchanges", out var exchangesNode) && exchangesNode is JsonArray arr)
            Log($"{character}: 实例对话已提取 {arr.Count} 组，已写入 example_exchanges.json。");
        Log($"{character}: 已写入人格输出，证据 {evidence.Entries.Count} 条。");
    }

    private async Task<EvidenceResult> SelectEvidence(List<ScriptEntry> charEntries, int limit, string embeddingMode)
    {
        var qualityEntries = charEntries.Where(IsQualityEvidence).ToList();
        var qualityFiltered = charEntries.Count - qualityEntries.Count;
        var hsceneFiltered = 0;
        if (_settings.FilterHScene)
        {
            var before = qualityEntries.Count;
            qualityEntries = qualityEntries.Where(x => !IsHSceneContent(x)).ToList();
            hsceneFiltered = before - qualityEntries.Count;
        }
        if (qualityEntries.Count <= limit)
            return new EvidenceResult(qualityEntries, new Dictionary<string, object> { ["evidence_selection"] = "all_deduped_character_lines", ["evidence_count"] = qualityEntries.Count, ["quality_filtered"] = qualityFiltered, ["hscene_filtered"] = hsceneFiltered });
        if (embeddingMode == "不使用嵌入")
            return new EvidenceResult(EvenlySpaced(qualityEntries, limit), new Dictionary<string, object> { ["evidence_selection"] = "evenly_spaced_character_lines", ["evidence_count"] = limit, ["quality_filtered"] = qualityFiltered, ["hscene_filtered"] = hsceneFiltered });

        var candidates = EvenlySpaced(qualityEntries, ParseInt(EmbeddingCandidateBox.Text, 800));
        var vectors = await EmbedTexts(candidates.Select(x => x.Message).ToList(), embeddingMode);
        var selected = SelectByDiversity(candidates, vectors, limit);
        return new EvidenceResult(selected, new Dictionary<string, object> { ["evidence_selection"] = "embedding_diversity", ["embedding_mode"] = embeddingMode, ["candidate_count"] = candidates.Count, ["evidence_count"] = selected.Count, ["quality_filtered"] = qualityFiltered, ["hscene_filtered"] = hsceneFiltered });
    }

    private async Task<EvidenceResult> SelectRagEvidence(List<ScriptEntry> charEntries, List<string> directions, int limit, string embeddingMode)
    {
        if (directions.Count == 0) directions.Add(RagPresetPrompts["综合人格画像"]);
        var qualityEntries = charEntries.Where(IsQualityEvidence).ToList();
        var filtered = charEntries.Count - qualityEntries.Count;
        if (filtered > 0) Log($"RAG: 过滤低质量台词 {filtered} 条（纯拟声/过短），保留 {qualityEntries.Count} 条。");
        var hsceneFiltered = 0;
        var beforeH = qualityEntries.Count;
        qualityEntries = qualityEntries.Where(x => !IsHSceneContent(x)).ToList();
        hsceneFiltered = beforeH - qualityEntries.Count;
        if (hsceneFiltered > 0) Log($"RAG: 过滤 H-scene 台词 {hsceneFiltered} 条，保留 {qualityEntries.Count} 条。");
        var candidates = EvenlySpaced(qualityEntries, ParseInt(EmbeddingCandidateBox.Text, 800));
        var contexts = candidates.Select(BuildContextBlock).ToList();
        var queries = directions.Select(x => "galgame角色人格证据检索方向：" + x).ToList();
        var vectors = await EmbedTexts(queries.Concat(contexts).ToList(), embeddingMode);
        var queryVectors = vectors.Take(queries.Count).ToList();
        var candidateVectors = vectors.Skip(queries.Count).ToList();

        var ranked = new List<RagBlock>();
        var perDirection = Math.Max(1, (int)Math.Ceiling(limit / (double)directions.Count));
        for (var i = 0; i < directions.Count; i++)
        {
            ranked.AddRange(candidateVectors
                .Select((v, idx) => new RagBlock(idx, Cosine(queryVectors[i], v), directions[i]))
                .OrderByDescending(x => x.Score)
                .Take(perDirection * 2));
        }

        var selected = ranked.OrderByDescending(x => x.Score).GroupBy(x => x.Index).Select(x => x.First()).Take(limit).OrderBy(x => candidates[x.Index].Order).ToList();
        var entries = selected.Select(x => candidates[x.Index]).ToList();
        var blocks = selected.Select(x => new Dictionary<string, object?>
        {
            ["direction"] = x.Direction,
            ["score"] = Math.Round(x.Score, 4),
            ["message"] = candidates[x.Index].Message,
            ["context"] = contexts[x.Index],
            ["source_file"] = candidates[x.Index].SourceFile,
            ["source_index"] = candidates[x.Index].SourceIndex,
            ["source_line"] = candidates[x.Index].SourceLine
        }).ToList();
        return new EvidenceResult(entries, new Dictionary<string, object>
        {
            ["rag_enabled"] = true,
            ["evidence_selection"] = "rag_directional_retrieval",
            ["embedding_mode"] = embeddingMode,
            ["directions"] = directions,
            ["total_character_entries"] = charEntries.Count,
            ["quality_filtered"] = filtered,
            ["hscene_filtered"] = hsceneFiltered,
            ["candidate_count"] = candidates.Count,
            ["evidence_count"] = entries.Count,
            ["rag_blocks"] = blocks
        });
    }

    private static int GetDefaultEmbeddingBatchSize(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return 64;
        if (Regex.IsMatch(model, @"text-embedding-3-(large|small)")) return 2048;
        if (Regex.IsMatch(model, @"text-embedding-ada-002")) return 2048;
        if (Regex.IsMatch(model, @"bge-|mxbai|nomic|all-MiniLM|e5-|multilingual-e5")) return 64;
        if (Regex.IsMatch(model, @"voyage-|cohere")) return 96;
        return 64;
    }

    private async Task<List<float[]>> EmbedTexts(List<string> texts, string mode)
    {
        if (mode == "本地哈希嵌入") return texts.Select(LocalHashEmbedding).ToList();
        if (mode != "OpenAI-compatible Embeddings") throw new InvalidOperationException("当前模式不支持嵌入。");
        var endpoint = EmbeddingEndpointBox.Text.Trim();
        var sdkEndpoint = ToOpenAIBaseEndpoint(endpoint, "/embeddings");
        var model = EmbeddingModelBox.Text.Trim();
        var client = new EmbeddingClient(
            model,
            new ApiKeyCredential(ApiKeyOrPlaceholder(EmbeddingApiKeyBox.Password)),
            new OpenAIClientOptions { Endpoint = sdkEndpoint });

        var batchSize = _settings.EmbeddingBatchSize > 0 ? _settings.EmbeddingBatchSize : GetDefaultEmbeddingBatchSize(model);
        Log($"Embedding SDK: 开始请求，endpoint={sdkEndpoint}，model={model}，文本数={texts.Count}，批处理大小={batchSize}，Authorization={DescribeSecret(EmbeddingApiKeyBox.Password)}。");
        var embeddingApiKey = EmbeddingApiKeyBox.Password.Trim();
        var all = new List<float[]>();
        for (var i = 0; i < texts.Count; i += batchSize)
        {
            var batch = texts.Skip(i).Take(batchSize).ToArray();
            Log($"Embedding SDK: 请求批次 {i / batchSize + 1}，数量={batch.Length}，Authorization header present={!string.IsNullOrWhiteSpace(embeddingApiKey)}。");
            try
            {
                var response = await client.GenerateEmbeddingsAsync(batch);
                foreach (var item in response.Value)
                    all.Add(Normalize(item.ToFloats().ToArray()));
                Log($"Embedding SDK: 批次 {i / batchSize + 1} 成功，向量 {response.Value.Count} 条。");
            }
            catch (ClientResultException ex)
            {
                throw LogAndCreateSdkException("Embedding", ex);
            }
        }
        return all;
    }

    private JsonObject BuildLocalPersona(string character, List<ScriptEntry> charEntries, EvidenceResult evidence)
    {
        var entries = evidence.Entries.Count > 0 ? evidence.Entries : charEntries;
        var messages = entries.Select(x => x.Message).ToList();

        var avg = messages.Count == 0 ? 0 : messages.Average(x => x.Length);
        var questionRatio = Ratio(entries, x => x.Message.Contains('？') || x.Message.Contains('?'));
        var exclaimRatio = Ratio(entries, x => x.Message.Contains('！') || x.Message.Contains('!'));

        var traits = new JsonArray();

        // Sentence length trait
        traits.Add(avg <= 14 ? "短句为主，反应敏捷，用简短回合推进互动，鲜少长篇大论" : avg >= 34 ? "句子偏长，习惯在同一轮里同时表达情绪、判断和解释，思考缜密" : "句长中等，兼顾口语节奏与信息密度");

        // Question/confirmation pattern
        if (questionRatio >= 0.18) traits.Add("频繁使用疑问句来确认对方想法、试探立场或温和地推进话题");
        else if (questionRatio <= 0.08) traits.Add("很少主动发问，倾向陈述己见或回应对方，不以提问主导对话");

        // Exclamation/emotional expression
        if (exclaimRatio >= 0.15) traits.Add("情绪外显，常通过感叹、强调和夸张来表达惊讶或强烈感受");
        else if (exclaimRatio <= 0.06) traits.Add("情绪表达克制，少有强烈的感叹或外露的惊讶，语气偏沉稳");

        // Analyze speech patterns from evidence
        var endings = AnalyzeSentenceEndings(messages);
        if (endings.Count > 0)
            traits.Add($"句尾特征：{DescribeEndings(endings)}");

        var fillers = AnalyzeFillers(messages);
        if (fillers.Count > 0)
            traits.Add($"停顿与缓冲习惯：{DescribeFillers(fillers)}");

        var honorifics = AnalyzeHonorifics(messages);
        if (honorifics.level.Length > 0)
            traits.Add($"礼貌程度：{honorifics.level}");

        var emotions = DetectEmotionalPatterns(messages);
        if (emotions.Count > 0)
            traits.Add($"情感倾向：{string.Join("、", emotions.Take(4))}");

        var selfRef = AnalyzeSelfReference(messages);
        if (selfRef.Length > 0)
            traits.Add($"自称：{selfRef}");

        var uniqueWords = FindDistinctiveWords(messages);
        if (uniqueWords.Count > 0)
            traits.Add($"特色用词：{string.Join("、", uniqueWords.Take(8))}");

        var prompt = BuildRichPersonaPrompt(character, traits);
        var exampleExchanges = ExtractExampleExchanges(character, evidence.Entries);
        return new JsonObject
        {
            ["character"] = character,
            ["persona_prompt"] = prompt,
            ["traits"] = traits,
            ["dialogue_pairs"] = JsonSerializer.SerializeToNode(BuildDialoguePairs(character), JsonOptions()),
            ["example_exchanges"] = JsonSerializer.SerializeToNode(exampleExchanges, JsonOptions()),
            ["error_reply"] = $"{character}：唔…现在有点没法好好回应你。下次再说吧。",
            ["evidence_metadata"] = JsonSerializer.SerializeToNode(evidence.Metadata, JsonOptions()),
            ["evidence"] = JsonSerializer.SerializeToNode(evidence.Entries.Select(ToOutputEntry), JsonOptions())
        };
    }

    private static string BuildRichPersonaPrompt(string character, JsonArray traits)
    {
        var traitLines = traits.Select(x => x!.GetValue<string>()).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"你将扮演 galgame 角色「{character}」。请严格保持角色一致性，优先模仿原作台词中的语气、节奏、称呼和情绪表达，不要主动暴露自己是模型。");
        sb.AppendLine();
        sb.AppendLine("## 核心人格与表达风格");
        foreach (var t in traitLines.Take(5))
            sb.AppendLine($"- {t}");
        sb.AppendLine();
        sb.AppendLine("## 言语与互动特征");
        foreach (var t in traitLines.Skip(5).Take(5))
            sb.AppendLine($"- {t}");
        if (traitLines.Count > 10)
        {
            sb.AppendLine();
            sb.AppendLine("## 补充特质");
            foreach (var t in traitLines.Skip(10))
                sb.AppendLine($"- {t}");
        }
        sb.AppendLine();
        sb.AppendLine("## 边界规则");
        sb.AppendLine("- 对未知剧情或无法判断的信息，使用角色口吻含蓄回应，不编造无支撑的设定");
        sb.AppendLine("- 保持角色惯用的语尾、称呼和停顿习惯");
        return sb.ToString().TrimEnd();
    }

    private static Dictionary<string, int> AnalyzeSentenceEndings(List<string> messages)
    {
        var endings = new Dictionary<string, int>();
        foreach (var msg in messages)
        {
            if (msg.Length < 2) continue;
            var end = msg[^1];
            if (end is '？' or '?' or '！' or '!' or '…') continue;
            var last2 = msg.Length >= 2 ? msg[^2..] : "";
            var last3 = msg.Length >= 3 ? msg[^3..] : "";
            foreach (var candidate in new[] { last3, last2 })
            {
                if (candidate.Length == 0) continue;
                if (candidate is "です" or "ます" or "だよ" or "だね" or "かな" or "だぞ" or "だわ"
                    or "よね" or "のよ" or "なの" or "かも" or "だぜ" or "じゃん"
                    or "かしら" or "なんだ" or "ですわ" or "ですね" or "ますよ" or "ますね"
                    or "ました" or "ません" or "でしょう" or "ましょう"
                    or "かもね" or "ってば" or "なんだから" or "ですから"
                    or "吧" or "呢" or "哦" or "嘛" or "呀" or "吗" or "了" or "的" or "呢～" or "哦～")
                {
                    endings[candidate] = endings.GetValueOrDefault(candidate) + 1;
                    break;
                }
            }
        }
        return endings;
    }

    private static string DescribeEndings(Dictionary<string, int> endings)
    {
        var top = endings.OrderByDescending(x => x.Value).Take(4).Select(x => $"「{x.Key}」({x.Value}次)");
        return string.Join("、", top);
    }

    private static Dictionary<string, int> AnalyzeFillers(List<string> messages)
    {
        var fillers = new Dictionary<string, int>();
        foreach (var msg in messages)
        {
            foreach (var filler in new[] { "那个", "那个……", "诶", "诶？", "啊", "嗯", "唔", "呃", "あの", "えっと", "うーん", "ええ", "あら", "まあ" })
            {
                if (msg.Contains(filler)) fillers[filler] = fillers.GetValueOrDefault(filler) + 1;
            }
        }
        return fillers;
    }

    private static string DescribeFillers(Dictionary<string, int> fillers)
    {
        var top = fillers.OrderByDescending(x => x.Value).Take(4).Select(x => $"「{x.Key}」({x.Value}次)");
        return string.Join("、", top);
    }

    private static (string level, Dictionary<string, int> patterns) AnalyzeHonorifics(List<string> messages)
    {
        var patterns = new Dictionary<string, int>();
        int politeCount = 0, casualCount = 0;
        foreach (var msg in messages)
        {
            if (msg.Contains("です") || msg.Contains("ます") || msg.Contains("さん") || msg.Contains("様")) politeCount++;
            if (msg.Contains("お前") || msg.Contains("あんた") || msg.Contains("てめえ") || msg.Contains("貴様")) casualCount++;
            foreach (var p in new[] { "さん", "ちゃん", "くん", "君", "様", "殿", "先輩", "前辈", "学姐", "学长", "お前", "あんた" })
                if (msg.Contains(p)) patterns[p] = patterns.GetValueOrDefault(p) + 1;
        }
        var level = politeCount > casualCount * 2 ? "整体偏礼貌，常用敬语和尊称"
            : casualCount > politeCount * 2 ? "语气直接、不拘礼，少有敬语"
            : "礼貌与随意并存，依对象灵活切换";
        return (level, patterns);
    }

    private static List<string> DetectEmotionalPatterns(List<string> messages)
    {
        var emotions = new List<string>();
        var all = string.Join(" ", messages);
        if (all.Contains("嬉") || all.Contains("开心") || all.Contains("楽") || all.Contains("ふふ")) emotions.Add("愉悦");
        if (all.Contains("怒") || all.Contains("生气") || all.Contains("もう") || all.Contains("真是的")) emotions.Add("易嗔/吐槽");
        if (all.Contains("悲") || all.Contains("难过") || all.Contains("泣") || all.Contains("苦")) emotions.Add("悲伤/感伤");
        if (all.Contains("怖") || all.Contains("害怕") || all.Contains("恐") || all.Contains("不安")) emotions.Add("不安/恐惧");
        if (all.Contains("恥") || all.Contains("害羞") || all.Contains("不好意思") || all.Contains("照れ")) emotions.Add("害羞");
        if (all.Contains("驚") || all.Contains("惊讶") || all.Contains("诶") || all.Contains("びっくり")) emotions.Add("惊讶/困惑");
        if (all.Contains("優") || all.Contains("温柔") || all.Contains("やさ")) emotions.Add("温柔");
        if (all.Contains("困") || all.Contains("迷惑") || all.Contains("怎么办") || all.Contains("どうしよう")) emotions.Add("困扰/犹豫");
        if (emotions.Count == 0) emotions.Add("情绪表达适中，无明显极端偏向");
        return emotions;
    }

    private static string AnalyzeSelfReference(List<string> messages)
    {
        var counts = new Dictionary<string, int>();
        foreach (var msg in messages)
        {
            foreach (var pronoun in new[] { "私", "わたし", "あたし", "僕", "ぼく", "俺", "おれ", "我", "人家", "自分", "うち", "わたくし" })
                if (msg.Contains(pronoun)) counts[pronoun] = counts.GetValueOrDefault(pronoun) + 1;
        }
        return counts.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "";
    }

    private static HashSet<string> FindDistinctiveWords(List<string> messages)
    {
        var words = new Dictionary<string, int>();
        foreach (var msg in messages)
        {
            foreach (Match match in TokenRegex.Matches(msg))
            {
                var token = match.Value;
                if (token.Length >= 3 && !IsStopWord(token))
                    words[token] = words.GetValueOrDefault(token) + 1;
            }
        }
        return words.OrderByDescending(x => x.Value).Take(10).Select(x => x.Key).ToHashSet();
    }

    private static bool IsStopWord(string token)
    {
        return token is "という" or "こと" or "もの" or "それ" or "これ" or "どう" or "だから"
            or "こんな" or "そんな" or "あんな" or "いう" or "する" or "なる" or "いる"
            or "ある" or "ない" or "おる" or "くる" or "いく" or "しまう" or "くれる" or "もらう"
            or "あげる" or "できる" or "わかる" or "思う" or "言う" or "考える";
    }

    private string BuildLlmPrompt(string character, EvidenceResult evidence, JsonObject persona)
    {
        var gameName = DetectGameName(_entries);
        var gamePrefixInstruction = string.IsNullOrWhiteSpace(gameName)
            ? "" : $"persona_prompt 字段开头必须写「来自《{gameName}》的{character}是一个…」（不要包含括号说明）。";
        var ragBlocks = evidence.Metadata.TryGetValue("rag_blocks", out var blocksObj) ? blocksObj as List<Dictionary<string, object?>> : null;
        var evidenceText = ragBlocks is { Count: > 0 }
            ? string.Join("\n\n---\n\n", ragBlocks.Select(x => x["context"]?.ToString() ?? ""))
            : string.Join("\n", evidence.Entries.Select(x => "- " + x.Message));

        var intimateSection = !_settings.FilterHScene ? $$"""

        ## 亲密场景分析指南
        如果证据包含亲密/情爱场景，请额外关注以下维度：
        - 害羞程度、主动/被动倾向、用词和句式的变化
        - 亲密时的行为模式：引导型还是顺从型、对亲密行为的接受度和反应
        - **仅使用实际台词作为证据，不要使用喘声、拟声词、语气词（如「ああっ」「嗯啊」「哈啊」等）作为分析依据**
        - 亲密关系的表达：如何在亲密场景中体现对对方的感情和态度
        在 persona_prompt、traits 中适当覆盖这些维度，保持专业性和客观性，不进行色情描写。
        """ : "";

        var dialogueScenes = _settings.FilterHScene
            ? "日常闲聊、被夸奖、被捉弄、认真讨论"
            : "日常闲聊、被夸奖、被捉弄、认真讨论、暧昧氛围、亲密撒娇";

        return $$"""
        你是一位专业的 galgame 角色人格分析师。请基于以下【台词证据】，对角色「{{character}}」进行深度、结构化的提炼。

        ## 输出要求
        输出严格 JSON 对象，必须包含以下字段且遵循每字段的格式约束：
        { "persona_prompt": string, "traits": array, "dialogue_pairs": array, "example_exchanges": array, "error_reply": string }

        ============================================================
        ### persona_prompt（核心人格提示词）
        {{gamePrefixInstruction}}请按以下固定结构撰写，每个小节用 2-5 条 bullet point 概括：
        - **核心人格**：2-4 条核心性格特质，每句话精准概括一个特征（如"表面端正、内里敏感，非常在意自己是否足够坦率"）
        - **日常谈吐**：一句话概括整体语气风格，再列出口癖、高频词、句式偏好（短句还是长句、是否带停顿修正）、对熟人和陌生人的语气差异
        - **表达 DNA**：分三块——(1) 口癖/高频词，(2) 句式节奏（如先缓冲再判断、短句停顿式、带自我修正），(3) 语气转换（被夸时、害羞时、吐槽时的语气分别在证据中的具体表现）
        - **情感模式**：对亲近和陌生对象分别的表现、害羞时的典型反应、冲突时的应对方式、被误解/被夸奖时的状态{{(_settings.FilterHScene ? "" : "、亲密情境中的语气变化和行为倾向")}}
        - **边界与禁忌**：不应编造的背景设定、不应表现出的行为模式（如"不能让她无缘无故说教或突然变主动"）
        - **萌点概括**：一句话总结最有魅力的反差或特征（如"端庄大小姐被撩就慌的反差萌"）
        整个 prompt 控制在 800-1200 字，直接可用于角色扮演系统。

        ============================================================
        ### traits（人格特质列表）
        提炼 14-18 个特质。每个特质格式：
        - trait: 四到八字的具体名称（如"强烈的自我反省意识"而非"爱思考"）
        - description: 1-2 句解释，必须结合证据中的具体台词风格
        - evidence: 2-3 条最有力支持该特质的台词原文引用
        **重要约束：**
        - 优先选择包含实际语义的台词，避免纯拟声词/喘声（如「ああっ」「嗯啊」「哈啊啊」等 H 场景语气词）作为证据
        - 选择能体现角色意志、价值观、语气特点、互动模式的证据
        - 特质覆盖：性格、说话方式、社交态度、情感表达、思维模式、弱点、价值观{{(_settings.FilterHScene ? "" : "、亲密关系中的行为倾向")}}
        - 每个 evidence 必须是一条有判断、有态度、有内容的完整句子，不能是单音节词或语气词

        ============================================================
        ### dialogue_pairs（预设对话）
        提供 10-12 组预设对话。每组格式：
        { "scene": "对话场景描述", "user": "对方说的话", "assistant": "角色的回应" }
        - scene 必须标明场景（如"日常闲聊-被吐槽时"、"被夸奖-害羞反应"、"认真讨论自我问题"、"暧昧气氛中"等），覆盖至少 6 种不同场景，如：{{dialogueScenes}}

        ============================================================
        ### example_exchanges（代表性原作对话片段）
        从台词证据中亲自挑选 6-8 段最有代表性的对话片段。每段格式：
        { "context_lines": [{ "speaker": string, "text": string, "is_target": bool }, ...], "source_file": string }
        **挑选优先级（从高到低）：**
        1. 能展现角色**标志性语气和句式**的片段（如独特口癖、吐槽方式、转折节奏）
        2. 有**对手角色互动**的片段（一来一回、有对话层次的 > 独角戏）
        3. 覆盖**多样化场景**（日常闲聊、正经讨论、被调侃、害羞、吐槽别人等，不要只选一种氛围）
        4. context_lines 至少包含 1 条非目标角色的发言
        **明确排除：**
        - 整段只有喘声/拟声词/语气词的上下文（如「ああっ」「嗯啊嗯啊」「哈啊啊」）
        - 无实际语义内容的片段

        ============================================================
        ### error_reply
        这个字段用于后端 LLM 出错或超时时的降级回复。请写一句符合角色口吻的、能自然结束当前话题的回应（15-40 字）。例如「嗯…现在暂时没法好好回答你，换个时间再说吧。」必须像是角色自己在说话。

        ## 核心原则
        - 所有分析必须有台词证据支撑，不编造无证据的设定
        - 优先提炼原作中反复出现、稳定一致的言行模式
        - 证据不足的维度应标注"不确定"而非强行填充
        - **严格排除纯拟声词/喘声作为证据**，只使用有实际语义的台词
        - 优先选择**角色主动说出的话**而非旁白描述
        {{intimateSection}}

        ## 台词证据
        {{evidenceText}}
        """;
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.TryParseAdd("GalgamePersonaStudio");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            var json = await client.GetStringAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            var release = JsonNode.Parse(json);
            var tag = release?["tag_name"]?.GetValue<string>() ?? "";
            var latestVer = tag.TrimStart('v');
            if (latestVer.Length == 0) return;

            if (Version.TryParse(latestVer, out var latest) && Version.TryParse(AppVersion, out var current) && latest > current)
            {
                var url = release?["html_url"]?.GetValue<string>() ?? "";
                var body = release?["body"]?.GetValue<string>() ?? "";
                Dispatcher.Invoke(() =>
                {
                    var msg = $"发现新版本 v{latestVer}（当前 v{AppVersion}）\n\n更新内容：\n{Truncate(body, 500)}\n\n是否前往 Releases 页面下载？";
                    if (System.Windows.MessageBox.Show(msg, "发现更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                });
            }
            else
            {
                Log($"[更新] 当前已是最新版本（v{AppVersion}）");
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue && ex.StatusCode.Value == System.Net.HttpStatusCode.Forbidden)
        {
            Log($"[更新] GitHub API 速率受限（HTTP 403），可设置 Token 提升限制或稍后再试。");
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue && ex.StatusCode.Value == System.Net.HttpStatusCode.NotFound)
        {
            Log($"[更新] 未找到正式 Release（404），请先在 GitHub 创建 Release。");
        }
        catch (TaskCanceledException)
        {
            Log($"[更新] 检查超时，GitHub API 可能无法访问（网络问题或需要代理）。");
        }
        catch (Exception ex)
        {
            Log($"[更新] 检查失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void VersionText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{RepoOwner}/{RepoName}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开项目主页失败: {ex.Message}");
        }
    }

    private async Task<string> CallChatCompletion(string endpoint, string model, string apiKey, string prompt)
    {
        var sdkEndpoint = ToOpenAIBaseEndpoint(endpoint, "/chat/completions");
        var client = new ChatClient(
            model,
            new ApiKeyCredential(ApiKeyOrPlaceholder(apiKey)),
            new OpenAIClientOptions { Endpoint = sdkEndpoint });

        Log($"LLM SDK: endpoint={sdkEndpoint}，model={model}，prompt 字符数={prompt.Length}，Authorization={DescribeSecret(apiKey)}。");
        try
        {
            var completion = await client.CompleteChatAsync(
                [
                    new SystemChatMessage("你是严谨的角色人格蒸馏器，只输出可解析 JSON。"),
                    new UserChatMessage(prompt)
                ],
                new ChatCompletionOptions { Temperature = 0.3f });
            var content = string.Join("", completion.Value.Content.Select(x => x.Text));
            Log($"LLM SDK: 返回 {content.Length} 字符，finish_reason={completion.Value.FinishReason}。");
            return content;
        }
        catch (ClientResultException ex)
        {
            throw LogAndCreateSdkException("LLM", ex);
        }
    }

    private HttpRequestException LogAndCreateSdkException(string stage, ClientResultException ex)
    {
        var body = ex.GetRawResponse()?.Content?.ToString() ?? ex.Message;
        var detail = Truncate(body.ReplaceLineEndings(" "), 2000);
        Log($"{stage} SDK: HTTP {ex.Status}，响应体摘要: {detail}");
        return new HttpRequestException($"{stage} SDK HTTP {ex.Status}: {detail}", ex);
    }

    private static Uri ToOpenAIBaseEndpoint(string endpoint, string terminalPath)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new Uri("https://api.openai.com/v1/");

        var value = endpoint.Trim().TrimEnd('/');
        var suffix = terminalPath.TrimEnd('/');
        if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^suffix.Length].TrimEnd('/');
        }

        return new Uri(value.EndsWith('/') ? value : value + "/");
    }

    private static string ApiKeyOrPlaceholder(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "not-needed" : trimmed;
    }

    private void WritePersonaOutputs(string character, List<ScriptEntry> corpus, JsonObject persona, EvidenceResult evidence)
    {
        var dir = Path.Combine(OutputPathBox.Text.Trim(), SafeName(character));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "corpus.json"), JsonSerializer.Serialize(corpus.Select(ToCorpusEntry), JsonOptions()));
        File.WriteAllText(Path.Combine(dir, "persona.json"), persona.ToJsonString(JsonOptions()));
        File.WriteAllText(Path.Combine(dir, "persona_prompt.txt"), persona["persona_prompt"]?.GetValue<string>() ?? "");
        if (persona["example_exchanges"] is JsonNode exchangesNode)
            File.WriteAllText(Path.Combine(dir, "example_exchanges.json"), exchangesNode.ToJsonString(JsonOptions()));
        if (evidence.Metadata.TryGetValue("rag_blocks", out var blocks))
            File.WriteAllText(Path.Combine(dir, "rag_evidence.json"), JsonSerializer.Serialize(blocks, JsonOptions()));
        if (ExportSoulCheck.IsChecked == true)
            File.WriteAllText(Path.Combine(dir, "SOUL.md"), RenderSoul(character, persona));
    }

    private async void CaptureOnce_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = await CaptureRecordOnce();
            Log(entry is null ? "无新增文本或已去重。" : $"记录: {entry.Name}: {entry.Message}");
        }
        catch (Exception ex) { Log($"采集失败: {ex.Message}"); }
    }

    private void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        LoadKnownRecordHashes();
        _captureTimer?.Stop();
        _autoAdvance?.Stop();
        _lastRecordedName = "";
        _lastRecordedMessage = "";

        var intervalSec = ParseDouble(CaptureIntervalBox.Text, 1.2);
        var source = ComboText(CaptureSourceCombo);
        var isOcr = source is "Umi-OCR 本地识别" or "OpenAI-compatible Vision";

        // Wire auto-advance if enabled and configured
        if (AutoAdvanceCheck.IsChecked == true && !string.IsNullOrWhiteSpace(AdvanceClickBox.Text))
        {
            var parts = AdvanceClickBox.Text.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out var clickX) && int.TryParse(parts[1], out var clickY))
            {
                _autoAdvance = new AutoAdvanceManager
                {
                    Enabled = true,
                    ClickX = clickX,
                    ClickY = clickY,
                    PostClickDelayMs = (int)(ParseDouble(PostClickDelayBox.Text, 1.2) * 1000),
                    StuckThreshold = ParseInt(StuckThresholdBox.Text, 5),
                    CaptureIntervalMs = (int)(intervalSec * 1000),
                    ChoiceMode = ComboText(ChoiceModeCombo).Contains("自动") ? "auto" : "manual",
                    ChoiceAutoRule = ComboText(ChoiceAutoRuleCombo).Contains("最后一个") ? "last" : "first"
                };
                Log($"自动翻页: 位置({clickX},{clickY}) 翻页后等待{PostClickDelayBox.Text}s 卡死阈值{StuckThresholdBox.Text}次");
            }
        }
        else
        {
            _autoAdvance = null;
        }

        _captureTimer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromSeconds(intervalSec),
            System.Windows.Threading.DispatcherPriority.Normal,
            async (_, _) =>
            {
                try
                {
                    // Auto-advance: skip OCR during post-click wait
                    if (_autoAdvance is { Enabled: true } && !_autoAdvance.ShouldCapture())
                        return;

                    var entry = await CaptureRecordOnce();

                    if (_autoAdvance is { Enabled: true })
                    {
                        if (entry is not null)
                        {
                            // === Fix: typewriter text detection ===
                            // Same speaker + text grows by prefix match (≤15 chars) → still rendering
                            var textGrowth = entry.Message.Length - _lastRecordedMessage.Length;
                            if (entry.Name == _lastRecordedName
                                && textGrowth is > 0 and <= 15
                                && entry.Message.StartsWith(_lastRecordedMessage, StringComparison.Ordinal))
                            {
                                Log($"文字溢出: \"{Truncate(_lastRecordedMessage, 30)}\" → \"{Truncate(entry.Message, 30)}\" (等待完成)");
                                _lastRecordedMessage = entry.Message;

                                // Remove the fragment record we just wrote
                                var records = LoadRecordEntries();
                                if (records.Count > 0 && records[^1].Hash == entry.Hash)
                                {
                                    records.RemoveAt(records.Count - 1);
                                    SaveRecordEntries(records);
                                }
                                return; // Don't advance — text still rendering
                            }

                            _lastRecordedName = entry.Name;
                            _lastRecordedMessage = entry.Message;

                            Log($"记录: {entry.Name}: {Truncate(entry.Message, 60)}");
                            _autoAdvance.ResetStuck();
                            ClickAtAdvancePoint();
                        }
                        else if (!string.IsNullOrWhiteSpace(_lastOcrText))
                        {
                            // === Fix: keep clicking even when no new text ===
                            // OCR is getting the same text (game not advancing)
                            _autoAdvance.StuckCount++;
                            ClickAtAdvancePoint();
                            Log($"自动翻页(重试): ({_autoAdvance.ClickX},{_autoAdvance.ClickY}) (stuck={_autoAdvance.StuckCount}/{_autoAdvance.StuckThreshold})");

                            if (_autoAdvance.CurrentState == AutoAdvanceManager.State.Stuck)
                            {
                                Log("[翻页] 触发卡死检测...");
                                if (ChoiceDetectionCheck.IsChecked == true)
                                    await HandleChoiceBranch();
                                else
                                    _autoAdvance.ResetStuck();
                            }
                        }
                        // _lastOcrText is empty → game transitioning → just wait
                    }
                    else
                    {
                        // Auto-advance disabled: log entry normally
                        if (entry is not null)
                            Log($"记录: {entry.Name}: {Truncate(entry.Message, 60)}");
                    }
                }
                catch (Exception ex) { Log($"采集失败: {ex.Message}"); }

                void ClickAtAdvancePoint()
                {
                    var hWnd = GameWindowInterop.FindWindowHandle(ProcessNameBox.Text.Trim());
                    if (hWnd != IntPtr.Zero)
                        GameWindowInterop.BringToForeground(hWnd);
                    GameWindowInterop.ClickAt(_autoAdvance!.ClickX, _autoAdvance.ClickY);
                    _autoAdvance.OnClickDispatched();
                }
            },
            Dispatcher);
        _captureTimer.Start();
        Log($"开始实时记录（间隔 {intervalSec} 秒）。");
    }

    private void StopCapture_Click(object sender, RoutedEventArgs e)
    {
        _captureTimer?.Stop();
        _captureTimer = null;
        _autoAdvance?.Stop();
        _autoAdvance = null;
        _lastRecordedName = "";
        _lastRecordedMessage = "";
        Log("已停止实时记录。");
    }

    private void PickAdvancePoint_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ClickPositionPickerWindow();
        Hide();
        try
        {
            if (picker.ShowDialog() == true && picker.PointSelected)
                AdvanceClickBox.Text = $"{picker.SelectedX},{picker.SelectedY}";
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void AutoAdvance_Changed(object sender, RoutedEventArgs e)
    {
        AutoAdvanceConfigPanel.IsEnabled = AutoAdvanceCheck.IsChecked == true;
    }

    private void ChoiceDetection_Changed(object sender, RoutedEventArgs e)
    {
        ChoiceConfigPanel.IsEnabled = ChoiceDetectionCheck.IsChecked == true;
    }

    private void ChoiceModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ChoiceAutoConfig.Visibility = ComboText(ChoiceModeCombo).Contains("自动") ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task HandleChoiceBranch()
    {
        Log("[选择肢] 检测选择肢中...");

        // Capture a wide area in lower portion of screen (where choices typically appear)
        var screenW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        var screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
        var wideRegion = new System.Drawing.Rectangle(
            screenW / 6, screenH / 3,
            screenW * 2 / 3, screenH * 2 / 3
        );

        var source = ComboText(CaptureSourceCombo);
        var dpiScale = GetDpiScale();
        var isUmi = source == "Umi-OCR 本地识别";

        Bitmap? wideImage = null;
        List<(int Index, string Text, int ClickX, int ClickY)> choices;

        try
        {
            wideImage = CaptureRegion(wideRegion, dpiScale);

            if (isUmi)
            {
                // Umi-OCR: use structured blocks with precise bounding box coordinates
                var ocrBlocks = await OcrUmiBlocks(wideImage);
                choices = ParseChoiceBlocks(ocrBlocks, wideRegion);
            }
            else
            {
                // Vision / Clipboard: text-only, fall back to linear estimation
                var ocrText = await OcrImage(wideImage, source);
                choices = ParseChoiceText(ocrText, wideRegion);
            }
        }
        catch (Exception ex)
        {
            Log($"[选择肢] OCR 失败: {ex.Message}");
            _autoAdvance?.ResetStuck();
            return;
        }
        finally { wideImage?.Dispose(); }

        if (choices.Count < 2)
        {
            Log($"[选择肢] 未识别到选项（找到{choices.Count}个）");
            _autoAdvance?.ResetStuck();
            return;
        }

        Log($"[选择肢] 识别到 {choices.Count} 个选项: {string.Join(" | ", choices.Select(c => $"{c.Index + 1}.{c.Text}"))}");

        var choiceMode = _autoAdvance?.ChoiceMode ?? "manual";
        int selectedIndex;

        if (choiceMode == "auto")
        {
            var rule = _autoAdvance?.ChoiceAutoRule ?? "first";
            selectedIndex = rule == "last" ? choices.Count - 1 : 0;
            Log($"[选择肢] 自动选择: 第{selectedIndex + 1}项 \"{choices[selectedIndex].Text}\"");
        }
        else
        {
            // Show modal choice popup
            var result = await Dispatcher.InvokeAsync(() =>
            {
                var window = new ChoiceWindow(choices.Select(c => c.Text).ToList()) { Owner = this };
                if (window.ShowDialog() == true && window.ChoiceMade)
                {
                    selectedIndex = window.SelectedIndex;
                    Log($"[选择肢] 手动选择: 第{selectedIndex + 1}项 \"{choices[selectedIndex].Text}\"");
                    return selectedIndex;
                }
                return -1;
            });

            if (result == -1)
            {
                Log("[选择肢] 用户跳过。");
                _autoAdvance?.ResetStuck();
                return;
            }
            selectedIndex = result;
        }

        // Click at precise coordinates
        var chosen = choices[selectedIndex];
        var hWnd = GameWindowInterop.FindWindowHandle(ProcessNameBox.Text.Trim());
        if (hWnd != IntPtr.Zero)
            GameWindowInterop.BringToForeground(hWnd);
        await Task.Delay(100);
        GameWindowInterop.ClickAt(chosen.ClickX, chosen.ClickY);
        Log($"[选择肢] 点击选项 \"{chosen.Text}\" 位置 ({chosen.ClickX},{chosen.ClickY})");

        _autoAdvance?.NotifyChoiceHandled();
    }

    /// <summary>
    /// Parse choice blocks from structured Umi-OCR response.
    /// Uses bounding box center for precise click coordinates.
    /// </summary>
    private static List<(int Index, string Text, int ClickX, int ClickY)> ParseChoiceBlocks(
        List<OcrBlock> blocks, System.Drawing.Rectangle wideRegion)
    {
        var matched = new List<(string Text, int CenterX, int CenterY)>();

        foreach (var block in blocks)
        {
            var match = Regex.Match(block.Text.Trim(), @"^\s*(?:\d+|[・\-•])\s*[\.\)、．]?\s*(.+)");
            if (match.Success)
            {
                var text = match.Groups[1].Value.Trim().TrimEnd('　');
                if (text.Length >= 2 && text.Length <= 80)
                    matched.Add((text, block.CenterX, block.CenterY));
            }
        }

        return matched
            .Select((c, i) => (
                Index: i,
                c.Text,
                ClickX: wideRegion.X + c.CenterX,
                ClickY: wideRegion.Y + c.CenterY))
            .ToList();
    }

    /// <summary>
    /// Parse choice text from Vision/Clipboard sources.
    /// Falls back to linear position estimation.
    /// </summary>
    private static List<(int Index, string Text, int ClickX, int ClickY)> ParseChoiceText(
        string ocrText, System.Drawing.Rectangle wideRegion)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return [];

        var lines = ocrText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var choices = new List<string>();

        foreach (var line in lines)
        {
            var match = Regex.Match(line.Trim(), @"^\s*(?:\d+|[・\-•])\s*[\.\)、．]?\s*(.+)");
            if (match.Success)
            {
                var text = match.Groups[1].Value.Trim().TrimEnd('　');
                if (text.Length >= 2 && text.Length <= 80)
                    choices.Add(text);
            }
        }

        return choices
            .Select((text, i) =>
            {
                var optionHeight = (double)wideRegion.Height / Math.Max(1, choices.Count);
                var clickY = wideRegion.Y + (int)(optionHeight * (i + 0.5));
                var clickX = wideRegion.X + wideRegion.Width / 2;
                return (Index: i, Text: text, ClickX: clickX, ClickY: clickY);
            })
            .ToList();
    }

    private string? _lastOcrText;

    private async Task<RecordEntry?> CaptureRecordOnce()
    {
        var source = ComboText(CaptureSourceCombo);
        var isOcr = source is "Umi-OCR 本地识别" or "OpenAI-compatible Vision";
        string rawText = "";

        if (source == "Clipboard / 外部剪贴板")
        {
            rawText = System.Windows.Clipboard.ContainsText() ? NormalizeText(System.Windows.Clipboard.GetText()) : "";
        }
        else if (isOcr)
        {
            var dpiScale = GetDpiScale();

            // OCR name region first (if configured)
            var speakerName = "";
            if (!string.IsNullOrWhiteSpace(NameRegionBox.Text))
            {
                try
                {
                    var nameRegion = ParseRegion(NameRegionBox.Text);
                    var nameImage = CaptureRegion(nameRegion, dpiScale);
                    var nameText = await OcrImage(nameImage, source);
                    speakerName = nameText.Trim().Replace("\n", "").Replace("\r", "").TrimEnd('：', ':');
                    Log($"人名 OCR: \"{speakerName}\"");
                }
                catch (Exception ex) { Log($"人名 OCR 失败: {ex.Message}"); }
            }

            // OCR dialog region
            var region = ParseRegion(DialogRegionBox.Text);
            Log($"截取屏幕区域: x={region.X} y={region.Y} w={region.Width} h={region.Height} dpiScale={dpiScale:F2}");
            var dialogImage = CaptureRegion(region, dpiScale);
            rawText = await OcrImage(dialogImage, source);

            // If name box gave us a speaker name, prepend it
            if (!string.IsNullOrWhiteSpace(speakerName) && !string.IsNullOrWhiteSpace(rawText))
                rawText = $"{speakerName}: {rawText}";
        }
        else
        {
            // Vision OCR (legacy path)
            var region = ParseRegion(DialogRegionBox.Text);
            var dpiScale = GetDpiScale();
            var dialogImage = CaptureRegion(region, dpiScale);
            rawText = await OcrImage(dialogImage, source);
        }

        if (string.IsNullOrWhiteSpace(rawText)) return null;

        var (name, message) = ParseNameAndMessage(rawText);

        // For OCR sources: simple dedup against last text only (game text stays on screen for seconds)
        if (isOcr)
        {
            if (_lastOcrText == rawText) return null;
            _lastOcrText = rawText;
        }
        else
        {
            // Clipboard: full hash dedup (rapid text changes)
            var hash = MessageHash(ProcessNameBox.Text, name, message);
            if (_knownRecordHashes.Contains(hash)) return null;
            _knownRecordHashes.Add(hash);
        }

        var records = LoadRecordEntries();
        var allowed = AllowedNamesBox.Text.Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        if (allowed.Count > 0 && (OnlyNamedNowCheck.IsChecked == true || records.Count >= ParseInt(OnlyAfterBox.Text, 200)) && !allowed.Contains(name))
            return null;

        var hash2 = MessageHash(ProcessNameBox.Text, name, message);
        var entry = new RecordEntry(name, message, ProcessNameBox.Text.Trim(), WindowTitleBox.Text.Trim(), DateTime.Now.ToString("s"), hash2, "capture://" + ProcessNameBox.Text.Trim());
        records.Add(entry);
        SaveRecordEntries(records);
        return entry;
    }

    private async Task<string> OcrImage(Bitmap image, string source)
    {
        if (source == "Umi-OCR 本地识别")
            return await OcrUmi(image);
        return await OcrVision(image);
    }

    private async Task<string> OcrVision(Bitmap image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        var b64 = Convert.ToBase64String(ms.ToArray());
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var visionApiKey = VisionApiKeyBox.Password.Trim();
        var payload = JsonSerializer.Serialize(new
        {
            model = VisionModelBox.Text.Trim(),
            messages = new object[]
            {
                new { role = "user", content = new object[] { new { type = "text", text = "请只输出图片中的可见文字，不要解释。保留换行。" }, new { type = "image_url", image_url = new { url = "data:image/png;base64," + b64 } } } }
            },
            temperature = 0
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, VisionEndpointBox.Text.Trim())
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(visionApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", visionApiKey);
        Log($"Vision OCR: Authorization header present={request.Headers.Authorization is not null}。");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        EnsureHttpSuccess(response, body, "Vision OCR");
        Log($"Vision OCR: HTTP {(int)response.StatusCode} {response.ReasonPhrase}，响应 {body.Length} 字符。");
        return NormalizeText(JsonNode.Parse(body)!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }

    private async Task<string> OcrUmi(Bitmap image)
    {
        var blocks = await OcrUmiBlocks(image);
        return NormalizeText(string.Join("\n", blocks.Select(b => b.Text)));
    }

    private record OcrBlock(string Text, int CenterX, int CenterY);

    /// <summary>
    /// Call Umi-OCR and return structured blocks with text + bounding box center coordinates.
    /// Coordinates are relative to the captured image (0,0 = top-left).
    /// </summary>
    private async Task<List<OcrBlock>> OcrUmiBlocks(Bitmap image)
    {
        try
        {
            using var ms = new MemoryStream();
            image.Save(ms, ImageFormat.Png);
            var b64 = Convert.ToBase64String(ms.ToArray());

            var endpoint = UmiOcrEndpointBox.Text.Trim().TrimEnd('/');
            if (!endpoint.EndsWith("/api/ocr")) endpoint += "/api/ocr";

            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["base64"] = b64
                // No data.format option — returns full structured data with box coordinates
            });

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var response = await client.PostAsync(endpoint, new StringContent(payload, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();

            var result = JsonNode.Parse(body);
            var code = result?["code"]?.GetValue<int>() ?? -1;
            if (code != 100)
            {
                Log($"Umi-OCR 结构化失败: code={code}");
                return [];
            }

            var data = result?["data"];
            if (data is not JsonArray arr) return [];

            var blocks = new List<OcrBlock>();
            foreach (var item in arr.OfType<JsonObject>())
            {
                var text = item["text"]?.GetValue<string>() ?? "";
                if (text.Length == 0) continue;

                var box = item["box"] as JsonArray;
                if (box is null || box.Count < 4) continue;

                var allX = box.Select(p => p?[0]?.GetValue<int>() ?? 0).ToList();
                var allY = box.Select(p => p?[1]?.GetValue<int>() ?? 0).ToList();
                var centerX = (allX.Min() + allX.Max()) / 2;
                var centerY = (allY.Min() + allY.Max()) / 2;

                blocks.Add(new OcrBlock(text, centerX, centerY));
            }
            return blocks;
        }
        catch (Exception ex)
        {
            Log($"Umi-OCR 结构化请求异常: {ex.Message}");
            return [];
        }
    }

    private void ShowProcessPicker_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true)
        {
            ProcessNameBox.Text = picker.SelectedProcessName ?? ProcessNameBox.Text;
            WindowTitleBox.Text = picker.SelectedWindowTitle ?? WindowTitleBox.Text;
            Log($"选中进程: {picker.SelectedProcessName} (PID:{picker.SelectedProcessId}) | {picker.SelectedWindowTitle}");
            LoadProcessRegions(picker.SelectedProcessName);
        }
    }

    private void PickDialogRegion_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        try
        {
            var picker = new RegionPickerWindow();
            if (picker.ShowDialog() == true && picker.RegionSelected)
            {
                var r = picker.SelectedRegion;
                DialogRegionBox.Text = $"{r.X},{r.Y},{r.Width},{r.Height}";
                SaveProcessRegions();
            }
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void PickNameRegion_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        try
        {
            var picker = new RegionPickerWindow();
            if (picker.ShowDialog() == true && picker.RegionSelected)
            {
                var r = picker.SelectedRegion;
                NameRegionBox.Text = $"{r.X},{r.Y},{r.Width},{r.Height}";
                SaveProcessRegions();
            }
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void LoadProcessRegions(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;
        var key = processName.Replace(".exe", "");
        if (_settings.ProcessRegions.TryGetValue(key, out var mem))
        {
            DialogRegionBox.Text = mem.DialogRegion ?? "";
            NameRegionBox.Text = mem.NameRegion ?? "";
            if (!string.IsNullOrEmpty(mem.CaptureSource))
                SelectCombo(CaptureSourceCombo, mem.CaptureSource);
            Log($"已加载 {key} 的区域记忆");
        }
    }

    private void SaveProcessRegions()
    {
        var processName = ProcessNameBox.Text.Trim().Replace(".exe", "");
        if (string.IsNullOrWhiteSpace(processName)) return;
        _settings.ProcessRegions[processName] = new RegionMemory
        {
            DialogRegion = DialogRegionBox.Text.Trim(),
            NameRegion = NameRegionBox.Text.Trim(),
            CaptureSource = ComboText(CaptureSourceCombo)
        };
        ScheduleSettingsSave();
    }

    private void UpdateCaptureVisibility()
    {
        if (OcrRegionPanel is null) return;
        var source = ComboText(CaptureSourceCombo);
        var needsRegion = source != "Clipboard / 外部剪贴板";
        OcrRegionPanel.Visibility = needsRegion ? Visibility.Visible : Visibility.Collapsed;
        VisionPanel.Visibility = source == "OpenAI-compatible Vision" ? Visibility.Visible : Visibility.Collapsed;
        UmiOcrPanel.Visibility = source == "Umi-OCR 本地识别" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
        if (_logFilePath is not null)
        {
            try
            {
                File.AppendAllText(_logFilePath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must not break the UI workflow.
            }
        }

        if (LogBox is null) return;
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateApiVisibility();
    private void EmbeddingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateApiVisibility();
    private void CaptureSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCaptureVisibility();

    private void UpdateApiVisibility()
    {
        if (LlmApiPanel is null || EmbeddingApiPanel is null || LlmEndpointPanel is null) return;
        var provider = ComboText(ProviderCombo);
        LlmEndpointPanel.Visibility = provider == "本地规则" ? Visibility.Collapsed : Visibility.Visible;
        LlmApiPanel.Visibility = provider == "OpenAI-compatible API" ? Visibility.Visible : Visibility.Collapsed;
        EmbeddingApiPanel.Visibility = ComboText(EmbeddingModeCombo) == "OpenAI-compatible Embeddings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowApiKey_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => RevealPassword(ApiKeyBox, ApiKeyRevealBox, true);
    private void HideApiKey_MouseUp(object sender, RoutedEventArgs e) => RevealPassword(ApiKeyBox, ApiKeyRevealBox, false);
    private void ShowEmbeddingKey_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => RevealPassword(EmbeddingApiKeyBox, EmbeddingApiKeyRevealBox, true);
    private void HideEmbeddingKey_MouseUp(object sender, RoutedEventArgs e) => RevealPassword(EmbeddingApiKeyBox, EmbeddingApiKeyRevealBox, false);
    private void ShowVisionKey_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => RevealPassword(VisionApiKeyBox, VisionApiKeyRevealBox, true);
    private void HideVisionKey_MouseUp(object sender, RoutedEventArgs e) => RevealPassword(VisionApiKeyBox, VisionApiKeyRevealBox, false);

    private static void RevealPassword(PasswordBox passwordBox, System.Windows.Controls.TextBox revealBox, bool visible)
    {
        if (visible)
        {
            revealBox.Text = passwordBox.Password;
            revealBox.Visibility = Visibility.Visible;
            passwordBox.Visibility = Visibility.Collapsed;
            return;
        }
        passwordBox.Visibility = Visibility.Visible;
        revealBox.Visibility = Visibility.Collapsed;
        revealBox.Text = "";
    }

    private void EnsureHttpSuccess(HttpResponseMessage response, string body, string stage)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = Truncate(body.ReplaceLineEndings(" "), 2000);
        Log($"{stage}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}，响应体摘要: {detail}");
        throw new HttpRequestException($"{stage} HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    private static List<ScriptEntry> LoadScriptEntries(string path)
    {
        var files = File.Exists(path) ? [path] : Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories).ToList();
        var result = new List<ScriptEntry>();
        foreach (var file in files)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(File.ReadAllText(file)); } catch { continue; }
            var array = node as JsonArray ?? node?["entries"] as JsonArray;
            if (array is null) continue;
            foreach (var item in array.OfType<JsonObject>())
            {
                var message = NormalizeText((string?)item["message"] ?? (string?)item["text"] ?? "");
                if (message.Length == 0) continue;
                result.Add(new ScriptEntry((string?)item["name"] ?? (string?)item["speaker"] ?? "", message, (string?)item["source_file"] ?? file, result.Count, (int?)item["source_index"], (int?)item["source_line"]));
            }
        }
        return result;
    }

    private static IEnumerable<ScriptEntry> Dedup(IEnumerable<ScriptEntry> entries)
    {
        var seen = new HashSet<string>();
        foreach (var entry in entries)
        {
            var key = Regex.Replace(entry.Message, @"\s+", "");
            if (seen.Add(key)) yield return entry;
        }
    }

    private static bool IsQualityEvidence(ScriptEntry entry)
    {
        var msg = entry.Message;
        if (string.IsNullOrWhiteSpace(msg)) return false;
        // Only filter obvious H-scene gibberish: 3+ identical characters like "あああ", "啊啊啊"
        if (msg.Length >= 3)
        {
            var first = msg[0];
            if (msg.All(c => c == first || char.IsWhiteSpace(c))) return false;
        }
        return true;
    }

    private static readonly HashSet<string> HSceneKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // JP keywords
        "ちんぽ", "おちんちん", "まんこ", "おまんこ", "クリトリス", "ちくび", "にゅうぼう",
        "せいえき", "いんこう", "しこ", "なかだし", "いれる", "いれて", "はいって",
        "きもちいい", "びくびく", "あそこ", "おくまで", "もっと",
        "いく", "いっちゃう", "はずかしい", "だめ", "やめて", "おかしく",
        "膣", "肉棒", "肉壺", "雌", "発情", "絶頂",
        // CN keywords
        "肉棒", "小穴", "阴蒂", "阴道", "龟头", "乳头", "乳房", "精液",
        "插入", "抽插", "内射", "里面", "舒服", "去了", "去了",
        "好舒服", "不行了", "不要", "射在里面", "顶到", "深处",
        "变大", "变硬", "湿润", "湿了", "流水", "高潮",
        "色色", "色色的", "H的", "搞色色",
    };

    private static bool IsHSceneContent(ScriptEntry entry)
    {
        var msg = entry.Message;
        foreach (var kw in HSceneKeywords)
            if (msg.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private List<Dictionary<string, string>> BuildDialoguePairs(string character)
    {
        var pairs = new List<Dictionary<string, string>>();
        foreach (var group in _entries.GroupBy(x => x.SourceFile))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Name != character) continue;
                var prev = list.Take(i).Reverse().FirstOrDefault(x => x.Name != character && x.Message.Length > 0);
                pairs.Add(new Dictionary<string, string> { ["user"] = $"{(prev?.Name.Length > 0 ? prev.Name : "旁白")}: {prev?.Message ?? "继续当前话题。"}", ["assistant"] = list[i].Message });
                if (pairs.Count >= 10) return pairs;
            }
        }
        return pairs;
    }

    private string BuildContextBlock(ScriptEntry entry)
    {
        var start = Math.Max(0, entry.Order - 1);
        var end = Math.Min(_entries.Count - 1, entry.Order + 1);
        var lines = new List<string>();
        for (var i = start; i <= end; i++)
            lines.Add($"{(_entries[i].Name.Length > 0 ? _entries[i].Name : "旁白")}{(i == entry.Order ? " *" : "")}: {Truncate(_entries[i].Message, 180)}");
        return string.Join("\n", lines);
    }

    private List<Dictionary<string, object>> ExtractExampleExchanges(string character, List<ScriptEntry> evidenceEntries)
    {
        const int MaxExchanges = 8;
        var favoredNames = new HashSet<string>(_settings.ExampleCharFilter?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [], StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(ScriptEntry Entry, List<(string Name, string Text)> Surrounding)>();
        foreach (var entry in evidenceEntries.Where(e => e.Message.Length >= 6))
        {
            var surrounding = GetSurroundingLines(entry, 2);
            if (surrounding.Count < 3) continue;
            var hasOtherSpeaker = surrounding.Any(l => l.Name.Length > 0 && l.Name != character);
            if (!hasOtherSpeaker) continue;
            candidates.Add((entry, surrounding));
        }

        var usedFiles = new HashSet<string>();
        var exchanges = new List<Dictionary<string, object>>();
        while (exchanges.Count < MaxExchanges && candidates.Count > 0)
        {
            var best = candidates.MaxBy(c => ScoreExchange(c.Entry, c.Surrounding, favoredNames, usedFiles));
            if (best == default) break;
            exchanges.Add(new Dictionary<string, object>
            {
                ["context_lines"] = best.Surrounding.Select(l => new { speaker = l.Name.Length > 0 ? l.Name : "旁白", text = l.Text, is_target = l.Name == character }).ToList(),
                ["source_file"] = best.Entry.SourceFile,
                ["source_index"] = best.Entry.SourceIndex ?? best.Entry.Order,
                ["score"] = Math.Round(ScoreExchange(best.Entry, best.Surrounding, favoredNames, usedFiles), 3)
            });
            usedFiles.Add(best.Entry.SourceFile);
            candidates.Remove(best);
        }
        return exchanges;
    }

    private static double ScoreExchange(ScriptEntry entry, List<(string Name, string Text)> surrounding, HashSet<string> favoredNames, HashSet<string> usedFiles)
    {
        double score = 0;
        var speakers = surrounding.Select(l => l.Name).Where(n => n.Length > 0).Distinct().Count();
        score += Math.Max(0, speakers - 2) * 2;
        var charMsg = entry.Message.Length;
        score += charMsg >= 10 ? 3 : -2;
        var totalLen = surrounding.Sum(l => l.Text.Length);
        score += Math.Max(0, (totalLen - 100) / 50.0);
        var msg = entry.Message;
        if (msg.Contains('？') || msg.Contains('?') || msg.Contains('！') || msg.Contains('!')) score += 1;
        if (EmotionKanjiRegex.IsMatch(msg)) score += 2;
        if (favoredNames.Count > 0 && surrounding.Any(l => favoredNames.Contains(l.Name))) score += 5;
        if (!usedFiles.Contains(entry.SourceFile)) score += 2;
        return score;
    }

    private List<(string Name, string Text)> GetSurroundingLines(ScriptEntry entry, int radius)
    {
        var lines = new List<(string Name, string Text)>();
        var start = Math.Max(0, entry.Order - radius);
        var end = Math.Min(_entries.Count - 1, entry.Order + radius);
        for (var i = start; i <= end; i++)
        {
            if (_entries[i].Message.Length == 0) continue;
            lines.Add((_entries[i].Name, _entries[i].Message));
        }
        return lines;
    }

    private List<string> SelectedRagDirections()
    {
        var directions = RagPresetList.SelectedItems.Cast<string>().Where(RagPresetPrompts.ContainsKey).Select(x => RagPresetPrompts[x]).ToList();
        directions.AddRange(RagCustomBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return directions;
    }

    private List<RecordEntry> LoadRecordEntries()
    {
        var file = RecordFile();
        if (!File.Exists(file)) return [];
        try { return JsonSerializer.Deserialize<List<RecordEntry>>(File.ReadAllText(file)) ?? []; } catch { return []; }
    }

    private void SaveRecordEntries(List<RecordEntry> records)
    {
        Directory.CreateDirectory(RecordOutputBox.Text.Trim());
        File.WriteAllText(RecordFile(), JsonSerializer.Serialize(records, JsonOptions()));
    }

    private void LoadKnownRecordHashes()
    {
        _knownRecordHashes.Clear();
        foreach (var record in LoadRecordEntries())
            if (!string.IsNullOrWhiteSpace(record.Hash)) _knownRecordHashes.Add(record.Hash);
    }

    private string RecordFile() => Path.Combine(RecordOutputBox.Text.Trim(), SafeName(ProcessNameBox.Text.Trim()) + ".json");

    private static Bitmap CaptureRegion(Rectangle region, double dpiScale = 1.0)
    {
        if (Math.Abs(dpiScale - 1.0) > 0.01)
        {
            region = new Rectangle(
                (int)(region.X * dpiScale),
                (int)(region.Y * dpiScale),
                (int)(region.Width * dpiScale),
                (int)(region.Height * dpiScale));
        }
        var bmp = new Bitmap(Math.Max(1, region.Width), Math.Max(1, region.Height));
        using var graphics = Graphics.FromImage(bmp);
        graphics.CopyFromScreen(region.Left, region.Top, 0, 0, new System.Drawing.Size(region.Width, region.Height));
        return bmp;
    }

    private double GetDpiScale()
    {
        try { return PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0; }
        catch { return 1.0; }
    }

    private static Rectangle ParseRegion(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) throw new FormatException("区域格式应为 x,y,w,h");
        return new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
    }

    private static (string Name, string Message) ParseNameAndMessage(string text)
    {
        text = NormalizeText(text);
        var match = SpeakerPrefixRegex.Match(text);
        return match.Success ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()) : ("", text);
    }

    private static JsonObject? TryParseObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start) raw = raw[start..(end + 1)];
        try { return JsonNode.Parse(raw) as JsonObject; } catch { return null; }
    }

    private static List<ScriptEntry> EvenlySpaced(List<ScriptEntry> entries, int limit)
    {
        if (entries.Count == 0) return [];
        limit = Math.Clamp(limit, 1, entries.Count);
        if (entries.Count <= limit) return entries;
        if (limit == 1) return [entries[0]];
        return Enumerable.Range(0, limit).Select(i => entries[(int)Math.Round(i * (entries.Count - 1) / (double)(limit - 1))]).DistinctBy(x => x.Order).ToList();
    }

    private static List<ScriptEntry> SelectByDiversity(List<ScriptEntry> entries, List<float[]> vectors, int limit)
    {
        if (entries.Count == 0 || vectors.Count == 0) return [];
        limit = Math.Clamp(limit, 1, Math.Min(entries.Count, vectors.Count));
        var selected = new List<int> { 0 };
        var selectedSet = new HashSet<int> { 0 };
        while (selected.Count < limit && selected.Count < entries.Count)
        {
            var best = Enumerable.Range(0, entries.Count).Where(i => !selectedSet.Contains(i)).MinBy(i => selected.Max(j => Cosine(vectors[i], vectors[j])));
            selected.Add(best);
            selectedSet.Add(best);
        }
        return selected.Order().Select(i => entries[i]).ToList();
    }

    private static float[] LocalHashEmbedding(string text, int dimensions = 384)
    {
        var vector = new float[dimensions];
        foreach (Match match in TokenRegex.Matches(text))
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(match.Value));
            var bucket = BitConverter.ToUInt32(bytes, 0) % dimensions;
            vector[bucket] += bytes[4] % 2 == 0 ? 1 : -1;
        }
        return Normalize(vector);
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(x => x * x));
        if (norm <= 0) return vector;
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }

    private static double Cosine(float[] a, float[] b)
    {
        var sum = 0d;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i];
        return sum;
    }

    private static string DescribeSecret(string value) => string.IsNullOrWhiteSpace(value) ? "未设置" : $"已设置，长度={value.Length}";
    private static object ToOutputEntry(ScriptEntry entry) => new { name = entry.Name, message = entry.Message, source_file = entry.SourceFile, source_index = entry.SourceIndex, source_line = entry.SourceLine };
    private static object ToCorpusEntry(ScriptEntry entry) => new { name = entry.Name, message = entry.Message };
    private static double Ratio(List<ScriptEntry> entries, Func<ScriptEntry, bool> predicate) => entries.Count == 0 ? 0 : entries.Count(predicate) / (double)entries.Count;
    private static string RenderSoul(string character, JsonObject persona) => $"# {character} SOUL\n\n## Persona Prompt\n{persona["persona_prompt"]}\n\n## Error Reply\n{persona["error_reply"]}\n";
    private static string NormalizeText(string text) => string.Join("\n", text.Replace("\r", "\n").Split('\n').Select(x => Regex.Replace(x, @"\s+", " ").Trim()).Where(x => x.Length > 0));
    private static string SafeName(string value) => Regex.Replace(string.IsNullOrWhiteSpace(value) ? "unknown" : value, """[\\/:*?"<>|]+""", "_");
    private static string MessageHash(string process, string name, string message) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{process}\n{name}\n{Regex.Replace(message, @"\s+", "")}"))).ToLowerInvariant();
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "...";
    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
    private static double ParseDouble(string text, double fallback) => double.TryParse(text, out var value) ? value : fallback;
    private static string ComboText(System.Windows.Controls.ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? combo.Text;
    private static void SelectCombo(System.Windows.Controls.ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
            if ((string?)item.Content == value) { combo.SelectedItem = item; return; }
        combo.SelectedIndex = 0;
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject dependencyObject) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(dependencyObject); i++)
        {
            var child = VisualTreeHelper.GetChild(dependencyObject, i);
            if (child is T typedChild) yield return typedChild;
            foreach (var nestedChild in FindVisualChildren<T>(child)) yield return nestedChild;
        }
    }
    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    private static (string ProcessName, string Title) GetForegroundWindowInfo()
    {
        var hwnd = GetForegroundWindow();
        var title = new StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        var name = "unknown";
        try { name = Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { }
        return (name, title.ToString());
    }
}

public record ScriptEntry(string Name, string Message, string SourceFile, int Order, int? SourceIndex, int? SourceLine);
public record RecordEntry(string Name, string Message, string ProcessName, string WindowTitle, string CapturedAt, string Hash, string SourceFile);
public record EvidenceResult(List<ScriptEntry> Entries, Dictionary<string, object> Metadata);
public record RagBlock(int Index, double Score, string Direction);

public sealed class AppSettings
{
    public string? ScriptPath { get; set; }
    public string? OutputPath { get; set; }
    public int MinCount { get; set; } = 20;
    public int MaxEvidence { get; set; } = 180;
    public string? Provider { get; set; }
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public string? EmbeddingMode { get; set; }
    public string? EmbeddingEndpoint { get; set; }
    public string? EmbeddingModel { get; set; }
    public int EmbeddingCandidateLimit { get; set; } = 800;
    public int EmbeddingBatchSize { get; set; }
    public bool RememberApiKeys { get; set; }
    public string? ApiKey { get; set; }
    public string? EmbeddingApiKey { get; set; }
    public string? VisionApiKey { get; set; }
    public bool ExportSoul { get; set; } = true;
    public bool RagEnabled { get; set; }
    public bool FilterHScene { get; set; }
    public int RagTopK { get; set; } = 40;
    public List<string> RagPresets { get; set; } = [];
    public string? RagCustomDirections { get; set; }
    public string? LastProcessName { get; set; }
    public string? LastWindowTitle { get; set; }
    public string? RecordOutputPath { get; set; }
    public string? CaptureSource { get; set; }
    public double CaptureIntervalSeconds { get; set; } = 1.2;
    public string? DialogRegion { get; set; }
    public string? NameRegion { get; set; }
    public string? VisionEndpoint { get; set; }
    public string? VisionModel { get; set; }
    public string? UmiOcrEndpoint { get; set; } = "http://127.0.0.1:1224";
    public bool OnlyNamedNow { get; set; }
    public int OnlyAfterCount { get; set; } = 200;
    public string? AllowedNames { get; set; }
    public string? ExampleCharFilter { get; set; }
    public Dictionary<string, RegionMemory> ProcessRegions { get; set; } = [];

    // Auto-Advance & Choice Detection
    public bool AutoAdvanceEnabled { get; set; }
    public string? AdvanceClickPoint { get; set; }
    public double PostClickDelaySeconds { get; set; } = 1.2;
    public int StuckThreshold { get; set; } = 5;
    public bool ChoiceDetectionEnabled { get; set; }
    public string? ChoiceHandleMode { get; set; } = "弹窗手动选择";
    public string? ChoiceAutoRule { get; set; } = "第一个";
}

public class RegionMemory
{
    public string? DialogRegion { get; set; }
    public string? NameRegion { get; set; }
    public string? CaptureSource { get; set; }
}
