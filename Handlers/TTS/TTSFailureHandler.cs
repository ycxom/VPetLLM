using VPetLLM.Utils.Audio;

namespace VPetLLM.Handlers.TTS
{
    /// <summary>
    /// TTS 失败处理器
    /// 用于处理 TTS 操作失败时的各种策略
    /// </summary>
    public class TTSFailureHandler
    {
        /// <summary>
        /// TTS 失败处理策略
        /// </summary>
        public enum FailureStrategy
        {
            /// <summary>
            /// 重试操作
            /// </summary>
            Retry,

            /// <summary>
            /// 回退到默认处理
            /// </summary>
            Fallback,

            /// <summary>
            /// 跳过失败的操作
            /// </summary>
            Skip
        }

        private readonly VPetLLM _plugin;
        private readonly FailureStrategy _strategy;
        private readonly int _maxRetryAttempts;
        private readonly int _retryDelayMs;

        /// <summary>
        /// 初始化 TTS 失败处理器
        /// </summary>
        /// <param name="plugin">VPetLLM 插件实例</param>
        /// <param name="strategy">失败处理策略</param>
        /// <param name="maxRetryAttempts">最大重试次数</param>
        /// <param name="retryDelayMs">重试延迟时间（毫秒）</param>
        public TTSFailureHandler(VPetLLM plugin, FailureStrategy strategy = FailureStrategy.Fallback, int maxRetryAttempts = 2, int retryDelayMs = 1000)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _strategy = strategy;
            _maxRetryAttempts = Math.Max(0, maxRetryAttempts);
            _retryDelayMs = Math.Max(100, retryDelayMs);
        }

        /// <summary>
        /// 处理 TTS 失败
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <param name="text">文本内容</param>
        /// <param name="exception">异常信息</param>
        /// <returns>处理任务</returns>
        public async Task HandleTTSFailure(string actionContent, string text, Exception exception)
        {
            Logger.Log($"TTSFailureHandler: 处理TTS失败 - 策略: {_strategy}, 文本: {text?.Substring(0, Math.Min(text?.Length ?? 0, 20))}..., 错误: {exception?.Message}");

            switch (_strategy)
            {
                case FailureStrategy.Retry:
                    await RetryTTSOperation(actionContent, text, exception);
                    break;

                case FailureStrategy.Fallback:
                    await ExecuteWithFallbackTiming(actionContent, text, exception);
                    break;

                case FailureStrategy.Skip:
                    Logger.Log($"TTSFailureHandler: 跳过失败的TTS操作: {text}");
                    LogFailureDetails(text, exception, "操作已跳过");
                    break;

                default:
                    Logger.Log($"TTSFailureHandler: 未知的失败处理策略: {_strategy}，使用回退策略");
                    await ExecuteWithFallbackTiming(actionContent, text, exception);
                    break;
            }
        }

        /// <summary>
        /// 重试 TTS 操作
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <param name="text">文本内容</param>
        /// <param name="originalException">原始异常</param>
        /// <returns>重试任务</returns>
        private async Task RetryTTSOperation(string actionContent, string text, Exception originalException)
        {
            Logger.Log($"TTSFailureHandler: 开始重试TTS操作，最大重试次数: {_maxRetryAttempts}");

            Exception lastException = originalException;

            for (int attempt = 1; attempt <= _maxRetryAttempts; attempt++)
            {
                try
                {
                    Logger.Log($"TTSFailureHandler: 重试第 {attempt} 次");

                    // 等待重试延迟
                    if (attempt > 1)
                    {
                        await Task.Delay(_retryDelayMs);
                    }

                    // 尝试重新下载和播放TTS
                    if (_plugin.TTSService is not null && !string.IsNullOrEmpty(text))
                    {
                        await _plugin.TTSService.PlayTextAsync(text);
                        Logger.Log($"TTSFailureHandler: 重试第 {attempt} 次成功");

                        // 重试成功，执行动画
                        await ExecuteActionWithTiming(actionContent, text);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Logger.Log($"TTSFailureHandler: 重试第 {attempt} 次失败: {ex.Message}");
                }
            }

            // 所有重试都失败，回退到默认处理
            Logger.Log($"TTSFailureHandler: 所有重试都失败，回退到默认处理");
            await ExecuteWithFallbackTiming(actionContent, text, lastException);
        }

        /// <summary>
        /// 使用回退时序执行操作
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <param name="text">文本内容</param>
        /// <param name="exception">异常信息</param>
        /// <returns>执行任务</returns>
        private async Task ExecuteWithFallbackTiming(string actionContent, string text, Exception exception)
        {
            Logger.Log($"TTSFailureHandler: 使用回退时序执行操作");

            // 记录失败详情和恢复建议
            LogFailureDetails(text, exception, "使用回退时序继续执行");

            // 执行动画并使用估算的时序
            await ExecuteActionWithTiming(actionContent, text);
        }

        /// <summary>
        /// 执行动作并使用估算的时序
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <param name="text">文本内容</param>
        /// <returns>执行任务</returns>
        private async Task ExecuteActionWithTiming(string actionContent, string text)
        {
            try
            {
                // 启动动画
                var actionTask = ExecuteAction(actionContent);

                // 计算估算的显示时间
                var estimatedDuration = CalculateEstimatedDuration(text);
                var minDisplayDuration = GetMinDisplayDuration();
                var displayDuration = Math.Max(estimatedDuration, minDisplayDuration);

                Logger.Log($"TTSFailureHandler: 使用估算时序 - 文本长度: {text?.Length ?? 0}, 估算时长: {estimatedDuration}ms, 最小时长: {minDisplayDuration}ms, 实际时长: {displayDuration}ms");

                // 等待动画和最小显示时间
                var timingTask = Task.Delay(displayDuration);

                await Task.WhenAll(actionTask, timingTask);

                Logger.Log($"TTSFailureHandler: 回退时序执行完成");
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSFailureHandler: 执行动作时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 执行动作
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <returns>执行任务</returns>
        private async Task ExecuteAction(string actionContent)
        {
            try
            {
                if (_plugin.TalkBox?.MessageProcessor is not null)
                {
                    // 使用现有的动作处理器执行动作
                    var actionQueue = _plugin.ActionProcessor.Process(actionContent, _plugin.Settings);

                    foreach (var action in actionQueue)
                    {
                        if (string.IsNullOrEmpty(action.Value))
                            await action.Handler.Execute(_plugin.MW).ConfigureAwait(false);
                        else if (int.TryParse(action.Value, out int intValue))
                            await action.Handler.Execute(intValue, _plugin.MW).ConfigureAwait(false);
                        else
                            await action.Handler.Execute(action.Value, _plugin.MW).ConfigureAwait(false);
                    }
                }
                else
                {
                    Logger.Log("TTSFailureHandler: 无法访问动作处理器，跳过动作执行");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"TTSFailureHandler: 执行动作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 计算基于文本长度的估算持续时间
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>估算持续时间（毫秒）</returns>
        private int CalculateEstimatedDuration(string text)
        {
            // 使用 TTSTimingCalculator 进行更精确的时序计算
            return TTSTimingCalculator.CalculateSimpleFallbackTiming(text);
        }

        /// <summary>
        /// 获取最小显示持续时间
        /// </summary>
        /// <returns>最小持续时间（毫秒）</returns>
        private int GetMinDisplayDuration()
        {
            // 从设置中获取最小显示时间，默认1秒
            return _plugin.Settings?.SayTimeMin ?? 1000;
        }

        /// <summary>
        /// 记录失败详情和恢复建议
        /// </summary>
        /// <param name="text">失败的文本</param>
        /// <param name="exception">异常信息</param>
        /// <param name="recoveryAction">恢复操作描述</param>
        private void LogFailureDetails(string text, Exception exception, string recoveryAction)
        {
            Logger.Log($"=== TTS失败详情 ===");
            Logger.Log($"文本内容: {text}");
            Logger.Log($"失败原因: {exception?.Message}");
            Logger.Log($"异常类型: {exception?.GetType().Name}");
            Logger.Log($"恢复操作: {recoveryAction}");

            // 提供恢复建议
            var suggestions = GenerateRecoverySuggestions(exception);
            if (!string.IsNullOrEmpty(suggestions))
            {
                Logger.Log($"恢复建议: {suggestions}");
            }

            Logger.Log($"==================");
        }

        /// <summary>
        /// 生成恢复建议
        /// </summary>
        /// <param name="exception">异常信息</param>
        /// <returns>恢复建议</returns>
        private string GenerateRecoverySuggestions(Exception exception)
        {
            if (exception is null)
                return "检查TTS服务是否正常运行";

            var exceptionType = exception.GetType().Name;
            var message = exception.Message?.ToLowerInvariant() ?? "";

            if (message.Contains("timeout") || message.Contains("超时"))
            {
                return "TTS操作超时，建议检查网络连接或增加超时时间";
            }
            else if (message.Contains("network") || message.Contains("网络"))
            {
                return "网络连接问题，建议检查网络设置或稍后重试";
            }
            else if (message.Contains("file") || message.Contains("文件"))
            {
                return "文件访问问题，建议检查文件权限或磁盘空间";
            }
            else if (message.Contains("plugin") || message.Contains("插件"))
            {
                return "插件相关问题，建议重启VPet或重新加载插件";
            }
            else if (exceptionType.Contains("ArgumentException"))
            {
                return "参数错误，建议检查TTS配置或文本内容";
            }
            else if (exceptionType.Contains("InvalidOperationException"))
            {
                return "操作状态错误，建议检查TTS服务状态";
            }
            else
            {
                return "未知错误，建议查看详细日志或联系技术支持";
            }
        }

        /// <summary>
        /// 获取当前失败处理策略
        /// </summary>
        public FailureStrategy CurrentStrategy => _strategy;

        /// <summary>
        /// 获取最大重试次数
        /// </summary>
        public int MaxRetryAttempts => _maxRetryAttempts;

        /// <summary>
        /// 获取重试延迟时间
        /// </summary>
        public int RetryDelayMs => _retryDelayMs;
    }
}