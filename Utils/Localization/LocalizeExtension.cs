using System.Windows.Data;
using System.Windows.Markup;

namespace VPetLLM.Utils.Localization
{
    [MarkupExtensionReturnType(typeof(string))]
    public class LocalizeExtension : MarkupExtension
    {
        public string KeyPath { get; set; }
        public string LangCode { get; set; } = "zh-hans";
        public string Prefix { get; set; }
        public string Suffix { get; set; }
        public string Default { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrWhiteSpace(KeyPath))
                return Default ?? $"[{KeyPath}]";

            var binding = new Binding($"[{KeyPath}]")
            {
                Source = LocalizationService.Instance,
                Mode = BindingMode.OneWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                IsAsync = false,
                TargetNullValue = Default ?? $"[{KeyPath}]",
                FallbackValue = Default ?? $"[{KeyPath}]",
            };

            var prefix = Prefix ?? string.Empty;
            var suffix = Suffix ?? string.Empty;
            if (!string.IsNullOrEmpty(prefix) || !string.IsNullOrEmpty(suffix))
            {
                binding.StringFormat = $"{prefix}{{0}}{suffix}";
            }

            return binding.ProvideValue(serviceProvider);
        }
    }
}