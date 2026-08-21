using System.Net.Http;
using System.Windows;
using VPetLLM.Utils.Localization;

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

        // 回复级别串行化：确保多个AI回复按顺序处理，防止跨回复的消息乱序
        private static readonly SemaphoreSlim _responseLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 获取消息处理器（用于流式处理等待）
        /// </summary>
        public SmartMessageProcessor MessageProcessor => _messageProcessor;

        /// <summary>
        /// 获取统一气泡门面（新系统）
        /// </summary>
        public UnifiedBubbleFacade BubbleFacade => _messageProcessor?.BubbleFacade;

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
                        var msgBar = BubbleGuard.RealMsgBar;
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
        /// 中断当前这一轮的输出：停掉思考动画、气泡队列和 TTS 播放。
        /// LLM 请求本身由 <see cref="InterruptManager"/> 的取消令牌断开，这里只负责已经落到本地的输出。
        /// </summary>
        public void AbortCurrentResponse()
        {
            Logger.Log("TalkBox: 中断当前回复");

            try { StopThinkingAnimationWithoutHide(); }
            catch (Exception ex) { Logger.Log($"TalkBox: 中断时停止思考动画失败: {ex.Message}"); }

            // 先让停播动起来（内部是火抛的后台任务）：宿主的气泡收尾分支会先看
            // PlayingVoice，语音还没停时它会继续等，早一点开始停就早一点收干净
            try { _messageProcessor?.Abort(); }
            catch (Exception ex) { Logger.Log($"TalkBox: 中断消息处理器失败: {ex.Message}"); }

            // 让气泡和动画走宿主自己的收尾流程。
            // StopThinkingAnimationWithoutHide 只清流式缓冲区、刻意不收气泡（流式接力要用），
            // 所以中断后气泡会一直挂到它自己的计时器到点，桌宠也一直卡在说话/思考循环里
            try { CloseBubbleAndRestoreAnimation(); }
            catch (Exception ex) { Logger.Log($"TalkBox: 中断时收尾气泡/动画失败: {ex.Message}"); }

            // 清空还没执行的命令队列并收掉正在接管的插件。
            // 火抛：结束接管要走插件自己的收尾逻辑，不能让中断卡在这
            _ = Handlers.Core.StreamingCommandProcessor.AbortAllAsync();

            ResetStreamingState();
        }

        /// <summary>
        /// 中断时把气泡和桌宠动画带回常态 —— 走宿主自己的收尾流程，只是把它快进。
        ///
        /// 不能用宿主的 ForceClose()：它内部是 <c>CloseTimer.Close()</c>（等同 Dispose），
        /// 而之后每条气泡结束时 EndTimer_Elapsed 都会去 <c>CloseTimer.Start()</c> ——
        /// 对已释放的计时器调用会在计时器线程上抛 ObjectDisposedException。
        /// 也就是说点一次中断，之后所有气泡的关闭流程就都废了。
        ///
        /// 快进的办法是喂给宿主它自己认得的终止条件：
        ///   · 清掉没打完的字 → 下一个 ShowTimer tick 走进"说完了"分支，
        ///     那里会停打字机、启动 EndTimer，并接上 C_End 把桌宠带回常态
        ///   · timeleft 打到底 → EndTimer 下一跳就转交 CloseTimer 淡出
        /// </summary>
        private void CloseBubbleAndRestoreAnimation()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var main = _plugin.MW?.Main;
                if (main is null) return;

                try { FastForwardBubbleClose(BubbleGuard.Real(main.MsgBar)); }
                catch (Exception ex) { Logger.Log($"TalkBox: 收起气泡失败: {ex.Message}"); }

                try { RestoreAnimationToNormal(main); }
                catch (Exception ex) { Logger.Log($"TalkBox: 恢复动画失败: {ex.Message}"); }
            });
        }

        /// <summary>
        /// 把气泡推进宿主的关闭流程。必须在 UI 线程调用。
        /// </summary>
        private void FastForwardBubbleClose(object msgBar)
        {
            if (msgBar is null) return;

            MessageBarHelper.GetFieldValue<List<char>>(msgBar, "outputtext")?.Clear();
            MessageBarHelper.SetFieldValue(msgBar, "timeleft", 1);

            // 思考气泡是直接写文本显示出来的，三个计时器一个都没在跑，
            // 没有任何人会去关它 —— 这种情况手动把它送进淡出流程
            if (IsBarTimerRunning(msgBar, "ShowTimer")
                || IsBarTimerRunning(msgBar, "EndTimer")
                || IsBarTimerRunning(msgBar, "CloseTimer"))
            {
                Logger.Log("TalkBox: 气泡已在宿主的收尾流程中，仅快进");
                return;
            }

            MessageBarHelper.GetFieldValue<global::System.Timers.Timer>(msgBar, "CloseTimer")?.Start();
            Logger.Log("TalkBox: 气泡无计时器推进（思考气泡），已手动启动关闭流程");
        }

        private static bool IsBarTimerRunning(object msgBar, string fieldName)
        {
            return MessageBarHelper.GetFieldValue<global::System.Timers.Timer>(msgBar, fieldName)?.Enabled == true;
        }

        /// <summary>
        /// 把桌宠动画带回常态。必须在 UI 线程调用。
        /// </summary>
        private void RestoreAnimationToNormal(VPet_Simulator.Core.Main main)
        {
            var display = main.DisplayType;
            if (display is null) return;

            // 思考动画不在气泡的收尾路径里（那时 DisplayType 是 think 而不是 say），
            // 不单独收的话，在等模型时中断，桌宠会一直转着思考动作
            if (display.Name == "think")
            {
                var thinkEnd = _plugin.MW.Core.Graph.FindGraphs(
                    "think",
                    VPet_Simulator.Core.GraphInfo.AnimatType.C_End,
                    _plugin.MW.Core.Save.Mode);

                if (thinkEnd.Count > 0)
                {
                    main.Display(thinkEnd[VPet_Simulator.Core.Function.Rnd.Next(thinkEnd.Count)],
                        () => Logger.Log("TalkBox: 思考结束动画播放完成（中断）"));
                }
                else
                {
                    main.DisplayToNomal();
                    Logger.Log("TalkBox: 无思考结束动画，直接回到常态（中断）");
                }
                return;
            }

            // 说话动画：气泡若已经打完字，宿主自己接过 C_End 了；
            // 没接上的情况（比如打字机被我们清空前就停了）在这里补一次。
            // 与宿主 ShowTimer 收尾分支同样的判断条件，避免重复触发。
            if (display.Type == VPet_Simulator.Core.GraphInfo.GraphType.Say
                && display.Animat != VPet_Simulator.Core.GraphInfo.AnimatType.C_End)
            {
                main.DisplayCEndtoNomal(display.Name);
                Logger.Log("TalkBox: 说话动画已接 C_End 回到常态（中断）");
            }
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
            // async void（由流式回调线程调用）：前奏异常必须兜住，否则直接崩掉宿主进程
            bool isFirstResponse;
            try
            {
                // 用户已中断本轮：在途的流式片段一律丢弃，不再进入输出管线
                if (InterruptManager.IsInterrupted)
                {
                    Logger.LogVerbose("HandleResponse: 本轮已被中断，丢弃该回复片段");
                    return;
                }

                Logger.LogVerbose($"HandleResponse: 收到AI回复: {response}");

                // 使用状态机管理流式处理
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
            }
            catch (Exception ex)
            {
                Logger.Log($"HandleResponse: 前置处理错误: {ex.Message}");
                return;
            }

            // 统一处理：无论流式还是非流式，都使用StreamingCommandProcessor
            // 这样可以避免两套不同的处理逻辑导致的问题
            _ = Task.Run(async () =>
            {
                try
                {
                    // 等待前一个回复处理完成，保证回复间的命令顺序。
                    //
                    // 两条分支都要在锁里：只含一条命令的回复（插件回执引出的那种，
                    // 多半就是一句 say）走的是下面那条，过去它完全不排队 ——
                    // 于是上一轮的语音还在放，它的气泡和语音就直接压上来，
                    // 用户听到的是同一次互动被回答了两遍。
                    await _responseLock.WaitAsync();
                    try
                    {
                        // 检查是否为完整消息（非流式模式的特征：包含多个完整命令）
                        if (IsCompleteMessage(response))
                        {
                            Logger.LogVerbose($"HandleResponse: 检测到完整消息，使用统一流式处理器拆分处理");
                            await ProcessCompleteMessageAsStreaming(response);
                        }
                        else
                        {
                            // 流式片段，直接使用现有的流式处理逻辑
                            await _messageProcessor.ProcessMessageAsync(response, !isFirstResponse, autoSetIdleOnComplete: false);
                        }
                    }
                    finally
                    {
                        _responseLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    if (InterruptManager.IsInterrupted)
                    {
                        Logger.Log("HandleResponse: 本轮已被用户中断，跳过错误提示");
                        return;
                    }

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

            // async void（宿主直接调用）：订阅方异常必须兜住，不能带崩宿主
            try
            {
                OnSendMessage?.Invoke(text);
            }
            catch (Exception ex)
            {
                Logger.Log($"OnSendMessage subscriber failed: {ex.Message}");
            }
            Logger.Log($"Responded called with text: {text}");

            // 通知生命周期插件：处理开始
            try
            {
                await _plugin.ProcessingLifecycleManager?.NotifyProcessingStartAsync(text);
            }
            catch (Exception ex)
            {
                Logger.Log($"ProcessingLifecycleManager.NotifyProcessingStartAsync failed: {ex.Message}");
            }

            // 更新状态灯为处理中
            // 注意：不在这里调用 BeginActiveSession()，会话跟踪由 StreamingCommandProcessor 和 ResultAggregator 管理
            try
            {
                // 换令牌、重置流式状态放到 ChatDispatcher 真正派发这条消息时才做：
                // 上一轮很可能还在飞，此刻动这些会掐断它的中断链路、把它剩下的分片
                // 当成新回复的开头
                _plugin.FloatingSidebarManager?.SetProcessingStatus();
            }
            catch (Exception ex)
            {
                Logger.Log($"Responded: 状态重置失败: {ex.Message}");
            }

            try
            {
                // 检查是否为 Debug 模式
                bool isDebugMode = _plugin.Settings.Role == "VPetLLM_DeBug";

                if (isDebugMode)
                {
                    Logger.Log("=== Debug 模式已激活 ===");
                    Logger.Log($"用户输入将直接作为 LLM 输出处理: {text}");

                    // 不经过调度器的旁路，新一轮的令牌/流式状态得自己开
                    ChatDispatcher.BeginRound();

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
                if (_plugin.ChatCore == null)
                {
                    Logger.Log("ChatCore is null, cannot process chat");
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        DirectBubbleManager.ShowBubble(_plugin, "Chat core is not initialized. Please restart the application.");
                    });
                    return;
                }
                
                // 经调度器过闸：触摸/插件/语音几路同时到达时先并成一条再发，
                // 不再各自唤起一次 LLM（见 ChatDispatcher）
                await ChatDispatcher.SubmitAsync(
                    text,
                    isBodyInteraction ? ChatPriority.Interaction : ChatPriority.User,
                    source: "TalkBox.Responded",
                    newRound: true);

                // 已中断就不再触发用户配置的工具回调，那是本轮的一部分
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Responded: 本轮已被中断，跳过工具处理");
                    return;
                }

                Logger.Log("Processing tools...");
                await ProcessTools(text);
                Logger.Log("Responded finished.");
            }
            catch (Exception e)
            {
                // 中断引发的异常不是错误：不亮红灯、不弹错误气泡
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("Responded: 本轮已被用户中断");
                    return;
                }

                Logger.Log($"An error occurred in Responded: {e}");

                // 通知生命周期插件：处理错误
                try
                {
                    await _plugin.ProcessingLifecycleManager?.NotifyProcessingErrorAsync(e);
                }
                catch (Exception ex)
                {
                    Logger.Log($"ProcessingLifecycleManager.NotifyProcessingErrorAsync failed: {ex.Message}");
                }
                
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
                // 注意：不在这里调用 NotifyProcessingCompleteAsync
                // 因为 LLM 的响应是异步处理的，在 HandleResponse 中处理
                // 会话结束将在 SmartMessageProcessor 完成所有处理后调用
                
                // 停止思考动画（确保清除）- 这是第二次调用，确保思考动画完全停止
                Logger.Log("Responded: 准备停止思考动画（确保清除）");
                StopThinkingAnimationWithoutHide();

                // 重置流式处理状态，为下一次对话做准备
                ResetStreamingState();

                // 注意：不在这里调用 EndActiveSession()，会话跟踪由 StreamingCommandProcessor 管理
                // StreamingCommandProcessor 会在所有命令处理完成后自动设置状态灯为 Idle

                Logger.Log("Responded: 思考动画已停止（确保清除完成），状态灯由 StreamingCommandProcessor 管理");
            }
        }

        /// <summary>
        /// 发送聊天消息（带动画处理）
        /// 注意：动画状态由 SmartMessageProcessor 在处理完成后自动管理
        /// </summary>
        /// <param name="text">消息文本</param>
        /// <param name="newRound">
        /// 是否开启新一轮对话。插件回执回灌时传 false —— 它是上一轮的延续，
        /// 换令牌会把这一轮的中断链路断开。
        /// </param>
        public async Task SendChat(string text, bool newRound = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            Logger.Log($"SendChat called with text: {text}");

            // 换令牌与重置流式状态由 ChatDispatcher 在真正派发这条消息时执行，
            // 避免打断可能仍在进行的上一轮

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
                    await ChatDispatcher.SubmitAsync(
                        text,
                        newRound ? ChatPriority.User : ChatPriority.Plugin,
                        source: "TalkBox.SendChat",
                        isRetry: !newRound,
                        newRound: newRound);
                }
                // 注意：不在这里停止思考动画，由 SmartMessageProcessor 处理
            }
            catch (Exception e)
            {
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("SendChat: 本轮已被用户中断");
                    return;
                }

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
        public Task SendChatWithImage(string text, byte[] imageData)
            => SendChatWithImages(text, new[] { imageData });

        /// <summary>
        /// 发送带多张图片的聊天消息（一次请求内一并送出）
        /// </summary>
        /// <param name="newRound">
        /// 是否开启新一轮对话。插件回执带图回灌时传 false —— 它是上一轮的延续，
        /// 换令牌会把这一轮的中断链路断开。
        /// </param>
        public async Task SendChatWithImages(string text, IReadOnlyList<byte[]> images, bool newRound = true)
        {
            images = images?.Where(i => i is not null && i.Length > 0).ToList() ?? new List<byte[]>();

            if (images.Count == 0)
            {
                await SendChat(text, newRound);
                return;
            }

            Logger.Log($"SendChatWithImages called with text: {text}, images: {images.Count}, total {images.Sum(i => i.Length)} bytes");

            // 换令牌与重置流式状态由 ChatDispatcher 在真正派发这条消息时执行，
            // 避免打断可能仍在进行的上一轮

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
                    await ChatDispatcher.SubmitAsync(
                        text,
                        newRound ? ChatPriority.User : ChatPriority.Plugin,
                        images,
                        "TalkBox.SendChatWithImages",
                        isRetry: !newRound,
                        newRound: newRound);
                }
                // 注意：不在这里停止思考动画，由 SmartMessageProcessor 处理
            }
            catch (Exception e)
            {
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("SendChatWithImages: 本轮已被用户中断");
                    return;
                }

                Logger.Log($"An error occurred in SendChatWithImages: {e}");
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
        /// 优化：增强退出检查，防止残留调用
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

                    // 三重检查：显示前、显示后、延迟后都检查状态
                    // 防止竞态条件导致的残留调用
                    if (!_isThinking || cts.Token.IsCancellationRequested)
                        break;

                    DirectBubbleManager.ShowThinkingBubble(_plugin, text);

                    if (!_isThinking || cts.Token.IsCancellationRequested)
                        break;

                    try { await Task.Delay(450, cts.Token); }
                    catch (TaskCanceledException) { break; }
                    
                    // 延迟后再次检查，确保及时退出
                    if (!_isThinking || cts.Token.IsCancellationRequested)
                        break;
                }
                
                Logger.Log("TalkBox: 思考动画循环已退出");
            });
        }

        /// <summary>
        /// 停止思考动画但不隐藏气泡（用于流式响应，让新气泡直接覆盖）
        /// 优化：改为同步清理，确保在返回前完成状态清理
        /// 注意：不调用 ClearStreamState，因为它会清除 oldsaysstream 导致正在显示的 Say 气泡消失
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

            // 同步清理思考动画循环状态（不触碰 oldsaysstream，保护正在显示的 Say 气泡）
            Logger.Log("TalkBox: 使用DirectBubbleManager停止思考动画（同步清理模式）");
            
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var msgBar = BubbleGuard.RealMsgBar;
                        if (msgBar is not null)
                        {
                            // 只清除流式输出缓冲区（outputtext/outputtextsample），不清除 oldsaysstream
                            // oldsaysstream 保存的是当前正在显示的 Say 气泡文本，清除会导致气泡消失
                            MessageBarHelper.ClearStreamBuffersOnly(msgBar);
                            Logger.Log("TalkBox: 流式状态已清理");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"TalkBox: 清理流式状态失败: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"TalkBox: 同步清理失败: {ex.Message}");
            }
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
        /// 修复：等待所有命令真正处理完成后再结束会话
        /// 修复：在外层管理独占会话，让会话覆盖所有命令
        /// </summary>
        private async Task ProcessCompleteMessageAsStreaming(string completeMessage)
        {
            // 检查是否需要使用独占会话模式
            var vpetTTSIntegration = _messageProcessor.GetVPetTTSIntegration();
            var useExclusiveSession = vpetTTSIntegration?.CanUseExclusiveMode() ?? false;
            string exclusiveSessionId = null;

            try
            {
                // 如果启用独占模式，在处理所有命令前启动独占会话
                if (useExclusiveSession)
                {
                    Logger.Log("统一流式处理: 启动独占会话（覆盖所有命令）");
                    exclusiveSessionId = await vpetTTSIntegration.StartExclusiveSessionAsync();
                    Logger.Log($"统一流式处理: 独占会话已启动，会话 ID: {exclusiveSessionId}");

                    // 使用 VPetTTSIntegrationManager 提取所有 talk 文本进行批量预加载
                    var talkTexts = vpetTTSIntegration.ExtractAllTalkTexts(completeMessage);
                    if (talkTexts.Count > 0)
                    {
                        Logger.Log($"统一流式处理: 批量预加载 {talkTexts.Count} 个文本");
                        var preloadCount = await vpetTTSIntegration.PreloadTextsAsync(talkTexts);
                        Logger.Log($"统一流式处理: 批量预加载完成，成功: {preloadCount}/{talkTexts.Count}");
                    }
                }

                // 预加载可能要跑好几秒，用户很可能就是在这期间按的中断。
                // 这里不拦一下的话，下面会白建一个处理器再把整条消息喂进去让它逐字丢弃。
                // 独占会话交给 finally 正常收尾。
                if (InterruptManager.IsInterrupted)
                {
                    Logger.Log("统一流式处理: 预加载后发现已中断，不再处理本条消息");
                    return;
                }

                // 创建一个完成信号，用于等待所有命令处理完成
                var allCommandsCompletedSource = new TaskCompletionSource<bool>();
                var commandCount = 0;
                var completedCount = 0;
                var lockObject = new object();

                // 创建StreamingCommandProcessor来处理完整消息
                var streamProcessor = new Handlers.Core.StreamingCommandProcessor(
                    async (command) =>
                    {
                        Logger.Log($"统一流式处理: 处理命令片段: {command}");
                        
                        try
                        {
                            // 所有命令都传递 skipInit=true，因为独占会话已经在外层启动
                            // autoSetIdleOnComplete=false，让最后统一管理状态灯
                            // 传递会话 ID，让 SmartMessageProcessor 知道外部会话的存在
                            await _messageProcessor.ProcessMessageAsync(
                                command, 
                                skipInitialization: true, 
                                autoSetIdleOnComplete: false,
                                existingSessionId: exclusiveSessionId  // 传递会话 ID
                            );
                        }
                        finally
                        {
                            // 命令处理完成，增加计数
                            lock (lockObject)
                            {
                                completedCount++;
                                Logger.Log($"统一流式处理: 命令完成 {completedCount}/{commandCount}");
                                
                                // 如果所有命令都完成了，设置完成信号
                                if (completedCount >= commandCount)
                                {
                                    allCommandsCompletedSource.TrySetResult(true);
                                }
                            }
                        }
                    },
                    _plugin
                );

                // 将完整消息逐字符添加到StreamingCommandProcessor
                // 这样可以模拟流式接收，触发命令检测和处理
                foreach (char c in completeMessage)
                {
                    // 中断后立刻停手：这是逐字符喂，一条几百字的回复就是几百次
                    // "已中断，丢弃"的空转 —— 既刷屏也白费
                    if (InterruptManager.IsInterrupted)
                    {
                        Logger.Log("统一流式处理: 已中断，停止投喂剩余内容");
                        break;
                    }

                    streamProcessor.AddText(c.ToString());
                }

                // 完成处理（触发所有命令的处理）
                streamProcessor.Complete();

                // 获取命令总数
                lock (lockObject)
                {
                    // StreamingCommandProcessor 会在 Complete() 后知道总共有多少命令
                    // 我们需要从 StreamingCommandProcessor 获取这个信息
                    // 暂时使用一个简单的方法：计算消息中的命令数量
                    commandCount = System.Text.RegularExpressions.Regex.Matches(
                        completeMessage, 
                        @"<\|\w+_begin\|>"
                    ).Count;
                    
                    Logger.Log($"统一流式处理: 检测到 {commandCount} 个命令");
                    
                    // 如果没有命令，直接完成
                    if (commandCount == 0)
                    {
                        allCommandsCompletedSource.TrySetResult(true);
                    }
                }

                Logger.Log("统一流式处理: StreamingCommandProcessor.Complete() 已调用，等待所有命令处理完成...");

                // 等待所有命令真正处理完成。
                // 必须同时等中断信号：中断会把队列里没执行的命令直接丢掉，
                // 那些命令的完成计数永远补不齐，只等这个信号量就是永久挂起 ——
                // 独占会话结束不了、_responseLock 也放不掉。
                await Task.WhenAny(allCommandsCompletedSource.Task, InterruptManager.WhenInterruptedAsync());
                Logger.Log(InterruptManager.IsInterrupted
                    ? "统一流式处理: 被中断，不再等待剩余命令"
                    : "统一流式处理: 所有命令处理完成");
            }
            catch (Exception ex)
            {
                Logger.Log($"统一流式处理: 处理完整消息失败: {ex.Message}");
                throw;
            }
            finally
            {
                // 确保独占会话一定会结束（在所有命令处理完成后）
                if (useExclusiveSession && !string.IsNullOrEmpty(exclusiveSessionId))
                {
                    try
                    {
                        await vpetTTSIntegration.EndExclusiveSessionAsync();
                        Logger.Log($"统一流式处理: 结束独占会话（所有命令完成后），会话 ID: {exclusiveSessionId}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"统一流式处理: 结束独占会话失败: {ex.Message}");
                    }
                }
                
                // 通知生命周期插件：处理完成（流式处理）
                try
                {
                    await _plugin.ProcessingLifecycleManager?.NotifyProcessingCompleteAsync();
                    Logger.Log("统一流式处理: 已通知生命周期插件处理完成");
                }
                catch (Exception ex)
                {
                    Logger.Log($"统一流式处理: 通知生命周期插件失败: {ex.Message}");
                }
            }
        }

    }
}
