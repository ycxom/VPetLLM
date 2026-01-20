using System.Text.RegularExpressions;

namespace VPetLLM.Handlers.Infrastructure
{
    /// <summary>
    /// 插件接管管理器 - 管理插件的完整接管流程
    /// </summary>
    public class PluginTakeoverManager
    {
        private IPluginTakeover _currentTakeoverPlugin;
        private StringBuilder _takeoverBuffer = new StringBuilder();
        private bool _isTakingOver = false;
        private string _takeoverPluginName;
        private readonly object _lock = new object();

        /// <summary>
        /// 是否正在接管中
        /// </summary>
        public bool IsTakingOver
        {
            get
            {
                lock (_lock)
                {
                    return _isTakingOver;
                }
            }
        }

        /// <summary>
        /// 当前接管的插件名称
        /// </summary>
        public string CurrentTakeoverPlugin
        {
            get
            {
                lock (_lock)
                {
                    return _takeoverPluginName;
                }
            }
        }

        /// <summary>
        /// 处理文本片段，检测并处理插件接管
        /// </summary>
        /// <param name="chunk">文本片段</param>
        /// <returns>处理后的文本（如果被接管则返回空）</returns>
        public async Task<string> ProcessChunkAsync(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
                return chunk;

            lock (_lock)
            {
                _takeoverBuffer.Append(chunk);
            }

            var currentBuffer = _takeoverBuffer.ToString();

            // 如果正在接管中
            if (_isTakingOver && _currentTakeoverPlugin is not null)
            {
                // 检查是否应该结束接管
                if (_currentTakeoverPlugin.ShouldEndTakeover(currentBuffer))
                {
                    Logger.Log($"PluginTakeoverManager: 检测到接管结束标记");
                    await EndTakeoverAsync();
                    return string.Empty; // 接管的内容不返回给主流程
                }

                // 继续处理接管内容
                var shouldContinue = await _currentTakeoverPlugin.ProcessTakeoverContentAsync(chunk);
                if (!shouldContinue)
                {
                    Logger.Log($"PluginTakeoverManager: 插件主动结束接管");
                    await EndTakeoverAsync();
                }

                return string.Empty; // 接管的内容不返回给主流程
            }

            // 检测是否有插件接管请求（只支持新格式）
            var takeoverMatch = Regex.Match(currentBuffer, @"<\|\s*plugin\s*_begin\s*\|>\s*(\w+)");
            if (takeoverMatch.Success)
            {
                var pluginName = takeoverMatch.Groups[1].Value;
                Logger.Log($"PluginTakeoverManager: 检测到插件调用: {pluginName}");

                // 查找支持接管的插件
                var plugin = VPetLLM.Instance?.Plugins.Find(p =>
                    p.Name.Replace(" ", "_").Equals(pluginName, StringComparison.OrdinalIgnoreCase) &&
                    p is IPluginTakeover takeover && takeover.SupportsTakeover);

                if (plugin is IPluginTakeover takeoverPlugin)
                {
                    Logger.Log($"PluginTakeoverManager: 插件 {pluginName} 支持接管，开始接管流程");

                    // 提取初始内容（从插件名之后的内容）
                    var startIndex = takeoverMatch.Index + takeoverMatch.Length;
                    var initialContent = currentBuffer.Substring(startIndex);

                    // 开始接管
                    var success = await takeoverPlugin.BeginTakeoverAsync(initialContent);
                    if (success)
                    {
                        lock (_lock)
                        {
                            _isTakingOver = true;
                            _currentTakeoverPlugin = takeoverPlugin;
                            _takeoverPluginName = pluginName;
                        }
                        Logger.Log($"PluginTakeoverManager: 插件 {pluginName} 接管成功");
                        return string.Empty; // 接管的内容不返回给主流程
                    }
                    else
                    {
                        Logger.Log($"PluginTakeoverManager: 插件 {pluginName} 接管失败，回退到正常流程");
                    }
                }
                else
                {
                    Logger.Log($"PluginTakeoverManager: 插件 {pluginName} 不支持接管或未找到");
                }
            }

            // 没有接管，返回原始内容
            return chunk;
        }

        /// <summary>
        /// 结束当前接管
        /// </summary>
        private async Task EndTakeoverAsync()
        {
            if (_currentTakeoverPlugin is null)
                return;

            try
            {
                var result = await _currentTakeoverPlugin.EndTakeoverAsync();
                Logger.Log($"PluginTakeoverManager: 接管结束，结果: {result}");

                // 将结果发送回 LLM（如果有结果）
                if (!string.IsNullOrEmpty(result))
                {
                    var formattedResult = $"[Plugin Result: {_takeoverPluginName}] {result}";
                    ResultAggregator.Enqueue(formattedResult);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"PluginTakeoverManager: 结束接管时出错: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _isTakingOver = false;
                    _currentTakeoverPlugin = null;
                    _takeoverPluginName = null;
                    _takeoverBuffer.Clear();
                }
            }
        }

        /// <summary>
        /// 重置接管状态
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _isTakingOver = false;
                _currentTakeoverPlugin = null;
                _takeoverPluginName = null;
                _takeoverBuffer.Clear();
            }
        }

        /// <summary>
        /// 强制结束当前接管
        /// </summary>
        public async Task ForceEndTakeoverAsync()
        {
            if (_isTakingOver)
            {
                Logger.Log($"PluginTakeoverManager: 强制结束接管");
                await EndTakeoverAsync();
            }
        }
    }
}
