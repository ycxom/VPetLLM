using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.Common;
using VPetLLM.Utils.Localization;
using VPetLLM.Utils.System;

namespace VPetLLM.Handlers.UI
{
    /// <summary>
    /// 身体交互处理器 - 处理用户与VPet身体的交互并提供LLM反馈
    /// </summary>
    public class TouchInteractionHandler
    {
        private readonly VPetLLM _plugin;
        private readonly IMainWindow _mainWindow;
        private readonly List<TouchInteraction> _recentInteractions;
        private readonly object _lockObject = new object();

        // 交互冷却时间（毫秒）
        private DateTime _lastInteractionTime = DateTime.MinValue;

        // 连续交互计数
        private int _consecutiveTouchCount = 0;
        private DateTime _lastTouchTime = DateTime.MinValue;

        // 智能过滤系统 - 跟踪VPetLLM动作执行状态
        private static bool _isVPetLLMActionInProgress = false;
        private static DateTime _lastVPetLLMActionTime = DateTime.MinValue;
        private static readonly object _actionStateLock = new object();

        // 动作执行监听
        private static readonly List<TouchInteractionHandler> _allInstances = new List<TouchInteractionHandler>();

        public TouchInteractionHandler(VPetLLM plugin)
        {
            _plugin = plugin;
            _mainWindow = plugin.MW;
            _recentInteractions = new List<TouchInteraction>();

            // 注册到全局实例列表
            lock (_actionStateLock)
            {
                _allInstances.Add(this);
            }

            // 延迟注册事件，确保Main已经初始化
            if (_plugin?.MW?.Main != null)
            {
                RegisterTouchEvents();
                Logger.Log("TouchInteractionHandler initialized with events registered.");
            }
            else
            {
                Logger.Log("TouchInteractionHandler initialized, events will be registered later.");
                // 使用定时器延迟注册事件
                var timer = new System.Timers.Timer(1000); // 1秒后重试
                timer.Elapsed += (s, e) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    TryRegisterTouchEvents();
                };
                timer.Start();
            }
        }

        /// <summary>
        /// 尝试注册触摸事件监听
        /// </summary>
        private void TryRegisterTouchEvents()
        {
            try
            {
                if (_plugin?.MW?.Main != null)
                {
                    RegisterTouchEvents();
                    Logger.Log("Touch events registered successfully (delayed).");
                }
                else
                {
                    Logger.Log("Failed to register touch events: MW.Main not available.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error registering touch events: {ex.Message}");
            }
        }

        /// <summary>
        /// 注册触摸事件监听
        /// </summary>
        private void RegisterTouchEvents()
        {
            try
            {
                if (_plugin?.MW?.Main == null)
                {
                    Logger.Log("Cannot register touch events: MW.Main is null.");
                    return;
                }

                // 监听摸头事件
                _plugin.MW.Main.Event_TouchHead += OnTouchHead;

                // 监听摸身体事件  
                _plugin.MW.Main.Event_TouchBody += OnTouchBody;

                // 监听捏脸事件（通过Hook DisplayPinch方法）
                HookDisplayPinch();

                Logger.Log("Touch events registered successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in RegisterTouchEvents: {ex.Message}");
            }
        }

        /// <summary>
        /// 摸头事件处理
        /// </summary>
        private async void OnTouchHead()
        {
            await HandleTouchInteraction(TouchType.Head);
        }

        /// <summary>
        /// 摸身体事件处理
        /// </summary>
        private async void OnTouchBody()
        {
            await HandleTouchInteraction(TouchType.Body);
        }

        /// <summary>
        /// 捏脸事件处理
        /// </summary>
        private async void OnTouchPinch()
        {
            await HandleTouchInteraction(TouchType.Pinch);
        }

        // 用于跟踪捏脸调用的时间戳，避免重复触发
        private DateTime _lastPinchCallTime = DateTime.MinValue;

        /// <summary>
        /// Hook DisplayPinch方法来监听捏脸事件
        /// 这是最简单可靠的方法，因为无论如何触发捏脸，都会调用DisplayPinch
        /// </summary>
        private void HookDisplayPinch()
        {
            try
            {
                // 检查是否有捏脸动画配置
                if (_plugin?.MW?.Main?.Core?.Graph?.GraphConfig?.Data?.ContainsLine("pinch") != true)
                {
                    Logger.Log("Pinch animation not available in current pet model.");
                    return;
                }

                // 使用定时器定期检查Main.DisplayType来检测捏脸动画
                var checkTimer = new System.Timers.Timer(100); // 每100ms检查一次
                checkTimer.Elapsed += (s, e) =>
                {
                    try
                    {
                        if (_plugin?.MW?.Main?.DisplayType == null)
                            return;

                        var displayType = _plugin.MW.Main.DisplayType;

                        // 检查是否正在播放捏脸动画
                        if (displayType.Name == "pinch" &&
                            displayType.Animat == VPet_Simulator.Core.GraphInfo.AnimatType.A_Start)
                        {
                            var now = DateTime.Now;
                            // 避免短时间内重复触发（500ms内只触发一次）
                            if ((now - _lastPinchCallTime).TotalMilliseconds > 500)
                            {
                                _lastPinchCallTime = now;
                                Logger.Log("Detected pinch animation start, triggering VPetLLM feedback.");

                                // 异步触发捏脸反馈
                                System.Threading.Tasks.Task.Run(() => OnTouchPinch());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error in pinch detection timer: {ex.Message}");
                    }
                };
                checkTimer.Start();

                Logger.Log("Pinch detection timer started successfully.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error hooking DisplayPinch: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 处理触摸交互
        /// </summary>
        private async Task HandleTouchInteraction(TouchType touchType)
        {
            try
            {
                var now = DateTime.Now;

                // 检查是否为默认插件
                if (!_plugin.IsVPetLLMDefaultPlugin())
                {
                    return;
                }

                // 检查是否启用触摸反馈
                if (!_plugin.Settings.TouchFeedback.EnableTouchFeedback)
                {
                    return;
                }

                // 检查是否是VPet自身动作触发的事件（避免误识别）
                if (IsVPetActionInProgress())
                {
                    Logger.Log($"VPet action in progress, ignoring {touchType} touch to avoid false interaction.");
                    return;
                }

                // 检查冷却时间
                if ((now - _lastInteractionTime).TotalMilliseconds < _plugin.Settings.TouchFeedback.TouchCooldown)
                {
                    Logger.Log($"Touch interaction on cooldown, ignoring {touchType} touch.");
                    return;
                }

                // 更新连续触摸计数
                UpdateConsecutiveTouchCount(now);

                // 记录交互
                var interaction = new TouchInteraction
                {
                    Type = touchType,
                    Timestamp = now,
                    ConsecutiveCount = _consecutiveTouchCount,
                    PetMood = GetPetMood(),
                    PetHealth = GetPetHealth()
                };

                lock (_lockObject)
                {
                    _recentInteractions.Add(interaction);
                    // 保持最近10次交互记录
                    if (_recentInteractions.Count > 10)
                    {
                        _recentInteractions.RemoveAt(0);
                    }
                }

                _lastInteractionTime = now;

                Logger.Log($"Processing {touchType} touch interaction (consecutive: {_consecutiveTouchCount})");

                // 生成LLM反馈
                await GenerateTouchFeedback(interaction);
            }
            catch (Exception ex)
            {
                Logger.Log($"Error handling touch interaction: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新连续触摸计数
        /// </summary>
        private void UpdateConsecutiveTouchCount(DateTime now)
        {
            // 如果距离上次触摸超过5秒，重置计数
            if ((now - _lastTouchTime).TotalSeconds > 5)
            {
                _consecutiveTouchCount = 1;
            }
            else
            {
                _consecutiveTouchCount++;
            }

            _lastTouchTime = now;
        }

        /// <summary>
        /// 生成触摸反馈 - 通过现有聊天系统处理
        /// </summary>
        private async Task GenerateTouchFeedback(TouchInteraction interaction)
        {
            if (_plugin.TalkBox == null || !_plugin.Settings.EnableAction)
            {
                return;
            }

            try
            {
                // 构建触摸上下文提示
                var touchPrompt = BuildTouchPrompt(interaction);

                Logger.Log($"Sending touch feedback through chat system: {touchPrompt}");

                // 通过现有的聊天系统处理触摸交互
                // 这样可以保持对话上下文的连续性
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _plugin.TalkBox.Responded(touchPrompt);
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"Error generating touch feedback: {ex.Message}");
            }
        }

        /// <summary>
        /// 构建触摸提示词
        /// </summary>
        private string BuildTouchPrompt(TouchInteraction interaction)
        {
            // 从JSON文件读取触摸提示词模板
            string templateKey;
            switch (interaction.Type)
            {
                case TouchType.Head:
                    templateKey = "TouchFeedback_Head";
                    break;
                case TouchType.Body:
                    templateKey = "TouchFeedback_Body";
                    break;
                case TouchType.Pinch:
                    templateKey = "TouchFeedback_Pinch";
                    break;
                default:
                    templateKey = "TouchFeedback_General";
                    break;
            }
            var template = PromptHelper.Get(templateKey, _plugin.Settings.PromptLanguage);

            // 如果没有找到特定模板，使用通用触摸模板
            if (string.IsNullOrEmpty(template))
            {
                template = PromptHelper.Get("TouchFeedback_General", _plugin.Settings.PromptLanguage);
            }

            // 如果还是没有找到，返回空字符串
            if (string.IsNullOrEmpty(template))
            {
                Logger.Log("TouchInteractionHandler: No touch prompt template found in JSON");
                return string.Empty;
            }

            // 替换模板中的变量
            var context = new Dictionary<string, string>
            {
                { "TouchArea", GetLocalizedTouchArea(interaction.Type, _plugin.Settings.PromptLanguage) },
                { "ConsecutiveCount", interaction.ConsecutiveCount.ToString() },
                { "PetMood", $"{interaction.PetMood:F0}%" },
                { "PetHealth", $"{interaction.PetHealth:F0}%" },
                { "PetName", _plugin.Settings.AiName },
                { "UserName", _plugin.Settings.UserName }
            };

            var prompt = template;
            foreach (var kvp in context)
            {
                prompt = prompt.Replace($"{{{kvp.Key}}}", kvp.Value);
            }

            // 标记为系统生成，便于与用户消息区分
            prompt = "[System] " + prompt;
            return prompt;
        }

        /// <summary>
        /// 获取本地化的触摸区域描述
        /// </summary>
        private string GetLocalizedTouchArea(TouchType touchType, string language)
        {
            string key;
            string defaultValue;

            switch (touchType)
            {
                case TouchType.Head:
                    key = "TouchArea_Head";
                    defaultValue = "head";
                    break;
                case TouchType.Body:
                    key = "TouchArea_Body";
                    defaultValue = "body";
                    break;
                case TouchType.Pinch:
                    key = "TouchArea_Pinch";
                    defaultValue = "face";
                    break;
                default:
                    return "unknown";
            }

            var localizedArea = LanguageHelper.Get(key, language);

            // 如果没有找到本地化文本，返回英文默认值
            if (string.IsNullOrEmpty(localizedArea))
            {
                return defaultValue;
            }

            return localizedArea;
        }

        /// <summary>
        /// 获取当前宠物心情值
        /// </summary>
        private double GetPetMood()
        {
            try
            {
                if (_plugin?.MW?.Main?.Core?.Save != null)
                {
                    return _plugin.MW.Main.Core.Save.Feeling;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting pet mood: {ex.Message}");
            }
            return 50.0; // 默认值
        }

        /// <summary>
        /// 获取当前宠物健康值
        /// </summary>
        private double GetPetHealth()
        {
            try
            {
                if (_plugin?.MW?.Main?.Core?.Save != null)
                {
                    return _plugin.MW.Main.Core.Save.Health;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error getting pet health: {ex.Message}");
            }
            return 50.0; // 默认值
        }



        /// <summary>
        /// 智能检查VPet是否正在执行动作（避免误识别为用户交互）
        /// </summary>
        private bool IsVPetActionInProgress()
        {
            try
            {
                lock (_actionStateLock)
                {
                    // 检查VPetLLM是否正在执行动作
                    if (_isVPetLLMActionInProgress)
                    {
                        Logger.Log("VPetLLM action in progress, ignoring touch event to avoid false trigger.");
                        return true;
                    }

                    // 检查距离上次VPetLLM动作的时间
                    var timeSinceLastAction = DateTime.Now - _lastVPetLLMActionTime;
                    if (timeSinceLastAction.TotalMilliseconds < 3000) // 3秒内
                    {
                        Logger.Log($"Recent VPetLLM action detected ({timeSinceLastAction.TotalMilliseconds}ms ago), ignoring touch event.");
                        return true;
                    }
                }

                // 额外检查：距离上次用户交互的时间
                var timeSinceLastInteraction = DateTime.Now - _lastInteractionTime;
                if (timeSinceLastInteraction.TotalMilliseconds < 1000) // 1秒内
                {
                    Logger.Log("Recent user interaction detected, applying cooldown.");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error checking VPet action status: {ex.Message}");
                return false; // 出错时不阻止交互
            }
        }

        /// <summary>
        /// 通知VPetLLM开始执行动作（由SmartMessageProcessor调用）
        /// </summary>
        public static void NotifyVPetLLMActionStart()
        {
            lock (_actionStateLock)
            {
                _isVPetLLMActionInProgress = true;
                _lastVPetLLMActionTime = DateTime.Now;
                Logger.Log("VPetLLM action started - touch events will be filtered");
            }
        }

        /// <summary>
        /// 通知VPetLLM完成动作执行（由SmartMessageProcessor调用）
        /// </summary>
        public static void NotifyVPetLLMActionEnd()
        {
            lock (_actionStateLock)
            {
                _isVPetLLMActionInProgress = false;
                _lastVPetLLMActionTime = DateTime.Now;
                Logger.Log("VPetLLM action completed - touch events filtering will continue for 3 seconds");
            }
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            try
            {
                // 从全局实例列表中移除
                lock (_actionStateLock)
                {
                    _allInstances.Remove(this);
                }

                if (_plugin?.MW?.Main != null)
                {
                    _plugin.MW.Main.Event_TouchHead -= OnTouchHead;
                    _plugin.MW.Main.Event_TouchBody -= OnTouchBody;
                    Logger.Log("TouchInteractionHandler disposed successfully.");
                }
                else
                {
                    Logger.Log("TouchInteractionHandler disposed (no events to unregister).");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error disposing TouchInteractionHandler: {ex.Message}");
            }
        }


    }

    /// <summary>
    /// 触摸交互记录
    /// </summary>
    public class TouchInteraction
    {
        public TouchType Type { get; set; }
        public DateTime Timestamp { get; set; }
        public int ConsecutiveCount { get; set; }
        public double PetMood { get; set; }
        public double PetHealth { get; set; }
    }

    /// <summary>
    /// 触摸类型
    /// </summary>
    public enum TouchType
    {
        Head,   // 摸头
        Body,   // 摸身体
        Pinch   // 捏脸
    }
}