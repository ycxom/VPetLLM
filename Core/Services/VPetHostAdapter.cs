using System.Reflection;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Core.Services
{
    /// <summary>
    /// VPet 宿主非公开成员的统一访问适配层。
    ///
    /// 约束：所有越过公共接口的宿主访问（字段/属性反射）必须收拢到这里。
    /// 反射引用按运行时类型解析并缓存一次；成员缺失时置能力开关并只记录一次日志，
    /// 调用方据此走公共 API 降级路径，而不是各自反射、每次调用都静默失败。
    /// </summary>
    public static class VPetHostAdapter
    {
        private static readonly object _lock = new object();

        // ---- Main 成员 ----
        // State：IMainWindow/接口包未暴露；VPet 本体为 public 字段，旧版本可能为属性。
        // 其余为动画同步所需的私有字段/方法，接口包同样未暴露。
        private static volatile Type _mainType;
        private static FieldInfo _stateField;
        private static PropertyInfo _stateProperty;
        private static FieldInfo _voicePlayerField;
        private static FieldInfo _petGridCrlfField;
        private static FieldInfo _petGridField;
        private static FieldInfo _petGrid2Field;
        private static FieldInfo _loopTimesField;
        private static MethodInfo _displayToMoveMethod;
        private static PropertyInfo _displayNomalProperty;

        private static void EnsureStateAccessor(object main)
        {
            var type = main.GetType();
            if (_mainType == type) return;

            lock (_lock)
            {
                if (_mainType == type) return;

                _stateField = type.GetField("State");
                _stateProperty = _stateField is null ? type.GetProperty("State") : null;

                // VoicePlayer/PetGrid(2) 在 XAML 里是 x:FieldModifier="public"，
                // petgridcrlf/looptimes 是 code-behind 私有字段——统一两种可见性都查
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _voicePlayerField = type.GetField("VoicePlayer", any);
                _petGridCrlfField = type.GetField("petgridcrlf", any);
                _petGridField = type.GetField("PetGrid", any);
                _petGrid2Field = type.GetField("PetGrid2", any);
                _loopTimesField = type.GetField("looptimes", any);
                _displayToMoveMethod = type.GetMethod("DisplayToMove", BindingFlags.Public | BindingFlags.Instance);
                _displayNomalProperty = type.GetProperty("DisplayNomal");

                var missing = new List<string>();
                if (_stateField is null && _stateProperty is null) missing.Add("State");
                if (_voicePlayerField is null) missing.Add("VoicePlayer");
                if (_petGridCrlfField is null) missing.Add("petgridcrlf");
                if (_petGridField is null || _petGrid2Field is null) missing.Add("PetGrid/PetGrid2");
                if (_loopTimesField is null) missing.Add("looptimes");
                if (_displayToMoveMethod is null) missing.Add("DisplayToMove");
                if (missing.Count > 0)
                    Logger.Log($"VPetHostAdapter: Main({type.Name}) 缺少成员: {string.Join(", ", missing)}，相关功能将降级");

                _mainType = type;
            }
        }

        /// <summary>
        /// 当前 VPet 版本是否可读写 Main.State。
        /// </summary>
        public static bool CanAccessState(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null) return false;
            EnsureStateAccessor(mainWindow.Main);
            return _stateField is not null || _stateProperty is not null;
        }

        public static Type GetStateType(IMainWindow mainWindow)
        {
            if (!CanAccessState(mainWindow)) return null;
            return _stateField?.FieldType ?? _stateProperty?.PropertyType;
        }

        public static object GetState(IMainWindow mainWindow)
        {
            if (!CanAccessState(mainWindow)) return null;
            return _stateField is not null
                ? _stateField.GetValue(mainWindow.Main)
                : _stateProperty?.GetValue(mainWindow.Main);
        }

        public static bool SetState(IMainWindow mainWindow, object value)
        {
            if (!CanAccessState(mainWindow)) return false;

            if (_stateField is not null)
            {
                _stateField.SetValue(mainWindow.Main, value);
                return true;
            }
            if (_stateProperty?.CanWrite == true)
            {
                _stateProperty.SetValue(mainWindow.Main, value);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 按枚举名设置 State（如 "SideLeft"/"SideRight" 贴墙状态）。
        /// 旧版本 VPet 无对应枚举值时返回 false。
        /// </summary>
        public static bool TrySetStateByName(IMainWindow mainWindow, string stateName)
        {
            var stateType = GetStateType(mainWindow);
            if (stateType is null) return false;

            try
            {
                var value = Enum.Parse(stateType, stateName, ignoreCase: true);
                return SetState(mainWindow, value);
            }
            catch (ArgumentException)
            {
                Logger.Log($"VPetHostAdapter: 当前 VPet 版本无状态 '{stateName}'");
                return false;
            }
        }

        /// <summary>
        /// Main.VoicePlayer 私有字段（MediaPlayer，用于估算语音剩余时长）。
        /// </summary>
        public static object GetVoicePlayer(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null) return null;
            EnsureStateAccessor(mainWindow.Main);
            return _voicePlayerField?.GetValue(mainWindow.Main);
        }

        /// <summary>
        /// Main.petgridcrlf 私有字段（当前显示的是 PetGrid 还是 PetGrid2）。
        /// </summary>
        public static bool? GetPetGridCrlf(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null) return null;
            EnsureStateAccessor(mainWindow.Main);
            return _petGridCrlfField?.GetValue(mainWindow.Main) as bool?;
        }

        /// <summary>
        /// Main.PetGrid / PetGrid2 私有字段（双缓冲动画容器）。任一缺失返回 false。
        /// </summary>
        public static bool TryGetPetGrids(IMainWindow mainWindow, out object petGrid, out object petGrid2)
        {
            petGrid = null;
            petGrid2 = null;
            if (mainWindow?.Main is null) return false;

            EnsureStateAccessor(mainWindow.Main);
            if (_petGridField is null || _petGrid2Field is null) return false;

            petGrid = _petGridField.GetValue(mainWindow.Main);
            petGrid2 = _petGrid2Field.GetValue(mainWindow.Main);
            return petGrid is not null && petGrid2 is not null;
        }

        /// <summary>
        /// Main.looptimes 私有字段（当前动画循环计数）。
        /// </summary>
        public static int? GetLoopTimes(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null) return null;
            EnsureStateAccessor(mainWindow.Main);
            return _loopTimesField?.GetValue(mainWindow.Main) as int?;
        }

        /// <summary>
        /// 调用 Main.DisplayToMove()（触发随机移动动画）。
        /// </summary>
        public static bool TryDisplayToMove(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null) return false;
            EnsureStateAccessor(mainWindow.Main);
            if (_displayToMoveMethod is null) return false;

            var result = _displayToMoveMethod.Invoke(mainWindow.Main, null);
            return result is not bool started || started;
        }

        /// <summary>
        /// Main.DisplayNomal 属性（动画结束回调委托）。
        /// </summary>
        public static Action GetDisplayNomalAction(IMainWindow mainWindow)
        {
            if (mainWindow?.Main is null) return null;
            EnsureStateAccessor(mainWindow.Main);
            return _displayNomalProperty?.GetValue(mainWindow.Main) as Action;
        }

        // ---- MainWindow（宿主窗口本体）成员 ----
        private static volatile Type _mainWindowType;
        private static MethodInfo _displayPinchMethod;

        /// <summary>
        /// 调用 MainWindow.DisplayPinch()（捏脸动画，接口包未暴露）。
        /// </summary>
        public static bool TryDisplayPinch(IMainWindow mainWindow)
        {
            if (mainWindow is null) return false;

            var type = mainWindow.GetType();
            if (_mainWindowType != type)
            {
                lock (_lock)
                {
                    if (_mainWindowType != type)
                    {
                        _displayPinchMethod = type.GetMethod("DisplayPinch");
                        if (_displayPinchMethod is null)
                            Logger.Log("VPetHostAdapter: 当前 VPet 版本无 DisplayPinch 方法");
                        _mainWindowType = type;
                    }
                }
            }

            if (_displayPinchMethod is null) return false;
            _displayPinchMethod.Invoke(mainWindow, null);
            return true;
        }

        // ---- Core.Controller 成员（移动区域，接口包未暴露）----
        private static volatile Type _controllerType;
        private static PropertyInfo _isPrimaryScreenProperty;
        private static PropertyInfo _screenBorderProperty;
        private static volatile Type _borderType;
        private static PropertyInfo _borderXProperty;
        private static PropertyInfo _borderYProperty;
        private static PropertyInfo _borderWidthProperty;
        private static PropertyInfo _borderHeightProperty;

        /// <summary>
        /// 读取自定义移动区域（Controller.ScreenBorder，仅当 IsPrimaryScreen=false 时有意义）。
        /// 成员缺失或使用主屏时返回 false，调用方回退到主屏尺寸。
        /// </summary>
        public static bool TryGetCustomMoveArea(IMainWindow mainWindow, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            var controller = mainWindow?.Core?.Controller;
            if (controller is null) return false;

            var type = controller.GetType();
            if (_controllerType != type)
            {
                lock (_lock)
                {
                    if (_controllerType != type)
                    {
                        _isPrimaryScreenProperty = type.GetProperty("IsPrimaryScreen");
                        _screenBorderProperty = type.GetProperty("ScreenBorder");
                        _controllerType = type;
                    }
                }
            }

            if (_isPrimaryScreenProperty is null || _screenBorderProperty is null) return false;
            if (_isPrimaryScreenProperty.GetValue(controller) is true) return false;

            var border = _screenBorderProperty.GetValue(controller);
            if (border is null) return false;

            var bType = border.GetType();
            if (_borderType != bType)
            {
                lock (_lock)
                {
                    if (_borderType != bType)
                    {
                        _borderXProperty = bType.GetProperty("X");
                        _borderYProperty = bType.GetProperty("Y");
                        _borderWidthProperty = bType.GetProperty("Width");
                        _borderHeightProperty = bType.GetProperty("Height");
                        _borderType = bType;
                    }
                }
            }

            if (_borderXProperty is null || _borderYProperty is null
                || _borderWidthProperty is null || _borderHeightProperty is null) return false;

            x = Convert.ToInt32(_borderXProperty.GetValue(border));
            y = Convert.ToInt32(_borderYProperty.GetValue(border));
            width = Convert.ToInt32(_borderWidthProperty.GetValue(border));
            height = Convert.ToInt32(_borderHeightProperty.GetValue(border));
            return true;
        }

        // ---- GameSave 特殊成员 ----
        // Core 的 IGameSave 接口：Level 只读、无 LevelMax；Exp setter 带升级结算副作用。
        // 运行时类型（GameSave_VPet）才有可写成员/私有 exp 字段。
        private static volatile Type _saveType;
        private static FieldInfo _expRawField;
        private static PropertyInfo _levelProperty;
        private static PropertyInfo _levelMaxProperty;

        private static void EnsureSaveAccessors(object save)
        {
            var type = save.GetType();
            if (_saveType == type) return;

            lock (_lock)
            {
                if (_saveType == type) return;

                _expRawField = type.GetField("exp", BindingFlags.NonPublic | BindingFlags.Instance);
                _levelProperty = type.GetProperty("Level");
                _levelMaxProperty = type.GetProperty("LevelMax");

                var missing = new List<string>();
                if (_expRawField is null) missing.Add("exp(私有字段)");
                if (_levelProperty?.CanWrite != true) missing.Add("Level(可写)");
                if (_levelMaxProperty?.CanWrite != true) missing.Add("LevelMax(可写)");
                if (missing.Count > 0)
                    Logger.Log($"VPetHostAdapter: GameSave({type.Name}) 缺少成员: {string.Join(", ", missing)}，相关命令将降级或不可用");

                _saveType = type;
            }
        }

        /// <summary>
        /// 直写私有 exp 字段，绕过 Exp setter 的升级结算副作用（用于设置/扣减经验）。
        /// 字段缺失时降级为 Exp 属性写入（会触发升级结算）。
        /// </summary>
        public static bool TrySetExpRaw(IMainWindow mainWindow, double value)
        {
            var save = mainWindow?.Core?.Save;
            if (save is null) return false;

            EnsureSaveAccessors(save);
            if (_expRawField is not null)
            {
                _expRawField.SetValue(save, value);
                return true;
            }

            save.Exp = value;
            return true;
        }

        /// <summary>
        /// 设置等级。IGameSave.Level 只读，须经运行时类型的可写属性。
        /// </summary>
        public static bool TrySetLevel(IMainWindow mainWindow, int level)
        {
            var save = mainWindow?.Core?.Save;
            if (save is null) return false;

            EnsureSaveAccessors(save);
            if (_levelProperty?.CanWrite != true) return false;

            _levelProperty.SetValue(save, level);
            return true;
        }

        /// <summary>
        /// 设置等级上限（突破次数）。IGameSave 无此成员，须经运行时类型。
        /// </summary>
        public static bool TrySetLevelMax(IMainWindow mainWindow, int levelMax)
        {
            var save = mainWindow?.Core?.Save;
            if (save is null) return false;

            EnsureSaveAccessors(save);
            if (_levelMaxProperty?.CanWrite != true) return false;

            _levelMaxProperty.SetValue(save, levelMax);
            return true;
        }
    }
}
