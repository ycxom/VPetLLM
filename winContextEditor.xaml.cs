using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPetLLM.Core;

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
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in DisplayHistory)
            {
                item.OriginalMessage.Content = item.Content;
            }

            _plugin.ChatCore.SetChatHistory(_originalHistory);
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
    }
}
