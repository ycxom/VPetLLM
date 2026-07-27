using System.Reflection;
using VPetLLMUtils = VPetLLM.Utils.System;

namespace VPetLLM.Utils.Plugin
{
    /// <summary>
    /// 读取插件（含已禁用插件）的本地化描述，且**不产生任何副作用**。
    ///
    /// 背景：插件普遍把描述写成 <c>if (_vpetLLM is null) return 兜底文案;</c>，宿主引用只在
    /// Initialize() 里赋值。老实现为了拿这行字会把禁用插件真的启动一遍再关掉——于是每次打开
    /// 设置窗口，禁用的 OneBot 会去连 WS、RemoteChat 会连中继、ForegroundApp 会起监控线程，
    /// 而它们的 Unload 大多是 fire-and-forget，收不干净。
    ///
    /// 现在改为只把宿主引用临时写进插件的字段，读完立刻还原：插件的构造函数早已跑过，
    /// 这一步不执行插件的任何代码，纯粹让 Description 的 getter 走到本地化分支。
    /// </summary>
    public static class PluginDescriptionProbe
    {
        /// <summary>键为 "插件名|语言"。</summary>
        private static readonly Dictionary<string, string> _cache = new();

        /// <summary>反射查找结果缓存，键为插件类型；值为 null 表示该类型没有可注入的宿主字段。</summary>
        private static readonly Dictionary<Type, FieldInfo?> _hostFields = new();

        public static string Get(IVPetLLMPlugin plugin, global::VPetLLM.VPetLLM host, string langCode)
        {
            if (plugin is null) return string.Empty;

            try
            {
                // 已启用的插件本来就持有宿主引用，直接读。
                if (plugin.Enabled) return plugin.Description ?? string.Empty;

                var cacheKey = $"{plugin.Name}|{langCode}";
                if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

                // 首选：插件自己声明了无副作用的取描述方式。
                if (plugin is ILocalizedDescription localized)
                {
                    var text = localized.GetLocalizedDescription(host) ?? string.Empty;
                    _cache[cacheKey] = text;
                    return text;
                }

                // 兜底：临时注入宿主引用。取不到字段就用插件的未初始化文案，绝不启动插件。
                if (TryReadWithInjectedHost(plugin, host, out var probed))
                {
                    _cache[cacheKey] = probed;
                    return probed;
                }

                return plugin.Description ?? string.Empty;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Failed to read description for plugin '{plugin.Name}': {ex.Message}");
                try { return plugin.Description ?? string.Empty; } catch { return string.Empty; }
            }
        }

        /// <summary>插件被启用/禁用或语言变更后调用，丢弃可能过期的文案。</summary>
        public static void Invalidate(string pluginName)
        {
            if (string.IsNullOrEmpty(pluginName)) return;
            var prefix = pluginName + "|";
            foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _cache.Remove(key);
        }

        /// <summary>语言切换时整体失效。</summary>
        public static void InvalidateAll() => _cache.Clear();

        private static bool TryReadWithInjectedHost(IVPetLLMPlugin plugin, global::VPetLLM.VPetLLM host, out string description)
        {
            description = string.Empty;
            var field = ResolveHostField(plugin.GetType());
            if (field is null) return false;

            var original = field.GetValue(plugin);
            try
            {
                field.SetValue(plugin, host);
                description = plugin.Description ?? string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                VPetLLMUtils.Logger.Log($"Description probe failed for plugin '{plugin.Name}': {ex.Message}");
                return false;
            }
            finally
            {
                // 必须还原：插件仍是禁用状态，不能留下一个"半初始化"的宿主引用。
                try { field.SetValue(plugin, original); } catch { }
            }
        }

        private static FieldInfo? ResolveHostField(Type pluginType)
        {
            if (_hostFields.TryGetValue(pluginType, out var known)) return known;

            FieldInfo? found = null;
            for (var type = pluginType; type is not null && found is null; type = type.BaseType)
            {
                found = type
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(f => f.FieldType == typeof(global::VPetLLM.VPetLLM) && !f.IsInitOnly);
            }

            _hostFields[pluginType] = found;
            if (found is null)
                VPetLLMUtils.Logger.Log($"Plugin type '{pluginType.Name}' exposes no host field; using its uninitialized description.");
            return found;
        }
    }
}
