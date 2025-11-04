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
                // 输出当前动画状态用于调试
                var currentAnimDesc = AnimationStateChecker.GetCurrentAnimationDescription(_plugin.MW);
                Logger.Log($"TalkBox.Responded: 当前动画状态 = {currentAnimDesc}");
                
                // 检查VPet是否正在执行重要动画（包括用户交互动画如捏脸）
                bool isPlayingImportantAnimation = AnimationStateChecker.IsPlayingImportantAnimation(_plugin.MW);
                
                // 显示思考动作和动态气泡（仅当VPet不在重要状态时）
                if (!isPlayingImportantAnimation)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Logger.Log("显示思考动作和动态气泡");
                        DisplayThink();
                        StartThinkingAnimation();
                    });
                }
                else
                {
                    Logger.Log($"VPet正在执行重要动画 ({currentAnimDesc})，完全跳过思考动画和气泡");
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
                StopThinkingAnimation();
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
                            ShowThinkingBubbleInstantly(currentText);
                        });

                        dotCount++;
                        
                        // 每100ms更新一次
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (System.Threading.Tasks.TaskCanceledException)
                {
                    // 正常取消，不记录日志
                }
                catch (Exception ex)
                {
                    Logger.Log($"思考动画异常: {ex.Message}");
                }
            }, cancellationToken);
        }

        /// <summary>
        /// 停止思考动画
        /// </summary>
        private void StopThinkingAnimation()
        {
            if (_isThinking && _thinkingCancellationTokenSource != null)
            {
                _thinkingCancellationTokenSource.Cancel();
                _thinkingCancellationTokenSource.Dispose();
                _thinkingCancellationTokenSource = null;
                _isThinking = false;
            }
        }

        /// <summary>
        /// 瞬时显示思考气泡（简化版 - 直接设置 UI）
        /// </summary>
        private void ShowThinkingBubbleInstantly(string text)
        {
            try
            {
                var msgBar = _plugin.MW.Main.MsgBar;
                if (msgBar == null) return;

                var msgBarType = msgBar.GetType();
                
                // 1. 直接设置 TText.Text（公共字段）
                var tTextField = msgBarType.GetField("TText", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (tTextField != null)
                {
                    var tText = tTextField.GetValue(msgBar) as System.Windows.Controls.TextBox;
                    if (tText != null)
                    {
                        tText.Text = text;
                    }
                }
                
                // 2. 设置 LName.Content
                var lNameField = msgBarType.GetField("LName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (lNameField != null)
                {
                    var lName = lNameField.GetValue(msgBar) as System.Windows.Controls.Label;
                    if (lName != null)
                    {
                        lName.Content = _plugin.MW.Core.Save.Name;
                    }
                }
                
                // 3. 清空 outputtext（防止流式显示继续）
                var outputtextField = msgBarType.GetField("outputtext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (outputtextField != null)
                {
                    var outputtext = outputtextField.GetValue(msgBar) as System.Collections.Generic.List<char>;
                    outputtext?.Clear();
                }
                
                // 4. 设置可见性
                ((System.Windows.UIElement)msgBar).Visibility = System.Windows.Visibility.Visible;
                ((System.Windows.UIElement)msgBar).Opacity = 0.8;
                
                // 5. 清空 MessageBoxContent
                var messageBoxContentField = msgBarType.GetField("MessageBoxContent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (messageBoxContentField != null)
                {
                    var messageBoxContent = messageBoxContentField.GetValue(msgBar) as System.Windows.Controls.Grid;
                    messageBoxContent?.Children.Clear();
                }
            }
            catch
            {
                // 静默失败，不记录日志
            }
        }
    }
}