# VPetLLM

VPetLLM 是一个为 [VPet-Simulator](https://github.com/LorisYounger/VPet) 设计的插件，它允许你使用各种大型语言模型（LLM）、语音识别（ASR）和文本转语音（TTS）技术与你的虚拟宠物进行深度互动。

## 📋 系统要求

- Windows 10/11
- .NET Framework 4.6.2 或更高版本
- VPet-Simulator 主程序
- **MPV 播放器**（用于 TTS 音频播放）

### MPV 播放器安装

TTS 功能需要 MPV 播放器支持。请按以下步骤安装：

1. 从 [mpv.io](https://mpv.io/installation/) 或 [SourceForge](https://sourceforge.net/projects/mpv-player-windows/files/) 下载 Windows 版本
2. 解压后将 `mpv` 文件夹放置到插件目录下（与 VPetLLM.dll 同级目录）
3. 确保 `mpv/mpv.exe` 文件存在

## ✨ 功能

- **多样的 LLM 提供商支持：**
  - **Ollama:** 与在本地运行的 Ollama 模型进行交互。
  - **OpenAI:** 集成 OpenAI 的 GPT 模型。
  - **Gemini:** 连接 Google 的 Gemini 模型。
  - **Free:** 通过免费的公共 API 访问 LLM 服务。
- **丰富的语音交互：**
  - **文本转语音 (TTS):** 让你的宠物用生动的声音说话。支持 `OpenAI`, `GPT-SoVITS`, `DIY` 和 `URL` 等多种 TTS 服务。
  - **语音识别 (ASR):** 通过语音与你的宠物交谈。支持 `OpenAI`, `Soniox` 和 `Free` 等 ASR 服务，并可通过快捷键激活。
- **LLM 驱动的智能行为：**
  - **动作执行:** LLM 可以根据对话内容，驱动宠物做出各种动作，如移动、触摸反馈等。
  - **状态感知:** LLM 能够感知宠物的当前状态（如饱食度、心情等），并作出相应的回应。
  - **购买反馈:** 当你为宠物购买物品时，它会给出智能的反馈。
- **灵活的上下文管理：**
  - **保持聊天上下文：** 在整个会话中保持对话的连续性。
  - **保存聊天记录：** 自动保存你的对话，以便下次启动时可以恢复。
  - **按提供商分离聊天记录：** 为每个 LLM 提供商维护独立的聊天历史。
- **高级模型设置：**
  - **温度控制：** 调整模型响应的随机性。
  - **最大 Token:** 设置模型单次响应的最大长度。
- **优化的用户界面：**
  - **选项卡式布局：** 清晰地组织不同 LLM 提供商的设置。
  - **可调整的角色设定窗口：** 通过拖拽轻松地调整角色设定文本框的大小。
  - **“未保存”提示：** 当有未保存的更改时，提供清晰的视觉提示。
- **强大的插件系统：**
  - **添加自定义工具：** 通过“工具列表”选项卡，添加、删除和配置外部工具。
  - **扩展核心功能：** 开发者可以创建自己的插件来连接外部 API、执行自定义动作，或为宠物赋予全新的技能。


## ⚙️ 配置

1. 打开 VPet-Simulator 的设置，并进入“MOD Config”选项卡。
2. 点击“VPetLLM”以打开插件设置。
3. **选择 LLM 提供商：** 从下拉菜单中选择你想要使用的 LLM 提供商（Ollama、OpenAI、Gemini 或 Free）。
4. **填写提供商设置：**
   - **Ollama:** 输入你的 Ollama 服务的 URL（默认为 `http://localhost:11434`）并选择一个模型。
   - **OpenAI:** 输入你的 API 密钥、API 地址和模型名称。
   - **Gemini:** 输入你的 API 密钥、API 地址和模型名称。
   - **Free:** 无需额外配置。
5. **语音服务 (TTS/ASR):**
   - 进入 **TTS** 选项卡，启用并选择你喜欢的 TTS 提供商，并填写相关配置（如 API Key, URL 等）。
   - 进入 **ASR** 选项卡，启用并选择你的 ASR 提供商，配置快捷键和相关 API 信息。
6. **角色设定：** 在可调整大小的文本框中定义你的虚拟宠物的角色和个性。
7. **上下文控制：** 根据你的偏好，配置聊天上下文和历史记录的保存选项。
8. **高级设置：** 如果需要，可以为每个提供商启用并调整高级设置，如温度和最大 Token。
9. **工具列表：**
   - 点击“工具列表”选项卡。
   - 点击“添加”按钮以添加一个新的工具。
   - 在表格中填写工具的“名称”、“URL”和“API Key”（如果需要）。
   - 勾选“启用”复选框以启用该工具。
   - 点击“删除”按钮以删除选定的工具。
10. **保存：** 点击“保存”按钮以应用你的设置。如果有未保存的更改，“未保存”的红色提示将会出现。

## 💬 使用

1. 在 VPet-Simulator 中打开聊天窗口。
2. 输入你的消息并按 Enter，或者使用你配置的快捷键进行语音输入。
3. 你的虚拟宠物将会使用你配置的 LLM、TTS 和 ASR 服务来与你互动。如果启用了任何工具，它也可能会使用这些工具来提供更丰富、更有用的响应。

## 🔌 插件系统

VPetLLM 拥有一个强大的插件系统，允许开发者扩展其功能。通过创建自己的插件，你可以：

- **连接外部 API**: 获取天气、新闻、股票等实时数据。
- **执行自定义动作**: 控制智能家居、发送邮件、或者任何你能想到的事情。
- **增强 AI 的能力**: 为你的桌宠赋予全新的、独一无二的技能。

我们为插件开发者提供了一套完整的接口和文档。

**➡️ [点击这里查看详细的插件开发文档](https://github.com/ycxom/VPetLLM_Plugin)**

## 📁 项目结构

```
VPetLLM/
├── Configuration/     # 配置管理模块
│   ├── ISettings.cs          # 设置接口
│   ├── SettingsManager.cs    # 设置管理器
│   └── *Settings.cs          # 各类设置（LLM/TTS/ASR/Proxy）
├── Core/              # 核心业务逻辑
│   ├── ASRCore/              # 语音识别核心实现
│   ├── ChatCore/             # 聊天核心实现（Ollama/OpenAI/Gemini/Free）
│   ├── TTSCore/              # 文本转语音核心实现
│   ├── ServiceContainer.cs   # 依赖注入容器
│   └── *Manager.cs           # 各类管理器（历史记录/重要记录）
├── Handlers/          # 动作处理器
│   ├── HandlerRegistry.cs    # 处理器注册中心
│   ├── ActionProcessor.cs    # 动作处理器
│   ├── SmartMessageProcessor.cs  # 智能消息处理
│   └── *Handler.cs           # 各类动作处理器
├── Services/          # 服务层
│   ├── VoiceInputService.cs  # 语音输入服务
│   ├── PurchaseService.cs    # 购买服务
│   └── I*Service.cs          # 服务接口
├── UI/                # 用户界面
│   ├── Controls/             # 自定义控件
│   ├── Styles/               # 样式资源
│   └── Windows/              # 窗口
├── Utils/             # 工具类
│   ├── MpvPlayer.cs          # MPV 音频播放器
│   ├── TTSService.cs         # TTS 服务
│   ├── ASRService.cs         # ASR 服务
│   └── ...                   # 其他工具类
└── VPetLLM.cs         # 插件主入口
```

## 🛠️ 开发

### 构建项目

```bash
# 克隆仓库
git clone https://github.com/ycxom/VPetLLM.git

# 使用 Visual Studio 打开 VPetLLM.sln
# 或使用命令行构建
dotnet build VPetLLM.sln
```

### 架构说明

项目采用模块化架构设计：

- **依赖注入**: 使用 `ServiceContainer` 管理服务依赖
- **处理器模式**: 通过 `HandlerRegistry` 统一管理动作处理器
- **接口抽象**: 核心功能通过接口定义，便于扩展和测试
- **分层设计**: Configuration → Core → Services → Handlers → UI

## 🤝 贡献

欢迎通过 Pull Request 为项目做出贡献。

*该项目在开发过程中得到了 AI 的辅助。*
