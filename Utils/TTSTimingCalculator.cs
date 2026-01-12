using System;
using System.Collections.Generic;
using System.Linq;

namespace VPetLLM.Utils
{
    /// <summary>
    /// TTS 时序计算器
    /// 用于计算基于文本的回退时序和性能监控
    /// </summary>
    public static class TTSTimingCalculator
    {
        /// <summary>
        /// 时序计算配置
        /// </summary>
        public class TimingConfig
        {
            /// <summary>
            /// 每字符基础时间（毫秒）
            /// </summary>
            public int BaseTimePerCharacter { get; set; } = 100;
            
            /// <summary>
            /// 标点符号额外停顿时间（毫秒）
            /// </summary>
            public int PunctuationDelayMs { get; set; } = 200;
            
            /// <summary>
            /// 最小回退时间（毫秒）
            /// </summary>
            public int MinFallbackTimeMs { get; set; } = 1000;
            
            /// <summary>
            /// 最大回退时间（毫秒）
            /// </summary>
            public int MaxFallbackTimeMs { get; set; } = 30000;
            
            /// <summary>
            /// 空格和换行的额外时间（毫秒）
            /// </summary>
            public int WhitespaceDelayMs { get; set; } = 50;
            
            /// <summary>
            /// 数字和英文单词的时间倍数
            /// </summary>
            public double EnglishWordMultiplier { get; set; } = 1.2;
        }

        /// <summary>
        /// 时序计算结果
        /// </summary>
        public class TimingResult
        {
            /// <summary>
            /// 计算的总时间（毫秒）
            /// </summary>
            public int TotalTimeMs { get; set; }
            
            /// <summary>
            /// 基础时间（毫秒）
            /// </summary>
            public int BaseTimeMs { get; set; }
            
            /// <summary>
            /// 标点符号时间（毫秒）
            /// </summary>
            public int PunctuationTimeMs { get; set; }
            
            /// <summary>
            /// 空格时间（毫秒）
            /// </summary>
            public int WhitespaceTimeMs { get; set; }
            
            /// <summary>
            /// 英文单词额外时间（毫秒）
            /// </summary>
            public int EnglishWordTimeMs { get; set; }
            
            /// <summary>
            /// 文本字符数
            /// </summary>
            public int CharacterCount { get; set; }
            
            /// <summary>
            /// 标点符号数量
            /// </summary>
            public int PunctuationCount { get; set; }
            
            /// <summary>
            /// 空格数量
            /// </summary>
            public int WhitespaceCount { get; set; }
            
            /// <summary>
            /// 英文单词数量
            /// </summary>
            public int EnglishWordCount { get; set; }
            
            /// <summary>
            /// 是否应用了最小时间限制
            /// </summary>
            public bool AppliedMinLimit { get; set; }
            
            /// <summary>
            /// 是否应用了最大时间限制
            /// </summary>
            public bool AppliedMaxLimit { get; set; }
        }

        /// <summary>
        /// 性能监控数据
        /// </summary>
        public class PerformanceMetrics
        {
            /// <summary>
            /// 计算次数
            /// </summary>
            public int CalculationCount { get; set; }
            
            /// <summary>
            /// 平均计算时间（毫秒）
            /// </summary>
            public double AverageCalculationTimeMs { get; set; }
            
            /// <summary>
            /// 最大计算时间（毫秒）
            /// </summary>
            public double MaxCalculationTimeMs { get; set; }
            
            /// <summary>
            /// 最小时间限制应用次数
            /// </summary>
            public int MinLimitApplications { get; set; }
            
            /// <summary>
            /// 最大时间限制应用次数
            /// </summary>
            public int MaxLimitApplications { get; set; }
            
            /// <summary>
            /// 平均文本长度
            /// </summary>
            public double AverageTextLength { get; set; }
        }

        private static TimingConfig _config = new TimingConfig();
        private static readonly List<double> _calculationTimes = new List<double>();
        private static readonly List<TimingResult> _recentResults = new List<TimingResult>();
        private static readonly object _metricsLock = new object();
        private static int _totalCalculations = 0;

        /// <summary>
        /// 设置时序计算配置
        /// </summary>
        /// <param name="config">配置对象</param>
        public static void SetConfig(TimingConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            Logger.Log($"TTSTimingCalculator: 配置已更新 - 基础时间: {_config.BaseTimePerCharacter}ms/字符, 最小时间: {_config.MinFallbackTimeMs}ms, 最大时间: {_config.MaxFallbackTimeMs}ms");
        }

        /// <summary>
        /// 获取当前配置
        /// </summary>
        /// <returns>当前配置</returns>
        public static TimingConfig GetConfig()
        {
            return new TimingConfig
            {
                BaseTimePerCharacter = _config.BaseTimePerCharacter,
                PunctuationDelayMs = _config.PunctuationDelayMs,
                MinFallbackTimeMs = _config.MinFallbackTimeMs,
                MaxFallbackTimeMs = _config.MaxFallbackTimeMs,
                WhitespaceDelayMs = _config.WhitespaceDelayMs,
                EnglishWordMultiplier = _config.EnglishWordMultiplier
            };
        }

        /// <summary>
        /// 计算文本的回退时序
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>时序计算结果</returns>
        public static TimingResult CalculateFallbackTiming(string text)
        {
            var startTime = DateTime.Now;
            
            var result = new TimingResult();
            
            if (string.IsNullOrEmpty(text))
            {
                result.TotalTimeMs = _config.MinFallbackTimeMs;
                result.AppliedMinLimit = true;
                RecordCalculation(result, (DateTime.Now - startTime).TotalMilliseconds);
                return result;
            }

            // 统计文本特征
            result.CharacterCount = text.Length;
            result.PunctuationCount = text.Count(char.IsPunctuation);
            result.WhitespaceCount = text.Count(char.IsWhiteSpace);
            result.EnglishWordCount = CountEnglishWords(text);

            // 计算各部分时间
            result.BaseTimeMs = result.CharacterCount * _config.BaseTimePerCharacter;
            result.PunctuationTimeMs = result.PunctuationCount * _config.PunctuationDelayMs;
            result.WhitespaceTimeMs = result.WhitespaceCount * _config.WhitespaceDelayMs;
            result.EnglishWordTimeMs = (int)(result.EnglishWordCount * _config.BaseTimePerCharacter * _config.EnglishWordMultiplier);

            // 计算总时间
            var calculatedTime = result.BaseTimeMs + result.PunctuationTimeMs + result.WhitespaceTimeMs + result.EnglishWordTimeMs;

            // 应用最小和最大时间限制
            if (calculatedTime < _config.MinFallbackTimeMs)
            {
                result.TotalTimeMs = _config.MinFallbackTimeMs;
                result.AppliedMinLimit = true;
            }
            else if (calculatedTime > _config.MaxFallbackTimeMs)
            {
                result.TotalTimeMs = _config.MaxFallbackTimeMs;
                result.AppliedMaxLimit = true;
            }
            else
            {
                result.TotalTimeMs = calculatedTime;
            }

            var calculationTime = (DateTime.Now - startTime).TotalMilliseconds;
            RecordCalculation(result, calculationTime);

            Logger.Log($"TTSTimingCalculator: 文本长度: {result.CharacterCount}, 计算时间: {result.TotalTimeMs}ms (基础: {result.BaseTimeMs}ms, 标点: {result.PunctuationTimeMs}ms, 空格: {result.WhitespaceTimeMs}ms, 英文: {result.EnglishWordTimeMs}ms)");

            return result;
        }

        /// <summary>
        /// 计算简化的回退时序（快速版本）
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>估算时间（毫秒）</returns>
        public static int CalculateSimpleFallbackTiming(string text)
        {
            if (string.IsNullOrEmpty(text))
                return _config.MinFallbackTimeMs;

            var baseTime = text.Length * _config.BaseTimePerCharacter;
            var punctuationBonus = text.Count(char.IsPunctuation) * _config.PunctuationDelayMs;
            var totalTime = baseTime + punctuationBonus;

            return Math.Max(Math.Min(totalTime, _config.MaxFallbackTimeMs), _config.MinFallbackTimeMs);
        }

        /// <summary>
        /// 统计英文单词数量
        /// </summary>
        /// <param name="text">文本内容</param>
        /// <returns>英文单词数量</returns>
        private static int CountEnglishWords(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            var words = text.Split(new char[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            int englishWordCount = 0;

            foreach (var word in words)
            {
                // 检查是否包含英文字母
                if (word.Any(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
                {
                    englishWordCount++;
                }
            }

            return englishWordCount;
        }

        /// <summary>
        /// 记录计算性能数据
        /// </summary>
        /// <param name="result">计算结果</param>
        /// <param name="calculationTimeMs">计算耗时</param>
        private static void RecordCalculation(TimingResult result, double calculationTimeMs)
        {
            lock (_metricsLock)
            {
                _totalCalculations++;
                _calculationTimes.Add(calculationTimeMs);
                _recentResults.Add(result);

                // 保持最近100次计算的记录
                if (_calculationTimes.Count > 100)
                {
                    _calculationTimes.RemoveAt(0);
                }
                
                if (_recentResults.Count > 100)
                {
                    _recentResults.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// 获取性能监控数据
        /// </summary>
        /// <returns>性能指标</returns>
        public static PerformanceMetrics GetPerformanceMetrics()
        {
            lock (_metricsLock)
            {
                var metrics = new PerformanceMetrics
                {
                    CalculationCount = _totalCalculations
                };

                if (_calculationTimes.Count > 0)
                {
                    metrics.AverageCalculationTimeMs = _calculationTimes.Average();
                    metrics.MaxCalculationTimeMs = _calculationTimes.Max();
                }

                if (_recentResults.Count > 0)
                {
                    metrics.MinLimitApplications = _recentResults.Count(r => r.AppliedMinLimit);
                    metrics.MaxLimitApplications = _recentResults.Count(r => r.AppliedMaxLimit);
                    metrics.AverageTextLength = _recentResults.Average(r => r.CharacterCount);
                }

                return metrics;
            }
        }

        /// <summary>
        /// 重置性能监控数据
        /// </summary>
        public static void ResetPerformanceMetrics()
        {
            lock (_metricsLock)
            {
                _calculationTimes.Clear();
                _recentResults.Clear();
                _totalCalculations = 0;
                Logger.Log("TTSTimingCalculator: 性能监控数据已重置");
            }
        }

        /// <summary>
        /// 检查性能并记录警告
        /// </summary>
        /// <param name="thresholdMs">警告阈值（毫秒）</param>
        public static void CheckPerformanceAndWarn(double thresholdMs = 5.0)
        {
            var metrics = GetPerformanceMetrics();
            
            if (metrics.AverageCalculationTimeMs > thresholdMs)
            {
                Logger.Log($"TTSTimingCalculator: 性能警告 - 平均计算时间 {metrics.AverageCalculationTimeMs:F2}ms 超过阈值 {thresholdMs}ms");
            }
            
            if (metrics.MaxCalculationTimeMs > thresholdMs * 3)
            {
                Logger.Log($"TTSTimingCalculator: 性能警告 - 最大计算时间 {metrics.MaxCalculationTimeMs:F2}ms 过高");
            }
        }

        /// <summary>
        /// 获取推荐的配置基于历史数据
        /// </summary>
        /// <returns>推荐配置</returns>
        public static TimingConfig GetRecommendedConfig()
        {
            var metrics = GetPerformanceMetrics();
            var config = GetConfig();
            
            // 基于历史数据调整配置
            if (metrics.MinLimitApplications > metrics.CalculationCount * 0.5)
            {
                // 如果超过50%的计算都应用了最小限制，说明基础时间可能太小
                config.BaseTimePerCharacter = Math.Min(config.BaseTimePerCharacter + 20, 200);
                Logger.Log($"TTSTimingCalculator: 建议增加基础时间到 {config.BaseTimePerCharacter}ms/字符");
            }
            
            if (metrics.MaxLimitApplications > metrics.CalculationCount * 0.1)
            {
                // 如果超过10%的计算都应用了最大限制，说明可能需要调整最大时间
                config.MaxFallbackTimeMs = Math.Min(config.MaxFallbackTimeMs + 5000, 60000);
                Logger.Log($"TTSTimingCalculator: 建议增加最大时间到 {config.MaxFallbackTimeMs}ms");
            }
            
            return config;
        }
    }
}