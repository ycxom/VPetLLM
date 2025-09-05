using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using VPetLLM.Core;

namespace VPetLLM
{
    public partial class winContextEditor : Window
    {
        private readonly IChatCore _chatCore;
        public ObservableCollection<Message> ChatHistory { get; set; }

        public winContextEditor(IChatCore chatCore)
        {
            _chatCore = chatCore;
            InitializeComponent();
            ChatHistory = new ObservableCollection<Message>(_chatCore.GetChatHistory());
            DataContext = this;
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            _chatCore.SetChatHistory(ChatHistory.ToList());
            Close();
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Button_Add_Click(object sender, RoutedEventArgs e)
        {
            ChatHistory.Add(new Message { Role = "user", Content = "" });
        }

        private void Button_Delete_Click(object sender, RoutedEventArgs e)
        {
            if (DataGrid_Context.SelectedItem is Message selectedMessage)
            {
                ChatHistory.Remove(selectedMessage);
            }
        }
    }
}
