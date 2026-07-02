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

        // ---- Main.State ----
        // IMainWindow/接口包未暴露；VPet 本体为 public 字段，旧版本可能为属性。
        private static volatile Type _mainType;
        private static FieldInfo _stateField;
        private static PropertyInfo _stateProperty;

        private static void EnsureStateAccessor(object main)
        {
            var type = main.GetType();
            if (_mainType == type) return;

            lock (_lock)
            {
                if (_mainType == type) return;

                _stateField = type.GetField("State");
                _stateProperty = _stateField is null ? type.GetProperty("State") : null;

                if (_stateField is null && _stateProperty is null)
                    Logger.Log("VPetHostAdapter: 当前 VPet 版本未暴露 Main.State，状态管理将走显示方法降级路径");

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
