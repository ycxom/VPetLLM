using VPetLLM.Utils.Audio;
using VPetLLM.Utils.System;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 动画时序协调器
    /// 用于协调动画播放与TTS播放的同步
    /// </summary>
    public class AnimationTimingCoordinator
    {
        /// <summary>
        /// 协调模式
        /// </summary>
        public enum CoordinationMode
        {
            /// <summary>
            /// 等待TTS完成
            /// </summary>
            WaitForTTS,

            /// <summary>
            /// 使用估算时间
            /// </summary>
            UseEstimatedTime,

            /// <summary>
            /// 混合模式（TTS + 最小时间）
            /// </summary>
            Hybrid
        }

        /// <summary>
        /// 协调结果
        /// </summary>
        public class CoordinationResult
        {
            /// <summary>
            /// 实际等待时间（毫秒）
            /// </summary>
            public int ActualWaitTimeMs { get; set; }

            /// <summary>
            /// 估算时间（毫秒）
            /// </summary>
            public int EstimatedTimeMs { get; set; }

            /// <summary>
            /// TTS实际播放时间（毫秒）
            /// </summary>
            public int TTSPlaybackTimeMs { get; set; }

            /// <summary>
            /// 是否应用了最小时间限制
            /// </summary>
            public bool AppliedMinTimeLimit { get; set; }

            /// <summary>
            /// 是否TTS超时
            /// </summary>
            public bool TTSTimedOut { get; set; }

            /// <summary>
            /// 使用的协调模式
            /// </summary>
            public CoordinationMode UsedMode { get; set; }

            /// <summary>
            /// 开始时间
            /// </summary>
            public DateTime StartTime { get; set; }

            /// <summary>
            /// 结束时间
            /// </summary>
            public DateTime EndTime { get; set; }

            /// <summary>
            /// 总耗时（毫秒）
            /// </summary>
            public int TotalDurationMs => (int)(EndTime - StartTime).TotalMilliseconds;
        }

        private readonly VPetLLM _plugin;
        private readonly CoordinationMode _defaultMode;
        private readonly int _minDisplayDurationMs;
        private readonly int _ttsTimeoutMs;

        /// <summary>
        /// 初始化动画时序协调器
        /// </summary>
        /// <param name="plugin">VPetLLM 插件实例</param>
        /// <param name="defaultMode">默认协调模式</param>
        /// <param name="minDisplayDurationMs">最小显示时间（毫秒）</param>
        /// <param name="ttsTimeoutMs">TTS超时时间（毫秒）</param>
        public AnimationTimingCoordinator(VPetLLM plugin, CoordinationMode defaultMode = CoordinationMode.Hybrid, int minDisplayDurationMs = 1000, int ttsTimeoutMs = 30000)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _defaultMode = defaultMode;
            _minDisplayDurationMs = Math.Max(500, minDisplayDurationMs);
            _ttsTimeoutMs = Math.Max(5000, ttsTimeoutMs);
        }

        /// <summary>
        /// 协调动画与TTS播放
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <param name="text">文本内容</param>
        /// <param name="mode">协调模式，null时使用默认模式</param>
        /// <returns>协调结果</returns>
        public async Task<CoordinationResult> CoordinateAnimationWithTTS(string actionContent, string text, CoordinationMode? mode = null)
        {
            var usedMode = mode ?? _defaultMode;
            var result = new CoordinationResult
            {
                StartTime = DateTime.Now,
                UsedMode = usedMode,
                EstimatedTimeMs = TTSTimingCalculator.CalculateSimpleFallbackTiming(text)
            };

            Logger.Log($"AnimationTimingCoordinator: 开始协调 - 模式: {usedMode}, 文本长度: {text?.Length ?? 0}, 估算时间: {result.EstimatedTimeMs}ms");

            try
            {
                switch (usedMode)
                {
                    case CoordinationMode.WaitForTTS:
                        await CoordinateWithTTSWait(actionContent, text, result);
                        break;

                    case CoordinationMode.UseEstimatedTime:
                        await CoordinateWithEstimatedTime(actionContent, text, result);
                        break;

                    case CoordinationMode.Hybrid:
                        await CoordinateWithHybridMode(actionContent, text, result);
                        break;

                    default:
                        Logger.Log($"AnimationTimingCoordinator: 未知协调模式 {usedMode}，使用混合模式");
                        await CoordinateWithHybridMode(actionContent, text, result);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationTimingCoordinator: 协调过程中发生错误: {ex.Message}");
                // 发生错误时使用估算时间作为回退
                await CoordinateWithEstimatedTime(actionContent, text, result);
            }
            finally
            {
                result.EndTime = DateTime.Now;
                LogCoordinationResult(result);
            }

            return result;
        }

        /// <summary>
        /// 使用TTS等待模式协调
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <param name="text">文本内容</param>
        /// <param name="result">协调结果</param>
        /// <returns>协调任务</returns>
        private async Task CoordinateWithTTSWait(string actionContent, string text, CoordinationResult result)
        {
            Logger.Log("AnimationTimingCoordinator: 使用TTS等待模式");

            // 启动动画
            var animationTask = ExecuteAnimation(actionContent);

            // 等待VPetTTS完成
            var ttsStartTime = DateTime.Now;
            var ttsTask = WaitForVPetTTSComplete(text);

            // 应用超时
            var timeoutTask = Task.Delay(_ttsTimeoutMs);
            var completedTask = await Task.WhenAny(ttsTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Logger.Log($"AnimationTimingCoordinator: TTS等待超时 ({_ttsTimeoutMs}ms)");
                result.TTSTimedOut = true;
                result.TTSPlaybackTimeMs = _ttsTimeoutMs;
            }
            else
            {
                result.TTSPlaybackTimeMs = (int)(DateTime.Now - ttsStartTime).TotalMilliseconds;
                Logger.Log($"AnimationTimingCoordinator: TTS播放完成，耗时: {result.TTSPlaybackTimeMs}ms");
            }

            // 确保最小显示时间
            var minTimeTask = Task.Delay(_minDisplayDurationMs);

            // 等待所有任务完成
            await Task.WhenAll(animationTask, minTimeTask);

            result.ActualWaitTimeMs = Math.Max(result.TTSPlaybackTimeMs, _minDisplayDurationMs);
            result.AppliedMinTimeLimit = result.TTSPlaybackTimeMs < _minDisplayDurationMs;
        }

        /// <summary>
        /// 使用估算时间模式协调
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <param name="text">文本内容</param>
        /// <param name="result">协调结果</param>
        /// <returns>协调任务</returns>
        private async Task CoordinateWithEstimatedTime(string actionContent, string text, CoordinationResult result)
        {
            Logger.Log("AnimationTimingCoordinator: 使用估算时间模式");

            // 启动动画
            var animationTask = ExecuteAnimation(actionContent);

            // 使用估算时间
            var displayTime = Math.Max(result.EstimatedTimeMs, _minDisplayDurationMs);
            result.AppliedMinTimeLimit = result.EstimatedTimeMs < _minDisplayDurationMs;

            var timingTask = Task.Delay(displayTime);

            // 等待动画和时序完成
            await Task.WhenAll(animationTask, timingTask);

            result.ActualWaitTimeMs = displayTime;
            result.TTSPlaybackTimeMs = 0; // 不等待TTS
        }

        /// <summary>
        /// 使用混合模式协调
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <param name="text">文本内容</param>
        /// <param name="result">协调结果</param>
        /// <returns>协调任务</returns>
        private async Task CoordinateWithHybridMode(string actionContent, string text, CoordinationResult result)
        {
            Logger.Log("AnimationTimingCoordinator: 使用混合模式");

            // 启动动画
            var animationTask = ExecuteAnimation(actionContent);

            // 同时启动TTS等待和估算时间
            var ttsStartTime = DateTime.Now;
            var ttsTask = WaitForVPetTTSComplete(text);
            var estimatedTimeTask = Task.Delay(result.EstimatedTimeMs);
            var minTimeTask = Task.Delay(_minDisplayDurationMs);

            // 等待TTS完成或估算时间到达（取较短者）
            var ttsTimeoutTask = Task.Delay(_ttsTimeoutMs);
            var firstCompleted = await Task.WhenAny(ttsTask, estimatedTimeTask, ttsTimeoutTask);

            if (firstCompleted == ttsTask)
            {
                result.TTSPlaybackTimeMs = (int)(DateTime.Now - ttsStartTime).TotalMilliseconds;
                Logger.Log($"AnimationTimingCoordinator: TTS先完成，耗时: {result.TTSPlaybackTimeMs}ms");
            }
            else if (firstCompleted == estimatedTimeTask)
            {
                result.TTSPlaybackTimeMs = (int)(DateTime.Now - ttsStartTime).TotalMilliseconds;
                Logger.Log($"AnimationTimingCoordinator: 估算时间先到达，TTS耗时: {result.TTSPlaybackTimeMs}ms");
            }
            else
            {
                result.TTSTimedOut = true;
                result.TTSPlaybackTimeMs = _ttsTimeoutMs;
                Logger.Log($"AnimationTimingCoordinator: TTS超时 ({_ttsTimeoutMs}ms)");
            }

            // 确保最小显示时间
            await Task.WhenAll(animationTask, minTimeTask);

            result.ActualWaitTimeMs = Math.Max(
                Math.Min(result.TTSPlaybackTimeMs, result.EstimatedTimeMs),
                _minDisplayDurationMs
            );
            result.AppliedMinTimeLimit = result.ActualWaitTimeMs == _minDisplayDurationMs;
        }

        /// <summary>
        /// 执行动画
        /// </summary>
        /// <param name="actionContent">动作内容</param>
        /// <returns>动画任务</returns>
        private async Task ExecuteAnimation(string actionContent)
        {
            try
            {
                if (_plugin.TalkBox?.MessageProcessor != null)
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
                    Logger.Log("AnimationTimingCoordinator: 无法访问动作处理器，跳过动画执行");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationTimingCoordinator: 执行动画失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 等待VPetTTS播放完成
        /// 简化版：直接使用传统VPet语音等待
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>等待任务</returns>
        private async Task WaitForVPetTTSComplete(string text)
        {
            try
            {
                Logger.Log("AnimationTimingCoordinator: 开始等待外置TTS播放完成...");

                // 直接回退到传统的 VPet 语音等待
                await WaitForVPetVoiceCompleteAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationTimingCoordinator: 等待外置 TTS 失败: {ex.Message}");
                // 发生异常时也添加等待时间，确保安全
                await Task.Delay(2000);
            }
        }

        /// <summary>
        /// 等待 VPet 主程序的语音播放完成（传统方式）
        /// </summary>
        /// <returns>等待任务</returns>
        private async Task WaitForVPetVoiceCompleteAsync()
        {
            try
            {
                // 检查是否启用了 VPetLLM 的 TTS 且没有检测到外部 TTS 插件
                if (_plugin.Settings.TTS.IsEnabled && !_plugin.IsVPetTTSPluginDetected)
                {
                    Logger.Log("AnimationTimingCoordinator: VPetLLM 内置 TTS 已启用且无外部 TTS 插件，跳过 VPet 语音等待");
                    return;
                }

                // 如果检测到外部 TTS 插件（如 VPetTTS），需要等待其播放完成
                if (_plugin.IsVPetTTSPluginDetected)
                {
                    Logger.Log("AnimationTimingCoordinator: 检测到外部 TTS 插件，等待其播放完成");
                }

                // 检查 VPet 主程序是否正在播放语音
                if (_plugin.MW?.Main == null)
                {
                    Logger.Log("AnimationTimingCoordinator: MW.Main 为 null，无法检查语音状态");
                    return;
                }

                // 等待 VPet 主程序的语音播放完成
                int maxWaitTime = _ttsTimeoutMs;
                int checkInterval = 200;
                int elapsedTime = 0;

                Logger.Log("AnimationTimingCoordinator: 开始等待VPet语音播放完成");

                while (_plugin.MW.Main.PlayingVoice && elapsedTime < maxWaitTime)
                {
                    await Task.Delay(checkInterval);
                    elapsedTime += checkInterval;
                }

                if (elapsedTime >= maxWaitTime)
                {
                    Logger.Log("AnimationTimingCoordinator: 等待 VPet 语音播放超时");
                    throw new TimeoutException($"VPet语音播放超时 ({maxWaitTime}ms)");
                }

                // 如果检测到VPetTTS插件，增加额外等待时间
                if (_plugin.IsVPetTTSPluginDetected && elapsedTime > 0)
                {
                    Logger.Log("AnimationTimingCoordinator: 检测到VPetTTS插件，添加额外等待时间");
                    await Task.Delay(1000);
                }
                else if (elapsedTime > 0)
                {
                    Logger.Log($"AnimationTimingCoordinator: VPet 语音播放完成，等待时间: {elapsedTime}ms");
                }
                else
                {
                    Logger.Log("AnimationTimingCoordinator: VPet 无语音播放，继续执行");
                }

                // 为外置TTS添加额外等待时间，确保播放完全
                await Task.Delay(1000);
            }
            catch (TimeoutException)
            {
                throw; // 重新抛出超时异常
            }
            catch (Exception ex)
            {
                Logger.Log($"AnimationTimingCoordinator: 等待 VPet 语音失败: {ex.Message}");
                // 发生异常时也添加等待时间，确保安全
                await Task.Delay(2000);
            }
        }

        /// <summary>
        /// 记录协调结果
        /// </summary>
        /// <param name="result">协调结果</param>
        private void LogCoordinationResult(CoordinationResult result)
        {
            Logger.Log($"=== 动画时序协调结果 ===");
            Logger.Log($"协调模式: {result.UsedMode}");
            Logger.Log($"总耗时: {result.TotalDurationMs}ms");
            Logger.Log($"实际等待时间: {result.ActualWaitTimeMs}ms");
            Logger.Log($"估算时间: {result.EstimatedTimeMs}ms");
            Logger.Log($"TTS播放时间: {result.TTSPlaybackTimeMs}ms");
            Logger.Log($"应用最小时间限制: {result.AppliedMinTimeLimit}");
            Logger.Log($"TTS超时: {result.TTSTimedOut}");
            Logger.Log($"========================");
        }

        /// <summary>
        /// 获取默认协调模式
        /// </summary>
        public CoordinationMode DefaultMode => _defaultMode;

        /// <summary>
        /// 获取最小显示时间
        /// </summary>
        public int MinDisplayDurationMs => _minDisplayDurationMs;

        /// <summary>
        /// 获取TTS超时时间
        /// </summary>
        public int TTSTimeoutMs => _ttsTimeoutMs;
    }
}