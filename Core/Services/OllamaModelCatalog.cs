using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace VPetLLM.Core.Services
{
    /// <summary>推荐名单里的模型。体积是下载量，运行时占用比它略大。</summary>
    /// <param name="DescriptionKey">词条路径。从模型库来的模型只有英文说明，这里留空。</param>
    /// <param name="Tiered">参与按硬件自动推荐；其余只列出来让用户自己选。</param>
    public sealed record OllamaModelInfo(
        string Name,
        long SizeBytes,
        string DescriptionKey,
        string DefaultDescription,
        bool Tiered);

    /// <summary>这台电脑跑某个模型的结论。</summary>
    public enum OllamaFit
    {
        /// <summary>不知道体积，或模型不在这台电脑上跑。</summary>
        Unknown,
        Smooth,
        /// <summary>放得下，但要靠 CPU 算，回复会慢。</summary>
        Slow,
        TooLarge
    }

    public sealed record OllamaHardware(long MemoryBytes, long VideoMemoryBytes, string GpuName = "", int LogicalCores = 8, bool HasAvx2 = true)
    {
        // 不足 1 GB 的是核显或虚拟显卡，放不下什么模型，算上去反而会把结论带偏
        private const long MinimumVideoMemory = 1024L * 1024 * 1024;

        public static OllamaHardware Detect()
        {
            var (gpu, video) = DetectLargestGpu();
            return new(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, video, gpu, Environment.ProcessorCount, Avx2.IsSupported);
        }

        public long UsableVideoMemory => VideoMemoryBytes >= MinimumVideoMemory ? VideoMemoryBytes : 0;

        /// <summary>Ollama 的 CPU 后端靠 AVX2 提速，没有它或核心太少时只能算能跑。</summary>
        public bool HasCapableCpu => HasAvx2 && LogicalCores >= 4;

        /// <summary>
        /// 取显存最大的那张显卡：Ollama 优先把模型放进一张卡。核显的共享内存不算在这个值里，
        /// 所以只有核显的机器会落到内存判断上。
        /// </summary>
        private static (string Name, long Size) DetectLargestGpu()
        {
            const string adapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            var largest = ("", 0L);
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(adapters);
                foreach (var name in root?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    if (name.Length != 4 || !name.All(char.IsDigit)) continue;

                    using var adapter = root!.OpenSubKey(name);
                    var size = ParseMemorySize(adapter?.GetValue("HardwareInformation.qwMemorySize"));
                    if (size == 0) size = ParseMemorySize(adapter?.GetValue("HardwareInformation.MemorySize"));
                    if (size > largest.Item2) largest = (adapter?.GetValue("DriverDesc") as string ?? "", size);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Logger.Log($"读取显存失败: {ex.Message}");
            }
            return largest;
        }

        /// <summary>不同驱动写的类型不一样：QWORD、DWORD（按无符号算）或二进制都见过。</summary>
        public static long ParseMemorySize(object? value) => value switch
        {
            long l => l,
            int i => (uint)i,
            byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
            byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
            _ => 0
        };
    }

    /// <summary>
    /// 一键部署用的模型名单。体积取自 registry.ollama.ai 的 manifest（2026-11-01 实测）。
    /// 模型库本身有几百个，新手看不过来，这里只留中文对话表现好、覆盖各档配置的几个。
    /// </summary>
    public static class OllamaModelCatalog
    {
        // 模型和对话上下文都要进显存，留三成余量才能整个放下；放不下时部分层落到 CPU，速度骤降
        private const double VideoMemoryHeadroom = 1.3;

        // 内存里还要装系统和桌宠本身
        private const long SystemMemoryReserve = 6L * 1024 * 1024 * 1024;

        // 再大的模型纯 CPU 回复会慢到没法聊天
        private const long CpuModelLimit = 3L * 1024 * 1024 * 1024;

        // 能跑但慢时可以挤一挤，不用像流畅档留那么多给系统
        private const long SlowMemoryReserve = 4L * 1024 * 1024 * 1024;

        private const int MaxPopularCandidates = 12;

        public static IReadOnlyList<OllamaModelInfo> Models { get; } = new[]
        {
            new OllamaModelInfo("qwen2.5:3b", 1_929_911_945, "OllamaManager.Model.Entry",
                "入门之选，回复快，普通电脑也能跑。", Tiered: true),
            new OllamaModelInfo("qwen3:4b", 2_497_293_444, "OllamaManager.Model.Balanced",
                "比入门款聪明一些，需要 4 GB 显存或 16 GB 内存。", Tiered: true),
            new OllamaModelInfo("qwen3:8b", 5_225_387_677, "OllamaManager.Model.Quality",
                "回答质量更好，建议 8 GB 显存。", Tiered: true),
            new OllamaModelInfo("qwen3.5:9b", 6_550_825_373, "OllamaManager.Model.Best",
                "这份名单里最强，还能看图，建议 10 GB 显存。", Tiered: true),
            new OllamaModelInfo("gemma4:e4b", 6_583_656_264, "OllamaManager.Model.Gemma",
                "Google 出品，英文和多语言表现好。", Tiered: false),
            new OllamaModelInfo("deepseek-r1:7b", 4_683_074_953, "OllamaManager.Model.DeepSeek",
                "擅长推理，会先思考再回答，回复较慢。", Tiered: false),
            new OllamaModelInfo("llama3.2:3b", 2_019_392_628, "OllamaManager.Model.Llama",
                "Meta 出品的轻量模型，更适合英文对话。", Tiered: false)
        };

        /// <summary>这台电脑能流畅运行的最大模型；什么都放不下时退回最小的，能跑只是慢。</summary>
        public static OllamaModelInfo Recommend(OllamaHardware hardware)
        {
            var tiers = Models.Where(m => m.Tiered).OrderBy(m => m.SizeBytes).ToList();
            return tiers.LastOrDefault(m => Assess(m.SizeBytes, hardware) == OllamaFit.Smooth) ?? tiers[0];
        }

        /// <summary>整个放进显存，或者小到 CPU 也算得过来，才叫流畅。</summary>
        public static long SmoothLimit(OllamaHardware hardware)
        {
            var video = (long)(hardware.UsableVideoMemory / VideoMemoryHeadroom);
            var cpu = hardware.HasCapableCpu ? Math.Min(CpuModelLimit, hardware.MemoryBytes - SystemMemoryReserve) : 0;
            return Math.Max(0, Math.Max(video, cpu));
        }

        /// <summary>显存放不下的层 Ollama 会留在内存里用 CPU 算，所以两边加起来放得下就能跑。</summary>
        public static long UsableLimit(OllamaHardware hardware)
            => Math.Max(SmoothLimit(hardware), (long)((hardware.MemoryBytes + hardware.UsableVideoMemory - SlowMemoryReserve) / VideoMemoryHeadroom));

        /// <param name="hardware">模型要跑在别的电脑上时传 null，这里不知道那边的配置。</param>
        public static OllamaFit Assess(long sizeBytes, OllamaHardware? hardware)
        {
            if (hardware is null || sizeBytes <= 0) return OllamaFit.Unknown;
            if (sizeBytes <= SmoothLimit(hardware)) return OllamaFit.Smooth;
            return sizeBytes <= UsableLimit(hardware) ? OllamaFit.Slow : OllamaFit.TooLarge;
        }

        /// <summary>
        /// 一个家族里默认选哪个规格：能流畅运行的里面最大的。都不流畅时选最小的，
        /// 配置不明时也一样，先下最小的最不容易出事。一个体积都没查到时返回 null。
        /// </summary>
        public static string? ChooseTag(IReadOnlyDictionary<string, long> sizes, OllamaHardware? hardware)
        {
            var smooth = sizes.Where(s => Assess(s.Value, hardware) == OllamaFit.Smooth).ToList();
            return smooth.Count > 0
                ? smooth.MaxBy(s => s.Value).Key
                : sizes.Count > 0 ? sizes.MinBy(s => s.Value).Key : null;
        }

        /// <summary>
        /// 模型库里值得放进推荐页的家族，按热度排。名单里已有的家族不重复，写代码和识字（OCR）专用的
        /// 不适合陪聊，不支持工具调用的用不了插件。
        /// </summary>
        public static IReadOnlyList<OllamaLibraryModel> PopularCandidates(IEnumerable<OllamaLibraryModel> library)
        {
            var listed = Models.Select(m => m.Name.Split(':')[0]).ToHashSet();
            return library
                .Where(m => m.Capabilities.Contains("tools") && !m.Name.Contains("code") && !m.Name.Contains("ocr") && !listed.Contains(m.Name))
                .Take(MaxPopularCandidates)
                .ToList();
        }
    }
}
