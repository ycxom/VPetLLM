using System;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 简化版购买来源检测器 - 直接使用VPet原生接口
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
        /// 简化版购买来源检测 - 直接使用VPet原生接口
        /// </summary>
        public static PurchaseSource DetectPurchaseSource(IMainWindow mainWindow)
        {
            // 简化版本：默认返回用户手动购买
            // 实际来源通过VPet原生TakeItemHandle接口处理
            return PurchaseSource.UserManual;
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
    }
}
