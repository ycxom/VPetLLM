using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 物品搜索服务 - 使用 VPet 原生 Item API（通过反射保持向后兼容）
    /// 使用向量检索和模糊搜索优化购物逻辑
    /// 减少token使用，提高搜索准确性
    /// </summary>
    public class FoodSearchService
    {
        private readonly IMainWindow _mainWindow;
        private List<ItemInfo> _itemCache;
        private DateTime _lastCacheUpdate;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// 物品信息（使用原生 Item API）
        /// </summary>
        public class ItemInfo
        {
            public string Name { get; set; }
            public string TranslateName { get; set; }
            public string ItemType { get; set; }  // 直接使用 Item.ItemType
            public Food.FoodType? FoodType { get; set; }  // 食物子类型
            public double Price { get; set; }
            public Food OriginalFood { get; set; }  // 原始食物引用
            public object OriginalItem { get; set; }  // 原始物品引用（使用 object 保持兼容性）
            public bool CanUse { get; set; } = true;  // 直接使用 Item.CanUse
            public int Count { get; set; } = 1;  // 物品数量

            // 用于向量检索的特征
            public string[] Keywords { get; set; }
            public string TypeName { get; set; }
        }

        /// <summary>
        /// 物品栏物品信息（桌宠拥有的物品）- 保持向后兼容
        /// </summary>
        public class InventoryItemInfo
        {
            public string Name { get; set; }
            public string TranslateName { get; set; }
            public string ItemType { get; set; }
            public double Price { get; set; }
            public bool CanUse { get; set; } = true;
            public int Count { get; set; } = 1;
            public object OriginalItem { get; set; }  // 原始 Item 引用
        }

        // 向后兼容的别名
        public class FoodItem : ItemInfo { }

        public FoodSearchService(IMainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            RefreshCache();
        }

        /// <summary>
        /// 刷新物品缓存（直接使用原生 Item API）
        /// </summary>
        public void RefreshCache()
        {
            _itemCache = new List<ItemInfo>();

            // 添加所有 Food 物品（商店物品）
            foreach (var food in _mainWindow.Foods)
            {
                _itemCache.Add(new ItemInfo
                {
                    Name = food.Name,
                    TranslateName = food.TranslateName,
                    ItemType = food.ItemType,  // 直接访问 Item 基类属性
                    FoodType = food.Type,
                    Price = food.Price,
                    OriginalFood = food,
                    OriginalItem = food,
                    CanUse = food.CanUse,  // 直接访问 Item 基类属性
                    Count = food.Count,    // 直接访问 Item 基类属性
                    Keywords = ExtractKeywords(food.Name, food.TranslateName),
                    TypeName = GetTypeName(food.ItemType, food.Type)
                });
            }

            _lastCacheUpdate = DateTime.Now;
            Logger.Log($"FoodSearchService: 缓存已刷新，共 {_itemCache.Count} 个物品");
        }

        /// <summary>
        /// 获取物品栏中的物品列表（桌宠拥有的物品）- 直接使用 IMainWindow.Items
        /// </summary>
        public List<InventoryItemInfo> GetInventoryItems()
        {
            var inventoryItems = new List<InventoryItemInfo>();

            try
            {
                // 直接使用 IMainWindow.Items 属性（新版本 API）
                var items = _mainWindow.Items;
                if (items is null || !items.Any())
                {
                    return inventoryItems;
                }

                foreach (var item in items)
                {
                    // 检查 Visibility 属性（使用反射保持兼容性，因为该属性可能在某些版本中不存在）
                    var visibilityProperty = item.GetType().GetProperty("Visibility");
                    if (visibilityProperty is not null)
                    {
                        var visibilityValue = visibilityProperty.GetValue(item);
                        if (visibilityValue is bool visibility && !visibility)
                        {
                            continue; // 跳过不可见的物品
                        }
                    }

                    inventoryItems.Add(new InventoryItemInfo
                    {
                        Name = item.Name,
                        TranslateName = item.TranslateName,
                        ItemType = item.ItemType,
                        Price = item.Price,
                        Count = item.Count,
                        CanUse = item.CanUse,
                        OriginalItem = item
                    });
                }

                Logger.Log($"FoodSearchService: 获取物品栏成功，共 {inventoryItems.Count} 个物品");
            }
            catch (Exception ex)
            {
                Logger.Log($"FoodSearchService: 获取物品栏失败: {ex.Message}");
            }

            return inventoryItems;
        }

        /// <summary>
        /// 在物品栏中查找物品
        /// </summary>
        public InventoryItemInfo FindItemInInventory(string itemName)
        {
            var items = GetInventoryItems();
            return items.FirstOrDefault(i =>
                string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.TranslateName, itemName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 获取物品栏摘要（用于 AI 提示）- 使用反射保持向后兼容
        /// </summary>
        public string GetInventorySummary(string language = "zh")
        {
            var items = GetInventoryItems();
            if (!items.Any())
            {
                return language == "zh" ? "物品栏为空" : "Inventory is empty";
            }

            var itemsByType = items.GroupBy(i => i.ItemType).ToList();
            var parts = new List<string>();

            foreach (var group in itemsByType)
            {
                var typeName = language == "zh" ? GetTypeNameChinese(group.Key.ToLower()) : group.Key;
                var itemNames = string.Join(", ", group.Select(i =>
                    $"{(!string.IsNullOrEmpty(i.TranslateName) ? i.TranslateName : i.Name)}x{i.Count}").Take(5));
                var more = group.Count() > 5 ? $"...({group.Count() - 5} more)" : "";
                parts.Add($"{typeName}: {itemNames}{more}");
            }

            return string.Join("; ", parts);
        }

        /// <summary>
        /// 检查物品是否可用 - 使用反射保持向后兼容
        /// </summary>
        public bool CheckItemCanUse(string itemName)
        {
            CheckCacheExpiration();

            // 先检查商店物品缓存
            var item = _itemCache.FirstOrDefault(i =>
                string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(i.TranslateName, itemName, StringComparison.OrdinalIgnoreCase));

            if (item is not null)
            {
                return item.CanUse;
            }

            // 再检查物品栏
            var inventoryItem = FindItemInInventory(itemName);
            return inventoryItem?.CanUse ?? true;
        }

        /// <summary>
        /// 检查桌宠是否拥有某物品 - 使用反射保持向后兼容
        /// </summary>
        public bool HasItemInInventory(string itemName)
        {
            return FindItemInInventory(itemName) is not null;
        }

        /// <summary>
        /// 检查缓存是否过期
        /// </summary>
        private void CheckCacheExpiration()
        {
            if (DateTime.Now - _lastCacheUpdate > CacheExpiration)
            {
                RefreshCache();
            }
        }

        /// <summary>
        /// 提取关键词用于搜索
        /// </summary>
        private string[] ExtractKeywords(string name, string translateName)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(name))
            {
                keywords.Add(name.ToLower());
                // 分词（简单实现）
                foreach (var word in SplitWords(name))
                {
                    keywords.Add(word.ToLower());
                }
            }

            if (!string.IsNullOrEmpty(translateName))
            {
                keywords.Add(translateName.ToLower());
                foreach (var word in SplitWords(translateName))
                {
                    keywords.Add(word.ToLower());
                }
            }

            return keywords.ToArray();
        }

        /// <summary>
        /// 简单分词
        /// </summary>
        private IEnumerable<string> SplitWords(string text)
        {
            // 按空格、下划线、连字符分割
            return text.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// 获取类型名称（支持通用 Item 系统）
        /// </summary>
        private string GetTypeName(string itemType, Food.FoodType? foodType)
        {
            // 如果是 Food 类型且有具体的 FoodType
            if (itemType == "Food" && foodType.HasValue)
            {
                return foodType.Value switch
                {
                    Food.FoodType.Food => "food",
                    Food.FoodType.Drink => "drink",
                    Food.FoodType.Drug => "drug",
                    Food.FoodType.Gift => "gift",
                    _ => "general"
                };
            }

            // 通用 Item 类型
            return itemType?.ToLower() ?? "item";
        }

        /// <summary>
        /// 模糊搜索食物（主要方法）
        /// </summary>
        public Food SearchFood(string query)
        {
            CheckCacheExpiration();

            if (string.IsNullOrWhiteSpace(query))
                return null;

            query = query.Trim();
            Logger.Log($"FoodSearchService: 搜索物品 '{query}'");

            // 1. 精确匹配（最高优先级）
            var exactMatch = _itemCache.FirstOrDefault(f =>
                string.Equals(f.Name, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.TranslateName, query, StringComparison.OrdinalIgnoreCase));

            if (exactMatch is not null)
            {
                Logger.Log($"FoodSearchService: 精确匹配 '{exactMatch.Name}' (type: {exactMatch.ItemType})");
                return exactMatch.OriginalFood;
            }

            // 2. 关键词匹配
            var keywordMatches = _itemCache
                .Select(f => new
                {
                    Item = f,
                    Score = CalculateKeywordScore(query.ToLower(), f.Keywords)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            if (keywordMatches.Any())
            {
                var best = keywordMatches.First();
                Logger.Log($"FoodSearchService: 关键词匹配 '{best.Item.Name}' (得分: {best.Score}, type: {best.Item.ItemType})");
                return best.Item.OriginalFood;
            }

            // 3. 模糊匹配（编辑距离）
            var fuzzyMatches = _itemCache
                .Select(f => new
                {
                    Item = f,
                    Score = CalculateFuzzyScore(query, f.Name, f.TranslateName)
                })
                .Where(x => x.Score > 0.5) // 相似度阈值
                .OrderByDescending(x => x.Score)
                .ToList();

            if (fuzzyMatches.Any())
            {
                var best = fuzzyMatches.First();
                Logger.Log($"FoodSearchService: 模糊匹配 '{best.Item.Name}' (相似度: {best.Score:F2}, type: {best.Item.ItemType})");
                return best.Item.OriginalFood;
            }

            Logger.Log($"FoodSearchService: 未找到匹配的物品 '{query}'");
            return null;
        }

        /// <summary>
        /// 按类型搜索食物
        /// </summary>
        public Food SearchFoodByType(string query, string itemType)
        {
            CheckCacheExpiration();

            if (string.IsNullOrWhiteSpace(query))
                return null;

            query = query.Trim();
            var filteredCache = _itemCache.Where(i =>
                string.Equals(i.ItemType, itemType, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!filteredCache.Any())
            {
                Logger.Log($"FoodSearchService: 没有找到类型为 '{itemType}' 的物品");
                return null;
            }

            // 精确匹配
            var exactMatch = filteredCache.FirstOrDefault(f =>
                string.Equals(f.Name, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.TranslateName, query, StringComparison.OrdinalIgnoreCase));

            if (exactMatch is not null)
            {
                return exactMatch.OriginalFood;
            }

            // 模糊匹配
            var fuzzyMatches = filteredCache
                .Select(f => new
                {
                    Item = f,
                    Score = CalculateFuzzyScore(query, f.Name, f.TranslateName)
                })
                .Where(x => x.Score > 0.5)
                .OrderByDescending(x => x.Score)
                .ToList();

            return fuzzyMatches.FirstOrDefault()?.Item.OriginalFood;
        }

        /// <summary>
        /// 计算关键词匹配得分
        /// </summary>
        private int CalculateKeywordScore(string query, string[] keywords)
        {
            int score = 0;
            var queryWords = SplitWords(query).Select(w => w.ToLower()).ToArray();

            foreach (var keyword in keywords)
            {
                foreach (var queryWord in queryWords)
                {
                    if (keyword.Contains(queryWord))
                    {
                        score += queryWord.Length; // 匹配长度越长得分越高
                    }
                    if (queryWord.Contains(keyword))
                    {
                        score += keyword.Length;
                    }
                }
            }

            return score;
        }

        /// <summary>
        /// 计算模糊匹配得分（基于编辑距离）
        /// </summary>
        private double CalculateFuzzyScore(string query, string name, string translateName)
        {
            var score1 = CalculateSimilarity(query, name);
            var score2 = string.IsNullOrEmpty(translateName) ? 0 : CalculateSimilarity(query, translateName);
            return Math.Max(score1, score2);
        }

        /// <summary>
        /// 计算字符串相似度（Levenshtein距离）
        /// </summary>
        private double CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
                return 0;

            s1 = s1.ToLower();
            s2 = s2.ToLower();

            int distance = LevenshteinDistance(s1, s2);
            int maxLength = Math.Max(s1.Length, s2.Length);

            return 1.0 - (double)distance / maxLength;
        }

        /// <summary>
        /// Levenshtein编辑距离算法
        /// </summary>
        private int LevenshteinDistance(string s1, string s2)
        {
            int[,] d = new int[s1.Length + 1, s2.Length + 1];

            for (int i = 0; i <= s1.Length; i++)
                d[i, 0] = i;
            for (int j = 0; j <= s2.Length; j++)
                d[0, j] = j;

            for (int i = 1; i <= s1.Length; i++)
            {
                for (int j = 1; j <= s2.Length; j++)
                {
                    int cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[s1.Length, s2.Length];
        }

        /// <summary>
        /// 按类型获取物品列表（用于生成简化的提示）
        /// </summary>
        public Dictionary<string, List<string>> GetFoodsByType()
        {
            return GetItemsByType();
        }

        /// <summary>
        /// 按类型获取物品列表（通用方法）
        /// </summary>
        public Dictionary<string, List<string>> GetItemsByType()
        {
            CheckCacheExpiration();

            return _itemCache
                .GroupBy(f => f.TypeName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(f => f.Name).ToList()
                );
        }

        /// <summary>
        /// 获取简化的食物列表提示（减少token）
        /// </summary>
        public string GetSimplifiedFoodListPrompt(string language = "zh")
        {
            return GetSimplifiedItemListPrompt(language);
        }

        /// <summary>
        /// 获取简化的物品列表提示（通用方法）
        /// </summary>
        public string GetSimplifiedItemListPrompt(string language = "zh")
        {
            var itemsByType = GetItemsByType();
            var parts = new List<string>();

            foreach (var kvp in itemsByType)
            {
                var typeName = language == "zh" ? GetTypeNameChinese(kvp.Key) : kvp.Key;
                var items = string.Join(",", kvp.Value.Take(5)); // 每类只显示前5个
                var more = kvp.Value.Count > 5 ? $"...({kvp.Value.Count - 5} more)" : "";
                parts.Add($"{typeName}:{items}{more}");
            }

            return string.Join("; ", parts);
        }

        /// <summary>
        /// 获取中文类型名称
        /// </summary>
        private string GetTypeNameChinese(string typeKey)
        {
            return typeKey switch
            {
                "food" => "食物",
                "drink" => "饮料",
                "drug" => "药品",
                "gift" => "礼物",
                "tool" => "道具",
                "toy" => "玩具",
                "item" => "物品",
                _ => "其他"
            };
        }

        /// <summary>
        /// 获取所有食物数量
        /// </summary>
        public int GetTotalFoodCount()
        {
            return GetTotalItemCount();
        }

        /// <summary>
        /// 获取所有物品数量
        /// </summary>
        public int GetTotalItemCount()
        {
            CheckCacheExpiration();
            return _itemCache.Count;
        }

        /// <summary>
        /// 获取指定类型的物品数量
        /// </summary>
        public int GetItemCountByType(string itemType)
        {
            CheckCacheExpiration();
            return _itemCache.Count(i =>
                string.Equals(i.ItemType, itemType, StringComparison.OrdinalIgnoreCase));
        }
    }
}
