using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// 决定样貌描述（Prompt.json 的 Appearance）要不要注入系统提示词。
    /// Appearance 写死的是默认皮肤 vup 的形象，换了皮肤就对不上了，
    /// 所以每次发现当前皮肤和上次判断时的不一样，就按皮肤重新给 EnableAppearance 定一次默认值：
    /// 默认皮肤开、其他皮肤关。之后由用户在设置里勾选覆盖，直到下次换皮肤。
    /// </summary>
    public static class AppearancePolicy
    {
        /// <summary>VPet 自带的默认桌宠皮肤名。</summary>
        public const string DefaultPetGraph = "vup";

        /// <summary>
        /// 取当前实际生效的桌宠皮肤名。取不到时返回 ""。
        /// </summary>
        public static string GetCurrentPetGraph(IMainWindow mainWindow)
        {
            try
            {
                var graph = mainWindow?.Set?.PetGraph;
                var pets = mainWindow?.Pets;

                // VPet 在 Set.PetGraph 找不到对应皮肤时会回退到列表第 0 个，这里跟随同样的回退
                if (pets is not null && pets.Count > 0 &&
                    (string.IsNullOrWhiteSpace(graph) ||
                     !pets.Any(p => string.Equals(p.Name, graph, StringComparison.OrdinalIgnoreCase))))
                {
                    graph = pets[0].Name;
                }

                return graph ?? "";
            }
            catch (Exception ex)
            {
                Logger.Log($"AppearancePolicy: 读取桌宠皮肤失败: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 皮肤变了就重置 EnableAppearance（默认皮肤 true / 其他皮肤 false）。
        /// 返回是否真的改动了设置，调用方据此决定要不要 Save。
        /// </summary>
        public static bool SyncWithPetGraph(Setting settings, IMainWindow mainWindow)
        {
            if (settings is null) return false;

            var current = GetCurrentPetGraph(mainWindow);

            // 读不到皮肤名就什么都别做，免得把用户手动勾的开关冲掉
            if (string.IsNullOrEmpty(current)) return false;

            if (string.Equals(current, settings.AppearancePetGraph, StringComparison.OrdinalIgnoreCase))
                return false;

            settings.AppearancePetGraph = current;
            settings.EnableAppearance = string.Equals(current, DefaultPetGraph, StringComparison.OrdinalIgnoreCase);

            Logger.Log($"AppearancePolicy: 桌宠皮肤变为 {current}，样貌提示词已默认{(settings.EnableAppearance ? "开启" : "关闭")}");
            return true;
        }
    }
}
