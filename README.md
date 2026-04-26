# Galgame Persona Studio - 让你的bot/agent更懂你的老婆！


基于游戏脚本的 Galgame 角色人格提取。可自动从脚本 JSON 中提取高频角色、分析台词特征并生成角色卡，解决直接询问LLM角色人格回答过于单薄/冷门角色没有数据。

## 主要特性

### 人格生成
- 读取 GalTransl 导出的剧本 JSON（`message`/`name` 格式），按频次筛选角色
- 台词质量过滤（排除纯拟声词、过短台词）
- H 场景关键词过滤
- 本地规则快速分析：句长分布、疑问/感叹比例、句尾特征、口癖、自称、礼貌程度、情感倾向、特色用词
- RAG 方向检索：通过 Embedding 按「综合人格画像」「恋爱与亲密关系」「日常吐槽」「弱点与不安」等方向检索代表性台词
- 支持 OpenAI-compatible Embedding API / 本地哈希嵌入

### LLM 增强
支持接入任意 OpenAI-compatible API（Ollama / OpenAI / 兼容端），基于脚本自动生成：
- **persona_prompt**：可直接用于 AI 角色扮演的核心人格提示词
- **traits**：人格特质列表（含台词证据引用）
- **dialogue_pairs**：预设对话示例（覆盖多种场景）
- **example_exchanges**：代表性原作对话片段
- **error_reply**：证据不足时的婉拒回复

### 实时记录（推荐使用[Umi-OCR](https://github.com/hiroi-sora/Umi-OCR)）
- **Clipboard 模式**：监听剪贴板，配合外部工具抓取译文
- **Vision 模式**：调用 OpenAI-compatible Vision API 截图识别对话区域
- **Umi-OCR 模式**：调用本地 [Umi-OCR](https://github.com/hiroi-sora/Umi-OCR) 进行离线文字识别
- 支持框选对话区域和人名区域，按进程记忆区域配置
- 可按角色名筛选记录，仅保留指定角色的台词

## 用法

1. 获得脚本json文件，可通过实时记录功能或解包获得，json文件格式兼容[GalTransl](https://github.com/XDkkk/GalTransl)。（注：解包相关内容可参照 [GalTransl](https://github.com/XDkkk/GalTransl) 中对应部分）
2. 在「角色管理」面板选择剧本目录，扫描并筛选角色
3. 在「人格生成」面板配置 LLM / Embedding 参数（可选）
4. 在 RAG 面板选择检索方向
5. 点击「生成人格文件」输出到指定目录

### 实时记录流程
1. 选择游戏进程，配置文本来源
2. OCR 模式需框选对话区域和人名区域
3. 点击「开始记录」自动循环采集

## 目录结构

```
GalgamePersonaStudio/
├── MainWindow.xaml(.cs)      # 主界面
├── RegionPickerWindow.xaml(.cs) # 区域框选窗口
├── ProcessPickerWindow.xaml(.cs) # 进程选择窗口
├── App.xaml(.cs)             # 应用入口
├── GalgamePersonaStudio.csproj
├── .github/workflows/build.yml # CI
└── README.md
```

## 依赖与开源许可

本项目使用以下 NuGet 包及库：

| 包名 | 许可证 | 用途 |
|------|--------|------|
| [OpenAI](https://www.nuget.org/packages/OpenAI/) | MIT | LLM / Embedding API 调用 |
| [WPF-UI](https://github.com/lepoco/wpfui) | MIT | UI 控件库 |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common/) | MIT | 屏幕截图与图像处理 |
| [Umi-OCR](https://github.com/hiroi-sora/Umi-OCR) | MIT | 可选，本地 OCR 引擎 |

本项目基于 **MIT License** 开源。

## TODO

- [ ] 连点器功能：与 OCR 结合，按设定间隔自动点击游戏窗口指定位置，实现对话文本的自动化提取
- [ ] 更多 Embedding 后端支持
- [ ] 批量角色人格生成
