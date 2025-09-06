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
            if (originalMessage.Role == "assistant")
            {
                var match = Regex.Match(originalMessage.Content, @"say\s*\(\s*""([^""]*)""");
                Content = match.Success ? match.Groups[1].Value : originalMessage.Content;
            }
            else
            {
                Content = originalMessage.Content;
            }
        }
    }
    public partial class winContextEditor : Window
    {
        private readonly IChatCore _chatCore;
        public ObservableCollection<ContextEditorItem> DisplayHistory { get; set; }
        private List<Message> _originalHistory;

        public winContextEditor(IChatCore chatCore)
        {
            InitializeComponent();
            _chatCore = chatCore;

            _originalHistory = _chatCore.GetChatHistory();
            var displayItems = _originalHistory
                .Where(m => m.Role != "system")
                .Select(m => new ContextEditorItem(m));
            DisplayHistory = new ObservableCollection<ContextEditorItem>(displayItems);

            DataContext = this;
        }

        private void DataGrid_Context_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Row.DataContext is ContextEditorItem item)
            {
                // Prevent editing of non-assistant roles' content
                if (item.Role != "assistant" && e.Column.Header.ToString() == "内容")
                {
                    e.Cancel = true;
                }
            }
        }
        
        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in DisplayHistory)
            {
                if (item.Role == "assistant")
                {
                    var originalSayMatch = Regex.Match(item.OriginalMessage.Content, @"(say\s*\(\s*"")([^""]*)(""[^)]*\))");
                    if (originalSayMatch.Success && originalSayMatch.Groups[2].Value != item.Content)
                    {
                        string newText = item.Content.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        item.OriginalMessage.Content = originalSayMatch.Groups[1].Value + newText + originalSayMatch.Groups[3].Value;
                    }
                }
                else
                {
                    item.OriginalMessage.Content = item.Content;
                }
            }

            _chatCore.SetChatHistory(_originalHistory);
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
