using System.ComponentModel;

namespace VPetLLM.Utils.Localization
{
    /// <summary>
    /// 简单的本地化服务：提供索引器以供绑定使用，并在语言切换时通知 UI 刷新。
    /// </summary>
    public sealed class LocalizationService : INotifyPropertyChanged
    {
        private static readonly Lazy<LocalizationService> _lazy = new(() => new LocalizationService());
        public static LocalizationService Instance => _lazy.Value;

        private string _langCode = "zh-hans";

        /// <summary>
        /// 当前语言代码（例如 zh-hans / en-us 等）
        /// </summary>
        public string LangCode
        {
            get => _langCode;
            set
            {
                if (!string.Equals(_langCode, value, StringComparison.OrdinalIgnoreCase))
                {
                    _langCode = value;
                    // 语言变化时，通知所有使用索引器绑定的控件刷新
                    OnIndexerChanged();
                }
            }
        }

        private LocalizationService() { }

        /// <summary>
        /// 通过索引器获取本地化文本，供 Binding 使用：Path="[{key}]"
        /// </summary>
        /// <summary>取词条；缺失时返回 null，让绑定的 TargetNullValue（XAML 的 Default=）接手。</summary>
        public string? this[string key]
        {
            get
            {
                if (string.IsNullOrWhiteSpace(key))
                    return null;
                // 缺词条时必须返回 null，绑定的 TargetNullValue/FallbackValue（即 XAML 里的
                // Default=）才会接手。用 LanguageHelper.Get 会拿到 "[key]" 占位串，
                // 那是非 null 的，兜底就永远不生效了。
                return LanguageHelper.GetOrNull(key, LangCode);
            }
        }

        /// <summary>
        /// 主动切换语言并刷新绑定（无论值是否相同，始终广播，确保所有绑定刷新）。
        /// </summary>
        public void ChangeLanguage(string langCode)
        {
            // 直接赋值，不做相等性短路，确保每次调用都会触发刷新
            _langCode = langCode;
            OnIndexerChanged();
        }

        /// <summary>
        /// 当外部语言资源变化但 LangCode 未变更时，可调用以强制刷新所有绑定。
        /// </summary>
        public void Refresh()
        {
            OnIndexerChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnIndexerChanged()
        {
            // 对于索引器，通知 "Item[]" 可触发所有 Path="[{key}]" 的绑定刷新
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }
}