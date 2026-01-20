// Plugin compatibility layer for backward compatibility
// This file provides conversion utilities between old and new plugin interfaces

using NewIActionPlugin = global::VPetLLM.Core.Abstractions.Interfaces.Plugin.IActionPlugin;
using NewIDynamicInfoPlugin = global::VPetLLM.Core.Abstractions.Interfaces.Plugin.IDynamicInfoPlugin;
using NewIPluginTakeover = global::VPetLLM.Core.Abstractions.Interfaces.Plugin.IPluginTakeover;
using NewIPluginWithData = global::VPetLLM.Core.Abstractions.Interfaces.Plugin.IPluginWithData;
using NewIVPetLLMPlugin = global::VPetLLM.Core.Abstractions.Interfaces.Plugin.IVPetLLMPlugin;

namespace VPetLLM.Core
{
    /// <summary>
    /// Legacy interface definitions for backward compatibility
    /// </summary>
    public interface IVPetLLMPlugin
    {
        string Name { get; }
        string Author { get; }
        string Description { get; }
        string Parameters { get; }
        string Examples { get; }
        bool Enabled { get; set; }
        string FilePath { get; set; }

        void Initialize(VPetLLM plugin);
        void Unload();
    }

    public interface IActionPlugin : IVPetLLMPlugin
    {
        Task<string> Function(string arguments);
    }

    public interface IPluginWithData : IVPetLLMPlugin
    {
        string PluginDataDir { get; set; }
    }

    public interface IDynamicInfoPlugin : IVPetLLMPlugin
    {
        string GetDynamicInfo();
    }

    public interface IPluginTakeover : IVPetLLMPlugin
    {
        bool SupportsTakeover { get; }
        Task<bool> BeginTakeoverAsync(string initialContent);
        Task<bool> ProcessTakeoverContentAsync(string content);
        Task<string> EndTakeoverAsync();
        bool ShouldEndTakeover(string content);
    }

    /// <summary>
    /// Adapter that wraps new-style plugins to work with the legacy interface system
    /// </summary>
    public class PluginAdapter : IVPetLLMPlugin, IActionPlugin, IPluginWithData, IDynamicInfoPlugin, IPluginTakeover
    {
        private readonly NewIVPetLLMPlugin _newPlugin;

        public PluginAdapter(NewIVPetLLMPlugin newPlugin)
        {
            _newPlugin = newPlugin;
        }

        // IVPetLLMPlugin implementation
        public string Name => _newPlugin.Name;
        public string Author => _newPlugin.Author;
        public string Description => _newPlugin.Description;
        public string Parameters => _newPlugin.Parameters;
        public string Examples => _newPlugin.Examples;
        public bool Enabled
        {
            get => _newPlugin.Enabled;
            set => _newPlugin.Enabled = value;
        }
        public string FilePath
        {
            get => _newPlugin.FilePath;
            set => _newPlugin.FilePath = value;
        }

        public void Initialize(VPetLLM plugin) => _newPlugin.Initialize(plugin);
        public void Unload() => _newPlugin.Unload();

        // IActionPlugin implementation
        public Task<string> Function(string arguments)
        {
            if (_newPlugin is NewIActionPlugin actionPlugin)
                return actionPlugin.Function(arguments);
            return Task.FromResult("Plugin does not support actions");
        }

        // IPluginWithData implementation
        public string PluginDataDir
        {
            get => (_newPlugin as NewIPluginWithData)?.PluginDataDir ?? "";
            set
            {
                if (_newPlugin is NewIPluginWithData pluginWithData)
                    pluginWithData.PluginDataDir = value;
            }
        }

        // IDynamicInfoPlugin implementation
        public string GetDynamicInfo()
        {
            if (_newPlugin is NewIDynamicInfoPlugin dynamicInfoPlugin)
                return dynamicInfoPlugin.GetDynamicInfo();
            return "";
        }

        // IPluginTakeover implementation
        public bool SupportsTakeover => (_newPlugin as NewIPluginTakeover)?.SupportsTakeover ?? false;

        public Task<bool> BeginTakeoverAsync(string initialContent)
        {
            if (_newPlugin is NewIPluginTakeover takeoverPlugin)
                return takeoverPlugin.BeginTakeoverAsync(initialContent);
            return Task.FromResult(false);
        }

        public Task<bool> ProcessTakeoverContentAsync(string content)
        {
            if (_newPlugin is NewIPluginTakeover takeoverPlugin)
                return takeoverPlugin.ProcessTakeoverContentAsync(content);
            return Task.FromResult(false);
        }

        public Task<string> EndTakeoverAsync()
        {
            if (_newPlugin is NewIPluginTakeover takeoverPlugin)
                return takeoverPlugin.EndTakeoverAsync();
            return Task.FromResult("");
        }

        public bool ShouldEndTakeover(string content)
        {
            if (_newPlugin is NewIPluginTakeover takeoverPlugin)
                return takeoverPlugin.ShouldEndTakeover(content);
            return true;
        }

        /// <summary>
        /// Gets the wrapped plugin instance
        /// </summary>
        public NewIVPetLLMPlugin WrappedPlugin => _newPlugin;
    }

    /// <summary>
    /// Utility methods for converting between plugin interface types
    /// </summary>
    public static class PluginCompatibility
    {
        /// <summary>
        /// Converts a new-style plugin to the legacy interface format
        /// </summary>
        public static IVPetLLMPlugin ToLegacy(NewIVPetLLMPlugin plugin)
        {
            return new PluginAdapter(plugin);
        }

        /// <summary>
        /// Converts a legacy plugin to the new interface format
        /// </summary>
        public static NewIVPetLLMPlugin ToNew(IVPetLLMPlugin plugin)
        {
            if (plugin is PluginAdapter adapter)
                return adapter.WrappedPlugin;

            throw new InvalidOperationException("Cannot convert legacy plugin to new format - plugin is not an adapter");
        }
    }
}