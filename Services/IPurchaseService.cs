using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Services
{
    /// <summary>
    /// 购买物品信息 - 支持通用物品系统
    /// </summary>
    public class PurchaseItem
    {
        /// <summary>
        /// 物品名称
        /// </summary>
        public string Name { get; set; } = "";
        
        /// <summary>
        /// 物品类型（通用字符串类型，支持 Item、Food、Tool、Toy 等）
        /// </summary>
        public string ItemType { get; set; } = "Food";
        
        /// <summary>
        /// 食物类型（仅当 ItemType 为 Food 时有效，保持向后兼容）
        /// </summary>
        public Food.FoodType? FoodType { get; set; }
        
        /// <summary>
        /// 物品价格
        /// </summary>
        public double Price { get; set; }
        
        /// <summary>
        /// 购买时间
        /// </summary>
        public DateTime PurchaseTime { get; set; }
        
        /// <summary>
        /// 购买数量
        /// </summary>
        public int Quantity { get; set; } = 1;
        
        /// <summary>
        /// 物品描述
        /// </summary>
        public string Description { get; set; } = "";
        
        /// <summary>
        /// 原始食物引用（用于向后兼容）
        /// </summary>
        public Food? OriginalFood { get; set; }
    }

    /// <summary>
    /// 购买事件服务接口 - 支持通用物品系统
    /// </summary>
    public interface IPurchaseService : IDisposable
    {
        /// <summary>
        /// 处理购买事件（Food 版本）
        /// </summary>
        /// <param name="food">购买的食物</param>
        /// <param name="count">购买数量</param>
        /// <param name="source">购买来源</param>
        void HandlePurchase(Food food, int count, string source);

        /// <summary>
        /// 处理待处理的购买批次
        /// </summary>
        void ProcessPendingPurchases();

        /// <summary>
        /// 获取待处理的购买数量
        /// </summary>
        int PendingPurchaseCount { get; }

        /// <summary>
        /// 购买批次处理完成事件
        /// </summary>
        event EventHandler<List<PurchaseItem>>? BatchProcessed;
    }
}
