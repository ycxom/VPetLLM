using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace VPetLLM.UI.Windows
{
    /// <summary>
    /// 上下文编辑器里的一条消息。
    ///
    /// 两种模式共享同一个实例，各自持有一份"真相"：
    /// 完全模式改 <see cref="Content"/>，简洁模式改 <see cref="Segments"/>。
    /// 切换模式时由窗口调用 <see cref="SyncToContent"/> / <see cref="RebuildSegments"/> 对齐，
    /// 避免两边各改各的最后打架。
    /// </summary>
    public class ContextEditorItem : INotifyPropertyChanged
    {
        /// <summary>历史里的原始消息对象。只有在用户点保存时才会被写入。</summary>
        public Message OriginalMessage { get; }

        /// <summary>
        /// 被摘出去的强调提示词尾巴（<c>[System: ...]</c>）。
        ///
        /// ChatCoreBase.CreateUserMessage 会把强调提示词直接拼在用户消息正文末尾并落库，
        /// 它是提示词而不是用户说的话——两种模式都不展示，保存时原样接回去。
        /// </summary>
        private readonly string _emphasisSuffix;

        public ContextEditorItem(Message originalMessage)
        {
            OriginalMessage = originalMessage;
            _role = originalMessage.Role ?? "user";

            var raw = originalMessage.Content ?? "";
            (_content, _emphasisSuffix) = SplitEmphasis(raw);

            // 这里刻意用 HasImage 而不是读 ImageData：后者会触发惰性加载，
            // 一开编辑器就把整段历史里的所有截图拉进内存。
            _hasImage = originalMessage.HasImage;

            RebuildSegments();
        }

        /// <summary>
        /// 把末尾的 <c>[System: ...]</c> 从正文里摘出来，返回 (可见正文, 提示词尾巴)。
        ///
        /// 用贪婪匹配并锚定行尾：强调提示词自身可能含有 ']'，非贪婪会从中间截断，
        /// 剩下半截照样漏进展示。
        /// </summary>
        private static (string Visible, string Emphasis) SplitEmphasis(string raw)
        {
            if (raw.Length == 0)
            {
                return (raw, "");
            }

            var match = EmphasisRegex.Match(raw);
            return match.Success
                ? (raw.Substring(0, match.Index), match.Value)
                : (raw, "");
        }

        private static readonly System.Text.RegularExpressions.Regex EmphasisRegex = new(
            @"\[System:\s*.*\]\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.Singleline);

        // ── 角色 ────────────────────────────────────────────────────────────

        private string _role;
        public string Role
        {
            get => _role;
            set
            {
                if (_role != value)
                {
                    _role = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsAssistant));
                    OnPropertyChanged(nameof(IsUser));
                    OnPropertyChanged(nameof(RoleDisplay));
                    OnPropertyChanged(nameof(AvatarInitial));
                }
            }
        }

        /// <summary>Gemini 用 "model" 表示助手，这里统一按 assistant 处理</summary>
        public bool IsAssistant =>
            Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
            Role.Equals("model", StringComparison.OrdinalIgnoreCase);

        public bool IsUser => !IsAssistant;

        /// <summary>气泡上方显示的名字，由窗口注入（AI 名 / 用户名）</summary>
        private string _aiName = "Assistant";
        private string _userName = "You";
        public void SetDisplayNames(string aiName, string userName)
        {
            _aiName = string.IsNullOrWhiteSpace(aiName) ? "Assistant" : aiName;
            _userName = string.IsNullOrWhiteSpace(userName) ? "You" : userName;
            OnPropertyChanged(nameof(RoleDisplay));
            OnPropertyChanged(nameof(AvatarInitial));
        }

        public string RoleDisplay => IsAssistant ? _aiName : _userName;

        public string AvatarInitial
        {
            get
            {
                var name = RoleDisplay;
                return string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            }
        }

        // ── 内容 ────────────────────────────────────────────────────────────

        private string _content;
        /// <summary>消息原文，含全部命令标记。完全模式直接绑定这里。</summary>
        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsEmpty));
                }
            }
        }

        public bool IsEmpty => string.IsNullOrWhiteSpace(Content);

        /// <summary>简洁模式下的可视片段</summary>
        public ObservableCollection<ContextSegment> Segments { get; } = new();

        /// <summary>由 <see cref="Content"/> 重新解析片段（进入简洁模式时调用）</summary>
        public void RebuildSegments()
        {
            Segments.Clear();
            foreach (var segment in ContextReplyRenderer.Parse(Content))
            {
                Segments.Add(segment);
            }

            // 空消息（比如刚点"+ 用户消息"新建的那条）解析不出任何片段，
            // 简洁模式下就会连一个输入框都没有，只能被迫切到完全模式才能打字。
            // 补一个空文本片段当作光标落点；它 Compose 出来仍然是空串。
            if (Segments.Count == 0)
            {
                Segments.Add(new ContextSegment { Kind = ContextSegmentKind.Text, Text = "" });
            }
        }

        /// <summary>把片段拼回 <see cref="Content"/>（离开简洁模式或保存时调用）</summary>
        public void SyncToContent()
        {
            Content = ContextReplyRenderer.Compose(Segments);
        }

        // ── 模式 ────────────────────────────────────────────────────────────

        private bool _isSimpleMode = true;
        public bool IsSimpleMode
        {
            get => _isSimpleMode;
            set
            {
                if (_isSimpleMode != value)
                {
                    _isSimpleMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsFullMode));
                }
            }
        }

        public bool IsFullMode => !_isSimpleMode;

        // ── 图像 ────────────────────────────────────────────────────────────

        private bool _hasImage;
        public bool HasImage
        {
            get => _hasImage;
            private set
            {
                if (_hasImage != value)
                {
                    _hasImage = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 用户是否在本次编辑中删掉了图像。
        ///
        /// 只有这个标志为 true 时保存才会去动 Message.ImageData——因为它的 setter 会顺手
        /// 把 ImageId 清空，无差别回写等于让每条带图消息在保存时重新落一份图片文件。
        /// </summary>
        public bool ImageRemoved { get; private set; }

        public void RemoveImage()
        {
            if (!HasImage) return;

            ImageRemoved = true;
            HasImage = false;
            _thumbnailSource = null;
            OnPropertyChanged(nameof(ThumbnailSource));
        }

        private BitmapSource? _thumbnailSource;
        private bool _thumbnailAttempted;
        /// <summary>
        /// 缩略图按需生成：配合列表虚拟化，只有真正滚到眼前的消息才会读图解码。
        /// </summary>
        public BitmapSource? ThumbnailSource
        {
            get
            {
                if (_thumbnailSource is null && HasImage && !_thumbnailAttempted)
                {
                    _thumbnailAttempted = true;
                    _thumbnailSource = CreateThumbnail(OriginalMessage.ImageData, 96);
                }
                return _thumbnailSource;
            }
        }

        /// <summary>点开大图时才需要完整数据</summary>
        public byte[]? GetFullImage() => HasImage ? OriginalMessage.ImageData : null;

        private static BitmapSource? CreateThumbnail(byte[]? imageData, int maxSize)
        {
            if (imageData is null || imageData.Length == 0)
            {
                return null;
            }

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
                Logger.Log($"上下文编辑器: 创建缩略图失败: {ex.Message}");
                return null;
            }
        }

        // ── 保存 ────────────────────────────────────────────────────────────

        /// <summary>把编辑结果写回真正的 Message 对象。仅在用户确认保存时调用。</summary>
        public void ApplyTo()
        {
            OriginalMessage.Role = Role;

            // 强调提示词接回原位：它没在界面上出现过，用户也就无从决定它的去留，
            // 保存时必须还原，否则一次"什么都没改"的保存就把它从历史里抹掉了。
            OriginalMessage.Content = Content + _emphasisSuffix;

            if (ImageRemoved)
            {
                OriginalMessage.ImageData = null;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
