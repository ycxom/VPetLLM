using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using VPetLLM.Utils.Localization;

namespace VPetLLM.UI.Windows
{
    public class ContextEditorItem : INotifyPropertyChanged
    {
        public Message OriginalMessage { get; }
        public string Role => OriginalMessage.Role;

        private string _content;
        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    OnPropertyChanged();
                }
            }
        }

        private byte[]? _imageData;
        public byte[]? ImageData
        {
            get => _imageData;
            private set
            {
                if (_imageData != value)
                {
                    _imageData = value;
                    _thumbnailSource = null; // 清除缓存
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasImage));
                    OnPropertyChanged(nameof(ThumbnailSource));
                }
            }
        }

        public bool HasImage => ImageData is not null && ImageData.Length > 0;

        private BitmapSource? _thumbnailSource;
        public BitmapSource? ThumbnailSource
        {
            get
            {
                if (_thumbnailSource is null && HasImage)
                {
                    _thumbnailSource = CreateThumbnail(ImageData!, 60);
                }
                return _thumbnailSource;
            }
        }

        public ContextEditorItem(Message originalMessage)
        {
            OriginalMessage = originalMessage;
            _content = originalMessage.Content ?? "";
            _imageData = originalMessage.ImageData;
        }

        /// <summary>
        /// 删除图像数据
        /// </summary>
        public void RemoveImage()
        {
            ImageData = null;
        }

        /// <summary>
        /// 创建缩略图
        /// </summary>
        private static BitmapSource? CreateThumbnail(byte[] imageData, int maxSize)
        {
            try
            {
                using var ms = new MemoryStream(imageData);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.DecodePixelHeight = maxSize;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Logger.Log($"创建缩略图失败: {ex.Message}");
                return null;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    public partial class winContextEditor : Window
    {
        private readonly VPetLLM _plugin;
        public ObservableCollection<ContextEditorItem> DisplayHistory { get; set; }
        private List<Message> _originalHistory;
        // 保存原始图像数据的备份，用于取消时恢复
        private Dictionary<Message, byte[]?> _originalImageBackup = new();

        public winContextEditor(VPetLLM plugin)
        {
            InitializeComponent();
            _plugin = plugin;

            _originalHistory = _plugin.ChatCore.GetChatHistory();

            // 备份原始图像数据
            foreach (var msg in _originalHistory)
            {
                _originalImageBackup[msg] = msg.ImageData;
            }

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
                // 同步文本内容
                item.OriginalMessage.Content = item.Content;
                // 同步图像数据（支持删除操作）
                item.OriginalMessage.ImageData = item.ImageData;
                newHistory.Add(item.OriginalMessage);
            }

            _plugin.ChatCore.SetChatHistory(newHistory);
            Logger.Log("上下文编辑器: 保存成功");
            Close();
        }

        private void Button_Cancel_Click(object sender, RoutedEventArgs e)
        {
            // 恢复原始图像数据
            foreach (var kvp in _originalImageBackup)
            {
                kvp.Key.ImageData = kvp.Value;
            }
            Logger.Log("上下文编辑器: 取消，已恢复原始数据");
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
            if (FindName("Column_Image") is DataGridTemplateColumn columnImage)
            {
                columnImage.Header = LanguageHelper.Get("ContextEditor.Image", langCode);
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

        /// <summary>
        /// 点击缩略图打开预览窗口
        /// </summary>
        private void Thumbnail_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Image image &&
                image.DataContext is ContextEditorItem item &&
                item.HasImage)
            {
                var preview = new winImagePreview(item.ImageData!);
                preview.Owner = this;
                preview.ShowDialog();
            }
        }

        /// <summary>
        /// 删除图像
        /// </summary>
        private void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ContextEditorItem item)
            {
                item.RemoveImage();
                Logger.Log("上下文编辑器: 图像已删除");
            }
        }
    }
}
