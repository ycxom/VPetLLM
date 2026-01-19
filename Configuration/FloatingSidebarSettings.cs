using VPetLLM.UI.Controls;

namespace VPetLLM.Configuration
{
    /// <summary>
    /// 悬浮侧边栏设置
    /// </summary>
    public class FloatingSidebarSettings : ISettings
    {
        /// <summary>
        /// 是否启用悬浮侧边栏（默认启用）
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 是否启用自动隐藏
        /// </summary>
        public bool AutoHide { get; set; } = true;

        /// <summary>
        /// 默认透明度 (0.0 - 1.0)
        /// </summary>
        public double DefaultOpacity { get; set; } = 0.9;

        /// <summary>
        /// 非活动状态透明度 (0.0 - 1.0)
        /// </summary>
        public double InactiveOpacity { get; set; } = 0.3;

        /// <summary>
        /// 自动隐藏延迟（秒）
        /// </summary>
        public int AutoHideDelay { get; set; } = 3;

        /// <summary>
        /// 侧边栏位置
        /// </summary>
        public SidebarPosition Position { get; set; } = SidebarPosition.Right;

        /// <summary>
        /// 启用的按钮列表
        /// </summary>
        public List<string> EnabledButtons { get; set; } = new();

        /// <summary>
        /// 按钮顺序配置 (ButtonId -> Order)
        /// </summary>
        public Dictionary<string, int> ButtonOrder { get; set; } = new();

        /// <summary>
        /// 自定义位置偏移X
        /// </summary>
        public double CustomOffsetX { get; set; } = 0;

        /// <summary>
        /// 自定义位置偏移Y
        /// </summary>
        public double CustomOffsetY { get; set; } = 0;

        /// <summary>
        /// 鼠标接近检测距离（像素）
        /// </summary>
        public int MouseProximityDistance { get; set; } = 50;

        /// <summary>
        /// 是否自动切换到后层 (参考 DemoClock 的 PlaceAutoBack)
        /// </summary>
        public bool PlaceAutoBack { get; set; } = true;

        /// <summary>
        /// 侧边栏顶部边距 (参考 DemoClock 的 PlaceTop)
        /// </summary>
        public double PlaceTop { get; set; } = 0;

        public FloatingSidebarSettings()
        {
            // 设置默认启用的按钮
            var defaultButtons = SidebarButton.GetDefaultButtons();
            EnabledButtons = defaultButtons.Select(b => b.ButtonId).ToList();

            // 设置默认按钮顺序
            ButtonOrder = defaultButtons.ToDictionary(b => b.ButtonId, b => b.Order);
        }

        /// <summary>
        /// 验证设置有效性
        /// </summary>
        public SettingsValidationResult Validate()
        {
            var result = new SettingsValidationResult { IsValid = true };

            try
            {
                // 检查透明度范围
                if (DefaultOpacity < 0.1 || DefaultOpacity > 1.0)
                {
                    result.AddError($"默认透明度必须在0.1到1.0之间，当前值: {DefaultOpacity}");
                }

                if (InactiveOpacity < 0.1 || InactiveOpacity > 1.0)
                {
                    result.AddError($"非活动透明度必须在0.1到1.0之间，当前值: {InactiveOpacity}");
                }

                // 检查自动隐藏延迟
                if (AutoHideDelay < 1 || AutoHideDelay > 60)
                {
                    result.AddError($"自动隐藏延迟必须在1到60秒之间，当前值: {AutoHideDelay}");
                }

                // 检查鼠标接近距离
                if (MouseProximityDistance < 10 || MouseProximityDistance > 200)
                {
                    result.AddError($"鼠标接近距离必须在10到200像素之间，当前值: {MouseProximityDistance}");
                }

                // 检查启用的按钮列表
                if (EnabledButtons == null)
                {
                    result.AddWarning("启用按钮列表为空，将使用默认按钮");
                    EnabledButtons = SidebarButton.GetDefaultButtons().Select(b => b.ButtonId).ToList();
                }

                // 检查按钮顺序配置
                if (ButtonOrder == null)
                {
                    result.AddWarning("按钮顺序配置为空，将使用默认顺序");
                    ButtonOrder = SidebarButton.GetDefaultButtons().ToDictionary(b => b.ButtonId, b => b.Order);
                }

                return result;
            }
            catch (Exception ex)
            {
                result.AddError($"验证过程中发生异常: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// 重置为默认值
        /// </summary>
        public void ResetToDefaults()
        {
            IsEnabled = true;
            AutoHide = true;
            DefaultOpacity = 0.9;
            InactiveOpacity = 0.3;
            AutoHideDelay = 3;
            Position = SidebarPosition.Right;
            CustomOffsetX = 0;
            CustomOffsetY = 0;
            MouseProximityDistance = 50;

            var defaultButtons = SidebarButton.GetDefaultButtons();
            EnabledButtons = defaultButtons.Select(b => b.ButtonId).ToList();
            ButtonOrder = defaultButtons.ToDictionary(b => b.ButtonId, b => b.Order);
        }

        /// <summary>
        /// 验证并修复配置，返回是否进行了修复
        /// </summary>
        public bool ValidateAndRepair()
        {
            bool repaired = false;

            // 修复透明度范围
            if (DefaultOpacity < 0.1 || DefaultOpacity > 1.0)
            {
                DefaultOpacity = Math.Max(0.1, Math.Min(1.0, DefaultOpacity));
                repaired = true;
            }

            if (InactiveOpacity < 0.1 || InactiveOpacity > 1.0)
            {
                InactiveOpacity = Math.Max(0.1, Math.Min(1.0, InactiveOpacity));
                repaired = true;
            }

            // 修复自动隐藏延迟
            if (AutoHideDelay < 1 || AutoHideDelay > 60)
            {
                AutoHideDelay = Math.Max(1, Math.Min(60, AutoHideDelay));
                repaired = true;
            }

            // 修复鼠标接近距离
            if (MouseProximityDistance < 10 || MouseProximityDistance > 200)
            {
                MouseProximityDistance = Math.Max(10, Math.Min(200, MouseProximityDistance));
                repaired = true;
            }

            // 修复空的按钮列表
            if (EnabledButtons == null || EnabledButtons.Count == 0)
            {
                EnabledButtons = SidebarButton.GetDefaultButtons().Select(b => b.ButtonId).ToList();
                repaired = true;
            }

            // 修复空的按钮顺序
            if (ButtonOrder == null || ButtonOrder.Count == 0)
            {
                ButtonOrder = SidebarButton.GetDefaultButtons().ToDictionary(b => b.ButtonId, b => b.Order);
                repaired = true;
            }

            return repaired;
        }

        /// <summary>
        /// 获取按钮的显示顺序
        /// </summary>
        public int GetButtonOrder(string buttonId)
        {
            return ButtonOrder.ContainsKey(buttonId) ? ButtonOrder[buttonId] : 999;
        }

        /// <summary>
        /// 设置按钮顺序
        /// </summary>
        public void SetButtonOrder(string buttonId, int order)
        {
            ButtonOrder[buttonId] = order;
        }

        /// <summary>
        /// 启用按钮
        /// </summary>
        public void EnableButton(string buttonId)
        {
            if (!EnabledButtons.Contains(buttonId))
            {
                EnabledButtons.Add(buttonId);
            }
        }

        /// <summary>
        /// 禁用按钮
        /// </summary>
        public void DisableButton(string buttonId)
        {
            EnabledButtons.Remove(buttonId);
        }

        /// <summary>
        /// 检查按钮是否启用
        /// </summary>
        public bool IsButtonEnabled(string buttonId)
        {
            return EnabledButtons.Contains(buttonId);
        }
    }

    /// <summary>
    /// 侧边栏位置枚举
    /// </summary>
    public enum SidebarPosition
    {
        Left,
        Right,
        Top,
        Bottom,
        Custom
    }
}