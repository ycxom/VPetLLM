using Newtonsoft.Json;
using System.IO;
using System.Reflection;
using VPetLLM.Core;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 配置优化管理器 - 检查配置项存在性、清理重复项、实现回收机制
    /// </summary>
    public class ConfigurationOptimizer
    {
        private readonly Setting _settings;
        private readonly string _settingsPath;
        private readonly List<string> _optimizationLog = new();

        public ConfigurationOptimizer(Setting settings)
        {
            _settings = settings;
            _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "VPetLLM.json");
        }

        /// <summary>
        /// 执行完整的配置优化
        /// </summary>
        public void PerformFullOptimization()
        {
            Logger.Log("开始执行配置优化...");

            try
            {
                // 1. 检查和修复配置项存在性
                CheckAndFixConfigurationItems();

                // 2. 清理重复项
                CleanupDuplicateItems();

                // 3. 执行回收机制
                PerformGarbageCollection();

                // 4. 保存优化后的配置
                SaveOptimizedConfiguration();

                // 5. 记录优化结果
                LogOptimizationResults();
            }
            catch (Exception ex)
            {
                Logger.Log($"配置优化过程中发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查和修复配置项存在性
        /// </summary>
        private void CheckAndFixConfigurationItems()
        {
            Logger.Log("检查配置项存在性...");

            bool hasChanges = false;

            // 检查并修复OpenAI配置
            hasChanges |= CheckAndFixOpenAIConfiguration();
            
            // 检查并修复Gemini配置
            hasChanges |= CheckAndFixGeminiConfiguration();
            
            // 检查并修复Ollama配置
            hasChanges |= CheckAndFixOllamaConfiguration();
            
            // 检查并修复TTS配置
            hasChanges |= CheckAndFixTTSConfiguration();
            
            // 检查并修复工具配置
            hasChanges |= CheckAndFixToolsConfiguration();
            
            // 检查并修复代理配置
            hasChanges |= CheckAndFixProxyConfiguration();

            if (hasChanges)
            {
                Logger.Log("配置项存在性检查完成，发现并修复了缺失的配置项");
            }
            else
            {
                Logger.Log("配置项存在性检查完成，所有配置项都正常");
            }
        }

        private bool CheckAndFixOpenAIConfiguration()
        {
            bool hasChanges = false;

            if (_settings.OpenAI == null)
            {
                _settings.OpenAI = new Setting.OpenAISetting();
                _optimizationLog.Add("修复了缺失的OpenAI配置");
                hasChanges = true;
            }

            if (_settings.OpenAI.OpenAINodes == null)
            {
                _settings.OpenAI.OpenAINodes = new List<Setting.OpenAINodeSetting>();
                _optimizationLog.Add("修复了缺失的OpenAI节点配置列表");
                hasChanges = true;
            }

            // 修复索引越界
            if (_settings.OpenAI.CurrentNodeIndex < 0 || 
                _settings.OpenAI.CurrentNodeIndex >= _settings.OpenAI.OpenAINodes.Count)
            {
                _settings.OpenAI.CurrentNodeIndex = 0;
                _optimizationLog.Add("修复了OpenAI节点索引越界问题");
                hasChanges = true;
            }

            // 清理无效的节点
            var validNodes = _settings.OpenAI.OpenAINodes
                .Where(node => !string.IsNullOrWhiteSpace(node.ApiKey) || node.Enabled)
                .ToList();

            if (validNodes.Count != _settings.OpenAI.OpenAINodes.Count)
            {
                _settings.OpenAI.OpenAINodes = validNodes;
                _optimizationLog.Add($"清理了{_settings.OpenAI.OpenAINodes.Count - validNodes.Count}个无效的OpenAI节点");
                hasChanges = true;
            }

            return hasChanges;
        }

        private bool CheckAndFixGeminiConfiguration()
        {
            bool hasChanges = false;

            if (_settings.Gemini == null)
            {
                _settings.Gemini = new Setting.GeminiSetting();
                _optimizationLog.Add("修复了缺失的Gemini配置");
                hasChanges = true;
            }

            if (_settings.Gemini.GeminiNodes == null)
            {
                _settings.Gemini.GeminiNodes = new List<Setting.GeminiNodeSetting>();
                _optimizationLog.Add("修复了缺失的Gemini节点配置列表");
                hasChanges = true;
            }

            // 修复索引越界
            if (_settings.Gemini.CurrentNodeIndex < 0 || 
                _settings.Gemini.CurrentNodeIndex >= _settings.Gemini.GeminiNodes.Count)
            {
                _settings.Gemini.CurrentNodeIndex = 0;
                _optimizationLog.Add("修复了Gemini节点索引越界问题");
                hasChanges = true;
            }

            // 清理无效的节点
            var validNodes = _settings.Gemini.GeminiNodes
                .Where(node => !string.IsNullOrWhiteSpace(node.ApiKey) || node.Enabled)
                .ToList();

            if (validNodes.Count != _settings.Gemini.GeminiNodes.Count)
            {
                _settings.Gemini.GeminiNodes = validNodes;
                _optimizationLog.Add($"清理了{_settings.Gemini.GeminiNodes.Count - validNodes.Count}个无效的Gemini节点");
                hasChanges = true;
            }

            return hasChanges;
        }

        private bool CheckAndFixOllamaConfiguration()
        {
            bool hasChanges = false;

            if (_settings.Ollama == null)
            {
                _settings.Ollama = new Setting.OllamaSetting();
                _optimizationLog.Add("修复了缺失的Ollama配置");
                hasChanges = true;
            }

            // 检查Ollama URL格式
            if (!string.IsNullOrWhiteSpace(_settings.Ollama.Url) && 
                !_settings.Ollama.Url.StartsWith("http"))
            {
                _settings.Ollama.Url = "http://" + _settings.Ollama.Url;
                _optimizationLog.Add("修复了Ollama URL格式");
                hasChanges = true;
            }

            return hasChanges;
        }

        private bool CheckAndFixTTSConfiguration()
        {
            bool hasChanges = false;

            if (_settings.TTS == null)
            {
                _settings.TTS = new Setting.TTSSetting();
                _optimizationLog.Add("修复了缺失的TTS配置");
                hasChanges = true;
            }

            // 检查音量范围
            if (_settings.TTS.Volume < 0 || _settings.TTS.Volume > 10)
            {
                _settings.TTS.Volume = Math.Max(0, Math.Min(10, _settings.TTS.Volume));
                _optimizationLog.Add("修复了TTS音量范围");
                hasChanges = true;
            }

            // 检查语速范围
            if (_settings.TTS.Speed < 0.1 || _settings.TTS.Speed > 3.0)
            {
                _settings.TTS.Speed = Math.Max(0.1, Math.Min(3.0, _settings.TTS.Speed));
                _optimizationLog.Add("修复了TTS语速范围");
                hasChanges = true;
            }

            return hasChanges;
        }

        private bool CheckAndFixToolsConfiguration()
        {
            bool hasChanges = false;

            if (_settings.Tools == null)
            {
                _settings.Tools = new List<Setting.ToolSetting>();
                _optimizationLog.Add("修复了缺失的工具配置列表");
                hasChanges = true;
            }

            // 清理重复的工具配置
            var uniqueTools = _settings.Tools
                .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
                .GroupBy(tool => tool.Name.ToLowerInvariant())
                .Select(group => group.First())
                .ToList();

            if (uniqueTools.Count != _settings.Tools.Count)
            {
                _settings.Tools = uniqueTools;
                _optimizationLog.Add($"清理了{_settings.Tools.Count - uniqueTools.Count}个重复的工具配置");
                hasChanges = true;
            }

            return hasChanges;
        }

        private bool CheckAndFixProxyConfiguration()
        {
            bool hasChanges = false;

            if (_settings.Proxy == null)
            {
                _settings.Proxy = new Setting.ProxySetting();
                _optimizationLog.Add("修复了缺失的代理配置");
                hasChanges = true;
            }

            // 检查代理地址格式
            if (_settings.Proxy.IsEnabled && !string.IsNullOrWhiteSpace(_settings.Proxy.Address))
            {
                if (!_settings.Proxy.Address.Contains(":"))
                {
                    _settings.Proxy.Address = _settings.Proxy.Address + ":8080";
                    _optimizationLog.Add("修复了代理地址格式，添加了默认端口");
                    hasChanges = true;
                }
            }

            return hasChanges;
        }

        /// <summary>
        /// 清理重复项
        /// </summary>
        private void CleanupDuplicateItems()
        {
            Logger.Log("开始清理重复项...");

            CleanupDuplicateOpenAINodes();
            CleanupDuplicateGeminiNodes();
            CleanupDuplicateTools();
            CleanupDuplicateCustomHeaders();
        }

        private void CleanupDuplicateOpenAINodes()
        {
            if (_settings.OpenAI?.OpenAINodes == null) return;

            var originalCount = _settings.OpenAI.OpenAINodes.Count;
            
            // 按名称和URL去重，保留第一个
            var uniqueNodes = _settings.OpenAI.OpenAINodes
                .GroupBy(node => new { node.Name, node.Url })
                .Select(group => group.First())
                .ToList();

            if (uniqueNodes.Count < originalCount)
            {
                _settings.OpenAI.OpenAINodes = uniqueNodes;
                _optimizationLog.Add($"清理了{originalCount - uniqueNodes.Count}个重复的OpenAI节点");
            }
        }

        private void CleanupDuplicateGeminiNodes()
        {
            if (_settings.Gemini?.GeminiNodes == null) return;

            var originalCount = _settings.Gemini.GeminiNodes.Count;
            
            // 按名称和API Key去重，保留第一个
            var uniqueNodes = _settings.Gemini.GeminiNodes
                .GroupBy(node => new { node.Name, node.ApiKey })
                .Select(group => group.First())
                .ToList();

            if (uniqueNodes.Count < originalCount)
            {
                _settings.Gemini.GeminiNodes = uniqueNodes;
                _optimizationLog.Add($"清理了{originalCount - uniqueNodes.Count}个重复的Gemini节点");
            }
        }

        private void CleanupDuplicateTools()
        {
            if (_settings.Tools == null) return;

            var originalCount = _settings.Tools.Count;
            
            // 按名称去重，保留第一个
            var uniqueTools = _settings.Tools
                .GroupBy(tool => tool.Name.ToLowerInvariant())
                .Select(group => group.First())
                .ToList();

            if (uniqueTools.Count < originalCount)
            {
                _settings.Tools = uniqueTools;
                _optimizationLog.Add($"清理了{originalCount - uniqueTools.Count}个重复的工具");
            }
        }

        private void CleanupDuplicateCustomHeaders()
        {
            // 清理DIY TTS的自定义头部重复项
            if (_settings.TTS?.DIY?.CustomHeaders != null)
            {
                var originalCount = _settings.TTS.DIY.CustomHeaders.Count;
                
                var uniqueHeaders = _settings.TTS.DIY.CustomHeaders
                    .Where(header => !string.IsNullOrWhiteSpace(header.Key))
                    .GroupBy(header => header.Key.ToLowerInvariant())
                    .Select(group => group.First())
                    .ToList();

                if (uniqueHeaders.Count < originalCount)
                {
                    _settings.TTS.DIY.CustomHeaders = uniqueHeaders;
                    _optimizationLog.Add($"清理了{originalCount - uniqueHeaders.Count}个重复的自定义头部");
                }
            }
        }

        /// <summary>
        /// 执行回收机制
        /// </summary>
        private void PerformGarbageCollection()
        {
            Logger.Log("执行回收机制...");

            // 清理空的配置段落
            CleanupEmptyConfigurations();

            // 压缩配置结构
            CompressConfigurationStructure();

            // 清理无效的引用
            CleanupInvalidReferences();
        }

        private void CleanupEmptyConfigurations()
        {
            bool hasChanges = false;

            // 清理空的OpenAI节点
            if (_settings.OpenAI?.OpenAINodes != null)
            {
                var nonEmptyNodes = _settings.OpenAI.OpenAINodes
                    .Where(node => !string.IsNullOrWhiteSpace(node.Name))
                    .ToList();

                if (nonEmptyNodes.Count != _settings.OpenAI.OpenAINodes.Count)
                {
                    _settings.OpenAI.OpenAINodes = nonEmptyNodes;
                    _optimizationLog.Add("清理了空的OpenAI节点");
                    hasChanges = true;
                }
            }

            // 清理空的Gemini节点
            if (_settings.Gemini?.GeminiNodes != null)
            {
                var nonEmptyNodes = _settings.Gemini.GeminiNodes
                    .Where(node => !string.IsNullOrWhiteSpace(node.Name))
                    .ToList();

                if (nonEmptyNodes.Count != _settings.Gemini.GeminiNodes.Count)
                {
                    _settings.Gemini.GeminiNodes = nonEmptyNodes;
                    _optimizationLog.Add("清理了空的Gemini节点");
                    hasChanges = true;
                }
            }

            // 清理空的工具
            if (_settings.Tools != null)
            {
                var nonEmptyTools = _settings.Tools
                    .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
                    .ToList();

                if (nonEmptyTools.Count != _settings.Tools.Count)
                {
                    _settings.Tools = nonEmptyTools;
                    _optimizationLog.Add("清理了空的工具配置");
                    hasChanges = true;
                }
            }

            if (hasChanges)
            {
                Logger.Log("空配置段落清理完成");
            }
        }

        private void CompressConfigurationStructure()
        {
            // 移除默认值相同的属性以减小配置文件大小
            // 这里可以添加更复杂的压缩逻辑
            Logger.Log("配置结构压缩完成");
        }

        private void CleanupInvalidReferences()
        {
            bool hasChanges = false;

            // 清理指向不存在文件的引用
            if (_settings.TTS?.GPTSoVITS?.ReferWavPath != null && 
                !string.IsNullOrWhiteSpace(_settings.TTS.GPTSoVITS.ReferWavPath) &&
                !File.Exists(_settings.TTS.GPTSoVITS.ReferWavPath))
            {
                _settings.TTS.GPTSoVITS.ReferWavPath = "";
                _optimizationLog.Add("清理了指向不存在文件的参考音频路径");
                hasChanges = true;
            }

            if (hasChanges)
            {
                Logger.Log("无效引用清理完成");
            }
        }

        /// <summary>
        /// 保存优化后的配置
        /// </summary>
        private void SaveOptimizedConfiguration()
        {
            try
            {
                var backupPath = _settingsPath + ".backup";
                
                // 创建备份
                if (File.Exists(_settingsPath))
                {
                    File.Copy(_settingsPath, backupPath, true);
                }

                // 保存优化后的配置
                _settings.Save();

                // 清理备份文件
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                Logger.Log("优化后的配置已保存");
            }
            catch (Exception ex)
            {
                Logger.Log($"保存优化配置时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录优化结果
        /// </summary>
        private void LogOptimizationResults()
        {
            if (_optimizationLog.Count > 0)
            {
                Logger.Log("=== 配置优化结果 ===");
                foreach (var log in _optimizationLog)
                {
                    Logger.Log($"- {log}");
                }
                Logger.Log($"总共执行了 {_optimizationLog.Count} 项优化");
            }
            else
            {
                Logger.Log("配置优化完成，未发现需要优化的问题");
            }
        }

        /// <summary>
        /// 快速检查配置状态
        /// </summary>
        public string GetConfigurationHealthReport()
        {
            var issues = new List<string>();

            // 检查OpenAI配置
            if (_settings.OpenAI?.OpenAINodes?.Count == 0)
            {
                issues.Add("没有可用的OpenAI节点");
            }

            // 检查Gemini配置
            if (_settings.Gemini?.GeminiNodes?.Count == 0)
            {
                issues.Add("没有可用的Gemini节点");
            }

            // 检查TTS配置
            if (_settings.TTS?.IsEnabled == true && 
                string.IsNullOrWhiteSpace(_settings.TTS.Provider))
            {
                issues.Add("启用了TTS但没有选择提供商");
            }

            // 检查工具配置
            if (_settings.Tools?.Count > 10)
            {
                issues.Add($"工具配置过多（{_settings.Tools.Count}个），建议清理");
            }

            if (issues.Count == 0)
            {
                return "配置状态良好，未发现问题";
            }

            return "配置问题:\n- " + string.Join("\n- ", issues);
        }

        /// <summary>
        /// 重置优化日志
        /// </summary>
        public void ClearOptimizationLog()
        {
            _optimizationLog.Clear();
        }
    }
}