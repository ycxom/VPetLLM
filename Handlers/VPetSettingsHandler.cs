using VPet_Simulator.Windows.Interface;
using VPetLLM.Utils.Common;
using VPetLLM.Utils.System;

namespace VPetLLM.Handlers
{
    public class VPetSettingsHandler : IActionHandler
    {
        public string Keyword => "vpet_settings";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;
        public string Description => "Control VPet settings";

        private readonly Setting _settings;

        public VPetSettingsHandler(Setting settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// 获取本地化的消息文本
        /// </summary>
        private string GetLocalizedMessage(string key, params object[] args)
        {
            var lang = _settings?.PromptLanguage ?? "en";
            var message = PromptHelper.Get(key, lang);

            if (string.IsNullOrEmpty(message))
            {
                // 如果找不到本地化文本，返回键名
                return key;
            }

            // 如果有参数，进行格式化
            if (args != null && args.Length > 0)
            {
                try
                {
                    return string.Format(message, args);
                }
                catch
                {
                    return message;
                }
            }

            return message;
        }

        public Task Execute(int value, IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public Task Execute(IMainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        public async Task Execute(string value, IMainWindow mainWindow)
        {
            // 权限检查 (Subtask 3.1)
            if (!_settings.EnableVPetSettingsControl)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Error_PermissionDenied")}");
                return;
            }

            // 命令解析 (Subtask 3.3)
            // 验证输入不为空
            if (string.IsNullOrWhiteSpace(value))
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Error_CommandEmpty")}");
                return;
            }

            // 解析 "action:value" 格式
            var parts = value.Split(new[] { ':' }, 2);
            if (parts.Length != 2)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Error_InvalidFormat", value)}");
                return;
            }

            var action = parts[0].Trim().ToLower();
            var parameter = parts[1].Trim();

            // 验证 action 不为空
            if (string.IsNullOrEmpty(action))
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Error_ActionEmpty", value)}");
                return;
            }

            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_CommandReceived", action, parameter)}");

            // 命令分发逻辑
            await ExecuteCommand(action, parameter, mainWindow);
        }

        public int GetAnimationDuration(string animationName) => 0;

        private async Task ExecuteCommand(string action, string parameter, IMainWindow mainWindow)
        {
            try
            {
                switch (action)
                {
                    case "set_topmost":
                        await SetTopMost(parameter, mainWindow);
                        break;
                    case "set_transparency":
                        await SetTransparency(parameter, mainWindow);
                        break;
                    case "set_zoom":
                        await SetZoom(parameter, mainWindow);
                        break;
                    case "set_pet_name":
                        await SetPetName(parameter, mainWindow);
                        break;
                    case "set_owner_name":
                        await SetOwnerName(parameter, mainWindow);
                        break;
                    case "set_owner_birthday":
                        await SetOwnerBirthday(parameter, mainWindow);
                        break;
                    default:
                        Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Error_UnknownAction", action)}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Error_ExecutionError", action, ex.Message)}");
            }
        }

        // Task 4: Implement set_topmost command
        private Task SetTopMost(string parameter, IMainWindow mainWindow)
        {
            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TopmostReceived", parameter)}");

            // 解析布尔值参数（true/false）
            bool topMostValue;
            string lowerParam = parameter.ToLower();

            if (lowerParam == "true")
            {
                topMostValue = true;
            }
            else if (lowerParam == "false")
            {
                topMostValue = false;
            }
            else
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TopmostFailed", GetLocalizedMessage("VPetSettings_Error_InvalidBoolean", parameter))}");
                return Task.CompletedTask;
            }

            try
            {
                // 从 mainWindow 获取 Dispatcher（更可靠的方式）
                var window = mainWindow as System.Windows.Window;
                if (window != null)
                {
                    window.Dispatcher.Invoke(() =>
                    {
                        mainWindow.Set.SetTopMost(topMostValue);
                    });
                }
                else
                {
                    // 回退到 Application.Current.Dispatcher
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        mainWindow.Set.SetTopMost(topMostValue);
                    });
                }

                // 记录执行日志
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TopmostSuccess", topMostValue)}");
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TopmostFailed", ex.Message)}");
            }

            return Task.CompletedTask;
        }

        private Task SetTransparency(string parameter, IMainWindow mainWindow)
        {
            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TransparencyReceived", parameter)}");

            // 解析 double 值参数
            if (!double.TryParse(parameter, out double transparencyValue))
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TransparencyFailed", GetLocalizedMessage("VPetSettings_Error_InvalidDouble", parameter))}");
                return Task.CompletedTask;
            }

            // 验证参数在 0.0-1.0 范围内
            if (transparencyValue < 0.0 || transparencyValue > 1.0)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TransparencyFailed", GetLocalizedMessage("VPetSettings_Error_TransparencyOutOfRange", transparencyValue))}");
                return Task.CompletedTask;
            }

            try
            {
                // 从 mainWindow 获取 Dispatcher（更可靠的方式）
                var window = mainWindow as System.Windows.Window;
                var dispatcher = window?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;

                if (dispatcher != null)
                {
                    dispatcher.Invoke(() =>
                    {
                        var windowType = mainWindow.GetType();
                        var opacityProperty = windowType.GetProperty("Opacity");

                        if (opacityProperty != null && opacityProperty.CanWrite)
                        {
                            opacityProperty.SetValue(mainWindow, transparencyValue);
                            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TransparencySuccess", transparencyValue)}");
                        }
                        else
                        {
                            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TransparencyFailed", GetLocalizedMessage("VPetSettings_Error_OpacityPropertyNotFound"))}");
                        }
                    });
                }
                else
                {
                    Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TransparencyFailed", "Dispatcher not available")}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_TransparencyFailed", ex.Message)}");
            }

            return Task.CompletedTask;
        }

        private Task SetZoom(string parameter, IMainWindow mainWindow)
        {
            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_ZoomReceived", parameter)}");

            // 解析 double 值参数
            if (!double.TryParse(parameter, out double zoomValue))
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_ZoomFailed", GetLocalizedMessage("VPetSettings_Error_InvalidDouble", parameter))}");
                return Task.CompletedTask;
            }

            // 验证参数在 0.5-3.0 范围内
            if (zoomValue < 0.5 || zoomValue > 3.0)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_ZoomFailed", GetLocalizedMessage("VPetSettings_Error_ZoomOutOfRange", zoomValue))}");
                return Task.CompletedTask;
            }

            try
            {
                // 从 mainWindow 获取 Dispatcher（更可靠的方式）
                var window = mainWindow as System.Windows.Window;
                var dispatcher = window?.Dispatcher ?? System.Windows.Application.Current?.Dispatcher;

                if (dispatcher != null)
                {
                    dispatcher.Invoke(() =>
                    {
                        mainWindow.SetZoomLevel(zoomValue);
                    });

                    // 记录执行日志
                    Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_ZoomSuccess", zoomValue)}");
                }
                else
                {
                    Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_ZoomFailed", "Dispatcher not available")}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_ZoomFailed", ex.Message)}");
            }

            return Task.CompletedTask;
        }

        private Task SetPetName(string parameter, IMainWindow mainWindow)
        {
            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_PetNameReceived", parameter)}");

            // 验证参数非空且不仅包含空白字符
            if (string.IsNullOrWhiteSpace(parameter))
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_PetNameFailed", GetLocalizedMessage("VPetSettings_Error_NameEmpty"))}");
                return Task.CompletedTask;
            }

            try
            {
                // 设置 mainWindow.GameSavesData.GameSave.Name
                mainWindow.GameSavesData.GameSave.Name = parameter;

                // 记录执行日志
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_PetNameSuccess", parameter)}");
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_PetNameFailed", ex.Message)}");
            }

            return Task.CompletedTask;
        }

        private Task SetOwnerName(string parameter, IMainWindow mainWindow)
        {
            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_OwnerNameReceived", parameter)}");

            // 验证参数非空且不仅包含空白字符
            if (string.IsNullOrWhiteSpace(parameter))
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_OwnerNameFailed", GetLocalizedMessage("VPetSettings_Error_NameEmpty"))}");
                return Task.CompletedTask;
            }

            try
            {
                // 设置 mainWindow.GameSavesData.GameSave.HostName
                mainWindow.GameSavesData.GameSave.HostName = parameter;

                // 记录执行日志
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_OwnerNameSuccess", parameter)}");
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_OwnerNameFailed", ex.Message)}");
            }

            return Task.CompletedTask;
        }

        private Task SetOwnerBirthday(string parameter, IMainWindow mainWindow)
        {
            Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_BirthdayReceived", parameter)}");

            // 解析字符串参数
            // 验证参数符合 YYYY-MM-DD 格式且表示有效日期
            if (string.IsNullOrWhiteSpace(parameter))
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_BirthdayFailed", GetLocalizedMessage("VPetSettings_Error_BirthdayEmpty"))}");
                return Task.CompletedTask;
            }

            // 尝试解析日期，使用 YYYY-MM-DD 格式
            if (!DateTime.TryParseExact(parameter, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out DateTime birthdayDate))
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_BirthdayFailed", GetLocalizedMessage("VPetSettings_Error_InvalidDateFormat", parameter))}");
                return Task.CompletedTask;
            }

            try
            {
                // 设置 mainWindow.GameSavesData["owner_birthday"]
                mainWindow.GameSavesData.SetString("owner_birthday", parameter);

                // 记录执行日志
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_BirthdaySuccess", parameter)}");
            }
            catch (Exception ex)
            {
                Logger.Log($"VPetSettingsHandler: {GetLocalizedMessage("VPetSettings_Log_BirthdayFailed", ex.Message)}");
            }

            return Task.CompletedTask;
        }
    }
}
