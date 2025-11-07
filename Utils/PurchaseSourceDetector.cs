using System;
using System.Linq;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 购买来源检测器 - 基于VPet原生TakeItemHandle接口
    /// </summary>
    public static class PurchaseSourceDetector
    {
        /// <summary>
        /// 购买来源类型枚举
        /// </summary>
        public enum PurchaseSource
        {
            UserManual,        // 用户手动购买
            VPetAuto,          // VPet自动购物
            VPetLLM,           // VPetLLM主动购买
            FriendGift,        // 朋友赠送
            FriendBuy,         // 朋友代购
            Unknown            // 未知来源
        }

        /// <summary>
        /// 根据来源字符串检测购买来源类型
        /// </summary>
        /// <param name="from">来源标记字符串（来自TakeItemHandle事件）</param>
        /// <returns>购买来源类型</returns>
        public static PurchaseSource DetectPurchaseSource(string from)
        {
            if (string.IsNullOrEmpty(from))
            {
                return PurchaseSource.Unknown;
            }

            string source = from.ToLower();

            // VPet系统自动购买
            if (IsVPetAutoSource(source))
            {
                return PurchaseSource.VPetAuto;
            }

            // VPetLLM主动购买
            if (source == "vpetllm")
            {
                return PurchaseSource.VPetLLM;
            }

            // 朋友赠送
            if (source == "friendgift" || source == "friend")
            {
                return PurchaseSource.FriendGift;
            }

            // 朋友代购
            if (source.StartsWith("friendbuy"))
            {
                return PurchaseSource.FriendBuy;
            }

            // 其他来源视为用户手动购买
            return PurchaseSource.UserManual;
        }

        /// <summary>
        /// 判断是否为VPet系统自动购买来源
        /// </summary>
        /// <param name="from">来源标记字符串</param>
        /// <returns>如果是自动购买返回true</returns>
        public static bool IsVPetAutoSource(string from)
        {
            if (string.IsNullOrEmpty(from))
            {
                return false;
            }

            string source = from.ToLower();
            var autoPurchaseSources = new[] { "autofood", "autodrink", "autofeel" };
            return autoPurchaseSources.Contains(source);
        }

        /// <summary>
        /// 判断是否应该触发AI反馈
        /// </summary>
        /// <param name="from">来源标记字符串</param>
        /// <returns>如果应该触发AI反馈返回true</returns>
        public static bool ShouldTriggerAIFeedback(string from)
        {
            var source = DetectPurchaseSource(from);
            
            // VPet自动购买和VPetLLM自己的购买不触发AI反馈
            return source != PurchaseSource.VPetAuto && 
                   source != PurchaseSource.VPetLLM;
        }

        /// <summary>
        /// 获取购买来源的描述
        /// </summary>
        public static string GetPurchaseSourceDescription(PurchaseSource source)
        {
            return source switch
            {
                PurchaseSource.UserManual => "用户手动购买",
                PurchaseSource.VPetAuto => "VPet自动购物",
                PurchaseSource.VPetLLM => "VPetLLM智能购买",
                PurchaseSource.FriendGift => "朋友赠送",
                PurchaseSource.FriendBuy => "朋友代购",
                PurchaseSource.Unknown => "未知来源",
                _ => "未知来源"
            };
        }

        /// <summary>
        /// 获取购买来源的详细描述（包含来源字符串）
        /// </summary>
        public static string GetDetailedDescription(string from)
        {
            var source = DetectPurchaseSource(from);
            var description = GetPurchaseSourceDescription(source);
            return $"{description} (from: {from})";
        }
    }
}
