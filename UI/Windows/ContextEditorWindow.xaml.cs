using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VPetLLM.Core;

namespace VPetLLM.UI.Windows
{
    public partial class ContextEditorWindow : Window
    {
        private readonly IChatCore _chatCore;
        private List<Core.Message> _originalHistory;

        public ContextEditorWindow(IChatCore chatCore)
        {
            _chatCore = chatCore;
            InitializeComponent();
            LoadHistory();
        }

        private void LoadHistory()
        {
            try
            {
                _originalHistory = _chatCore.GetHistoryForEditing();
                DataGrid_History.ItemsSource = _originalHistory.ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载历史记录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void Button_Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var editedHistory = DataGrid_History.ItemsSource as List<Message> ?? new List<Message>();
                
                // 验证数据
                foreach (var message in editedHistory)
                {
                    if (string.IsNullOrWhiteSpace(message.Role) || string.IsNullOrWhiteSpace(message.Content))
                    {
                        MessageBox.Show("角色和内容不能为空", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                _chatCore.UpdateHistory(editedHistory);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存历史记录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}