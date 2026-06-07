using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core.Abstractions.Base;
using VPetLLM.Core.Data.Database;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    public class RecordEditorItem
    {
        public ImportantRecord OriginalRecord { get; }
        public int Id => OriginalRecord.Id;
        public string Content { get; set; }
        public int Weight { get; set; }
        public DateTime CreatedAt => OriginalRecord.CreatedAt;

        public RecordEditorItem(ImportantRecord originalRecord)
        {
            OriginalRecord = originalRecord;
            Content = originalRecord.Content;
            Weight = originalRecord.DisplayWeight;
        }
    }

    /// <summary>
    /// Display model for overflow summary rows.
    /// </summary>
    public class OverflowSummaryItem
    {
        public int Id { get; set; }
        public string SummaryText { get; set; } = "";
        public int SegmentStartIndex { get; set; }
        public int SegmentEndIndex { get; set; }
        public int TokenCount { get; set; }
        public DateTime CreatedAt { get; set; }

        public string RangeDisplay => $"[{SegmentStartIndex}..{SegmentEndIndex}]";
    }

    public partial class winRecordEditor : Window
    {
        private readonly VPetLLM _plugin;
        public ObservableCollection<RecordEditorItem> DisplayRecords { get; set; }
        public ObservableCollection<OverflowSummaryItem> DisplayOverflowSummaries { get; set; }

        public winRecordEditor(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            // Load important records
            var records = _plugin.ChatCore?.RecordManager?.GetAllRecordsForEditing() ?? new List<ImportantRecord>();
            var displayItems = records.Select(r => new RecordEditorItem(r));
            DisplayRecords = new ObservableCollection<RecordEditorItem>(displayItems);

            // Load overflow summaries
            var summaries = (_plugin.ChatCore as ChatCoreBase)?.OverflowManager?.SearchSummaries("", 100) ?? new List<OverflowSummaryRecord>();
            var summaryItems = summaries.Select(s => new OverflowSummaryItem
            {
                Id = s.Id,
                SummaryText = s.SummaryText,
                SegmentStartIndex = s.SegmentStartIndex,
                SegmentEndIndex = s.SegmentEndIndex,
                TokenCount = s.TokenCount,
                CreatedAt = s.CreatedAt
            });
            DisplayOverflowSummaries = new ObservableCollection<OverflowSummaryItem>(summaryItems);

            DataContext = this;
            DataGrid_Overflow.ItemsSource = DisplayOverflowSummaries;
            UpdateUIForLanguage();
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var item in DisplayRecords)
                {
                    item.Weight = Math.Clamp(item.Weight, 0, 10);
                    item.OriginalRecord.Content = item.Content;
                    item.OriginalRecord.Weight = item.Weight;
                    _plugin.ChatCore?.RecordManager?.UpdateRecord(item.OriginalRecord);
                }

                MessageBox.Show(
                    LanguageHelper.Get("RecordEditor.SaveSuccess", _plugin.Settings.Language),
                    LanguageHelper.Get("RecordEditor.Title", _plugin.Settings.Language),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save records: {ex.Message}");
                MessageBox.Show($"Failed to save records: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Button_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (DataGrid_Records.SelectedItem is RecordEditorItem selectedItem)
            {
                var result = MessageBox.Show(
                    LanguageHelper.Get("RecordEditor.DeleteConfirm", _plugin.Settings.Language),
                    LanguageHelper.Get("RecordEditor.Delete", _plugin.Settings.Language),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var database = new ImportantRecordsDatabase(GetDatabasePath());
                    database.DeleteRecord(selectedItem.Id);
                    DisplayRecords.Remove(selectedItem);
                }
            }
        }

        private void Button_OverflowDelete_Click(object sender, RoutedEventArgs e)
        {
            if (DataGrid_Overflow.SelectedItem is OverflowSummaryItem selected)
            {
                var result = MessageBox.Show(
                    $"确认删除溢出总结 #{selected.Id}？\n范围: {selected.RangeDisplay}",
                    "删除溢出总结",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var overflowDb = new OverflowDatabase(GetDatabasePath());
                    // Delete segments first then summary
                    overflowDb.DeleteSummary(selected.Id);
                    DisplayOverflowSummaries.Remove(selected);
                }
            }
        }

        private void Button_OverflowClearAll_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "确认清空所有溢出总结？此操作不可撤销。",
                "清空溢出总结",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                (_plugin.ChatCore as ChatCoreBase)?.OverflowManager?.ClearAll();
                DisplayOverflowSummaries.Clear();
            }
        }

        private void UpdateUIForLanguage()
        {
            var langCode = _plugin.Settings.Language;
            Title = "记忆/溢出管理";

            // Tab headers
            if (FindName("TabItem_Records") is TabItem tabRecords)
                tabRecords.Header = new TextBlock { Text = LanguageHelper.Get("RecordEditor.Title", langCode) ?? "重要记忆", FontSize = 14, FontWeight = System.Windows.FontWeights.SemiBold };
            if (FindName("TabItem_Overflow") is TabItem tabOverflow)
                tabOverflow.Header = new TextBlock { Text = "溢出总结 (Overflow Summaries)", FontSize = 14, FontWeight = System.Windows.FontWeights.SemiBold };

            // Records columns
            UpdateDataGridColumn(DataGrid_Records, "Column_Id", LanguageHelper.Get("RecordEditor.Id", langCode));
            UpdateDataGridColumn(DataGrid_Records, "Column_Content", LanguageHelper.Get("RecordEditor.Content", langCode));
            UpdateDataGridColumn(DataGrid_Records, "Column_Weight", LanguageHelper.Get("RecordEditor.Weight", langCode));
            UpdateDataGridColumn(DataGrid_Records, "Column_CreatedAt", LanguageHelper.Get("RecordEditor.CreatedAt", langCode));

            // Overflow columns
            UpdateDataGridColumn(DataGrid_Overflow, "Column_Overflow_Id", "ID");
            UpdateDataGridColumn(DataGrid_Overflow, "Column_Overflow_Summary", "总结内容");
            UpdateDataGridColumn(DataGrid_Overflow, "Column_Overflow_Range", "消息范围");
            UpdateDataGridColumn(DataGrid_Overflow, "Column_Overflow_Tokens", "Token");
            UpdateDataGridColumn(DataGrid_Overflow, "Column_Overflow_CreatedAt", "创建时间");

            if (FindName("Button_Save") is Button btnSave) btnSave.Content = LanguageHelper.Get("RecordEditor.Save", langCode);
            if (FindName("Button_Cancel") is Button btnCancel) btnCancel.Content = LanguageHelper.Get("RecordEditor.Cancel", langCode);
            if (FindName("Button_Delete") is Button btnDelete) btnDelete.Content = LanguageHelper.Get("RecordEditor.Delete", langCode);
            if (FindName("Button_Overflow_Delete") is Button btnOvDel) btnOvDel.Content = "删除选中";
            if (FindName("Button_Overflow_ClearAll") is Button btnOvClr) btnOvClr.Content = "清空全部溢出总结";
        }

        private void UpdateDataGridColumn(DataGrid grid, string columnName, string? header)
        {
            if (string.IsNullOrEmpty(header)) return;
            foreach (var col in grid.Columns)
            {
                if (col is DataGridTextColumn textCol && textCol.Header?.ToString() == header) return;
                if (col is DataGridTextColumn tc && tc.GetType().GetProperty("Name")?.GetValue(tc)?.ToString() == columnName)
                {
                    tc.Header = header;
                    return;
                }
            }
            // Fallback: find by x:Name via FindName
            var namedCol = FindName(columnName) as DataGridTextColumn;
            if (namedCol != null) namedCol.Header = header;
        }

        private string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = System.IO.Path.Combine(docPath, "VPetLLM", "Chat");
            return System.IO.Path.Combine(dataPath, "chat_history.db");
        }
    }
}
