# Galgame Persona Studio - 让你的 bot/agent 更懂你的老婆！

基于游戏脚本的 Galgame 角色人格提取。可自动从脚本 JSON 中提取高频角色、分析台词特征并生成角色卡，解决直接询问 LLM 角色人格回答过于单薄 / 冷门角色没有数据的问题。理论上支持所有基于对话框对话的游戏的人物人格提取。

## 主要特性

### 人格生成
- 读取剧本 JSON（`message`/`name` 格式），按频次筛选角色
- 台词质量过滤（排除纯重复字符如 `あああ`）
- H 场景关键词过滤（可选）
- 本地规则快速分析：句长分布、疑问/感叹比例、句尾特征、口癖、自称、礼貌程度、情感倾向、特色用词
- **RAG 方向检索**：通过 Embedding 按「综合人格画像」「恋爱与亲密关系」「日常吐槽」「弱点与不安」等方向检索代表性台词
- 支持 OpenAI-compatible Embedding API / 本地哈希嵌入
- **世界观提取**：从剧本旁白中提取游戏世界背景，嵌入检索到与角色最相关的世界观片段，增强 persona_prompt 的上下文一致性

### LLM 增强
支持接入任意 OpenAI-compatible API（Ollama / OpenAI / 兼容端），基于脚本自动生成：
- **persona_prompt**：可直接用于 AI 角色扮演的核心人格提示词（含世界观参考）
- **traits**：人格特质列表（含台词证据引用）
- **dialogue_pairs**：预设对话示例（覆盖多种场景）
- **example_exchanges**：代表性原作对话片段（可筛选角色）
- **error_reply**：后端 LLM 出错或超时时的降级回复，保持角色口吻

### 自动化剧本提取（连点器） 
- **OCR 驱动自动翻页**：识别到新对话文本后自动点击游戏翻页位置
- **选择肢检测**：连续多次无新文本后触发，通过宽区域 OCR + 正则识别选项（可配框选区域） （暂不支持如D.C系列. Rewrite等有地图类选项的游戏的自动选择）
- **弹窗手动选择 / 自动选择**（第一条/最后一条）
- **黑屏/过场跳过**：连续无文字时自动点击推进


### 实时记录
- **Clipboard 模式**：监听剪贴板，配合外部工具抓取译文
- **Vision 模式**：调用 OpenAI-compatible Vision API 截图识别对话区域
- **Umi-OCR 模式**：调用本地 [Umi-OCR](https://github.com/hiroi-sora/Umi-OCR) 进行离线文字识别（推荐，无需联网）
- 支持框选对话区域、人名区域
- 可按角色名/时间筛选记录，仅保留指定角色的台词
- 记录输出为精简 JSON（仅 `name` + `message`，进程名作为文件名）

## 用法

### 人格生成
1. 获得脚本 JSON 文件，可通过实时记录功能或解包获得，格式兼容 [GalTransl](https://github.com/GalTransl/GalTransl)
2. 在「角色管理」面板选择剧本目录，扫描并筛选角色。可选：在角色名筛选框填入优先互动的角色名
3. 在「人格生成」面板配置 LLM / Embedding 参数（可选）
4. 在 RAG 面板选择检索方向
5. 点击「生成人格文件」输出到指定目录

### 实时记录
1. 选择游戏进程，配置文本来源（推荐 Umi-OCR）
2. 框选对话区域和人名区域
3. 点击「开始记录」自动循环采集

### 自动化提取
1. 在「启用自动翻页」中拾取翻页点击位置和翻页后等待时间
2. 设置卡死/选择肢阈值（默认 5 次）
3. 可选：启用选择肢检测，框选选择肢区域，配置处理模式
4. 可选：设置快捷键（默认 Ctrl+F1 开始，Ctrl+F2 停止）
5. 点击「开始记录」→ 程序自动最小化 → 全自动提取


## 目录结构

```
GalgamePersonaStudio/
├── MainWindow.xaml(.cs)                # 主界面
├── AutoAdvanceManager.cs               # 自动翻页状态机
├── GameWindowInterop.cs                # Win32 全局点击/进程查找
├── RegionPickerWindow.xaml(.cs)        # 区域框选窗口
├── ClickPositionPickerWindow.xaml(.cs) # 翻页位置拾取窗口
├── ProcessPickerWindow.xaml(.cs)       # 进程选择窗口
├── ChoiceWindow.xaml(.cs)              # 选择肢弹窗
├── App.xaml(.cs)                       # 应用入口
├── GalgamePersonaStudio.csproj
├── .github/workflows/build.yml         # CI
└── README.md
```

## 依赖与开源许可

本项目使用以下 NuGet 包及库：

| 包名 | 许可证 | 用途 |
|------|--------|------|
| [OpenAI](https://www.nuget.org/packages/OpenAI/) | MIT | LLM / Embedding API 调用 |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common/) | MIT | 屏幕截图与图像处理 |
| [Umi-OCR](https://github.com/hiroi-sora/Umi-OCR) | MIT | 可选，本地 OCR 引擎 |

本项目基于 **MIT License** 开源。

## TODO

- [x] 连点器：与 OCR 结合实现自动化文本提取
- [x] 选择肢识别与处理
- [ ] 兼容地图类选择肢
- [ ] 批量角色人格生成
