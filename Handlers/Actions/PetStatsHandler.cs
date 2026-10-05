using LinePutScript;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Handlers.Actions
{
    /// <summary>
    /// 「翻旧账」工具：读取 VPet 的累积统计（GameSavesData.Statistics）。
    /// 命令格式：&lt;|pet_stats_begin|&gt; 想查什么 &lt;|pet_stats_end|&gt;
    ///
    /// 与状态提示词的区别：每轮注入的状态是**此刻**的数值（等级/心情/饱食度），
    /// 这里给的是**累计史**（被摸过多少次头、最常买什么、饿到过几次）。
    /// 累计史体量大且大多数对话用不上，所以做成按需调用，不占每轮 token。
    /// </summary>
    public class PetStatsHandler : IActionHandler
    {
        public string Keyword => "pet_stats";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;

        public string Description =>
            PromptHelper.Get("Handler_PetStats_Description",
                VPetLLM.Instance?.Settings?.PromptLanguage ?? "zh");

        public async Task Execute(string value, IMainWindow mainWindow)
        {
            await Task.CompletedTask;

            try
            {
                var stats = mainWindow?.GameSavesData?.Statistics;
                if (stats is null)
                {
                    Logger.Log("PetStatsHandler: 宿主未提供统计数据");
                    ResultAggregator.Enqueue("[统计查询] 宿主未提供统计数据。");
                    return;
                }

                var query = (value ?? "").Trim();
                Logger.Log($"PetStatsHandler: AI 查询统计: {(query.Length == 0 ? "(概览)" : query)}");

                var report = BuildReport(stats, query);
                ResultAggregator.Enqueue($"[统计查询]\n{report}\n[/统计查询]");
            }
            catch (Exception ex)
            {
                Logger.Log($"PetStatsHandler: Error: {ex.Message}");
                ResultAggregator.Enqueue($"[统计查询] 读取失败: {ex.Message}");
            }
        }

        private static string BuildReport(Statistics stats, string query)
        {
            var sb = new StringBuilder();
            var q = query.ToLowerInvariant();

            bool wantAll = q.Length == 0
                || q.Contains("全部") || q.Contains("所有") || q.Contains("all") || q.Contains("总");
            bool wantTouch = wantAll || q.Contains("摸") || q.Contains("互动") || q.Contains("touch") || q.Contains("interact");
            bool wantTime = wantAll || q.Contains("时间") || q.Contains("时长") || q.Contains("工作") || q.Contains("学习")
                                    || q.Contains("睡") || q.Contains("time") || q.Contains("work") || q.Contains("sleep");
            bool wantBuy = wantAll || q.Contains("买") || q.Contains("吃") || q.Contains("喝") || q.Contains("花")
                                    || q.Contains("消费") || q.Contains("buy") || q.Contains("food") || q.Contains("spend");
            bool wantNeglect = wantAll || q.Contains("饿") || q.Contains("渴") || q.Contains("病") || q.Contains("难过")
                                    || q.Contains("委屈") || q.Contains("hungry") || q.Contains("ill") || q.Contains("neglect");
            bool wantHabit = wantAll || wantTime || q.Contains("陪") || q.Contains("连续") || q.Contains("天")
                                    || q.Contains("最近") || q.Contains("习惯") || q.Contains("完成")
                                    || q.Contains("streak") || q.Contains("recent") || q.Contains("habit") || q.Contains("day");

            // 一个都没命中：按概览给，别让 AI 拿到空结果
            if (!wantTouch && !wantTime && !wantBuy && !wantNeglect && !wantHabit)
                wantTouch = wantTime = wantBuy = wantNeglect = wantHabit = true;

            if (wantTouch)
            {
                sb.AppendLine("互动：");
                sb.AppendLine($"  被摸头 {SafeInt(stats, "stat_touch_head")} 次，被摸身体 {SafeInt(stats, "stat_touch_body")} 次");
                sb.AppendLine($"  说话 {SafeInt(stats, "stat_say_times")} 次，菜单被打开 {SafeInt(stats, "stat_menu_pop")} 次");
            }

            if (wantTime)
            {
                sb.AppendLine("时长：");
                sb.AppendLine($"  工作 {Hours(SafeLong(stats, "stat_work_time"))}，学习 {Hours(SafeLong(stats, "stat_study_time"))}，睡觉 {Hours(SafeLong(stats, "stat_sleep_time"))}");
                sb.AppendLine($"  累计陪伴 {Hours(SafeLong(stats, "stat_total_time"))}，启动 {SafeInt(stats, "stat_open_times")} 次");
            }

            if (wantHabit)
                AppendHabits(sb, stats);

            if (wantBuy)
            {
                sb.AppendLine("消费：");
                sb.AppendLine($"  正餐 ${SafeDouble(stats, "stat_bb_meal"):F0}，零食 ${SafeDouble(stats, "stat_bb_snack"):F0}，" +
                              $"饮料 ${SafeDouble(stats, "stat_bb_drink"):F0}，药品 ${SafeDouble(stats, "stat_bb_drug"):F0}，" +
                              $"功能 ${SafeDouble(stats, "stat_bb_functional"):F0}，礼物 ${SafeDouble(stats, "stat_bb_gift"):F0}");
                sb.AppendLine($"  共购买 {SafeInt(stats, "stat_buytimes")} 次");

                var top = TopPurchases(stats, 5);
                if (top.Count > 0)
                    sb.AppendLine("  买得最多：" + string.Join("、", top.Select(t => $"{t.Name}×{t.Count}")));
            }

            if (wantNeglect)
            {
                sb.AppendLine("受过的委屈：");
                sb.AppendLine($"  饿到过 {SafeInt(stats, "stat_0_strengthfood")} 次，渴到过 {SafeInt(stats, "stat_0_strengthdrink")} 次");
                sb.AppendLine($"  心情跌到底 {SafeInt(stats, "stat_0_feel")} 次，没钱看病 {SafeInt(stats, "stat_ill_nomoney")} 次");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// stat_*_time / eval_*_seconds 都是秒：宿主每个逻辑帧加一次 LogicInterval（秒）。
        /// </summary>
        private static string Hours(long seconds)
        {
            if (seconds >= 3600) return $"{seconds / 3600.0:F1} 小时";
            return $"{seconds / 60} 分钟";
        }

        /// <summary>
        /// 宿主 11073 起新增的 eval_* 本地统计（活跃天数、连续天数、最近陪伴、完成率）。
        /// 旧宿主没有这些键，读出来全是 0 —— 整段跳过，别给 AI 一串"0 天"让它误以为被冷落。
        /// </summary>
        private static void AppendHabits(StringBuilder sb, Statistics stats)
        {
            var activeDays = SafeInt(stats, "eval_active_days");
            if (activeDays <= 0)
                return;

            sb.AppendLine("陪伴习惯：");
            sb.AppendLine($"  一起度过 {activeDays} 天，当前连续 {SafeInt(stats, "eval_active_streak")} 天，" +
                          $"最长连续 {SafeInt(stats, "eval_longest_active_streak")} 天");
            sb.AppendLine($"  最近 7 天陪伴 {Hours(SafeLong(stats, "eval_recent_7_days_seconds"))}，" +
                          $"最近 30 天 {Hours(SafeLong(stats, "eval_recent_30_days_seconds"))}，" +
                          $"最长一次 {Hours(SafeLong(stats, "eval_longest_session_seconds"))}");

            AppendCompletion(sb, stats, "work", "工作", "赚了", "eval_work_total_money", "$");
            AppendCompletion(sb, stats, "study", "学习", "获得经验", "eval_study_total_exp", "");
        }

        private static void AppendCompletion(StringBuilder sb, Statistics stats,
            string type, string label, string yieldVerb, string yieldKey, string unit)
        {
            var started = SafeInt(stats, $"eval_{type}_started");
            if (started <= 0)
                return;

            var completed = SafeInt(stats, $"eval_{type}_completed");
            sb.AppendLine($"  {label}开始 {started} 次、做完 {completed} 次（完成率 {SafeDouble(stats, $"eval_{type}_completion_rate"):P0}），" +
                          $"共{yieldVerb} {unit}{SafeDouble(stats, yieldKey):F0}");
        }

        /// <summary>
        /// 逐商品购买次数存在 "buy_商品名" 键里。
        /// </summary>
        private static List<(string Name, int Count)> TopPurchases(Statistics stats, int take)
        {
            var result = new List<(string, int)>();
            foreach (var kv in SnapshotData(stats))
            {
                if (kv.Key is null || !kv.Key.StartsWith("buy_", StringComparison.Ordinal))
                    continue;

                var count = SafeInt(stats, kv.Key);
                if (count > 0)
                    result.Add((kv.Key.Substring(4), count));
            }

            return result.OrderByDescending(x => x.Item2).Take(take).ToList();
        }

        /// <summary>
        /// Statistics.Data 是裸 SortedDictionary，VPet 会在购买和逻辑帧里并发写它
        /// （MainWindow 的 Statistics["buy_"+name]++ 等）。直接遍历会撞上"集合已修改"，
        /// 所以先取快照；快照本身也可能在拷贝途中被改，失败就重试，仍不行就放弃这一段
        /// —— 少一条"买得最多"远好过让整个工具抛异常。
        /// </summary>
        private static KeyValuePair<string, SetObject>[] SnapshotData(Statistics stats)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    return stats.Data.ToArray();
                }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
            }

            Logger.Log("PetStatsHandler: 统计字典正在被并发修改，跳过逐商品统计");
            return Array.Empty<KeyValuePair<string, SetObject>>();
        }

        private static int SafeInt(Statistics stats, string key)
        {
            try { return stats[(gint)key]; }
            catch { return 0; }
        }

        private static long SafeLong(Statistics stats, string key)
        {
            try { return stats[(gi64)key]; }
            catch { return 0; }
        }

        private static double SafeDouble(Statistics stats, string key)
        {
            try { return stats[(gdbe)key]; }
            catch { return 0; }
        }

        public Task Execute(int value, IMainWindow mainWindow) => Execute(value.ToString(), mainWindow);
        public Task Execute(IMainWindow mainWindow) => Execute("", mainWindow);
        public int GetAnimationDuration(string animationName) => 0;
    }
}
