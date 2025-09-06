# VPetLLM

VPetLLM 是一个为 VPet-Simulator 设计的插件，它允许你使用各种大型语言模型（LLM）与你的虚拟宠物进行交谈。

## ✨ 功能

- **多样的 LLM 提供商支持：**
  - **Ollama:** 与在本地运行的 Ollama 模型进行交互。
  - **OpenAI:** 集成 OpenAI 的 GPT 模型。
  - **Gemini:** 连接 Google 的 Gemini 模型。
- **灵活的上下文管理：**
  - **保持聊天上下文：** 在整个会话中保持对话的连续性。
  - **保存聊天记录：** 自动保存你的对话，以便下次启动时可以恢复。
  - **按提供商分离聊天记录：** 为每个 LLM 提供商维护独立的聊天历史。
  - **自动迁移聊天记录：** 在切换 LLM 提供商时，无缝地转移你的对话历史。
- **高级模型设置：**
  - **温度控制：** 调整模型响应的随机性。
  - **最大 Token:** 设置模型单次响应的最大长度。
- **优化的用户界面：**
  - **选项卡式布局：** 清晰地组织不同 LLM 提供商的设置。
  - **可调整的角色设定窗口：** 通过拖拽轻松地调整角色设定文本框的大小。
  - **“未保存”提示：** 当有未保存的更改时，提供清晰的视觉提示。
- **可扩展的工具：**
  - **添加自定义工具：** 通过“工具列表”选项卡，添加、删除和配置外部工具。

## 🚀 安装

1. 从 [Releases 页面](https://github.com/ycxom/VPetLLM/releases)下载最新版本。
2. 将解压后的 `3000_VPetLLM` 文件夹移动到你的 VPet-Simulator 安装目录下的 `mod` 文件夹中。
3. 启动 VPet-Simulator 并在设置中启用该插件。

## ⚙️ 配置

1. 打开 VPet-Simulator 的设置，并进入“MOD Config”选项卡。
2. 点击“VPetLLM”以打开插件设置。
3. **选择 LLM 提供商：** 从下拉菜单中选择你想要使用的 LLM 提供商（Ollama、OpenAI 或 Gemini）。
4. **填写提供商设置：**
   - **Ollama:** 输入你的 Ollama 服务的 URL（默认为 `http://localhost:11434`）并选择一个模型。
   - **OpenAI:** 输入你的 API 密钥、API 地址和模型名称。
   - **Gemini:** 输入你的 API 密钥、API 地址和模型名称。
5. **角色设定：** 在可调整大小的文本框中定义你的虚拟宠物的角色和个性。
6. **上下文控制：** 根据你的偏好，配置聊天上下文和历史记录的保存选项。
7. **高级设置：** 如果需要，可以为每个提供商启用并调整高级设置，如温度和最大 Token。
8. **工具列表：**
   - 点击“工具列表”选项卡。
   - 点击“添加”按钮以添加一个新的工具。
   - 在表格中填写工具的“名称”、“URL”和“API Key”（如果需要）。
   - 勾选“启用”复选框以启用该工具。
   - 点击“删除”按钮以删除选定的工具。
9. **保存：** 点击“保存”按钮以应用你的设置。如果有未保存的更改，“未保存”的红色提示将会出现。

## 💬 使用

1. 在 VPet-Simulator 中打开聊天窗口。
2. 输入你的消息并按 Enter。
3. 你的虚拟宠物将会使用你配置的 LLM 来回应你，并且如果启用了任何工具，它也可能会使用这些工具来提供更丰富、更有用的响应。

## 🤝 贡献

欢迎各位大佬PR！

- 该项目由Gemini 等AI编写！

## 🔌 插件系统

VPetLLM 现在拥有一个强大的插件系统，允许开发者扩展其功能。通过创建自己的插件，你可以：
-   **连接外部 API**: 获取天气、新闻、股票等实时数据。
-   **执行自定义动作**: 控制智能家居、发送邮件、或者任何你能想到的事情。
-   **增强 AI 的能力**: 为你的桌宠赋予全新的、独一-无二的技能。

我们为插件开发者提供了一套完整的接口和详细的文档，让你能够轻松上手。

**➡️ [点击这里查看详细的插件开发文档](PLUGIN_README.md)**

## 📦 API 文档

VPetLLM 插件提供了一组公共 API，允许其他插件或外部程序与之交互。

### 获取插件实例

首先，你需要获取 `VPetLLM` 插件的实例：

```csharp
var vpetLLM = VPetLLM.VPetLLM.Instance;
if (vpetLLM == null)
{
    // 插件未加载或未启用
    return;
}
```

### 核心接口: IChatCore

所有与聊天相关的功能都通过 `IChatCore` 接口提供。你可以通过 `vpetLLM.ChatCore` 来访问它。

```csharp
var chatCore = vpetLLM.ChatCore;
```

### 发送聊天消息

异步地向当前配置的 LLM 发送一条消息。

**方法签名:**
```csharp
Task<string> Chat(string prompt, bool isFunctionCall = false)
```
-   `prompt`: 要发送给 AI 的文本。
-   `isFunctionCall`: 一个布尔值，指示此调用是否源自插件（函数调用）。当为 `true` 时，`prompt` 不会被作为新的 `user` 消息添加到历史记录中，因为它被假定为插件返回的结果。

**示例:**
```csharp
// 普通用户聊天
await chatCore.Chat("你好！");

// 模拟插件返回结果
await chatCore.Chat("这是插件返回的信息。", true);
```
*注意：该方法现在主要通过回调机制处理响应，返回值主要用于兼容旧的处理流程，在新的实现中可能为空。*

### 响应处理

通过注册一个响应处理器，你可以异步地接收并处理来自 AI 的回复。

**方法签名:**
```csharp
void SetResponseHandler(Action<string> handler)
```

**示例:**
```csharp
chatCore.SetResponseHandler(response =>
{
    Console.WriteLine($"收到 AI 回复: {response}");
    // 在这里处理回复，例如更新 UI 或执行动作
});
```

### 聊天记录管理

**获取聊天记录:**
```csharp
List<Message> history = chatCore.GetChatHistory();
```

**设置聊天记录:**
```csharp
var newHistory = new List<Message> { ... };
chatCore.SetChatHistory(newHistory);
```

**清除聊天记录:**
```csharp
chatCore.ClearContext();
```