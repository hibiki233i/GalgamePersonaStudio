using System.IO;

namespace GalgamePersonaStudio;

/// <summary>
/// State machine for auto-advance through visual novel dialogue.
/// Tracks OCR text history and detects when the game is stuck (choice screen).
/// OCR result driven: only clicks when new text is recorded.
/// </summary>
public class AutoAdvanceManager
{
    private string _lastText = "";
    private int _postClickSkipCount;

    public enum State { Idle, Active, Stuck }
    public State CurrentState { get; private set; } = State.Idle;

    public bool Enabled { get; set; }
    public int ClickX { get; set; }
    public int ClickY { get; set; }
    public int StuckCount { get; set; }
    public int StuckThreshold { get; set; } = 5;
    public int PostOcrDelayMs { get; set; } = 300;
    public int PostClickDelayMs { get; set; } = 1200;
    public int CaptureIntervalMs { get; set; } = 1200;
    public string ChoiceMode { get; set; } = "manual";
    public string ChoiceAutoRule { get; set; } = "first";

    /// <summary>
    /// Called each timer tick. Returns true if OCR should proceed.
    /// Blocks OCR during post-click wait period.
    /// </summary>
    public bool ShouldCapture()
    {
        if (!Enabled || CurrentState == State.Idle) return true;

        if (_postClickSkipCount > 0)
        {
            _postClickSkipCount--;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Called after OCR capture. Returns true if a click should be scheduled (new text recorded).
    /// </summary>
    public bool NotifyCaptureResult(string? capturedText, bool isDuplicate)
    {
        if (!Enabled) return false;

        if (capturedText == null || isDuplicate || capturedText == _lastText)
        {
            StuckCount++;
            if (StuckCount >= StuckThreshold)
            {
                CurrentState = State.Stuck;
                LogToFile($"[AutoAdvance] Stuck after {StuckCount} identical OCR results, entering choice mode.");
            }
            return false;
        }

        _lastText = capturedText;
        StuckCount = 0;
        return true;
    }

    /// <summary>
    /// Called after click is dispatched. Blocks OCR for PostClickDelayMs.
    /// </summary>
    public void OnClickDispatched()
    {
        if (!Enabled) return;
        var skipTicks = Math.Max(1, PostClickDelayMs / Math.Max(1, CaptureIntervalMs));
        _postClickSkipCount = skipTicks;
    }

    /// <summary>
    /// Called after choice branch is handled.
    /// </summary>
    public void NotifyChoiceHandled()
    {
        StuckCount = 0;
        _lastText = "";
        CurrentState = State.Active;
    }

    /// <summary>
    /// Reset stuck count without full stop.
    /// </summary>
    public void ResetStuck()
    {
        StuckCount = 0;
        CurrentState = State.Active;
    }

    /// <summary>
    /// Stop auto-advance completely.
    /// </summary>
    public void Stop()
    {
        CurrentState = State.Idle;
        StuckCount = 0;
        _lastText = "";
        _postClickSkipCount = 0;
    }

    private static void LogToFile(string message)
    {
        try
        {
            var logDir = Path.Combine(AppContext.BaseDirectory, "log");
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, $"auto-advance-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
