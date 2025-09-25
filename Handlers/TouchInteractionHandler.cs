using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
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

        public TouchInteractionHandler(VPetLLM plugin)
        {
            _plugin = plugin;
            _mainWindow = plugin.MW;
            _recentInteractions = new List<TouchInteraction>();
            
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
        /// 处理触摸交互
        /// </summary>
        private async Task HandleTouchInteraction(TouchType touchType)
        {
            try
            {
                var now = DateTime.Now;
                
                // 检查是否启用触摸反馈
                if (!_plugin.Settings.TouchFeedback.EnableTouchFeedback)
                {
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
            var templateKey = interaction.Type == TouchType.Head ? "TouchFeedback_Head" : "TouchFeedback_Body";
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
                { "PetMood", interaction.PetMood.ToString("F0") },
                { "PetHealth", interaction.PetHealth.ToString("F0") },
                { "PetName", _plugin.Settings.AiName },
                { "UserName", _plugin.Settings.UserName }
            };
            
            var prompt = template;
            foreach (var kvp in context)
            {
                prompt = prompt.Replace($"{{{kvp.Key}}}", kvp.Value);
            }
            
            return prompt;
        }

        /// <summary>
        /// 获取本地化的触摸区域描述
        /// </summary>
        private string GetLocalizedTouchArea(TouchType touchType, string language)
        {
            var key = touchType == TouchType.Head ? "TouchArea_Head" : "TouchArea_Body";
            var localizedArea = LanguageHelper.Get(key, language);
            
            // 如果没有找到本地化文本，返回英文默认值
            if (string.IsNullOrEmpty(localizedArea))
            {
                return touchType == TouchType.Head ? "head" : "body";
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
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            try
            {
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
        Body    // 摸身体
    }
}