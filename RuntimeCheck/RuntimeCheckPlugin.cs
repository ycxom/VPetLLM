using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VPet_Simulator.Windows.Interface;

namespace Ycxom.VPetMods.RuntimeCheck
{
    /// <summary>
    /// 旧版游戏本体检测：本 MOD 主体按 .NET 10 编译，比 .NET 10 旧的游戏本体加载不了它，
    /// 宿主只会弹一句「插件损坏，请联系 MOD 作者」—— 用户会去找作者报 bug，而真正该做的是更新游戏。
    ///
    /// <b>为什么是独立的小程序集</b>：主体在旧宿主上连类型都加载不出来，里面的代码一行都跑不到。
    /// 本程序集刻意按 net8 编译、不引用主体，新旧宿主都能加载，由它把原因说清楚。
    ///
    /// <b>为什么来得及拦住宿主的弹窗</b>：宿主先依次调各插件的 <see cref="MainPlugin.LoadPlugin"/>，
    /// 之后才遍历 MOD 报「插件损坏」（.NET 10 前后的宿主都是这个顺序）。所以在这里确认运行时确实
    /// 太旧之后，把<b>本 MOD</b>的失败标记清掉，换成说明原因的弹窗。新宿主上什么都不做 ——
    /// 主体真加载失败时宿主照常报错，不会被吞。
    ///
    /// <b>三份同源</b>：VPetLLM / VPetTTS / LLM表情包 各带一份，源文件逐字节相同（规范版本在
    /// VPetLLM/RuntimeCheck），只有程序集名不同（宿主按 DLL 文件名去重，必须各不相同）。
    /// MOD 名运行时从宿主的 MOD 列表里按目录认领，所以源码里不写死。
    /// </summary>
    public sealed class RuntimeCheckPlugin : MainPlugin
    {
        /// <summary>主体编译所用的 .NET 主版本。</summary>
        private const int RequiredRuntimeMajor = 10;

        /// <summary>几个 MOD 的检测共用一个弹窗；跨程序集只能经 AppDomain 共享状态。</summary>
        private const string PendingKey = "Ycxom.VPetMods.RuntimeCheck.Pending";

        public RuntimeCheckPlugin(IMainWindow mainwin) : base(mainwin)
        {
        }

        public override string PluginName => GetType().Assembly.GetName().Name ?? "RuntimeCheck";

        public override void LoadPlugin()
        {
            if (Environment.Version.Major >= RequiredRuntimeMajor)
                return;

            var mod = FindOwnMod();
            if (mod is not null)
                SuppressHostCorruptNotice(mod);

            Enqueue(mod?.Name ?? PluginName);
        }

        /// <summary>按本程序集所在目录认领自己的 MOD：<c>&lt;mod&gt;/plugin/本程序集.dll</c>。</summary>
        private IModInfo FindOwnMod()
        {
            try
            {
                var pluginDir = Path.GetDirectoryName(GetType().Assembly.Location);
                var modDir = pluginDir is null ? null : Directory.GetParent(pluginDir)?.FullName;
                if (modDir is null)
                    return null;

                return MW.ModInfo?.FirstOrDefault(m =>
                    m?.Path is not null &&
                    string.Equals(m.Path.FullName.TrimEnd('\\', '/'), modDir.TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 清掉宿主给本 MOD 记的加载失败，免得它再弹「插件损坏，请联系作者」。
        /// 这两个是宿主 CoreMOD 的公开字段，不在 IModInfo 接口里，只能反射；
        /// 宿主改了结构就退化成两个弹窗都出，不影响本提示。
        /// </summary>
        private static void SuppressHostCorruptNotice(IModInfo mod)
        {
            try
            {
                SetMember(mod, "SuccessLoad", true);
                SetMember(mod, "ErrorMessage", string.Empty);
            }
            catch
            {
                // 拦不住就让宿主照常报
            }
        }

        private static void SetMember(object target, string name, object value)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = target.GetType();
            var field = type.GetField(name, flags);
            if (field is not null && field.FieldType.IsInstanceOfType(value))
            {
                field.SetValue(target, value);
                return;
            }
            var property = type.GetProperty(name, flags);
            if (property is not null && property.CanWrite && property.PropertyType.IsInstanceOfType(value))
                property.SetValue(target, value);
        }

        private static void Enqueue(string modName)
        {
            // 各副本是不同的程序集，静态字段不共享；驻留字符串在进程内唯一，拿它当跨程序集的锁
            lock (string.Intern(PendingKey))
            {
                if (AppDomain.CurrentDomain.GetData(PendingKey) is List<string> pending)
                {
                    if (!pending.Contains(modName))
                        pending.Add(modName);
                    return;
                }

                AppDomain.CurrentDomain.SetData(PendingKey, new List<string> { modName });
            }

            // 第一个发现的负责弹窗。排到后台优先级：宿主这一轮 LoadPlugin 循环和它自己的 MOD 报错
            // 都在当前这次调度里，等它们走完，其余副本也都登记好了，合成一个弹窗。
            var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowNotice));
        }

        private static void ShowNotice()
        {
            List<string> mods;
            lock (string.Intern(PendingKey))
            {
                mods = (AppDomain.CurrentDomain.GetData(PendingKey) as List<string>)?.ToList() ?? new List<string>();
            }
            if (mods.Count == 0)
                return;

            var list = string.Join("\n", mods.Select(m => "    · " + m));
            var message =
                "以下 MOD 需要更新版本的「虚拟桌宠模拟器」才能运行，本次没有加载：\n" + list + "\n\n" +
                $"当前游戏运行在 .NET {Environment.Version}，这些 MOD 需要 .NET {RequiredRuntimeMajor} 或更高版本。\n" +
                "请在 Steam 中更新虚拟桌宠模拟器（库 → 虚拟桌宠模拟器 → 更新）后重新启动游戏。\n\n" +
                "The following MODs require a newer version of VPet-Simulator and were not loaded:\n" + list + "\n\n" +
                $"The game is running on .NET {Environment.Version}; these MODs need .NET {RequiredRuntimeMajor} or later.\n" +
                "Please update VPet-Simulator in Steam and restart the game.";
            const string title = "需要更新游戏 / Game update required";

            try
            {
                // 桌宠主窗口是置顶的，不指定 owner 弹窗会被压在它下面
                var owner = Application.Current?.MainWindow;
                if (owner is not null && owner.IsVisible)
                    MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // 提示本身失败不能再连累宿主启动
            }
        }
    }
}
