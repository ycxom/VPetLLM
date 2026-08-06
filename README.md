# VPetLLM

VPetLLM 是 [VPet Simulator](https://github.com/LorisYounger/VPet) 的大语言模型扩展插件，为桌宠提供对话、语音输入、语音合成、动作指令、截图识别和插件扩展能力。

## 功能

- 支持 OpenAI 兼容接口、Gemini、Ollama、LM Studio 和免费通道
- 支持流式对话、上下文管理、备用通道与节点负载均衡
- 支持 ASR 语音输入和多种 TTS 提供方
- 支持 VPet 动作、状态、购买、截图和媒体播放指令
- 支持扩展插件加载与插件商店
- 主代理和插件商店代理独立配置，互不覆盖
- 启动时通过远程 `3000_VPetLLM/info.lps` 检查最新版本

## 安装

1. 下载发布包并解压。
2. 将 `3000_VPetLLM` 目录放入 VPet 的 `mod` 目录。
3. 启动 VPet，在模组管理中启用 VPetLLM。
4. 打开插件设置，配置对话提供方、模型和 API Key。

运行环境为 Windows 10/11 和支持当前插件接口的 VPet Simulator。

## 代理设置

VPetLLM 将两类代理开关分开处理：

- **启动代理**：供聊天、ASR、TTS 等主插件网络请求使用。
- **启动插件商店代理**：只供插件商店请求使用，不读取或受主代理开关影响。

代理连通性检查会按所选协议和地址建立实际请求，并给出可定位的失败原因。关闭某一代理不会改变另一代理的启用状态。

## 版本检查

插件启动后会读取：

```text
https://raw.githubusercontent.com/ycxom/VPetLLM/main/3000_VPetLLM/info.lps
```

本地版本来自随插件发布的 `info.lps`。远程版本更高时才提示更新；网络失败或远程文件格式异常不会阻止插件启动。

## 开发与构建

需要安装 .NET 8 SDK。项目目标框架为 `net8.0-windows`，仅支持 Windows。

```powershell
dotnet restore VPetLLM.csproj
dotnet build VPetLLM.csproj -p:SkipPluginCopy=true
```

省略 `SkipPluginCopy` 时，构建产物会复制到 `3000_VPetLLM/plugin`，用于本地加载测试。

独立回归检查：

```powershell
dotnet run --project Tests/VPetLLM.MovementChecks --no-restore
dotnet run --project Tests/VPetLLM.ProxyChecks --no-restore
dotnet run --project Tests/VPetLLM.VersionChecks --no-restore
dotnet run --project Tests/VPetLLM.PluginChecks --no-restore
```

## 代码结构

```text
VPetLLM.cs / Setting.cs   插件入口、生命周期和持久化设置
Core/                     对话提供方、TTS、数据和插件核心
Handlers/                 消息、动作、动画和语音处理
Infrastructure/           配置、日志、事件、依赖注入和应用服务
Services/                 当前业务服务与 VPet 能力适配
UI/                       设置窗口、对话框和控件
Utils/                    网络、音频、配置和通用工具
Tests/                    移动、代理、版本和插件加载回归项目
3000_VPetLLM/             可直接安装的模组目录
```

## 兼容性说明

以下兼容层仍参与运行，不应视为可删除的旧代码：

- `Handlers/Legacy/ActionHandler.cs`：处理当前注册的 `action` 指令。
- `Core/PluginAdapter.cs`：在插件管理器中适配旧版插件接口。
- `Infrastructure/Configuration/ConfigurationMigrator.cs`：启动时迁移旧配置。

已经移除的内容包括无调用私有方法、失效的 TTS 下载队列、始终不可用的 EdgeTTS 占位实现、重复配置模型、重复服务实现以及历史源码归档。

## 已知限制

### 多图截图与对话历史

一次可以截多张图并在同一次请求里一并发给模型（原生多模态走 content 数组多图，
前置多模态逐张识别后合并文本）。但**对话历史每条消息只保留第一张图**：
`Message` 只有单个图像槽位（`ImageId` 对应磁盘上的一个文件），要存全部图需要
改数据库 schema、惰性加载器和上下文编辑器。

影响：当轮提问模型能看到全部图；如果后续轮次重放历史，只会带上第一张。
对「截图 → 提问 → 得到回答」的主流程没有影响。

## 第三方致谢与引用说明

### ShareX

截图模块（`UI/Windows/winScreenshotCapture`、`Services/WindowDetector`、`Services/ScreenCapture`）的交互设计参考了
[ShareX](https://github.com/ShareX/ShareX) 的区域截图实现，具体借鉴了以下**公开做法**：

- 进入选区前先冻结整个虚拟桌面为一张位图，后续选区、放大镜取样、最终裁剪都基于这张冻结底图
- 自动窗口识别：`EnumWindows` 的回调顺序即 z-order，命中测试取第一个包含光标的窗口
- 用 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 取窗口边框，避开 Win10+ 的不可见阴影余量
- 过滤 `DWMWA_CLOAKED` 窗口，以及 `WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE` 的非激活浮层
- 悬停窗口切换时用矩形插值做过渡动画，并在切换目标时从动画当前帧接续

**重要说明：本项目未复制、未改编 ShareX 的任何源代码。** 上述模块均为独立编写的实现，
借鉴范围仅限于"调用哪些系统 API、需要过滤哪几类窗口"这类功能性做法，不涉及代码表达本身。

之所以要强调这一点：ShareX 以 **GPL**（`LICENSE.txt` 为 GPL v3，源文件头为 GPL v2 or later）发布，
而 GPL 是强著佐权协议——任何包含或改编其代码的作品，整体必须同样以 GPL 发布。
本项目使用宽松协议，两者方向上不兼容，因此**不得**将 ShareX 的源代码引入本仓库。
在 README 中致谢**不能**豁免这一义务。

若未来确有复用其代码的需要，唯一合规路径是将本项目整体改为 GPLv3 发布，请先做出该决定再实施。

## 许可证

本项目使用 [Apache License 2.0](LICENSE)。
