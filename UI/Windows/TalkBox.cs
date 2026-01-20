using System.Net.Http;
using System.Windows;
using VPetLLM.Utils.Localization;
using VPetLLM.Utils.UI;

namespace VPetLLM.UI.Windows
{
    public class TalkBox : VPet_Simulator.Windows.Interface.TalkBox
    {
        public override string APIName { get; } = "VPetLLM";
        private readonly VPetLLM _plugin;
        private readonly SmartMessageProcessor _messageProcessor;
        public event Action<string> OnSendMessage;

        // 思考动画控制
        private System.Threading.CancellationTokenSource _thinkingCancellationTokenSource;
        private bool _isThinking = false;

        // 流式处理状态管理
        private enum StreamingState { Idle, FirstResponse, Streaming }
        private StreamingState _streamingState = StreamingState.Idle;
        private readonly object _stateLock = new object();

        /// <summary>
        /// 获取消息处理器（用于流式处理等待）
        /// </summary>
        public SmartMessageProcessor MessageProcessor => _messageProcessor;

        /// <summary>
        /// 获取统一气泡门面（新系统）
        /// </summary>
        public UnifiedBubbleFacade BubbleFacade => _messageProcessor?.BubbleFacade;

        /// <summary>
        /// 获取气泡管理器（向后兼容，已弃用）
        /// </summary>
        [Obsolete("BubbleManager is deprecated. Use BubbleFacade instead.")]
        public object BubbleManager => null; // 返回null，强制使用新系统

        /// <summary>
        /// 获取气泡管理器适配器（向后兼容，已弃用）
        /// </summary>
        [Obsolete("BubbleManagerAdapter is deprecated. Use BubbleFacade instead.")]
        public object BubbleManagerAdapter => null; // 返回null，强制使用新系统

        public TalkBox(VPetLLM plugin) : base(plugin)
        {
            _plugin = plugin;
            _messageProcessor = new SmartMessageProcessor(_plugin);
            if (_plugin.ChatCore is not null)
            {
                _plugin.ChatCore.SetResponseHandler(HandleResponse);
            }

            // 预初始化 MessageBarHelper
            _ = Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    try
                    {
                        var msgBar = _plugin.MW?.Main?.MsgBar;
                        if (msgBar is not null)
                        {
                            MessageBarHelper.PreInitialize(msgBar);
                        }
                    }
                    catch { }
                }));

            Logger.Log("TalkBox created with BubbleManager integration.");
        }

        public void HandleNormalResponse(string message)
        {
            // 使用直接气泡管理器
            DirectBubbleManager.ShowBubble(_plugin, message);
        }

        /// <summary>
        /// 重置流式处理状态（在新对话开始时调用）
        /// </summary>
        public void ResetStreamingState()
        {
            lock (_stateLock)
            {
                _streamingState = StreamingState.Idle;
                Logger.Log("ResetStreamingState: 流式状态已重置为Idle");
            }
        }

        /// <summary>
        /// 处理AI回复 - 统一流式处理核心方法（优化：避免UI线程阻塞）
        /// 修改：统一流式和非流式处理流程，简化架构
        /// </summary>
        public async void HandleResponse(string response)
        {
            Logger.Log($"HandleResponse: 收到AI回复: {response}");

            // 使用状态机管理流式处理
            bool isFirstResponse;
            lock (_stateLock)
            {
                isFirstResponse = _streamingState == StreamingState.Idle;
                if (isFirstResponse)
                    _streamingState = StreamingState.FirstResponse;
                else if (_streamingState == StreamingState.FirstResponse)
                    _streamingState = StreamingState.Streaming;
            }

            // 首次响应：快速停止思考动画，更新状态灯为输出中
            if (isFirstResponse)
            {
                _isThinking = false;
                var cts = _thinkingCancellationTokenSource;
                if (cts is not null)
                {
                    _thinkingCancellationTokenSource = null;
                    try { cts.Cancel(); cts.Dispose(); } catch { }
                }

                // 更新状态灯为输出中
                _plugin.FloatingSidebarManager?.SetOutputtingStatus();
            }

            // 统一处理：无论流式还是非流式，都使用StreamingCommandProcessor
            // 这样可以避免两套不同的处理逻辑导致的问题
            _ = Task.Run(async () =>
            {
                try
                {
                    // 检查是否为完整消息（非流式模式的特征：包含多个完整命令）
                    if (IsCompleteMessage(response))
                    {
                        Logger.Log($"HandleResponse: 检测到完整消息，使用统一流式处理器拆分处理");

                        // 使用StreamingCommandProcessor处理完整消息
                        // 这样可以统一流式和非流式的处理逻辑
                        await ProcessCompleteMessageAsStreaming(response);
                    }
                    else
                    {
                        // 流式片段，直接使用现有的流式处理逻辑
                        await _messageProcessor.ProcessMessageAsync(response, !isFirstResponse, autoSetIdleOnComplete: false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"HandleResponse: 处理错误: {ex.Message}");
                    // 更新状态灯为错误状态
                    _plugin.FloatingSidebarManager?.SetErrorStatus();
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 使用直接气泡管理器显示错误消息
                        DirectBubbleManager.ShowBubble(_plugin, response);
                    }));
                }
            });
        }
        public override async void Responded(string text)
        {
            // 检查是否为默认插件，如果不是则不处理
            if (!_plugin.IsVPetLLMDefaultPlugin())
            {
                Logger.Log("VPetLLM不是默认插件，忽略消息处理");
                return;
            }

            OnSendMessage?.Invoke(text);
            Logger.Log($"Responded called with text: {text}");

            // 更新状态灯为处理中
            // 注意：不在这里调用 BeginActiveSession()，会话跟踪由 StreamingCommandProcessor 和 ResultAggregator 管理
            _plugin.FloatingSidebarManager?.SetProcessingStatus();

            // 重置流式处理状态，为新对话做准备
            ResetStreamingState();

            try
            {
                // 检查是否为 Debug 模式
                bool isDebugMode = _plugin.Settings.Role == "VPetLLM_DeBug";

                if (isDebugMode)
                {
                    Logger.Log("=== Debug 模式已激活 ===");
                    Logger.Log($"用户输入将直接作为 LLM 输出处理: {text}");

                    // 直接将用户输入作为 LLM 的输出处理
                    HandleResponse(text);

                    Logger.Log("Debug 模式处理完成");
                    return;
                }

                // 输出当前动画状态用于调试
                var currentAnimDesc = AnimationStateChecker.GetCurrentAnimationDescription(_plugin.MW);
                Logger.Log($"TalkBox.Responded: 当前动画状态 = {currentAnimDesc}");

                // 检查是否为身体交互触发（带有[System]标记）
                bool isBodyInteraction = text.StartsWith("[System]");

                // 检查VPet是否正在执行重要动画（包括用户交互动画如捏脸）
                bool isPlayingImportantAnimation = AnimationStateChecker.IsPlayingImportantAnimation(_plugin.MW);

                if (isBodyInteraction)
                {
                    // 身体交互触发：完全跳过思考动画和气泡
                    Logger.Log("身体交互触发，完全跳过思考动画和气泡");
                }
                else
                {
                    // 输入触发：显示气泡，仅在非重要动画时显示思考动作
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (isPlayingImportantAnimation)
                        {
                            // 重要动画播放中：不显示思考动作，仅显示气泡
                            Logger.Log($"输入触发且重要动画播放中 ({currentAnimDesc})，仅显示思考气泡");
                        }
                        else
                        {
                            // 非重要动画：显示思考动作和气泡
                            Logger.Log("输入触发且无重要动画，显示思考动作和气泡");
                            DisplayThink();
                        }
                        // 无论是否在重要动画中，都显示思考气泡
                        StartThinkingAnimation();
                    });
                }

                Logger.Log("Calling ChatCore.Chat...");
                await Task.Run(() => _plugin.ChatCore.Chat(text));

                Logger.Log("Processing tools...");
                await ProcessTools(text);
                Logger.Log("Responded finished.");
            }
            catch (Exception e)
            {
                Logger.Log($"An error occurred in Responded: {e}");
                // 更新状态灯为错误状态
                _plugin.FloatingSidebarManager?.SetErrorStatus();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // 使用直接气泡管理器显示错误消息
                    DirectBubbleManager.ShowBubble(_plugin, e.ToString());
                });
            }
            finally
            {
                // 停止思考动画（如果有的话）- 但不隐藏气泡，因为流式消息可能还在处理中
                Logger.Log("Responded: 准备停止思考动画（仅停止动画，不隐藏气泡）");
                StopThinkingAnimationWithoutHide();

                // 重置流式处理状态，为下一次对话做准备
                ResetStreamingState();

                // 注意：不在这里调用 EndActiveSession()，会话跟踪由 StreamingCommandProcessor 管理
                // StreamingCommandProcessor 会在所有命令处理完成后自动设置状态灯为 Idle

                Logger.Log("Responded: 思考动画已停止，状态灯由 StreamingCommandProcessor 管理");
            }
        }

        /// <summary>
        /// 发送聊天消息（带动画处理）
        /// 注意：动画状态由 SmartMessageProcessor 在处理完成后自动管理
        /// </summary>
        /// <param name="text">消息文本</param>
        public async Task SendChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            Logger.Log($"SendChat called with text: {text}");

            // 重置流式处理状态
            ResetStreamingState();

            try
            {
                // 检查VPet是否正在执行重要动画
                bool isPlayingImportantAnimation = AnimationStateChecker.IsPlayingImportantAnimation(_plugin.MW);

                // 显示思考动画
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!isPlayingImportantAnimation)
                    {
                        DisplayThink();
                    }
                    StartThinkingAnimation();
                });

                // 发送消息
                // ChatCore.Chat() 内部会通过 ResponseHandler 触发 SmartMessageProcessor
                // SmartMessageProcessor 会在处理完成后自动管理动画状态
                if (_plugin.ChatCore is not null)
                {
                    await _plugin.ChatCore.Chat(text);
                }
                // 注意：不在这里停止思考动画，由 SmartMessageProcessor 处理
            }
            catch (Exception e)
            {
                Logger.Log($"An error occurred in SendChat: {e}");
                // 只有在发生异常时才停止思考动画
                StopThinkingAnimationWithoutHide();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // 使用直接气泡管理器显示错误消息
                    DirectBubbleManager.ShowBubble(_plugin, e.ToString());
                });
            }
        }

        /// <summary>
        /// 发送带图片的聊天消息（带动画处理）
        /// 注意：动画状态由 SmartMessageProcessor 在处理完成后自动管理
        /// </summary>
        /// <param name="text">消息文本</param>
        /// <param name="imageData">图片数据</param>
        public async Task SendChatWithImage(string text, byte[] imageData)
        {
            if (imageData is null || imageData.Length == 0)
            {
                await SendChat(text);
                return;
            }

            Logger.Log($"SendChatWithImage called with text: {text}, image size: {imageData.Length}");

            // 重置流式处理状态
            ResetStreamingState();

            try
            {
                // 检查VPet是否正在执行重要动画
                bool isPlayingImportantAnimation = AnimationStateChecker.IsPlayingImportantAnimation(_plugin.MW);

                // 显示思考动画
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!isPlayingImportantAnimation)
                    {
                        DisplayThink();
                    }
                    StartThinkingAnimation();
                });

                // 发送带图片的消息
                // ChatCore.ChatWithImage() 内部会通过 ResponseHandler 触发 SmartMessageProcessor
                // SmartMessageProcessor 会在处理完成后自动管理动画状态
                if (_plugin.ChatCore is not null)
                {
                    await _plugin.ChatCore.ChatWithImage(text, imageData);
                }
                // 注意：不在这里停止思考动画，由 SmartMessageProcessor 处理
            }
            catch (Exception e)
            {
                Logger.Log($"An error occurred in SendChatWithImage: {e}");
                // 只有在发生异常时才停止思考动画
                StopThinkingAnimationWithoutHide();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // 使用直接气泡管理器显示错误消息
                    DirectBubbleManager.ShowBubble(_plugin, e.ToString());
                });
            }
        }

        private async Task ProcessTools(string text)
        {
            if (_plugin.Settings.Tools is null) return;

            foreach (var tool in _plugin.Settings.Tools)
            {
                if (!tool.IsEnabled) continue;

                // 使用正确的代理设置创建HttpClient
                using (var client = CreateHttpClientWithProxy())
                {
                    var requestData = new { prompt = text };
                    var content = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

                    try
                    {
                        var response = await client.PostAsync(tool.Url, content);
                        response.EnsureSuccessStatusCode();
                        var responseString = await response.Content.ReadAsStringAsync();
                        var actionQueue = _plugin.ActionProcessor.Process(responseString, _plugin.Settings);

                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            foreach (var item in actionQueue)
                            {
                                await item.Handler.Execute(item.Value, _plugin.MW);
                            }
                        });
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"An error occurred in ProcessTools: {e}");
                    }
                }
            }
        }

        public override void Setting()
        {
            _plugin.Setting();
        }

        /// <summary>
        /// 创建带有正确代理设置的HttpClient
        /// </summary>
        private HttpClient CreateHttpClientWithProxy()
        {
            var handler = new HttpClientHandler();

            // 获取插件代理设置
            var proxy = GetPluginProxy();
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            else
            {
                // 明确禁用代理，防止使用系统默认代理
                handler.UseProxy = false;
                handler.Proxy = null;
            }

            return new HttpClient(handler);
        }

        /// <summary>
        /// 获取插件专用的代理设置
        /// </summary>
        private System.Net.IWebProxy GetPluginProxy()
        {
            var proxySettings = _plugin.Settings.Proxy;

            // 如果代理未启用，返回null
            if (proxySettings is null || !proxySettings.IsEnabled)
            {
                return null;
            }

            bool useProxy = false;

            // 如果ForAllAPI为true，则对所有API使用代理
            if (proxySettings.ForAllAPI)
            {
                useProxy = true;
            }
            else
            {
                // 如果ForAllAPI为false，则根据ForPlugin设置决定
                useProxy = proxySettings.ForPlugin;
            }

            if (useProxy)
            {
                if (proxySettings.FollowSystemProxy)
                {
                    return System.Net.WebRequest.GetSystemWebProxy();
                }
                else if (!string.IsNullOrEmpty(proxySettings.Address))
                {
                    if (string.IsNullOrEmpty(proxySettings.Protocol))
                    {
                        proxySettings.Protocol = "http";
                    }
                    var protocol = proxySettings.Protocol.ToLower() == "socks" ? "socks5" : "http";
                    return new System.Net.WebProxy(new Uri($"{protocol}://{proxySettings.Address}"));
                }
            }

            return null;
        }

        /// <summary>
        /// 启动思考动画 - 显示动态的"思考中"气泡（优化：直接使用 DirectBubbleManager）
        /// </summary>
        public void StartThinkingAnimation()
        {
            // 如果已经在思考，先停止
            if (_isThinking)
            {
                _isThinking = false;
                var oldCts = _thinkingCancellationTokenSource;
                if (oldCts is not null)
                {
                    _thinkingCancellationTokenSource = null;
                    try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
                }
            }

            _isThinking = true;
            var cts = new System.Threading.CancellationTokenSource();
            _thinkingCancellationTokenSource = cts;

            // 获取宠物名称和思考文本
            var petName = _plugin.Settings.AiName ?? "VPet";
            var template = LanguageHelper.Get("Thinking.Text", _plugin.Settings.Language);
            if (string.IsNullOrEmpty(template)) template = "{PetName} thinking";
            var baseText = template.Replace("{PetName}", petName);

            // 直接使用DirectBubbleManager，实现真正的直接覆盖
            Logger.Log("TalkBox: 使用DirectBubbleManager启动思考动画（直接覆盖模式）");
            StartThinkingAnimationDirect(baseText, cts);
        }

        /// <summary>
        /// 直接思考动画实现（使用DirectBubbleManager）
        /// </summary>
        private void StartThinkingAnimationDirect(string baseText, System.Threading.CancellationTokenSource cts)
        {
            var dots = new[] { "", " ..", " ....", " ......", " ........" };

            // 启动后台任务循环显示思考动画，直接使用DirectBubbleManager
            _ = Task.Run(async () =>
            {
                int i = 0;
                while (_isThinking && !cts.Token.IsCancellationRequested)
                {
                    var text = baseText + dots[i++ % dots.Length];

                    // 直接使用DirectBubbleManager显示思考气泡
                    if (_isThinking)
                    {
                        DirectBubbleManager.ShowThinkingBubble(_plugin, text);
                    }

                    try { await Task.Delay(450, cts.Token); }
                    catch (TaskCanceledException) { break; }
                }
            });
        }

        /// <summary>
        /// 回退的思考动画实现（保留用于兼容性）
        /// </summary>
        private void StartThinkingAnimationFallback(string baseText, System.Threading.CancellationTokenSource cts)
        {
            var dots = new[] { "", " ..", " ....", " ......", " ........" };

            // 启动后台任务循环显示思考动画
            _ = Task.Run(async () =>
            {
                int i = 0;
                while (_isThinking && !cts.Token.IsCancellationRequested)
                {
                    var text = baseText + dots[i++ % dots.Length];

                    // 使用直接气泡管理器显示思考气泡
                    if (_isThinking)
                    {
                        DirectBubbleManager.ShowThinkingBubble(_plugin, text);
                    }

                    try { await Task.Delay(450, cts.Token); }
                    catch (TaskCanceledException) { break; }
                }
            });
        }

        /// <summary>
        /// 停止思考动画但不隐藏气泡（用于流式响应，让新气泡直接覆盖）
        /// 优化：直接使用 DirectBubbleManager
        /// </summary>
        public void StopThinkingAnimationWithoutHide()
        {
            // 快速设置标志，阻止思考动画继续更新
            _isThinking = false;

            // 取消思考动画任务
            var cts = _thinkingCancellationTokenSource;
            if (cts is not null)
            {
                _thinkingCancellationTokenSource = null;
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }

            // 直接使用DirectBubbleManager进行状态清理（不隐藏气泡）
            Logger.Log("TalkBox: 使用DirectBubbleManager停止思考动画（无隐藏，直接覆盖模式）");

            // 使用低优先级异步清理流式状态
            Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    try
                    {
                        var msgBar = _plugin.MW.Main.MsgBar;
                        if (msgBar is not null)
                        {
                            MessageBarHelper.ClearStreamState(msgBar);
                        }
                    }
                    catch { }
                }));
        }

        /// <summary>
        /// 停止思考动画并隐藏气泡（用于错误情况或强制停止）
        /// 优化：直接使用 DirectBubbleManager
        /// </summary>
        private void StopThinkingAnimation()
        {
            // 快速设置标志
            _isThinking = false;

            // 取消思考动画任务
            var cts = _thinkingCancellationTokenSource;
            if (cts is not null)
            {
                _thinkingCancellationTokenSource = null;
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }

            // 直接使用DirectBubbleManager清理和隐藏
            Logger.Log("TalkBox: 使用DirectBubbleManager停止思考动画并隐藏（直接覆盖模式）");

            // 直接使用DirectBubbleManager进行清理
            try
            {
                DirectBubbleManager.ClearBubbleState(_plugin);
                DirectBubbleManager.HideBubble(_plugin);
            }
            catch (Exception ex)
            {
                Logger.Log($"TalkBox: 清理气泡状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 瞬时显示思考气泡（优化版，减少异常处理开销）
        /// </summary>
        private void ShowThinkingBubbleInstantly(string text)
        {
            // 快速检查状态，避免不必要的操作
            if (!_isThinking) return;

            // 使用直接气泡管理器
            DirectBubbleManager.ShowThinkingBubble(_plugin, text);
        }

        /// <summary>
        /// 检测是否为完整消息（非流式模式的特征）
        /// </summary>
        private bool IsCompleteMessage(string response)
        {
            if (string.IsNullOrEmpty(response))
                return false;

            // 检测是否包含多个完整的命令标记
            // 完整消息通常包含多个 <|xxx_begin|>...<|xxx_end|> 对
            var beginCount = System.Text.RegularExpressions.Regex.Matches(response, @"<\|\w+_begin\|>").Count;
            var endCount = System.Text.RegularExpressions.Regex.Matches(response, @"<\|\w+_end\|>").Count;

            // 如果begin和end标记数量相等且大于1，认为是完整消息
            bool isComplete = beginCount == endCount && beginCount > 1;

            if (isComplete)
            {
                Logger.Log($"HandleResponse: 检测到完整消息 - 包含 {beginCount} 个完整命令");
            }

            return isComplete;
        }

        /// <summary>
        /// 将完整消息按流式方式处理
        /// 统一流式和非流式的处理逻辑，避免架构复杂性
        /// </summary>
        private async Task ProcessCompleteMessageAsStreaming(string completeMessage)
        {
            try
            {
                // 创建StreamingCommandProcessor来处理完整消息
                var streamProcessor = new Handlers.Core.StreamingCommandProcessor(
                    async (command) =>
                    {
                        // 每个完整命令都通过现有的流式处理逻辑处理
                        Logger.Log($"统一流式处理: 处理命令片段: {command}");
                        await _messageProcessor.ProcessMessageAsync(command, true, autoSetIdleOnComplete: false);
                    },
                    _plugin
                );

                // 将完整消息逐字符添加到StreamingCommandProcessor
                // 这样可以模拟流式接收，触发命令检测和处理
                foreach (char c in completeMessage)
                {
                    streamProcessor.AddText(c.ToString());
                }

                // 完成处理
                streamProcessor.Complete();

                Logger.Log("统一流式处理: 完整消息处理完成");
            }
            catch (Exception ex)
            {
                Logger.Log($"统一流式处理: 处理完整消息失败: {ex.Message}");
                throw;
            }
        }
    }
}