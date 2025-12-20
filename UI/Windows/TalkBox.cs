using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Windows;
using VPetLLM.Handlers;
using VPetLLM.Utils;

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
        /// 获取气泡管理器（通过消息处理器）
        /// </summary>
        public BubbleManager BubbleManager => _messageProcessor?.BubbleManager;

        public TalkBox(VPetLLM plugin) : base(plugin)
        {
            _plugin = plugin;
            _messageProcessor = new SmartMessageProcessor(_plugin);
            if (_plugin.ChatCore != null)
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
                        if (msgBar != null)
                        {
                            Utils.MessageBarHelper.PreInitialize(msgBar);
                        }
                    }
                    catch { }
                }));
            
            Logger.Log("TalkBox created with BubbleManager integration.");
        }

        public void HandleNormalResponse(string message)
        {
            _plugin.MW.Main.Say(message);
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
        /// 处理AI回复 - 流式处理核心方法（优化：避免UI线程阻塞）
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

            // 首次响应：快速停止思考动画
            if (isFirstResponse)
            {
                _isThinking = false;
                var cts = _thinkingCancellationTokenSource;
                if (cts != null)
                {
                    _thinkingCancellationTokenSource = null;
                    try { cts.Cancel(); cts.Dispose(); } catch { }
                }
            }

            // 在后台线程处理消息，避免阻塞UI
            _ = Task.Run(async () =>
            {
                try
                {
                    await _messageProcessor.ProcessMessageAsync(response, !isFirstResponse);
                }
                catch (Exception ex)
                {
                    Logger.Log($"HandleResponse: 处理错误: {ex.Message}");
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { _plugin.MW.Main.Say(response); } catch { }
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
                await Application.Current.Dispatcher.InvokeAsync(() => _plugin.MW.Main.Say(e.ToString()));
            }
            finally
            {
                // 停止思考动画（如果有的话）- 但不隐藏气泡，因为流式消息可能还在处理中
                Logger.Log("Responded: 准备停止思考动画（仅停止动画，不隐藏气泡）");
                StopThinkingAnimationWithoutHide();
                
                // 重置流式处理状态，为下一次对话做准备
                ResetStreamingState();
                
                Logger.Log("Responded: 思考动画已停止，流式状态已重置");
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
                if (_plugin.ChatCore != null)
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
                await Application.Current.Dispatcher.InvokeAsync(() => _plugin.MW.Main.Say(e.ToString()));
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
            if (imageData == null || imageData.Length == 0)
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
                if (_plugin.ChatCore != null)
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
                await Application.Current.Dispatcher.InvokeAsync(() => _plugin.MW.Main.Say(e.ToString()));
            }
        }

        private async Task ProcessTools(string text)
        {
            if (_plugin.Settings.Tools == null) return;

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
            if (proxy != null)
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
            if (proxySettings == null || !proxySettings.IsEnabled)
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
        /// 启动思考动画 - 显示动态的"思考中"气泡（优化：使用 BubbleManager）
        /// </summary>
        public void StartThinkingAnimation()
        {
            // 如果已经在思考，先停止
            if (_isThinking)
            {
                _isThinking = false;
                var oldCts = _thinkingCancellationTokenSource;
                if (oldCts != null)
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
            var dots = new[] { "", " ..", " ....", " ......", " ........" };

            // 使用 BubbleManager 的 TimerCoordinator 暂停定时器
            BubbleManager?.TimerCoordinator?.PauseAllTimers();

            // 启动后台任务循环显示思考动画
            _ = Task.Run(async () =>
            {
                int i = 0;
                while (_isThinking && !cts.Token.IsCancellationRequested)
                {
                    var text = baseText + dots[i++ % dots.Length];
                    
                    // 使用 BubbleManager 显示思考气泡
                    if (_isThinking)
                    {
                        BubbleManager?.ShowThinkingBubble(text);
                    }

                    try { await Task.Delay(450, cts.Token); }
                    catch (TaskCanceledException) { break; }
                }
            });
        }

        /// <summary>
        /// 停止思考动画但不隐藏气泡（用于流式响应，让新气泡直接覆盖）
        /// 优化：使用 BubbleManager 的状态过渡
        /// </summary>
        public void StopThinkingAnimationWithoutHide()
        {
            // 快速设置标志，阻止思考动画继续更新
            _isThinking = false;
            
            // 取消思考动画任务
            var cts = _thinkingCancellationTokenSource;
            if (cts != null)
            {
                _thinkingCancellationTokenSource = null;
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
            
            // 使用 BubbleManager 的 TimerCoordinator 停止定时器
            // 但不清理状态，保持气泡可见以便平滑过渡
            BubbleManager?.TimerCoordinator?.ForceStopAll();
            
            // 使用低优先级异步清理流式状态
            Application.Current.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    try
                    {
                        var msgBar = _plugin.MW.Main.MsgBar;
                        if (msgBar != null)
                        {
                            Utils.MessageBarHelper.ClearStreamState(msgBar);
                        }
                    }
                    catch { }
                }));
        }

        /// <summary>
        /// 停止思考动画并隐藏气泡（用于错误情况或强制停止）
        /// 优化：使用 BubbleManager 统一管理
        /// </summary>
        private void StopThinkingAnimation()
        {
            // 快速设置标志
            _isThinking = false;
            
            // 取消思考动画任务
            var cts = _thinkingCancellationTokenSource;
            if (cts != null)
            {
                _thinkingCancellationTokenSource = null;
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
            
            // 使用 BubbleManager 清理状态并隐藏气泡
            BubbleManager?.Clear();
            BubbleManager?.HideBubble();
        }

        /// <summary>
        /// 瞬时显示思考气泡（优化版，减少异常处理开销）
        /// </summary>
        private void ShowThinkingBubbleInstantly(string text)
        {
            // 快速检查状态，避免不必要的操作
            if (!_isThinking) return;
            
            try
            {
                var msgBar = _plugin.MW.Main.MsgBar;
                if (msgBar != null)
                {
                    Utils.MessageBarHelper.ShowBubbleQuick(msgBar, text, _plugin.MW.Core.Save.Name);
                }
            }
            catch { /* 忽略显示错误，避免日志开销 */ }
        }
    }
}