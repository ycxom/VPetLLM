using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SystemTimers = System.Timers;
using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// MessageBar帮助类 - 缓存反射引用，提供高效的状态管理方法
    /// 用于优化流式传输模式下的气泡显示性能
    /// </summary>
    public static class MessageBarHelper
    {
        // 缓存的字段引用
        private static FieldInfo _showTimerField;
        private static FieldInfo _endTimerField;
        private static FieldInfo _closeTimerField;
        private static FieldInfo _tTextField;
        private static FieldInfo _lNameField;
        private static FieldInfo _oldsaystreamField;
        private static FieldInfo _outputtextField;
        private static FieldInfo _outputtextsampleField;
        private static FieldInfo _messageBoxContentField;

        // 缓存的类型引用
        private static Type _msgBarType;

        // 初始化状态
        private static bool _isInitialized = false;
        private static readonly object _initLock = new object();

        // 防抖控制
        private static DateTime _lastClearTime = DateTime.MinValue;
        private static readonly object _debounceLock = new object();
        private static CancellationTokenSource _debounceCts;

        /// <summary>
        /// 初始化缓存（线程安全）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <returns>是否初始化成功</returns>
        public static bool Initialize(object msgBar)
        {
            if (msgBar is null) return false;

            lock (_initLock)
            {
                if (_isInitialized && _msgBarType == msgBar.GetType())
                    return true;

                try
                {
                    _msgBarType = msgBar.GetType();

                    // 缓存公共字段
                    _showTimerField = _msgBarType.GetField("ShowTimer", BindingFlags.Public | BindingFlags.Instance);
                    _endTimerField = _msgBarType.GetField("EndTimer", BindingFlags.Public | BindingFlags.Instance);
                    _closeTimerField = _msgBarType.GetField("CloseTimer", BindingFlags.Public | BindingFlags.Instance);
                    _tTextField = _msgBarType.GetField("TText", BindingFlags.Public | BindingFlags.Instance);
                    _messageBoxContentField = _msgBarType.GetField("MessageBoxContent", BindingFlags.Public | BindingFlags.Instance);

                    // 缓存私有字段
                    _lNameField = _msgBarType.GetField("LName", BindingFlags.NonPublic | BindingFlags.Instance);
                    _oldsaystreamField = _msgBarType.GetField("oldsaystream", BindingFlags.NonPublic | BindingFlags.Instance);
                    _outputtextField = _msgBarType.GetField("outputtext", BindingFlags.NonPublic | BindingFlags.Instance);
                    _outputtextsampleField = _msgBarType.GetField("outputtextsample", BindingFlags.NonPublic | BindingFlags.Instance);

                    _isInitialized = true;

                    // 能力探测：任一关键成员缺失（宿主内部结构变化）即降级，
                    // 只在初始化时报告一次，而不是每次调用静默失败
                    var missing = new List<string>();
                    if (_showTimerField is null) missing.Add("ShowTimer");
                    if (_endTimerField is null) missing.Add("EndTimer");
                    if (_closeTimerField is null) missing.Add("CloseTimer");
                    if (_tTextField is null) missing.Add("TText");
                    if (_lNameField is null) missing.Add("LName");
                    if (_oldsaystreamField is null) missing.Add("oldsaystream");
                    if (_outputtextField is null) missing.Add("outputtext");
                    if (_outputtextsampleField is null) missing.Add("outputtextsample");

                    if (missing.Count > 0)
                    {
                        VPetLLMUtils.Logger.Log($"MessageBarHelper: 宿主 MessageBar 缺少成员 [{string.Join(", ", missing)}]，流式微控不可用，气泡将走公共 Show API 降级");
                    }
                    else
                    {
                        VPetLLMUtils.Logger.Log("MessageBarHelper: 缓存初始化成功");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"MessageBarHelper: 缓存初始化失败: {ex.Message}");
                    _isInitialized = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// 检查是否已初始化
        /// </summary>
        public static bool IsInitialized => _isInitialized;

        /// <summary>
        /// 流式微控所需的 MessageBar 私有成员是否全部命中。
        /// false 表示宿主内部结构已变化（如字段改名），调用方须走公共 Show API。
        /// </summary>
        public static bool SupportsStreamControl =>
            _isInitialized
            && _showTimerField is not null
            && _endTimerField is not null
            && _closeTimerField is not null
            && _tTextField is not null
            && _lNameField is not null
            && _oldsaystreamField is not null
            && _outputtextField is not null
            && _outputtextsampleField is not null;

        /// <summary>
        /// 预初始化（在应用启动时调用，提前缓存反射引用）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <returns>是否预初始化成功</returns>
        public static bool PreInitialize(object msgBar)
        {
            if (msgBar is null) return false;

            // 预初始化就是调用 Initialize，但记录不同的日志
            var result = Initialize(msgBar);
            if (result)
            {
                VPetLLMUtils.Logger.Log("MessageBarHelper: 预初始化完成，反射缓存已就绪");
            }
            return result;
        }

        /// <summary>
        /// 获取字段值（泛型版本）
        /// </summary>
        /// <typeparam name="T">字段类型</typeparam>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="fieldName">字段名称</param>
        /// <returns>字段值，失败返回默认值</returns>
        public static T GetFieldValue<T>(object msgBar, string fieldName)
        {
            if (msgBar is null) return default;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                var field = GetCachedField(fieldName);
                if (field is not null)
                {
                    var value = field.GetValue(msgBar);
                    if (value is T typedValue)
                    {
                        return typedValue;
                    }
                }

                // 回退：直接反射获取
                var directField = msgBar.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (directField is not null)
                {
                    var value = directField.GetValue(msgBar);
                    if (value is T typedValue)
                    {
                        return typedValue;
                    }
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.GetFieldValue<{typeof(T).Name}>: 获取字段 {fieldName} 失败: {ex.Message}");
            }

            return default;
        }

        /// <summary>
        /// 设置字段值
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="fieldName">字段名称</param>
        /// <param name="value">要设置的值</param>
        /// <returns>是否设置成功</returns>
        public static bool SetFieldValue(object msgBar, string fieldName, object value)
        {
            if (msgBar is null) return false;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                var field = GetCachedField(fieldName);
                if (field is not null)
                {
                    field.SetValue(msgBar, value);
                    return true;
                }

                // 回退：直接反射设置
                var directField = msgBar.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (directField is not null)
                {
                    directField.SetValue(msgBar, value);
                    return true;
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.SetFieldValue: 设置字段 {fieldName} 失败: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// 获取缓存的字段引用
        /// </summary>
        private static FieldInfo GetCachedField(string fieldName)
        {
            return fieldName switch
            {
                "ShowTimer" => _showTimerField,
                "EndTimer" => _endTimerField,
                "CloseTimer" => _closeTimerField,
                "TText" => _tTextField,
                "LName" => _lNameField,
                "oldsaystream" => _oldsaystreamField,
                "outputtext" => _outputtextField,
                "outputtextsample" => _outputtextsampleField,
                "MessageBoxContent" => _messageBoxContentField,
                _ => null
            };
        }

        /// <summary>
        /// 停止所有定时器（使用缓存的引用）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static void StopAllTimers(object msgBar)
        {
            if (msgBar is null) return;

            // 确保已初始化
            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                if (_showTimerField is not null)
                {
                    var showTimer = _showTimerField.GetValue(msgBar) as SystemTimers.Timer;
                    showTimer?.Stop();
                }

                if (_endTimerField is not null)
                {
                    var endTimer = _endTimerField.GetValue(msgBar) as SystemTimers.Timer;
                    endTimer?.Stop();
                }

                if (_closeTimerField is not null)
                {
                    var closeTimer = _closeTimerField.GetValue(msgBar) as SystemTimers.Timer;
                    closeTimer?.Stop();
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.StopAllTimers: 停止定时器失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空流式传输状态
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static void ClearStreamState(object msgBar)
        {
            if (msgBar is null) return;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                // 清空 oldsaystream
                if (_oldsaystreamField is not null)
                {
                    _oldsaystreamField.SetValue(msgBar, null);
                }

                // 清空 outputtext
                if (_outputtextField is not null)
                {
                    var outputtext = _outputtextField.GetValue(msgBar) as List<char>;
                    outputtext?.Clear();
                }

                // 清空 outputtextsample
                if (_outputtextsampleField is not null)
                {
                    var outputtextsample = _outputtextsampleField.GetValue(msgBar) as StringBuilder;
                    outputtextsample?.Clear();
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.ClearStreamState: 清空流式状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 仅清空流式输出缓冲区（outputtext/outputtextsample），保留 oldsaysstream。
        /// 用于停止思考动画时保护正在显示的 Say 气泡不被意外清除。
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static void ClearStreamBuffersOnly(object msgBar)
        {
            if (msgBar is null) return;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                // 关键守卫：ShowTimer.Enabled == true 表示非流式 Say 正文打字机正在逐字打印
                // （MessageBar.Show(name, text) 是唯一会启动 ShowTimer 的入口；
                //  思考气泡 ShowBubbleQuick 与流式 Show 都会 Stop 掉 ShowTimer）。
                // 此时 outputtext/outputtextsample 正承载着正在显示的正文，绝不能清空，
                // 否则会把打字机掐断，气泡冻结在已显示的前几个字（如 "哎呀，YC"）。
                if (_showTimerField is not null)
                {
                    var showTimer = _showTimerField.GetValue(msgBar) as SystemTimers.Timer;
                    if (showTimer?.Enabled == true)
                    {
                        VPetLLMUtils.Logger.Log("MessageBarHelper.ClearStreamBuffersOnly: 检测到正文打字机正在播放(ShowTimer.Enabled)，跳过清空以保护气泡");
                        return;
                    }
                }

                // 清空 outputtext（流式输出字符缓冲区）
                if (_outputtextField is not null)
                {
                    var outputtext = _outputtextField.GetValue(msgBar) as List<char>;
                    outputtext?.Clear();
                }

                // 清空 outputtextsample（流式输出字符串缓冲区）
                if (_outputtextsampleField is not null)
                {
                    var outputtextsample = _outputtextsampleField.GetValue(msgBar) as StringBuilder;
                    outputtextsample?.Clear();
                }

                // 注意：不清除 oldsaysstream！它保存的是当前 Say 气泡的文本，
                // 清除它会导致正在显示的 Say 气泡消失。
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.ClearStreamBuffersOnly: 清空缓冲区失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步清理MessageBar状态（不阻塞UI线程）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static async Task ClearStateAsync(object msgBar)
        {
            if (msgBar is null) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ClearStateInternal(msgBar);
            });
        }

        /// <summary>
        /// 内部状态清理方法
        /// </summary>
        private static void ClearStateInternal(object msgBar)
        {
            if (msgBar is null) return;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                // 停止所有定时器
                StopAllTimers(msgBar);

                // 清空流式状态
                ClearStreamState(msgBar);
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.ClearStateInternal: 清理状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 带防抖的状态清理（合并短时间内的多次调用）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="debounceMs">防抖时间（毫秒）</param>
        public static async Task ClearStateDebounced(object msgBar, int debounceMs = 50)
        {
            if (msgBar is null) return;

            lock (_debounceLock)
            {
                var now = DateTime.Now;
                var timeSinceLastClear = (now - _lastClearTime).TotalMilliseconds;

                // 如果距离上次清理时间小于防抖时间，跳过本次清理
                if (timeSinceLastClear < debounceMs)
                {
                    return;
                }

                // 取消之前的防抖任务
                _debounceCts?.Cancel();
                _debounceCts = new CancellationTokenSource();
            }

            try
            {
                // 等待防抖时间
                await Task.Delay(debounceMs, _debounceCts.Token).ConfigureAwait(false);

                // 执行清理
                await ClearStateAsync(msgBar).ConfigureAwait(false);

                lock (_debounceLock)
                {
                    _lastClearTime = DateTime.Now;
                }
            }
            catch (TaskCanceledException)
            {
                // 防抖取消，忽略
            }
        }

        /// <summary>
        /// 直接更新文本内容（不触发完整状态重置）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="text">要显示的文本</param>
        public static void UpdateTextDirect(object msgBar, string text)
        {
            if (msgBar is null) return;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                if (_tTextField is not null)
                {
                    var tText = _tTextField.GetValue(msgBar) as TextBox;
                    if (tText is not null)
                    {
                        tText.Text = text;
                    }
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.UpdateTextDirect: 更新文本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新说话者名称
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="name">说话者名称</param>
        public static void UpdateSpeakerName(object msgBar, string name)
        {
            if (msgBar is null) return;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                if (_lNameField is not null)
                {
                    var lName = _lNameField.GetValue(msgBar) as Label;
                    if (lName is not null)
                    {
                        lName.Content = name;
                    }
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.UpdateSpeakerName: 更新名称失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空消息框内容
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static void ClearMessageBoxContent(object msgBar)
        {
            if (msgBar is null) return;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                if (_messageBoxContentField is not null)
                {
                    var messageBoxContent = _messageBoxContentField.GetValue(msgBar) as Grid;
                    messageBoxContent?.Children.Clear();
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.ClearMessageBoxContent: 清空内容失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置气泡可见性和透明度
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="visible">是否可见</param>
        /// <param name="opacity">透明度（0-1）</param>
        public static void SetVisibility(object msgBar, bool visible, double opacity = 0.8)
        {
            if (msgBar is null) return;

            try
            {
                var uiElement = msgBar as UIElement;
                if (uiElement is not null)
                {
                    uiElement.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    uiElement.Opacity = opacity;
                }
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.SetVisibility: 设置可见性失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 快速显示气泡（优化版，减少不必要的操作）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="text">要显示的文本</param>
        /// <param name="speakerName">说话者名称</param>
        public static void ShowBubbleQuick(object msgBar, string text, string speakerName)
        {
            if (msgBar is null) return;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            // 宿主内部结构变化时降级为公共 Show API（带打字机效果，但保证有气泡）
            if (!SupportsStreamControl)
            {
                try
                {
                    // 公共 Show 的打字机结束时会对正在播放的 Say 动画执行 C_End 收尾，
                    // 说话动画播放中直接跳过，避免思考气泡掐断说话动画
                    var main = VPetLLM.Instance?.MW?.Main;
                    if (main?.DisplayType?.Type == VPet_Simulator.Core.GraphInfo.GraphType.Say)
                    {
                        VPetLLMUtils.Logger.Log("MessageBarHelper.ShowBubbleQuick: Say 动画播放中，降级路径跳过显示");
                        return;
                    }
                    (msgBar as VPet_Simulator.Core.IMassageBar)?.Show(speakerName, text);
                }
                catch (Exception ex)
                {
                    VPetLLMUtils.Logger.Log($"MessageBarHelper.ShowBubbleQuick: 公共 API 降级失败: {ex.Message}");
                }
                return;
            }

            try
            {
                // 1. 停止定时器（防止状态冲突）
                StopAllTimers(msgBar);

                // 2. 清空流式状态
                ClearStreamState(msgBar);

                // 3. 直接设置文本
                UpdateTextDirect(msgBar, text);

                // 4. 设置说话者名称
                UpdateSpeakerName(msgBar, speakerName);

                // 5. 清空消息框内容
                ClearMessageBoxContent(msgBar);

                // 6. 设置可见性
                SetVisibility(msgBar, true, 0.8);
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.ShowBubbleQuick: 显示气泡失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查气泡是否正在打印
        /// 通过检查ShowTimer.Enabled 状态判断气泡是否正在逐字打印
        /// </summary>
        /// <param name="msgBar">MessageBar 实例</param>
        /// <summary>
        /// 检查气泡是否正在打印
        /// 注意：此方法已不再使用，保留以备将来需要
        /// </summary>
        /// <param name="msgBar">MessageBar 实例</param>
        /// <returns>true 表示正在打印，false 表示打印完成或无法检测</returns>
        public static bool IsBubblePrinting(object msgBar)
        {
            if (msgBar is null) return false;

            if (!_isInitialized)
            {
                Initialize(msgBar);
            }

            try
            {
                // 检查 ShowTimer
                if (_showTimerField is not null)
                {
                    var showTimer = _showTimerField.GetValue(msgBar) as SystemTimers.Timer;
                    return showTimer?.Enabled ?? false;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"MessageBarHelper.IsBubblePrinting: 检测失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 等待气泡打印完成
        /// 根据 VPet 源代码的逻辑计算准确的等待时间
        /// </summary>
        /// <param name="msgBar">MessageBar 实例</param>
        /// <param name="maxWaitMs">最大等待时间（毫秒），默认 10000ms</param>
        public static async Task WaitForPrintCompleteAsync(object msgBar, int maxWaitMs = 10000)
        {
            if (msgBar is null) return;

            // 根据 VPet 源代码计算准确的等待时间
            // 参考：VPet/VPet-Simulator.Core/Display/MessageBar.xaml.cs
            // 
            // 1. 打印时间：ShowTimer.Interval = 150ms，每次打印 2-3 个字符
            //    打印总时间 ≈ (字符数 / 2.5) * 150ms
            // 
            // 2. 显示时间：timeleft = ComCheck(text) * 10 + 20
            //    EndTimer.Interval = 200ms
            //    显示总时间 = timeleft * 200ms
            // 
            // 3. 总等待时间 = 打印时间 + 显示时间
            
            // 由于我们使用的是预估时间（BubbleDisplayConfig.CalculateActualDisplayTime），
            // 它已经包含了打印和显示的时间，所以直接使用即可
            
            VPetLLMUtils.Logger.Log($"MessageBarHelper: 开始等待气泡显示，预估时间: {maxWaitMs}ms");
            
            await Task.Delay(maxWaitMs).ConfigureAwait(false);
            
            VPetLLMUtils.Logger.Log($"MessageBarHelper: 气泡显示等待完成，等待时间: {maxWaitMs}ms");
        }

        /// <summary>
        /// 重置缓存（用于测试或重新初始化）
        /// </summary>
        public static void ResetCache()
        {
            lock (_initLock)
            {
                _isInitialized = false;
                _msgBarType = null;
                _showTimerField = null;
                _endTimerField = null;
                _closeTimerField = null;
                _tTextField = null;
                _lNameField = null;
                _oldsaystreamField = null;
                _outputtextField = null;
                _outputtextsampleField = null;
                _messageBoxContentField = null;
            }

            lock (_debounceLock)
            {
                _lastClearTime = DateTime.MinValue;
                _debounceCts?.Cancel();
                _debounceCts = null;
            }

            VPetLLMUtils.Logger.Log("MessageBarHelper: 缓存已重置");
        }
    }
}