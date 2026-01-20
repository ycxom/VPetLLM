using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Controls
{
    /// <summary>
    /// 侧边栏按钮控件
    /// </summary>
    public class SidebarButton
    {
        public string ButtonId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public string IconText { get; set; } = string.Empty;
        public string ToolTip { get; set; } = string.Empty;
        public Action<VPetLLM>? Action { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int Order { get; set; } = 0;

        public SidebarButton()
        {
        }

        public SidebarButton(string buttonId, string displayName, string iconText, string toolTip, Action<VPetLLM>? action = null)
        {
            ButtonId = buttonId;
            DisplayName = displayName;
            IconText = iconText;
            ToolTip = toolTip;
            Action = action;
        }

        /// <summary>
        /// 获取按钮内容（图标或文本）- 使用 VPet 主题颜色
        /// </summary>
        public object GetContent()
        {
            try
            {
                // 优先使用图标路径
                if (!string.IsNullOrEmpty(IconPath))
                {
                    try
                    {
                        var image = new Image
                        {
                            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(IconPath, UriKind.RelativeOrAbsolute)),
                            Width = 20,
                            Height = 20,
                            Stretch = Stretch.Uniform
                        };
                        return image;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error loading icon from path {IconPath}: {ex.Message}");
                        // 如果图标加载失败，回退到文本
                    }
                }

                // 使用图标文本（Emoji）
                if (!string.IsNullOrEmpty(IconText))
                {
                    var textBlock = new TextBlock
                    {
                        Text = IconText,
                        FontSize = 14,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    // 使用 DynamicResource 跟随 VPet 主题颜色（与 DemoClock 一致）
                    textBlock.SetResourceReference(TextBlock.ForegroundProperty, "DARKPrimaryText");
                    return textBlock;
                }

                // 回退到显示名称的首字母
                if (!string.IsNullOrEmpty(DisplayName))
                {
                    var textBlock = new TextBlock
                    {
                        Text = DisplayName.Substring(0, 1).ToUpper(),
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    textBlock.SetResourceReference(TextBlock.ForegroundProperty, "DARKPrimaryText");
                    return textBlock;
                }

                // 最后的回退选项
                var fallbackText = new TextBlock
                {
                    Text = "?",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                fallbackText.SetResourceReference(TextBlock.ForegroundProperty, "DARKPrimaryText");
                return fallbackText;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error creating button content for {ButtonId}: {ex.Message}");
                return new TextBlock { Text = "?", FontSize = 12 };
            }
        }

        /// <summary>
        /// 执行按钮动作
        /// </summary>
        public void ExecuteAction(VPetLLM vpetLLM)
        {
            try
            {
                if (Action is not null && IsEnabled)
                {
                    Action.Invoke(vpetLLM);
                    Logger.Log($"Executed action for button: {ButtonId}");
                }
                else
                {
                    Logger.Log($"No action or button disabled for: {ButtonId}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error executing action for button {ButtonId}: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建预定义的设置按钮
        /// </summary>
        public static SidebarButton CreateSettingsButton()
        {
            return new SidebarButton
            {
                ButtonId = "settings",
                DisplayName = "Settings",
                IconText = "⚙️",
                ToolTip = LocalizationService.Instance["FloatingSidebar.Settings"] ?? "打开设置",
                Action = (vpetLLM) => OpenOrFocusSettingsWindow(vpetLLM),
                Order = 1
            };
        }

        /// <summary>
        /// 打开或聚焦设置窗口
        /// </summary>
        private static void OpenOrFocusSettingsWindow(VPetLLM vpetLLM)
        {
            try
            {
                // 检查设置窗口是否已经打开
                if (vpetLLM.SettingWindow is not null && vpetLLM.SettingWindow.IsVisible)
                {
                    // 将现有窗口置于前台
                    vpetLLM.SettingWindow.Activate();
                    vpetLLM.SettingWindow.Focus();
                    if (vpetLLM.SettingWindow.WindowState == System.Windows.WindowState.Minimized)
                    {
                        vpetLLM.SettingWindow.WindowState = System.Windows.WindowState.Normal;
                    }
                    Logger.Log("Settings window activated and focused");
                }
                else
                {
                    // 打开新的设置窗口
                    vpetLLM.Setting();
                    Logger.Log("Settings window opened");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error opening/focusing settings window: {ex.Message}");
                // 回退到直接打开
                vpetLLM.Setting();
            }
        }

        /// <summary>
        /// 创建预定义的ASR按钮
        /// </summary>
        public static SidebarButton CreateASRButton()
        {
            return new SidebarButton
            {
                ButtonId = "asr",
                DisplayName = "Voice Input",
                IconText = "🎤",
                ToolTip = LocalizationService.Instance["FloatingSidebar.VoiceInput"] ?? "语音输入",
                Action = (vpetLLM) => vpetLLM.ShowVoiceInputWindow(),
                Order = 2
            };
        }

        /// <summary>
        /// 创建预定义的清除历史按钮
        /// </summary>
        public static SidebarButton CreateClearHistoryButton()
        {
            return new SidebarButton
            {
                ButtonId = "clear_history",
                DisplayName = "Clear History",
                IconText = "🗑️",
                ToolTip = LocalizationService.Instance["FloatingSidebar.ClearHistory"] ?? "清除聊天历史",
                Action = (vpetLLM) =>
                {
                    try
                    {
                        vpetLLM.ClearChatHistory();
                        Logger.Log("Chat history cleared via sidebar button");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error clearing chat history: {ex.Message}");
                    }
                },
                Order = 3
            };
        }

        /// <summary>
        /// 创建预定义的聊天切换按钮
        /// </summary>
        public static SidebarButton CreateToggleChatButton()
        {
            return new SidebarButton
            {
                ButtonId = "toggle_chat",
                DisplayName = "Toggle Chat",
                IconText = "💬",
                ToolTip = LocalizationService.Instance["FloatingSidebar.ToggleChat"] ?? "切换聊天功能",
                Action = (vpetLLM) =>
                {
                    try
                    {
                        // 这里可以添加切换聊天功能的逻辑
                        Logger.Log("Chat toggle requested via sidebar button");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error toggling chat: {ex.Message}");
                    }
                },
                Order = 4
            };
        }

        /// <summary>
        /// 创建预定义的插件管理按钮
        /// </summary>
        public static SidebarButton CreatePluginsButton()
        {
            return new SidebarButton
            {
                ButtonId = "plugins",
                DisplayName = "Plugins",
                IconText = "🔌",
                ToolTip = LocalizationService.Instance["FloatingSidebar.Plugins"] ?? "插件管理",
                Action = (vpetLLM) =>
                {
                    try
                    {
                        // 打开设置窗口并导航到插件页面
                        vpetLLM.Setting();
                        Logger.Log("Plugin management requested via sidebar button");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Error opening plugin management: {ex.Message}");
                    }
                },
                Order = 5
            };
        }

        /// <summary>
        /// 创建预定义的截图按钮
        /// </summary>
        public static SidebarButton CreateScreenshotButton()
        {
            return new SidebarButton
            {
                ButtonId = "screenshot",
                DisplayName = "Screenshot",
                IconText = "📷",
                ToolTip = LocalizationService.Instance["FloatingSidebar.Screenshot"] ?? "截图",
                Action = (vpetLLM) => vpetLLM.StartScreenshotCapture(),
                Order = 3
            };
        }

        /// <summary>
        /// 获取所有预定义按钮（只保留设置和ASR）
        /// </summary>
        public static List<SidebarButton> GetDefaultButtons()
        {
            return new List<SidebarButton>
            {
                CreateSettingsButton(),
                CreateASRButton(),
                CreateScreenshotButton()
            };
        }
    }
}