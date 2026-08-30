using System.Reflection;
using System.Windows;
using HarmonyLib;
using VPet_Simulator.Core;

namespace VPetLLM.Utils.UI
{
    /// <summary>
    /// 把宿主气泡右键菜单里的「关闭」也变成一个"停止回复"的入口。
    ///
    /// <b>为什么</b>：桌宠正念着一段长回复时，用户想让它闭嘴，最直觉的动作就是
    /// 右键气泡 → 关闭。但宿主的这个菜单项只调 <c>ForceClose()</c> 关掉那个框，
    /// 在途的 LLM 请求、排队的动作、还没播完的语音全都照跑不误 ——
    /// 气泡关掉了，下一段又自己冒出来，看起来就像"关不掉"。
    ///
    /// 装上之后它和另外两个入口（侧边栏状态按钮、输入框的中断按钮）语义完全一致，
    /// 共用 <see cref="VPetLLM.InterruptCurrentResponse"/>：取消请求、停语音、
    /// 在历史里留中断标记、状态归位。
    ///
    /// <b>只挂右键菜单，不挂双击</b>：双击气泡也会走 <c>ForceClose</c>，但双击桌宠
    /// 本身就是个高频误触动作，把它也变成"中断"会让人莫名其妙地被打断。
    /// 右键 → 关闭是明确的、要两步的意图表达。
    ///
    /// <b>独立于气泡独占开关</b>：和 <see cref="BubbleCopyGuard"/> 一样单独装。
    /// 「关闭即中断」是一条交互语义，跟"回复期间要不要吞掉别人的气泡"没有关系。
    /// </summary>
    public static class BubbleCloseInterrupt
    {
        private static readonly object _lock = new();
        private static Harmony? _harmony;

        private const string HarmonyId = "com.vpetllm.bubblecloseinterrupt";

        /// <summary>宿主那个关闭菜单的处理方法名。</summary>
        private const string TargetMethod = "MenuItemClose_Click";

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
                        Logger.Log($"BubbleCloseInterrupt: 宿主没有 MessageBar.{TargetMethod}(object, RoutedEventArgs)，" +
                                   $"右键关闭不会中断回复（宿主内部结构可能变了）");
                        return;
                    }

                    var harmony = new Harmony(HarmonyId);
                    harmony.Patch(
                        target,
                        prefix: new HarmonyMethod(typeof(BubbleCloseInterrupt)
                            .GetMethod(nameof(ClosePrefix), BindingFlags.NonPublic | BindingFlags.Static)!),
                        finalizer: new HarmonyMethod(typeof(BubbleCloseInterrupt)
                            .GetMethod(nameof(CloseFinalizer), BindingFlags.NonPublic | BindingFlags.Static)!));

                    _harmony = harmony;
                    Logger.Log("BubbleCloseInterrupt: 右键关闭气泡即中断回复，已安装");
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleCloseInterrupt: 安装失败，右键关闭不会中断回复: {ex.Message}");
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
                        Logger.Log("BubbleCloseInterrupt: 已卸载");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"BubbleCloseInterrupt: 卸载失败: {ex.Message}");
                }
                finally
                {
                    _harmony = null;
                }
            }
        }

        /// <summary>
        /// 前置补丁：先中断，再让宿主照常把气泡关掉。
        ///
        /// 不返回 false —— 关窗口这件事本来就该发生，我们只是在它前面加一件事。
        /// 中断动作自己会去停气泡和语音，两者叠加是幂等的。
        /// </summary>
        private static void ClosePrefix()
        {
            // 声明用户手势，让接下来的 ForceClose 一定能穿过气泡独占守卫。
            //
            // 这一步是必需的：守卫本来靠"栈上有没有 MessageBar 的方法"来识别用户手势，
            // 而我们恰恰把 MenuItemClose_Click 打了补丁 —— 打过补丁的方法在栈上是个
            // 动态方法，DeclaringType 不再是 MessageBar，走栈判断当场失效。
            // 结果就是：中断照做了，气泡却被守卫吞掉不关 —— 正是这个功能要消灭的"关不掉"。
            BubbleGuard.EnterUserGesture();

            try
            {
                // 没有在跑的会话就什么都不做。不然每次关个闲聊气泡都会往日志里
                // 写一行"当前没有可中断的会话"
                if (!Common.InterruptManager.HasActiveSession) return;

                if (VPetLLM.Instance?.InterruptCurrentResponse() == true)
                {
                    Logger.Log("BubbleCloseInterrupt: 用户关闭气泡，已中断本轮回复");
                }
            }
            catch (Exception ex)
            {
                // 中断失败绝不能挡住"把气泡关掉"这件用户真正要做的事
                Logger.Log($"BubbleCloseInterrupt: 中断失败，但仍照常关闭气泡: {ex.Message}");
            }
        }

        /// <summary>
        /// 收尾：手势作用域必须解除，否则这个线程之后所有的关闭都会被无条件放行。
        /// 用 finalizer 而不是在 prefix 里 try/finally —— 作用域要一直罩到
        /// <b>宿主原方法执行完</b>，而 ForceClose 就发生在那里面。
        /// </summary>
        private static void CloseFinalizer()
        {
            BubbleGuard.ExitUserGesture();
        }
    }
}
