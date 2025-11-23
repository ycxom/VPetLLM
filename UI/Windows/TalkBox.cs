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

        /// <summary>
        /// 获取消息处理器（用于流式处理等待）
        /// </summary>
        public SmartMessageProcessor MessageProcessor => _messageProcessor;

        public TalkBox(VPetLLM plugin) : base(plugin)
        {
            _plugin = plugin;
            _messageProcessor = new SmartMessageProcessor(_plugin);
            if (_plugin.ChatCore != null)
            {
                _plugin.ChatCore.SetResponseHandler(HandleResponse);
            }
            Logger.Log("TalkBox created.");
        }

        public void HandleNormalResponse(string message)
        {
            _plugin.MW.Main.Say(message);
        }
        public async void HandleResponse(string response)
        {
            Logger.Log($"HandleResponse: 收到AI回复: {response}");

            // 先停止思考动画，确保气泡状态清理
            StopThinkingAnimation();
            
            // 短暂延迟，确保思考动画完全停止
            await Task.Delay(150);

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // 使用智能消息处理器处理回复
                    await _messageProcessor.ProcessMessageAsync(response);
                }
                catch (Exception ex)
                {
                    Logger.Log($"HandleResponse: 处理AI回复时发生错误: {ex.Message}");
                    // 发生错误时回退到简单显示
                    _plugin.MW.Main.Say(response);
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
                // 停止思考动画（如果有的话）
                Logger.Log("Responded: 准备停止思考动画");
                StopThinkingAnimation();
                
                // 额外延迟，确保气泡状态完全清理后再继续
                await Task.Delay(100);
                Logger.Log("Responded: 思考动画已停止，气泡状态已清理");
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
        /// 启动思考动画 - 显示动态的"思考中"气泡
        /// </summary>
        private void StartThinkingAnimation()
        {
            // 如果已经在思考，先停止
            if (_isThinking)
            {
                StopThinkingAnimation();
            }

            _isThinking = true;
            _thinkingCancellationTokenSource = new System.Threading.CancellationTokenSource();
            var cancellationToken = _thinkingCancellationTokenSource.Token;

            // 获取宠物名称和思考文本模板
            var petName = _plugin.Settings.AiName ?? "VPet";
            var thinkingTemplate = LanguageHelper.Get("Thinking.Text", _plugin.Settings.Language);
            
            // 如果没有找到翻译，使用默认文本
            if (string.IsNullOrEmpty(thinkingTemplate))
            {
                thinkingTemplate = "{PetName} thinking";
            }

            // 启动后台任务循环显示思考动画
            Task.Run(async () =>
            {
                try
                {
                    int dotCount = 0;
                    var thinkingTexts = new[]
                    {
                        thinkingTemplate.Replace("{PetName}", petName),
                        thinkingTemplate.Replace("{PetName}", petName) + " ..",
                        thinkingTemplate.Replace("{PetName}", petName) + " ....",
                        thinkingTemplate.Replace("{PetName}", petName) + " ......",
                        thinkingTemplate.Replace("{PetName}", petName) + " ........"
                    };

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var currentText = thinkingTexts[dotCount % thinkingTexts.Length];
                        
                        // 在UI线程上更新气泡
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                ShowThinkingBubbleInstantly(currentText);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"思考动画更新失败: {ex.Message}");
                            }
                        });

                        dotCount++;
                        
                        // 增加更新间隔到500ms，减少频繁更新导致的状态冲突
                        await Task.Delay(500, cancellationToken);
                    }
                }
                catch (System.Threading.Tasks.TaskCanceledException)
                {
                    // 正常取消，不记录日志
                    Logger.Log("思考动画已取消");
                }
                catch (Exception ex)
                {
                    Logger.Log($"思考动画异常: {ex.Message}");
                }
            }, cancellationToken);
        }

        /// <summary>
        /// 停止思考动画并清理状态
        /// </summary>
        private void StopThinkingAnimation()
        {
            if (_isThinking && _thinkingCancellationTokenSource != null)
            {
                try
                {
                    _thinkingCancellationTokenSource.Cancel();
                    _thinkingCancellationTokenSource.Dispose();
                    _thinkingCancellationTokenSource = null;
                    _isThinking = false;
                    
                    Logger.Log("StopThinkingAnimation: 思考动画已停止");
                    
                    // 清理MessageBar状态，但不关闭气泡（让新内容可以立即显示）
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            var msgBar = _plugin.MW.Main.MsgBar;
                            if (msgBar == null) return;
                            
                            var msgBarType = msgBar.GetType();
                            
                            // 停止所有定时器
                            var showTimerField = msgBarType.GetField("ShowTimer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            var endTimerField = msgBarType.GetField("EndTimer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            var closeTimerField = msgBarType.GetField("CloseTimer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            
                            if (showTimerField != null && endTimerField != null && closeTimerField != null)
                            {
                                var showTimer = showTimerField.GetValue(msgBar) as System.Timers.Timer;
                                var endTimer = endTimerField.GetValue(msgBar) as System.Timers.Timer;
                                var closeTimer = closeTimerField.GetValue(msgBar) as System.Timers.Timer;
                                
                                showTimer?.Stop();
                                endTimer?.Stop();
                                closeTimer?.Stop();
                            }
                            
                            // 清空流式传输状态
                            var oldsaystreamField = msgBarType.GetField("oldsaystream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (oldsaystreamField != null)
                            {
                                oldsaystreamField.SetValue(msgBar, null);
                            }
                            
                            Logger.Log("StopThinkingAnimation: MessageBar状态已清理");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"StopThinkingAnimation: 清理MessageBar状态失败: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log($"StopThinkingAnimation: 停止思考动画失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 瞬时显示思考气泡（直接设置UI - 优化版）
        /// </summary>
        private void ShowThinkingBubbleInstantly(string text)
        {
            try
            {
                var msgBar = _plugin.MW.Main.MsgBar;
                if (msgBar == null) return;

                var msgBarType = msgBar.GetType();
                
                // 1. 停止所有定时器，防止状态冲突
                var showTimerField = msgBarType.GetField("ShowTimer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var endTimerField = msgBarType.GetField("EndTimer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var closeTimerField = msgBarType.GetField("CloseTimer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                
                if (showTimerField != null && endTimerField != null && closeTimerField != null)
                {
                    var showTimer = showTimerField.GetValue(msgBar) as System.Timers.Timer;
                    var endTimer = endTimerField.GetValue(msgBar) as System.Timers.Timer;
                    var closeTimer = closeTimerField.GetValue(msgBar) as System.Timers.Timer;
                    
                    showTimer?.Stop();
                    endTimer?.Stop();
                    closeTimer?.Stop();
                }
                
                // 2. 清空 outputtext 和 outputtextsample（防止流式显示继续）
                var outputtextField = msgBarType.GetField("outputtext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (outputtextField != null)
                {
                    var outputtext = outputtextField.GetValue(msgBar) as System.Collections.Generic.List<char>;
                    outputtext?.Clear();
                }
                
                var outputtextsampleField = msgBarType.GetField("outputtextsample", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (outputtextsampleField != null)
                {
                    var outputtextsample = outputtextsampleField.GetValue(msgBar) as System.Text.StringBuilder;
                    outputtextsample?.Clear();
                }
                
                // 3. 直接设置 TText.Text
                var tTextField = msgBarType.GetField("TText", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (tTextField != null)
                {
                    var tText = tTextField.GetValue(msgBar) as System.Windows.Controls.TextBox;
                    if (tText != null)
                    {
                        tText.Text = text;
                    }
                }
                
                // 4. 设置 LName.Content
                var lNameField = msgBarType.GetField("LName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (lNameField != null)
                {
                    var lName = lNameField.GetValue(msgBar) as System.Windows.Controls.Label;
                    if (lName != null)
                    {
                        lName.Content = _plugin.MW.Core.Save.Name;
                    }
                }
                
                // 5. 清空 MessageBoxContent
                var messageBoxContentField = msgBarType.GetField("MessageBoxContent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (messageBoxContentField != null)
                {
                    var messageBoxContent = messageBoxContentField.GetValue(msgBar) as System.Windows.Controls.Grid;
                    messageBoxContent?.Children.Clear();
                }
                
                // 6. 设置可见性和透明度
                ((System.Windows.UIElement)msgBar).Visibility = System.Windows.Visibility.Visible;
                ((System.Windows.UIElement)msgBar).Opacity = 0.8;
                
                // 7. 清空 oldsaystream，防止流式传输干扰
                var oldsaystreamField = msgBarType.GetField("oldsaystream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (oldsaystreamField != null)
                {
                    oldsaystreamField.SetValue(msgBar, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ShowThinkingBubbleInstantly: 显示思考气泡失败: {ex.Message}");
            }
        }
    }
}