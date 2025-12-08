using System;
using System.Threading;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 气泡显示状态管理类
    /// 用于跟踪气泡的当前状态，避免重复操作
    /// </summary>
    public class BubbleState
    {
        private readonly object _lock = new object();
        
        // 状态属性
        private bool _isVisible;
        private string _currentText;
        private DateTime _lastUpdateTime;
        private bool _isThinking;
        private bool _isCleared;
        private int _updateCount;
        
        /// <summary>
        /// 气泡是否可见
        /// </summary>
        public bool IsVisible
        {
            get { lock (_lock) return _isVisible; }
            private set { lock (_lock) _isVisible = value; }
        }
        
        /// <summary>
        /// 当前显示的文本
        /// </summary>
        public string CurrentText
        {
            get { lock (_lock) return _currentText; }
            private set { lock (_lock) _currentText = value; }
        }
        
        /// <summary>
        /// 上次更新时间
        /// </summary>
        public DateTime LastUpdateTime
        {
            get { lock (_lock) return _lastUpdateTime; }
            private set { lock (_lock) _lastUpdateTime = value; }
        }
        
        /// <summary>
        /// 是否处于思考状态
        /// </summary>
        public bool IsThinking
        {
            get { lock (_lock) return _isThinking; }
            set { lock (_lock) _isThinking = value; }
        }
        
        /// <summary>
        /// 状态是否已清理（用于幂等性检查）
        /// </summary>
        public bool IsCleared
        {
            get { lock (_lock) return _isCleared; }
            private set { lock (_lock) _isCleared = value; }
        }
        
        /// <summary>
        /// 更新计数（用于调试和测试）
        /// </summary>
        public int UpdateCount
        {
            get { lock (_lock) return _updateCount; }
        }
        
        public BubbleState()
        {
            _isVisible = false;
            _currentText = string.Empty;
            _lastUpdateTime = DateTime.MinValue;
            _isThinking = false;
            _isCleared = true;
            _updateCount = 0;
        }
        
        /// <summary>
        /// 检查是否需要更新气泡
        /// </summary>
        /// <param name="newText">新文本</param>
        /// <returns>是否需要更新</returns>
        public bool NeedsUpdate(string newText)
        {
            lock (_lock)
            {
                // 如果文本相同且气泡已可见，不需要更新
                if (_isVisible && _currentText == newText)
                    return false;
                
                // 如果文本为空，不需要更新
                if (string.IsNullOrEmpty(newText))
                    return false;
                
                return true;
            }
        }
        
        /// <summary>
        /// 检查是否需要更新（带防抖）
        /// </summary>
        /// <param name="newText">新文本</param>
        /// <param name="debounceMs">防抖时间（毫秒）</param>
        /// <returns>是否需要更新</returns>
        public bool NeedsUpdateWithDebounce(string newText, int debounceMs = 50)
        {
            lock (_lock)
            {
                // 基本检查
                if (!NeedsUpdate(newText))
                    return false;
                
                // 防抖检查
                var timeSinceLastUpdate = (DateTime.Now - _lastUpdateTime).TotalMilliseconds;
                if (timeSinceLastUpdate < debounceMs)
                    return false;
                
                return true;
            }
        }
        
        /// <summary>
        /// 更新气泡状态
        /// </summary>
        /// <param name="text">新文本</param>
        /// <param name="visible">是否可见</param>
        public void Update(string text, bool visible)
        {
            lock (_lock)
            {
                _currentText = text ?? string.Empty;
                _isVisible = visible;
                _lastUpdateTime = DateTime.Now;
                _isCleared = !visible;
                _updateCount++;
            }
        }
        
        /// <summary>
        /// 设置为思考状态
        /// </summary>
        /// <param name="thinkingText">思考文本</param>
        public void SetThinking(string thinkingText)
        {
            lock (_lock)
            {
                _isThinking = true;
                _currentText = thinkingText ?? string.Empty;
                _isVisible = true;
                _lastUpdateTime = DateTime.Now;
                _isCleared = false;
                _updateCount++;
            }
        }
        
        /// <summary>
        /// 从思考状态过渡到响应状态
        /// 保持可见性，避免闪烁
        /// </summary>
        /// <param name="responseText">响应文本</param>
        public void TransitionToResponse(string responseText)
        {
            lock (_lock)
            {
                _isThinking = false;
                _currentText = responseText ?? string.Empty;
                // 保持可见性，实现平滑过渡
                _isVisible = true;
                _lastUpdateTime = DateTime.Now;
                _isCleared = false;
                _updateCount++;
            }
        }
        
        /// <summary>
        /// 清理状态（幂等操作）
        /// </summary>
        /// <returns>是否实际执行了清理（用于避免重复操作）</returns>
        public bool Clear()
        {
            lock (_lock)
            {
                // 幂等性检查：如果已经清理过，直接返回
                if (_isCleared)
                    return false;
                
                _isVisible = false;
                _currentText = string.Empty;
                _isThinking = false;
                _isCleared = true;
                _lastUpdateTime = DateTime.Now;
                
                return true;
            }
        }
        
        /// <summary>
        /// 隐藏气泡但保留状态（用于临时隐藏）
        /// </summary>
        public void Hide()
        {
            lock (_lock)
            {
                _isVisible = false;
                _lastUpdateTime = DateTime.Now;
            }
        }
        
        /// <summary>
        /// 显示气泡（恢复之前的文本）
        /// </summary>
        public void Show()
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(_currentText))
                {
                    _isVisible = true;
                    _isCleared = false;
                    _lastUpdateTime = DateTime.Now;
                }
            }
        }
        
        /// <summary>
        /// 重置更新计数（用于测试）
        /// </summary>
        public void ResetUpdateCount()
        {
            lock (_lock)
            {
                _updateCount = 0;
            }
        }
        
        /// <summary>
        /// 获取状态快照（用于调试）
        /// </summary>
        public BubbleStateSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                return new BubbleStateSnapshot
                {
                    IsVisible = _isVisible,
                    CurrentText = _currentText,
                    LastUpdateTime = _lastUpdateTime,
                    IsThinking = _isThinking,
                    IsCleared = _isCleared,
                    UpdateCount = _updateCount
                };
            }
        }
    }
    
    /// <summary>
    /// 气泡状态快照（不可变）
    /// </summary>
    public class BubbleStateSnapshot
    {
        public bool IsVisible { get; init; }
        public string CurrentText { get; init; }
        public DateTime LastUpdateTime { get; init; }
        public bool IsThinking { get; init; }
        public bool IsCleared { get; init; }
        public int UpdateCount { get; init; }
    }
}
