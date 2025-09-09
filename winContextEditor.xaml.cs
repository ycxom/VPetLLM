using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core;
using VPetLLM.Utils;

namespace VPetLLM
{
    public class ContextEditorItem
    {
        public Message OriginalMessage { get; }
        public string Role => OriginalMessage.Role;
        public string Content { get; set; }

        public ContextEditorItem(Message originalMessage)
        {
            OriginalMessage = originalMessage;
            Content = originalMessage.Content;
        }
    }
    public partial class winContextEditor : Window
    {
        private readonly VPetLLM _plugin;
        public ObservableCollection<ContextEditorItem> DisplayHistory { get; set; }
        private List<Message> _originalHistory;

        public winContextEditor(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            _originalHistory = _plugin.ChatCore.GetChatHistory();
            var displayItems = _originalHistory
                .Where(m => m.Role != "system")
                .Select(m => new ContextEditorItem(m));
            DisplayHistory = new ObservableCollection<ContextEditorItem>(displayItems);

            DataContext = this;
            UpdateUIForLanguage();
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
           var systemMessages = _originalHistory.Where(m => m.Role == "system").ToList();
           var newHistory = new List<Message>(systemMessages);

           foreach (var item in DisplayHistory)
           {
               item.OriginalMessage.Content = item.Content;
               newHistory.Add(item.OriginalMessage);
           }

           _plugin.ChatCore.SetChatHistory(newHistory);
            Close();
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Button_Add_Click(object sender, RoutedEventArgs e)
        {
            var newMessage = new Message { Role = "user", Content = "" };
            _originalHistory.Add(newMessage);
            DisplayHistory.Add(new ContextEditorItem(newMessage));
        }

        private void Button_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (DataGrid_Context.SelectedItem is ContextEditorItem selectedItem)
            {
                DisplayHistory.Remove(selectedItem);
                _originalHistory.Remove(selectedItem.OriginalMessage);
            }
        }

        private void UpdateUIForLanguage()
        {
            var langCode = _plugin.Settings.Language;
            Title = LanguageHelper.Get("ContextEditor.Title", langCode);
            if (FindName("Column_Role") is DataGridTextColumn columnRole)
            {
                columnRole.Header = LanguageHelper.Get("ContextEditor.Role", langCode);
            }
            if (FindName("Column_Content") is DataGridTextColumn columnContent)
            {
                columnContent.Header = LanguageHelper.Get("ContextEditor.Content", langCode);
            }
            if (FindName("Button_Save") is Button buttonSave)
            {
                buttonSave.Content = LanguageHelper.Get("ContextEditor.Save", langCode);
            }
            if (FindName("Button_Cancel") is Button buttonCancel)
            {
                buttonCancel.Content = LanguageHelper.Get("ContextEditor.Cancel", langCode);
            }
            if (FindName("Button_Add") is Button buttonAdd)
            {
                buttonAdd.Content = LanguageHelper.Get("ContextEditor.Add", langCode);
            }
            if (FindName("Button_Delete") is Button buttonDelete)
            {
                buttonDelete.Content = LanguageHelper.Get("ContextEditor.Delete", langCode);
            }
        }
    }
}
