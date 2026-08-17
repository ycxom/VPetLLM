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
        public Message OriginalMessage { get; private set; }

        /// <summary>
        /// 被摘出去的强调提示词尾巴（<c>[System: ...]</c>）。
        ///
        /// ChatCoreBase.CreateUserMessage 会把强调提示词直接拼在用户消息正文末尾并落库，
        /// 它是提示词而不是用户说的话——两种模式都不展示，保存时原样接回去。
        /// </summary>
        private string _emphasisSuffix;
        private bool _suppressDirty;
        private bool _isLoaded = true;
        private bool _isDirty;

        public ContextEditorItem(Message originalMessage)
        {
            OriginalMessage = originalMessage;
            _role = originalMessage.Role ?? "user";

            var raw = originalMessage.Content ?? "";
            (_content, _emphasisSuffix) = SplitEmphasis(raw);

            // 这里刻意用 HasImage 而不是读 ImageData：后者会触发惰性加载，
            // 一开编辑器就把整段历史里的所有截图拉进内存。
            _hasImage = originalMessage.HasImage;

            ClassifySource();
            RebuildSegments();
        }

        public ContextEditorItem(int index, string role)
        {
            Index = index;
            OriginalMessage = new Message { Role = role, Content = "" };
            _role = role;
            _content = "";
            _emphasisSuffix = "";
            _isLoaded = false;
        }

        public int Index { get; private set; }
        public void SetIndex(int index) => Index = index;
        public bool IsLoaded
        {
            get => _isLoaded;
            private set
            {
                if (_isLoaded != value)
                {
                    _isLoaded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsLoading));
                    // 壳子阶段没有正文可判来源，收纳开关也不该出现
                    NotifyCollapseChanged();
                }
            }
        }

        public bool IsLoading => !IsLoaded;
        public bool IsDirty => _isDirty;
        public bool IsDeleted { get; private set; }

        /// <summary>
        /// 已有一次取数在飞、正等着往这条上写。
        ///
        /// 用它做去重：滚动时同一批壳会被多个容器的 Loaded 同时点名，
        /// 没有这个标记就会对同一段发出好几次重复查询。
        /// </summary>
        public bool LoadRequested { get; set; }

        public void MarkDeleted()
        {
            IsDeleted = true;
            _isDirty = true;
            OnPropertyChanged(nameof(IsDeleted));
        }

        public void LoadFrom(Message message)
        {
            _suppressDirty = true;
            OriginalMessage = message;
            _role = message.Role ?? "user";
            var raw = message.Content ?? "";
            (_content, _emphasisSuffix) = SplitEmphasis(raw);
            _hasImage = message.HasImage;
            _thumbnailSource = null;
            _thumbnailAttempted = false;
            IsLoaded = true;
            // 先分类再建片段：分类会重置收纳初值，而 CanCollapse 依赖 IsLoaded
            ClassifySource();
            RebuildSegments();
            OnPropertyChanged(nameof(Role));
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(IsAssistant));
            OnPropertyChanged(nameof(IsUser));
            OnPropertyChanged(nameof(ShowUserAvatar));
            OnPropertyChanged(nameof(RoleDisplay));
            OnPropertyChanged(nameof(AvatarInitial));
            _suppressDirty = false;
        }

        public void Unload()
        {
            if (!IsLoaded || IsDirty)
            {
                return;
            }

            OriginalMessage = new Message { Role = Role, Content = "" };
            _content = "";
            _hasImage = false;
            Segments.Clear();
            _thumbnailSource = null;
            _thumbnailAttempted = false;
            // 卸载后正文为空，判定结果必须一起清掉：否则滚回来的壳子会顶着
            // 上一条的来源名，而正文还没载入
            _isSystemGenerated = false;
            _sourceName = "";
            _isCollapsed = false;
            IsLoaded = false;
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(IsSystemGenerated));
            OnPropertyChanged(nameof(ShowUserAvatar));
            OnPropertyChanged(nameof(RoleDisplay));
            OnPropertyChanged(nameof(AvatarInitial));
        }

        public void MarkClean()
        {
            _isDirty = false;
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
                    if (!_suppressDirty && IsLoaded) _isDirty = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsAssistant));
                    OnPropertyChanged(nameof(IsUser));
                    OnPropertyChanged(nameof(ShowUserAvatar));
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

        /// <summary>
        /// 是否画用户头像。
        ///
        /// 系统灌入的消息虽然也是 user 角色，但不该挂用户头像、也不该贴在用户那一侧 ——
        /// 两个头像都不画时中间那列自动撑满，于是它自成一条通栏的"通知"，
        /// 一眼就能和"用户说的"/"桌宠说的"区分开。这样做不必改 <see cref="IsUser"/>，
        /// 它还被气泡配色、保存逻辑等处依赖。
        /// </summary>
        public bool ShowUserAvatar => IsUser && !IsSystemGenerated;

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

        /// <summary>
        /// 气泡上方的名字。
        ///
        /// user 角色里混着两种东西：用户真正打的字，和插件回执／看屏幕／触摸事件这类
        /// 系统灌入。后者挂用户名是错的——那些话用户从没说过，标成他的名字会让人
        /// 在编辑器里以为自己说过，也无法一眼看出这条是谁产生的。所以系统灌入显示
        /// 来源名（插件名或功能名）。
        /// </summary>
        public string RoleDisplay => IsAssistant
            ? _aiName
            : (IsSystemGenerated ? _sourceName : _userName);

        public string AvatarInitial
        {
            get
            {
                var name = RoleDisplay;
                return string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            }
        }

        // ── 来源识别与收纳 ──────────────────────────────────────────────────

        private bool _isSystemGenerated;
        private string _sourceName = "";

        /// <summary>
        /// 这条 user 消息是系统／插件灌入的，不是用户打的字。
        /// </summary>
        public bool IsSystemGenerated => _isSystemGenerated;

        /// <summary>
        /// 判定用的是**载入时**的原文，之后用户在完全模式怎么改都不重新判定。
        /// 否则边打字边变名字、边折叠，光标还会因为模板切换而丢。
        /// </summary>
        private void ClassifySource()
        {
            var previousSystem = _isSystemGenerated;
            var previousName = _sourceName;

            (_isSystemGenerated, _sourceName) = IsAssistant
                ? (false, "")
                : ContextSourceClassifier.Classify(_content);

            // 收纳的初值跟着判定走：系统灌入默认收起，用户消息不收
            _isCollapsed = _isSystemGenerated;

            if (previousSystem != _isSystemGenerated || previousName != _sourceName)
            {
                OnPropertyChanged(nameof(IsSystemGenerated));
                OnPropertyChanged(nameof(ShowUserAvatar));
                OnPropertyChanged(nameof(RoleDisplay));
                OnPropertyChanged(nameof(AvatarInitial));
            }

            NotifyCollapseChanged();
        }

        private bool _isCollapsed;

        /// <summary>
        /// 用户点过展开／收起。仅对系统灌入的消息有意义。
        /// </summary>
        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                if (_isCollapsed != value)
                {
                    _isCollapsed = value;
                    OnPropertyChanged();
                    NotifyCollapseChanged();
                }
            }
        }

        public void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

        /// <summary>
        /// 是否给这条提供收纳开关。
        ///
        /// 完全模式一律不收纳：那个模式的用途就是直面原文，把内容藏起来与它相悖，
        /// 而且原文编辑框正在双向绑定 Content，藏掉它等于让用户改不了。
        /// </summary>
        public bool CanCollapse => IsSystemGenerated && IsSimpleMode && IsLoaded;

        /// <summary>当前是否以收起形态显示（只出一行摘要）</summary>
        public bool IsCollapsedView => CanCollapse && IsCollapsed;

        /// <summary>简洁模式下是否展示渲染后的片段</summary>
        public bool ShowSegments => IsSimpleMode && !IsCollapsedView;

        /// <summary>收起时附图也一起藏掉，否则"收起"了却还占着一大块缩略图</summary>
        public bool ShowImage => HasImage && !IsCollapsedView;

        public string CollapseGlyph => IsCollapsed ? "▸" : "▾";

        /// <summary>
        /// 收起时显示的一行摘要：去掉来源标记本身，压平换行，截断到 ~80 字。
        /// </summary>
        public string CollapsedPreview => ContextSourceClassifier.BuildPreview(_content);

        private void NotifyCollapseChanged()
        {
            OnPropertyChanged(nameof(CanCollapse));
            OnPropertyChanged(nameof(IsCollapsedView));
            OnPropertyChanged(nameof(ShowSegments));
            OnPropertyChanged(nameof(ShowImage));
            OnPropertyChanged(nameof(CollapseGlyph));
            OnPropertyChanged(nameof(CollapsedPreview));
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
                    if (!_suppressDirty && IsLoaded) _isDirty = true;
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
                    // 完全模式不收纳，所以模式一变，收纳相关的可见性全要重算
                    NotifyCollapseChanged();
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
            _isDirty = true;
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
                if (!IsLoaded || (_thumbnailSource is null && HasImage && !_thumbnailAttempted))
                {
                    if (!IsLoaded) return null;
                    _thumbnailAttempted = true;
                    _thumbnailSource = CreateThumbnail(OriginalMessage.ImageData, 96);
                }
                return _thumbnailSource;
            }
        }

        /// <summary>点开大图时才需要完整数据</summary>
        public byte[]? GetFullImage() => IsLoaded && HasImage ? OriginalMessage.ImageData : null;

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
            if (!IsLoaded)
            {
                throw new InvalidOperationException("Cannot apply an unloaded context item.");
            }
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
