using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HarmonyLib;
using VPet_Simulator.Core;

namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// 给宿主气泡右键菜单里的「复制」加一层保险。
    ///
    /// <b>问题</b>：宿主的 <c>MessageBar.MenuItemCopy_Click</c> 一行没有保护 ——
    /// <code>private void MenuItemCopy_Click(...) { Clipboard.SetText(TText.Text); }</code>
    /// 而 Windows 剪贴板是全局独占的：浏览器、远程桌面、剪贴板管理器只要正占着它，
    /// 这一行就抛 <c>COMException 0x800401D0 (CLIPBRD_E_CANT_OPEN)</c>。异常从
    /// <c>Clipboard.Flush()</c> 一路冒到宿主的全局异常处理，用户看到的是
    /// 「游戏发生错误,可能是游戏或者MOD导致的」—— 点一下复制就劝退。
    ///
    /// <b>为什么由我们来补</b>：这个坑在宿主里，改不了它的二进制；但我们本来就已经
    /// 用 Harmony 补着同一个 <see cref="MessageBar"/>（见 <see cref="BubbleGuard"/>），
    /// 顺手把这条路铺平的成本几乎为零，而收益是用户不会因为复制一句话丢掉整个存档进度。
    ///
    /// <b>和 <see cref="BubbleGuard"/> 分开装</b>：气泡独占是可以关掉的功能，关掉时
    /// 一个字节都不该动宿主；而"别崩"不是功能，跟那个开关无关，所以独立成一个补丁。
    /// </summary>
    public static class BubbleCopyGuard
    {
        private static readonly object _lock = new();
        private static Harmony? _harmony;

        private const string HarmonyId = "com.vpetllm.bubblecopyguard";

        /// <summary>宿主那个复制菜单的处理方法名。</summary>
        private const string TargetMethod = "MenuItemCopy_Click";

        /// <summary>装上补丁。重复调用是安全的（幂等）。</summary>
        public static void Install()
        {
            lock (_lock)
            {
                if (_harmony is not null) return;

                try
                {
                    // 私有实例方法，签名是 (object sender, RoutedEventArgs e)
                    var target = typeof(MessageBar).GetMethod(
                        TargetMethod,
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        binder: null,
                        types: new[] { typeof(object), typeof(RoutedEventArgs) },
                        modifiers: null);

                    if (target is null)
                    {
                        // 宿主改了内部结构。出声，别静默地少一层保护
                        Logger.Log($"BubbleCopyGuard: 宿主没有 MessageBar.{TargetMethod}(object, RoutedEventArgs)，" +
                                   $"气泡复制仍走宿主原路（剪贴板被占用时可能崩溃）");
                        return;
                    }

                    var harmony = new Harmony(HarmonyId);
                    harmony.Patch(
                        target,
                        prefix: new HarmonyMethod(typeof(BubbleCopyGuard)
                            .GetMethod(nameof(CopyPrefix), BindingFlags.NonPublic | BindingFlags.Static)!),
                        finalizer: new HarmonyMethod(typeof(BubbleCopyGuard)
                            .GetMethod(nameof(CopyFinalizer), BindingFlags.NonPublic | BindingFlags.Static)!));

                    _harmony = harmony;
                    Logger.Log("BubbleCopyGuard: 气泡复制保护已安装");
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleCopyGuard: 安装失败，气泡复制不受保护: {ex.Message}");
                    _harmony = null;
                }
            }
        }

        /// <summary>
        /// 摘掉补丁。插件卸载时必须调用 —— 补丁里的委托指向本程序集。
        /// </summary>
        public static void Uninstall()
        {
            lock (_lock)
            {
                try
                {
                    if (_harmony is not null)
                    {
                        _harmony.UnpatchAll(HarmonyId);
                        Logger.Log("BubbleCopyGuard: 气泡复制保护已卸载");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleCopyGuard: 卸载失败: {ex.Message}");
                }
                finally
                {
                    _harmony = null;
                }
            }
        }

        /// <summary>
        /// 前置补丁：自己用带退路的方式把文本放上剪贴板，然后跳过宿主原方法。
        ///
        /// 返回 false = 不执行原方法。读不到气泡文本时返回 true 放行 ——
        /// 那种情况下宁可让宿主自己去试（还有 <see cref="CopyFinalizer"/> 兜底），
        /// 也不能让"复制"变成一个什么都不做的菜单项。
        /// </summary>
        private static bool CopyPrefix(MessageBar __instance)
        {
            try
            {
                var text = ReadBubbleText(__instance);
                if (text is null) return true;

                Utils.Common.ClipboardHelper.TrySetText(text);
                return false;
            }
            catch (Exception ex)
            {
                // 补丁自己出问题不该改变宿主行为，放行给原方法
                Logger.Log($"BubbleCopyGuard: 复制前置补丁异常，交回宿主处理: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 后置兜底：万一走到了宿主原方法（前置放行）并且它抛了异常，在这里咽掉。
        /// Harmony 的 finalizer 返回 null 即表示"异常已处理"。
        /// </summary>
        private static Exception? CopyFinalizer(Exception? __exception)
        {
            if (__exception is null) return null;

            Logger.Log($"BubbleCopyGuard: 宿主复制气泡时抛出 {__exception.GetType().Name}: " +
                       $"{__exception.Message}（已拦下，不再上抛给宿主的全局异常处理）");
            return null;
        }

        /// <summary>
        /// 读气泡正文。<c>TText</c> 是 XAML 生成的字段，跨程序集是 internal，
        /// 所以走 <see cref="MessageBarHelper"/> 已有的反射缓存；它拿不到时再退回
        /// <see cref="FrameworkElement.FindName"/>（公开 API，走 NameScope）。
        /// </summary>
        private static string? ReadBubbleText(MessageBar msgBar)
        {
            if (msgBar is null) return null;

            var textBox = MessageBarHelper.GetFieldValue<TextBox>(msgBar, "TText");
            textBox ??= msgBar.FindName("TText") as TextBox;

            return textBox?.Text;
        }
    }
}
