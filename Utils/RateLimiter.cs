using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VPetLLM.Utils
{
    public static class RateLimiter
    {
                private static double _availableTpmTokens = 0;
                private static double _availableRpmTokens = 0;
        
                private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
                private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        
        private static void RefillTokens(int apiKeyCount, Setting.RateLimiterSettings settings)
        {
            double totalTpmLimit = settings.TpmLimitPerKey * apiKeyCount;
            double totalRpmLimit = settings.RpmLimitPerKey * apiKeyCount;
            double tokensPerSecond = totalTpmLimit / 60.0;
            double requestsPerSecond = totalRpmLimit / 60.0;

            var elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Restart();

            _availableTpmTokens = Math.Min(_availableTpmTokens + elapsedSeconds * tokensPerSecond, totalTpmLimit);
            _availableRpmTokens = Math.Min(_availableRpmTokens + elapsedSeconds * requestsPerSecond, totalRpmLimit);
        }

        public static async Task WaitForReady(int estimatedTokens, int apiKeyCount, Setting.RateLimiterSettings settings)
        {
            if (settings == null || !settings.IsEnabled) return;

            if (apiKeyCount <= 0) apiKeyCount = 1;

            double totalTpmLimit = settings.TpmLimitPerKey * apiKeyCount;
            double tokensPerSecond = totalTpmLimit / 60.0;
            double requestsPerSecond = (settings.RpmLimitPerKey * apiKeyCount) / 60.0;

            if (estimatedTokens > totalTpmLimit)
            {
                estimatedTokens = (int)totalTpmLimit;
            }

            while (true)
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);

                RefillTokens(apiKeyCount, settings);

                if (_availableTpmTokens >= estimatedTokens && _availableRpmTokens >= 1)
                {
                    _availableTpmTokens -= estimatedTokens;
                    _availableRpmTokens -= 1;
                    _semaphore.Release();
                    return;
                }

                double secondsToWaitTpm = (_availableTpmTokens < estimatedTokens) ? (estimatedTokens - _availableTpmTokens) / tokensPerSecond : 0;
                double secondsToWaitRpm = (_availableRpmTokens < 1) ? (1 - _availableRpmTokens) / requestsPerSecond : 0;
                var delaySeconds = Math.Max(secondsToWaitTpm, secondsToWaitRpm);

                _semaphore.Release();
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds + 0.05)).ConfigureAwait(false);
            }
        }
    }
}