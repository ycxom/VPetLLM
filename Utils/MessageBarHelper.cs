using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VPetLLM.Utils
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
            if (msgBar == null) return false;
            
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
                    Logger.Log("MessageBarHelper: 缓存初始化成功");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"MessageBarHelper: 缓存初始化失败: {ex.Message}");
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
        /// 停止所有定时器（使用缓存的引用）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static void StopAllTimers(object msgBar)
        {
            if (msgBar == null) return;
            
            // 确保已初始化
            if (!_isInitialized)
            {
                Initialize(msgBar);
            }
            
            try
            {
                if (_showTimerField != null)
                {
                    var showTimer = _showTimerField.GetValue(msgBar) as System.Timers.Timer;
                    showTimer?.Stop();
                }
                
                if (_endTimerField != null)
                {
                    var endTimer = _endTimerField.GetValue(msgBar) as System.Timers.Timer;
                    endTimer?.Stop();
                }
                
                if (_closeTimerField != null)
                {
                    var closeTimer = _closeTimerField.GetValue(msgBar) as System.Timers.Timer;
                    closeTimer?.Stop();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MessageBarHelper.StopAllTimers: 停止定时器失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 清空流式传输状态
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static void ClearStreamState(object msgBar)
        {
            if (msgBar == null) return;
            
            if (!_isInitialized)
            {
                Initialize(msgBar);
            }
            
            try
            {
                // 清空 oldsaystream
                if (_oldsaystreamField != null)
                {
                    _oldsaystreamField.SetValue(msgBar, null);
                }
                
                // 清空 outputtext
                if (_outputtextField != null)
                {
                    var outputtext = _outputtextField.GetValue(msgBar) as System.Collections.Generic.List<char>;
                    outputtext?.Clear();
                }
                
                // 清空 outputtextsample
                if (_outputtextsampleField != null)
                {
                    var outputtextsample = _outputtextsampleField.GetValue(msgBar) as System.Text.StringBuilder;
                    outputtextsample?.Clear();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MessageBarHelper.ClearStreamState: 清空流式状态失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 异步清理MessageBar状态（不阻塞UI线程）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static async Task ClearStateAsync(object msgBar)
        {
            if (msgBar == null) return;
            
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
            if (msgBar == null) return;
            
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
                Logger.Log($"MessageBarHelper.ClearStateInternal: 清理状态失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 带防抖的状态清理（合并短时间内的多次调用）
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="debounceMs">防抖时间（毫秒）</param>
        public static async Task ClearStateDebounced(object msgBar, int debounceMs = 50)
        {
            if (msgBar == null) return;
            
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
            if (msgBar == null) return;
            
            if (!_isInitialized)
            {
                Initialize(msgBar);
            }
            
            try
            {
                if (_tTextField != null)
                {
                    var tText = _tTextField.GetValue(msgBar) as TextBox;
                    if (tText != null)
                    {
                        tText.Text = text;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MessageBarHelper.UpdateTextDirect: 更新文本失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 更新说话者名称
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        /// <param name="name">说话者名称</param>
        public static void UpdateSpeakerName(object msgBar, string name)
        {
            if (msgBar == null) return;
            
            if (!_isInitialized)
            {
                Initialize(msgBar);
            }
            
            try
            {
                if (_lNameField != null)
                {
                    var lName = _lNameField.GetValue(msgBar) as Label;
                    if (lName != null)
                    {
                        lName.Content = name;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MessageBarHelper.UpdateSpeakerName: 更新名称失败: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 清空消息框内容
        /// </summary>
        /// <param name="msgBar">MessageBar实例</param>
        public static void ClearMessageBoxContent(object msgBar)
        {
            if (msgBar == null) return;
            
            if (!_isInitialized)
            {
                Initialize(msgBar);
            }
            
            try
            {
                if (_messageBoxContentField != null)
                {
                    var messageBoxContent = _messageBoxContentField.GetValue(msgBar) as Grid;
                    messageBoxContent?.Children.Clear();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MessageBarHelper.ClearMessageBoxContent: 清空内容失败: {ex.Message}");
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
            if (msgBar == null) return;
            
            try
            {
                var uiElement = msgBar as UIElement;
                if (uiElement != null)
                {
                    uiElement.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    uiElement.Opacity = opacity;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MessageBarHelper.SetVisibility: 设置可见性失败: {ex.Message}");
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
            if (msgBar == null) return;
            
            if (!_isInitialized)
            {
                Initialize(msgBar);
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
                Logger.Log($"MessageBarHelper.ShowBubbleQuick: 显示气泡失败: {ex.Message}");
            }
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
            
            Logger.Log("MessageBarHelper: 缓存已重置");
        }
    }
}
