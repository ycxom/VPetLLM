using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace VPetLLM.Utils.System
{
    public class GlobalHotkey : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;

        // 修饰键常量
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        private IntPtr _windowHandle;
        private int _hotkeyId;
        private HwndSource? _source;
        private bool _isRegistered;

        public event EventHandler? HotkeyPressed;

        public GlobalHotkey(IntPtr windowHandle, int hotkeyId)
        {
            _windowHandle = windowHandle;
            _hotkeyId = hotkeyId;
        }

        public bool Register(uint modifiers, uint key)
        {
            if (_isRegistered)
            {
                Unregister();
            }

            try
            {
                _isRegistered = RegisterHotKey(_windowHandle, _hotkeyId, modifiers, key);

                if (_isRegistered)
                {
                    _source = HwndSource.FromHwnd(_windowHandle);
                    if (_source != null)
                    {
                        _source.AddHook(HwndHook);
                        Logger.Log($"GlobalHotkey: Registered hotkey with ID {_hotkeyId}");
                    }
                }
                else
                {
                    Logger.Log($"GlobalHotkey: Failed to register hotkey with ID {_hotkeyId}");
                }

                return _isRegistered;
            }
            catch (Exception ex)
            {
                Logger.Log($"GlobalHotkey: Error registering hotkey: {ex.Message}");
                return false;
            }
        }

        public void Unregister()
        {
            if (!_isRegistered)
                return;

            try
            {
                if (_source != null)
                {
                    _source.RemoveHook(HwndHook);
                    _source = null;
                }

                UnregisterHotKey(_windowHandle, _hotkeyId);
                _isRegistered = false;
                Logger.Log($"GlobalHotkey: Unregistered hotkey with ID {_hotkeyId}");
            }
            catch (Exception ex)
            {
                Logger.Log($"GlobalHotkey: Error unregistering hotkey: {ex.Message}");
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
            {
                Logger.Log($"GlobalHotkey: Hotkey {_hotkeyId} pressed");
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                handled = true;
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
        }

        public static uint ParseModifiers(string modifiersString)
        {
            uint modifiers = 0;
            var parts = modifiersString.Split('+');

            foreach (var part in parts)
            {
                var trimmed = part.Trim().ToLower();
                switch (trimmed)
                {
                    case "alt":
                        modifiers |= MOD_ALT;
                        break;
                    case "ctrl":
                    case "control":
                        modifiers |= MOD_CONTROL;
                        break;
                    case "shift":
                        modifiers |= MOD_SHIFT;
                        break;
                    case "win":
                    case "windows":
                        modifiers |= MOD_WIN;
                        break;
                }
            }

            return modifiers;
        }

        public static uint ParseKey(string keyString)
        {
            try
            {
                var key = (Key)Enum.Parse(typeof(Key), keyString, true);
                return (uint)KeyInterop.VirtualKeyFromKey(key);
            }
            catch
            {
                Logger.Log($"GlobalHotkey: Failed to parse key '{keyString}'");
                return 0;
            }
        }
    }
}
