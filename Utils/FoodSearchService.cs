using System;
using System.Collections.Generic;
using System.Linq;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 食物搜索服务 - 使用向量检索和模糊搜索优化购物逻辑
    /// 减少token使用，提高搜索准确性
    /// </summary>
    public class FoodSearchService
    {
        private readonly IMainWindow _mainWindow;
        private List<FoodItem> _foodCache;
        private DateTime _lastCacheUpdate;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

        public class FoodItem
        {
            public string Name { get; set; }
            public string TranslateName { get; set; }
            public Food.FoodType Type { get; set; }
            public double Price { get; set; }
            public Food OriginalFood { get; set; }
            
            // 用于向量检索的特征
            public string[] Keywords { get; set; }
            public string TypeName { get; set; }
        }

        public FoodSearchService(IMainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            RefreshCache();
        }

        /// <summary>
        /// 刷新食物缓存
        /// </summary>
        public void RefreshCache()
        {
            _foodCache = _mainWindow.Foods.Select(f => new FoodItem
            {
                Name = f.Name,
                TranslateName = f.TranslateName,
                Type = f.Type,
                Price = f.Price,
                OriginalFood = f,
                Keywords = ExtractKeywords(f.Name, f.TranslateName),
                TypeName = GetTypeName(f.Type)
            }).ToList();
            
            _lastCacheUpdate = DateTime.Now;
            Logger.Log($"FoodSearchService: 缓存已刷新，共 {_foodCache.Count} 个物品");
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
        /// 获取类型名称
        /// </summary>
        private string GetTypeName(Food.FoodType type)
        {
            return type switch
            {
                Food.FoodType.Food => "food",
                Food.FoodType.Drink => "drink",
                Food.FoodType.Drug => "drug",
                Food.FoodType.Gift => "gift",
                _ => "general"
            };
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
            var exactMatch = _foodCache.FirstOrDefault(f =>
                string.Equals(f.Name, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.TranslateName, query, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                Logger.Log($"FoodSearchService: 精确匹配 '{exactMatch.Name}'");
                return exactMatch.OriginalFood;
            }

            // 2. 关键词匹配
            var keywordMatches = _foodCache
                .Select(f => new
                {
                    Food = f,
                    Score = CalculateKeywordScore(query.ToLower(), f.Keywords)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            if (keywordMatches.Any())
            {
                var best = keywordMatches.First();
                Logger.Log($"FoodSearchService: 关键词匹配 '{best.Food.Name}' (得分: {best.Score})");
                return best.Food.OriginalFood;
            }

            // 3. 模糊匹配（编辑距离）
            var fuzzyMatches = _foodCache
                .Select(f => new
                {
                    Food = f,
                    Score = CalculateFuzzyScore(query, f.Name, f.TranslateName)
                })
                .Where(x => x.Score > 0.5) // 相似度阈值
                .OrderByDescending(x => x.Score)
                .ToList();

            if (fuzzyMatches.Any())
            {
                var best = fuzzyMatches.First();
                Logger.Log($"FoodSearchService: 模糊匹配 '{best.Food.Name}' (相似度: {best.Score:F2})");
                return best.Food.OriginalFood;
            }

            Logger.Log($"FoodSearchService: 未找到匹配的物品 '{query}'");
            return null;
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
        /// 按类型获取食物列表（用于生成简化的提示）
        /// </summary>
        public Dictionary<string, List<string>> GetFoodsByType()
        {
            CheckCacheExpiration();
            
            return _foodCache
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
            var foodsByType = GetFoodsByType();
            var parts = new List<string>();

            foreach (var kvp in foodsByType)
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
                _ => "其他"
            };
        }

        /// <summary>
        /// 获取所有食物数量
        /// </summary>
        public int GetTotalFoodCount()
        {
            CheckCacheExpiration();
            return _foodCache.Count;
        }
    }
}
