using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 上下文编辑器（Open WebUI 风格）。
    ///
    /// 两种模式：
    /// * 简洁模式 —— 渲染 AI 的回复格式，台词直接可改，动作/插件调用以只读芯片呈现；
    /// * 完全模式 —— 一律不渲染，原样给出模型返回的整段文本供直接编辑。
    ///
    /// 系统提示词（人设、能力清单、状态注入等）不进入本窗口：它由 ChatCoreBase
    /// 在每次请求时现生成，本就不在历史里；万一历史中混入了 system 消息，
    /// 这里也只是原样保管、既不显示也不改动。
    /// </summary>
    public partial class winContextEditor : Window, INotifyPropertyChanged
    {
        private readonly VPetLLM _plugin;
        private readonly string _langCode;

        /// <summary>历史中的 system 消息：不展示、不编辑，保存时原样放回最前面</summary>
        private readonly List<Message> _systemMessages;

        /// <summary>打开编辑器那一刻的内容快照，用于判断是否有未保存改动</summary>
        private readonly List<string> _contentSnapshot;
        private readonly int _snapshotCount;

        private bool _initialized;
        private bool _saved;

        public ObservableCollection<ContextEditorItem> Items { get; } = new();

        public winContextEditor(VPetLLM plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            if (_plugin.ChatCore is null)
            {
                throw new InvalidOperationException("ChatCore is not initialized");
            }

            _langCode = _plugin.Settings?.Language ?? "zh-hans";

            InitializeComponent();

            // GetChatHistory 返回的是 HistoryManager 内部那个活列表本身。
            // 必须先拷一份再操作，否则"添加/删除消息"会立刻改到真实上下文，
            // 用户点取消也收不回来。
            var liveHistory = _plugin.ChatCore.GetChatHistory().ToList();

            _systemMessages = liveHistory
                .Where(m => string.Equals(m.NormalizedRole, "system", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var aiName = _plugin.Settings?.AiName ?? "Assistant";
            var userName = _plugin.Settings?.UserName ?? "You";

            foreach (var message in liveHistory)
            {
                if (string.Equals(message.NormalizedRole, "system", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var item = new ContextEditorItem(message);
                item.SetDisplayNames(aiName, userName);
                Items.Add(item);
            }

            _contentSnapshot = Items.Select(i => i.Content).ToList();
            _snapshotCount = Items.Count;

            DataContext = this;
            _initialized = true;

            ApplyLocalization();
            ApplyMode(simple: true);
            UpdateStats();
        }

        // ── 模板里用到的本地化文案 ──────────────────────────────────────────

        public string LocDeleteMessage { get; private set; } = "删除这条消息";
        public string LocViewImage { get; private set; } = "点击查看大图";
        public string LocRemoveImage { get; private set; } = "移除图像";

        // ── 模式切换 ────────────────────────────────────────────────────────

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            // XAML 里 Radio_Simple 带 IsChecked="True"，构造期就会触发一次
            if (!_initialized) return;

            ApplyMode(simple: Radio_Simple.IsChecked == true);
        }

        /// <summary>
        /// 切模式前必须把当前模式的编辑成果落到另一边，否则用户在简洁模式改的台词
        /// 一切到完全模式就"消失"了（因为完全模式读的是 Content）。
        /// </summary>
        private void ApplyMode(bool simple)
        {
            foreach (var item in Items)
            {
                if (simple)
                {
                    // 完全模式可能整段重写过原文，片段必须按新原文重新解析
                    item.RebuildSegments();
                }
                else
                {
                    item.SyncToContent();
                }

                item.IsSimpleMode = simple;
            }

            UpdateStats();
        }

        // ── 增删 ────────────────────────────────────────────────────────────

        private void AddUserMessage_Click(object sender, RoutedEventArgs e) => AddMessage("user");

        private void AddAssistantMessage_Click(object sender, RoutedEventArgs e) => AddMessage("assistant");

        private void AddMessage(string role)
        {
            var message = new Message { Role = role, Content = "" };
            var item = new ContextEditorItem(message);
            item.SetDisplayNames(_plugin.Settings?.AiName ?? "Assistant", _plugin.Settings?.UserName ?? "You");
            item.IsSimpleMode = Radio_Simple.IsChecked == true;

            Items.Add(item);
            UpdateStats();

            List_Messages.ScrollIntoView(item);
        }

        private void DeleteMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ContextEditorItem item })
            {
                Items.Remove(item);
                UpdateStats();
            }
        }

        // ── 图像 ────────────────────────────────────────────────────────────

        private void Thumbnail_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Image { DataContext: ContextEditorItem item })
            {
                var imageData = item.GetFullImage();
                if (imageData is null || imageData.Length == 0) return;

                var preview = new winImagePreview(imageData) { Owner = this };
                preview.ShowDialog();
            }
        }

        private void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ContextEditorItem item })
            {
                item.RemoveImage();
                Logger.Log("上下文编辑器: 图像已标记删除（保存后生效）");
            }
        }

        // ── 保存 / 取消 ─────────────────────────────────────────────────────

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.ChatCore is null)
            {
                ShowError(LanguageHelper.Get("Error.ChatCoreNotInitialized", _langCode,
                    "Chat core is not initialized. Cannot save changes."));
                Logger.Log("上下文编辑器: ChatCore 为空，无法保存");
                return;
            }

            try
            {
                // 简洁模式下真相在片段里，先合回原文再落库
                if (Radio_Simple.IsChecked == true)
                {
                    foreach (var item in Items)
                    {
                        item.SyncToContent();
                    }
                }

                // system 消息始终排在最前：多数 Provider 都要求 system 位于对话首部
                var newHistory = new List<Message>(_systemMessages);

                foreach (var item in Items)
                {
                    item.ApplyTo();
                    newHistory.Add(item.OriginalMessage);
                }

                _plugin.ChatCore.SetChatHistory(newHistory);
                _saved = true;

                Logger.Log($"上下文编辑器: 保存成功，共 {newHistory.Count} 条（其中 system {_systemMessages.Count} 条未改动）");
                Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"上下文编辑器: 保存失败: {ex.Message}");
                ShowError($"Failed to save context: {ex.Message}");
            }
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 取消/关闭本身不需要"恢复"——全程都在副本上编辑，只有保存才写回。
        /// 这里只是拦一下，免得改了半天顺手点了叉。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_saved && HasUnsavedChanges())
            {
                var result = MessageBox.Show(
                    LanguageHelper.Get("ContextEditor.UnsavedBody", _langCode, "有未保存的改动，确定要放弃吗？"),
                    LanguageHelper.Get("ContextEditor.UnsavedTitle", _langCode, "放弃改动"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                Logger.Log("上下文编辑器: 用户放弃改动，历史未变更");
            }

            base.OnClosing(e);
        }

        private bool HasUnsavedChanges()
        {
            // 比较前先把简洁模式的片段合回原文，否则刚改完台词就关窗会被判成"没动过"
            if (Radio_Simple.IsChecked == true)
            {
                foreach (var item in Items)
                {
                    item.SyncToContent();
                }
            }

            if (Items.Count != _snapshotCount)
            {
                return true;
            }

            for (var i = 0; i < Items.Count; i++)
            {
                if (!string.Equals(Items[i].Content, _contentSnapshot[i], StringComparison.Ordinal))
                {
                    return true;
                }

                if (Items[i].ImageRemoved)
                {
                    return true;
                }
            }

            return false;
        }

        // ── 杂项 ────────────────────────────────────────────────────────────

        private void UpdateStats()
        {
            var tokens = 0;
            try
            {
                tokens = _plugin.ChatCore?.GetCurrentTokenCount() ?? 0;
            }
            catch (Exception ex)
            {
                // Token 估算失败不该挡住编辑器
                Logger.Log($"上下文编辑器: Token 估算失败: {ex.Message}");
            }

            var template = LanguageHelper.Get("ContextEditor.Stats", _langCode, "{0} 条消息 · 约 {1} tokens");
            Text_Stats.Text = string.Format(template, Items.Count, tokens);

            Text_Empty.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyLocalization()
        {
            Title = LanguageHelper.Get("ContextEditor.Title", _langCode, "上下文编辑器");
            Text_Title.Text = Title;

            Radio_Simple.Content = LanguageHelper.Get("ContextEditor.SimpleMode", _langCode, "简洁模式");
            Radio_Full.Content = LanguageHelper.Get("ContextEditor.FullMode", _langCode, "完全模式");

            Text_Notice.Text = LanguageHelper.Get("ContextEditor.Notice", _langCode,
                "系统提示词与人设不在此展示，也不会被本窗口修改。");
            Text_Empty.Text = LanguageHelper.Get("ContextEditor.Empty", _langCode, "还没有对话记录");

            Button_AddUser.Content = LanguageHelper.Get("ContextEditor.AddUser", _langCode, "+ 用户消息");
            Button_AddAssistant.Content = LanguageHelper.Get("ContextEditor.AddAssistant", _langCode, "+ 助手消息");
            Button_Save.Content = LanguageHelper.Get("ContextEditor.Save", _langCode, "保存");
            Button_Cancel.Content = LanguageHelper.Get("ContextEditor.Cancel", _langCode, "取消");

            LocDeleteMessage = LanguageHelper.Get("ContextEditor.DeleteMessage", _langCode, "删除这条消息");
            LocViewImage = LanguageHelper.Get("ContextEditor.ViewImage", _langCode, "点击查看大图");
            LocRemoveImage = LanguageHelper.Get("ContextEditor.RemoveImage", _langCode, "移除图像");

            OnPropertyChanged(nameof(LocDeleteMessage));
            OnPropertyChanged(nameof(LocViewImage));
            OnPropertyChanged(nameof(LocRemoveImage));
        }

        private void ShowError(string message)
        {
            MessageBox.Show(
                message,
                LanguageHelper.Get("Error.Title", _langCode, "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
