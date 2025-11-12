using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core;
using VPetLLM.Utils;

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
            Weight = originalRecord.DisplayWeight; // Display rounded weight
        }
    }

    public partial class winRecordEditor : Window
    {
        private readonly VPetLLM _plugin;
        public ObservableCollection<RecordEditorItem> DisplayRecords { get; set; }

        public winRecordEditor(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            var records = _plugin.ChatCore?.RecordManager?.GetAllRecordsForEditing() ?? new List<ImportantRecord>();
            var displayItems = records.Select(r => new RecordEditorItem(r));
            DisplayRecords = new ObservableCollection<RecordEditorItem>(displayItems);

            DataContext = this;
            UpdateUIForLanguage();
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var item in DisplayRecords)
                {
                    // Clamp weight to valid range
                    item.Weight = Math.Clamp(item.Weight, 0, 10);
                    
                    // Update the original record
                    item.OriginalRecord.Content = item.Content;
                    item.OriginalRecord.Weight = item.Weight;
                    
                    // Save to database
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
                MessageBox.Show(
                    $"Failed to save records: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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
                    // Delete from database
                    var database = new ImportantRecordsDatabase(GetDatabasePath());
                    database.DeleteRecord(selectedItem.Id);
                    
                    // Remove from display
                    DisplayRecords.Remove(selectedItem);
                }
            }
        }

        private void UpdateUIForLanguage()
        {
            var langCode = _plugin.Settings.Language;
            Title = LanguageHelper.Get("RecordEditor.Title", langCode);
            
            if (FindName("Column_Id") is DataGridTextColumn columnId)
            {
                columnId.Header = LanguageHelper.Get("RecordEditor.Id", langCode);
            }
            if (FindName("Column_Content") is DataGridTextColumn columnContent)
            {
                columnContent.Header = LanguageHelper.Get("RecordEditor.Content", langCode);
            }
            if (FindName("Column_Weight") is DataGridTextColumn columnWeight)
            {
                columnWeight.Header = LanguageHelper.Get("RecordEditor.Weight", langCode);
            }
            if (FindName("Column_CreatedAt") is DataGridTextColumn columnCreatedAt)
            {
                columnCreatedAt.Header = LanguageHelper.Get("RecordEditor.CreatedAt", langCode);
            }
            if (FindName("Button_Save") is Button buttonSave)
            {
                buttonSave.Content = LanguageHelper.Get("RecordEditor.Save", langCode);
            }
            if (FindName("Button_Cancel") is Button buttonCancel)
            {
                buttonCancel.Content = LanguageHelper.Get("RecordEditor.Cancel", langCode);
            }
            if (FindName("Button_Delete") is Button buttonDelete)
            {
                buttonDelete.Content = LanguageHelper.Get("RecordEditor.Delete", langCode);
            }
        }

        private string GetDatabasePath()
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var dataPath = System.IO.Path.Combine(docPath, "VPetLLM", "Chat");
            return System.IO.Path.Combine(dataPath, "chat_history.db");
        }
    }
}
