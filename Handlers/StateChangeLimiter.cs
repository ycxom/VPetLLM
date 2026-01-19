using VPetLLM.Utils.System;

namespace VPetLLM.Handlers
{
    /// <summary>
    /// 状态变化限制器，用于限制LLM返回的状态修改值
    /// </summary>
    public static class StateChangeLimiter
    {
        /// <summary>
        /// 限制状态变化值，确保不超过当前值的20%且不会导致负数
        /// </summary>
        /// <param name="requestedChange">请求的变化值</param>
        /// <param name="currentValue">当前状态值</param>
        /// <returns>限制后的变化值</returns>
        public static int LimitStateChange(int requestedChange, double currentValue)
        {
            // 如果当前值为0或负数，不允许减少
            if (currentValue <= 0 && requestedChange < 0)
            {
                Logger.Log($"StateChangeLimiter: 当前值为 {currentValue}，拒绝负向变化 {requestedChange}");
                return 0;
            }

            // 计算20%的限制值
            double maxChange = Math.Abs(currentValue * 0.2);

            // 对于正向变化
            if (requestedChange > 0)
            {
                int limitedChange = (int)Math.Min(requestedChange, maxChange);
                if (limitedChange != requestedChange)
                {
                    Logger.Log($"StateChangeLimiter: 正向变化从 {requestedChange} 限制到 {limitedChange} (当前值: {currentValue}, 20%限制: {maxChange})");
                }
                return limitedChange;
            }
            // 对于负向变化
            else if (requestedChange < 0)
            {
                // 确保不会导致负数
                double minAllowedChange = -currentValue;

                // 应用20%限制
                double limitedByPercent = Math.Max(requestedChange, -maxChange);

                // 取两个限制中较小的（绝对值）
                int limitedChange = (int)Math.Max(limitedByPercent, minAllowedChange);

                if (limitedChange != requestedChange)
                {
                    Logger.Log($"StateChangeLimiter: 负向变化从 {requestedChange} 限制到 {limitedChange} (当前值: {currentValue}, 20%限制: {-maxChange}, 防负数限制: {minAllowedChange})");
                }
                return limitedChange;
            }

            // 变化值为0，直接返回
            return 0;
        }
    }
}
