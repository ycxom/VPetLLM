namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// 三个气泡补丁（<see cref="BubbleGuard"/> / <see cref="BubbleCopyGuard"/> /
    /// <see cref="BubbleCloseInterrupt"/>）碰 Harmony 的唯一入口。
    ///
    /// <b>为什么需要</b>：宿主 <c>CoreMOD</c> 按<b>文件名</b>给插件 DLL 去重
    /// （<c>LoadedDLL</c> 是全进程静态集合），几个 MOD 各带一份 0Harmony.dll 时，进程里只有
    /// 最先被扫到的那份 —— 本地 MOD 按目录名排序，<c>1102_VPetTTS</c> 先于 <c>3000_VPetLLM</c>。
    /// 那份比我们编译时引用的旧，方法体里出现 Harmony 类型的方法在 <b>JIT 时</b>就抛
    /// <see cref="FileLoadException"/>，而且抛在调用方栈帧里，方法自己的 try 接不住。
    /// 实测：<c>BeginReply</c> 里顺手的一次 <c>Install</c> 就能让每条回复都失败。
    ///
    /// <b>规矩</b>：碰 Harmony 的代码（包括读写 <c>Harmony</c> 类型的字段）一律写进
    /// <c>*Core</c> 方法，只经 <see cref="Run"/> 调用；公开方法体里不出现任何 Harmony 类型。
    /// 绑不上就整进程停用这几个补丁、只报一次，插件其余部分照常工作。
    /// </summary>
    internal static class HarmonyGate
    {
        private static volatile bool _unavailable;
        private static int _reported;

        /// <summary>进程里的 0Harmony 和我们引用的对不上，补丁全部停用。</summary>
        public static bool Unavailable => _unavailable;

        public static void Run(string owner, Action harmonyWork)
        {
            if (_unavailable) return;

            try
            {
                harmonyWork();
            }
            catch (Exception ex) when (IsHarmonyBindingFailure(ex))
            {
                _unavailable = true;
                if (Interlocked.Exchange(ref _reported, 1) == 0)
                {
                    Logger.Log($"{owner}: {DescribeMismatch()}；气泡独占、气泡复制保护、右键关闭中断全部停用: {ex.Message}");
                }
            }
        }

        private static bool IsHarmonyBindingFailure(Exception ex) => ex switch
        {
            FileLoadException f => IsHarmonyAssembly(f.FileName),
            FileNotFoundException f => IsHarmonyAssembly(f.FileName),
            TypeLoadException t => t.TypeName?.StartsWith("HarmonyLib.", StringComparison.Ordinal) == true,
            // 旧版里缺我们用到的 API
            MissingMemberException m => m.Message.Contains("HarmonyLib", StringComparison.Ordinal),
            _ => false
        };

        private static bool IsHarmonyAssembly(string? name)
            => name?.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase) == true;

        private static string DescribeMismatch()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "0Harmony");
            var required = typeof(HarmonyGate).Assembly.GetReferencedAssemblies()
                .FirstOrDefault(n => n.Name == "0Harmony")?.Version;

            if (loaded is null)
                return $"加载不到本插件需要的 0Harmony {required}";

            return $"进程里的 0Harmony 是 {loaded.GetName().Version}（{loaded.Location}），" +
                   $"与本插件需要的 {required} 不兼容。多半是另一个 MOD 自带了旧版 0Harmony 且先于本插件加载，" +
                   $"更新那个 MOD 即可";
        }
    }
}
