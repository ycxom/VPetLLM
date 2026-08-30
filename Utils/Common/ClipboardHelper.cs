using System.Runtime.InteropServices;
using System.Windows;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 写剪贴板的安全封装：失败就失败，绝不把异常抛回 WPF 的事件管线。
    ///
    /// 为什么必须包一层：Windows 的剪贴板是**全局独占**资源，同一时刻只有一个进程能打开它。
    /// 浏览器、远程桌面、各种剪贴板管理器都会短暂占着不放，这时任何写入都会炸：
    ///
    ///   System.Runtime.InteropServices.COMException (0x800401D0):
    ///   OpenClipboard 失败 (CLIPBRD_E_CANT_OPEN)
    ///
    /// 这个异常从 <c>Clipboard.Flush()</c> 里抛出来，沿着 <c>EventRoute.InvokeHandlersImpl</c>
    /// 一路冒到宿主的全局异常处理，于是"点一下复制"变成"游戏发生错误"整个弹窗劝退。
    ///
    /// WPF 自己**已经**在 <c>Flush()</c> 里重试了 10 次 × 100ms，还失败说明对方占得很死，
    /// 再堆重试次数没有意义。所以这里不加重试，只做两件事：给一条真正有区别的退路，
    /// 以及保证无论如何都不往外抛。
    /// </summary>
    public static class ClipboardHelper
    {
        /// <summary>
        /// 把文本放上剪贴板。返回是否成功；失败只记日志，不抛。
        ///
        /// 两级退路：
        ///   1. <c>SetDataObject(text, copy: true)</c> —— 正常路径，数据交给系统，
        ///      本进程退出后仍留在剪贴板上。
        ///   2. <c>SetDataObject(text, copy: false)</c> —— 不 flush，数据仍归本进程所有。
        ///      粘贴照样能用，只是 VPet 关掉之后剪贴板里就没了；对"复制气泡里这句话马上去粘贴"
        ///      这个场景，这个代价用户根本感知不到。
        ///
        /// 第 2 步能救的是**用户报的那一种**：栈顶停在 <c>Clipboard.Flush()</c>，说明
        /// OleSetClipboard 已经成功、只有紧随其后的 OleFlushClipboard 失败 —— 跳过 flush 即可。
        /// 如果剪贴板压根打不开（OleSetClipboard 自己就失败），两步都会失败，
        /// 那就只剩最后一条底线：记日志，绝不上抛。
        /// </summary>
        public static bool TrySetText(string? text)
        {
            // 空内容不算失败：宿主的复制菜单在气泡为空时也点得动
            if (string.IsNullOrEmpty(text)) return true;

            if (TrySet(text!, copy: true, out var firstError))
                return true;

            if (TrySet(text!, copy: false, out var secondError))
            {
                Logger.Log($"ClipboardHelper: 剪贴板被其它程序占用（{Describe(firstError)}），" +
                           $"已改用不驻留模式复制成功（VPet 退出后剪贴板内容会失效）");
                return true;
            }

            Logger.Log($"ClipboardHelper: 复制失败，剪贴板被其它程序独占: {Describe(secondError)}");
            return false;
        }

        private static bool TrySet(string text, bool copy, out Exception? error)
        {
            try
            {
                Clipboard.SetDataObject(text, copy);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                // COMException / ExternalException 是剪贴板被占，其余（极少见）一并兜住 ——
                // 一次复制失败无论如何都不该掀翻宿主
                error = ex;
                return false;
            }
        }

        private static string Describe(Exception? ex)
        {
            if (ex is null) return "未知原因";
            if (ex is COMException com)
                return $"{ex.GetType().Name} 0x{com.HResult:X8}: {ex.Message}";
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
