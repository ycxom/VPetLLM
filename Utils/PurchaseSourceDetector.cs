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
            Inventory,         // 物品栏使用
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

            // 物品栏使用（支持 "inventory" 和 "item" 两种来源标记）
            // "item" 是 VPet 新版本中从物品栏使用食物时的来源标记
            if (source == "inventory" || source == "item")
            {
                return PurchaseSource.Inventory;
            }

            // 用户手动购买（明确识别"betterbuy"）
            if (source == "betterbuy")
            {
                return PurchaseSource.UserManual;
            }

            // 其他未知来源（保守策略：不触发AI反馈）
            return PurchaseSource.Unknown;
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
            
            // 已知的自动购买来源
            var autoPurchaseSources = new[] { "autofood", "autodrink", "autofeel" };
            if (autoPurchaseSources.Contains(source))
            {
                return true;
            }
            
            // 兼容未来可能添加的自动购买来源（以"auto"开头的都视为自动购买）
            if (source.StartsWith("auto"))
            {
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// 判断是否应该触发AI反馈
        /// </summary>
        /// <param name="from">来源标记字符串</param>
        /// <returns>如果应该触发AI反馈返回true</returns>
        public static bool ShouldTriggerAIFeedback(string from)
        {
            var source = DetectPurchaseSource(from);
            
            // 明确列出应该触发AI反馈的来源
            return source == PurchaseSource.UserManual ||
                   source == PurchaseSource.FriendGift ||
                   source == PurchaseSource.FriendBuy ||
                   source == PurchaseSource.Inventory;
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
                PurchaseSource.Inventory => "物品栏使用",
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
