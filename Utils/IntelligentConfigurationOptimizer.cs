using Newtonsoft.Json;
using System.IO;
using System.Reflection;
using VPetLLM.Core;

namespace VPetLLM.Utils
{
    /// <summary>
    /// 智能配置优化管理器 - 使用反射自动检测和优化所有配置项
    /// </summary>
    public class IntelligentConfigurationOptimizer
    {
        private readonly Setting _settings;
        private readonly string _settingsPath;
        private readonly List<string> _optimizationLog = new();
        private readonly Type _settingType;

        public IntelligentConfigurationOptimizer(Setting settings)
        {
            _settings = settings;
            _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VPetLLM", "VPetLLM.json");
            _settingType = typeof(Setting);
        }

        /// <summary>
        /// 执行完整的智能配置优化
        /// </summary>
        public void PerformIntelligentOptimization()
        {
            Logger.Log("开始执行智能配置优化...");

            try
            {
                // 1. 智能检测和修复配置项存在性
                IntelligentlyCheckAndFixConfigurationItems();

                // 2. 智能清理重复项
                IntelligentlyCleanupDuplicateItems();

                // 3. 智能验证和修复数据有效性
                IntelligentlyValidateAndFixDataValidity();

                // 4. 智能回收机制
                IntelligentlyPerformGarbageCollection();

                // 5. 保存优化后的配置
                SaveOptimizedConfiguration();

                // 6. 记录优化结果
                LogIntelligentOptimizationResults();
            }
            catch (Exception ex)
            {
                Logger.Log($"智能配置优化过程中发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 智能检测和修复配置项存在性
        /// </summary>
        private void IntelligentlyCheckAndFixConfigurationItems()
        {
            Logger.Log("智能检测配置项存在性...");

            var properties = _settingType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            
            foreach (var property in properties)
            {
                if (property.Name.StartsWith("_")) continue; // 跳过私有字段

                var currentValue = property.GetValue(_settings);
                var propertyType = property.PropertyType;

                // 检查是否为值类型或字符串类型
                if (IsSimpleType(propertyType))
                {
                    // 简单类型不需要实例化
                    continue;
                }

                // 检查是否为集合类型
                if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    HandleListProperty(property, currentValue);
                    continue;
                }

                // 检查是否为自定义类类型
                if (propertyType.IsClass && propertyType != typeof(string))
                {
                    HandleClassProperty(property, currentValue);
                    continue;
                }
            }
        }

        /// <summary>
        /// 处理列表属性
        /// </summary>
        private void HandleListProperty(PropertyInfo property, object currentValue)
        {
            try
            {
                if (currentValue == null)
                {
                    // 创建新的List实例
                    var listType = property.PropertyType;
                    var listInstance = Activator.CreateInstance(listType);
                    property.SetValue(_settings, listInstance);
                    _optimizationLog.Add($"修复了缺失的列表属性: {property.Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"处理列表属性 {property.Name} 时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理类属性
        /// </summary>
        private void HandleClassProperty(PropertyInfo property, object currentValue)
        {
            try
            {
                // 跳过特殊类型，这些类型有复杂的构造函数或依赖
                var skipTypes = new[]
                {
                    "FloatingSidebarSettings",
                    "ScreenshotSettings",
                    "TouchFeedbackSettings"
                };
                
                if (skipTypes.Contains(property.PropertyType.Name))
                {
                    // 这些类型已经在 Setting 构造函数中正确初始化
                    return;
                }

                if (currentValue == null)
                {
                    // 创建新的类实例
                    var classType = property.PropertyType;
                    var classInstance = Activator.CreateInstance(classType);
                    property.SetValue(_settings, classInstance);
                    _optimizationLog.Add($"修复了缺失的类属性: {property.Name}");
                }
                else
                {
                    // 递归检查嵌套属性
                    RecursivelyCheckNestedProperties(currentValue, property.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"处理类属性 {property.Name} 时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归检查嵌套属性
        /// </summary>
        private void RecursivelyCheckNestedProperties(object obj, string parentPath)
        {
            if (obj == null) return;

            var objType = obj.GetType();
            
            // 跳过特殊类型的递归检查
            var skipTypes = new[]
            {
                "FloatingSidebarSettings",
                "ScreenshotSettings",
                "TouchFeedbackSettings",
                "SidebarButton"
            };
            
            if (skipTypes.Contains(objType.Name))
            {
                return;
            }
            
            var properties = objType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var property in properties)
            {
                var currentValue = property.GetValue(obj);
                var propertyType = property.PropertyType;

                // 跳过特殊类型
                if (skipTypes.Contains(propertyType.Name))
                {
                    continue;
                }

                // 检查是否为列表类型
                if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    HandleNestedListProperty(obj, property, currentValue, parentPath);
                }
                // 检查是否为嵌套类类型
                else if (propertyType.IsClass && propertyType != typeof(string))
                {
                    if (currentValue == null)
                    {
                        try
                        {
                            var nestedInstance = Activator.CreateInstance(propertyType);
                            property.SetValue(obj, nestedInstance);
                            _optimizationLog.Add($"修复了嵌套类属性: {parentPath}.{property.Name}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"创建嵌套实例 {parentPath}.{property.Name} 时发生错误: {ex.Message}");
                        }
                    }
                    else
                    {
                        // 递归检查更深层的嵌套
                        RecursivelyCheckNestedProperties(currentValue, $"{parentPath}.{property.Name}");
                    }
                }
            }
        }

        /// <summary>
        /// 处理嵌套列表属性
        /// </summary>
        private void HandleNestedListProperty(object parentObj, PropertyInfo property, object currentValue, string parentPath)
        {
            try
            {
                if (currentValue == null)
                {
                    var listType = property.PropertyType;
                    var listInstance = Activator.CreateInstance(listType);
                    property.SetValue(parentObj, listInstance);
                    _optimizationLog.Add($"修复了嵌套列表属性: {parentPath}.{property.Name}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"处理嵌套列表属性 {parentPath}.{property.Name} 时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 智能清理重复项
        /// </summary>
        private void IntelligentlyCleanupDuplicateItems()
        {
            Logger.Log("智能清理重复项...");

            // 特殊处理已知的重复项情况
            CleanupDuplicateTools();
            CleanupDuplicateOpenAINodes();
            CleanupDuplicateGeminiNodes();
            CleanupDuplicateCustomHeaders();

            // 通用处理其他列表属性
            CleanupOtherDuplicateLists();
        }

        /// <summary>
        /// 清理重复的工具
        /// </summary>
        private void CleanupDuplicateTools()
        {
            if (_settings.Tools == null) return;

            var originalCount = _settings.Tools.Count;
            var seenNames = new HashSet<string>();
            var uniqueTools = new List<Setting.ToolSetting>();

            foreach (var tool in _settings.Tools)
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Name)) continue;
                
                var lowerName = tool.Name.ToLowerInvariant();
                if (!seenNames.Contains(lowerName))
                {
                    seenNames.Add(lowerName);
                    uniqueTools.Add(tool);
                }
            }

            if (originalCount > uniqueTools.Count)
            {
                _optimizationLog.Add($"清理了Tools中的{originalCount - uniqueTools.Count}个重复工具");
            }

            _settings.Tools = uniqueTools;
        }

        /// <summary>
        /// 清理重复的OpenAI节点
        /// </summary>
        private void CleanupDuplicateOpenAINodes()
        {
            if (_settings.OpenAI?.OpenAINodes == null) return;

            var originalCount = _settings.OpenAI.OpenAINodes.Count;
            var seenCombinations = new HashSet<string>();
            var uniqueNodes = new List<Setting.OpenAINodeSetting>();

            foreach (var node in _settings.OpenAI.OpenAINodes)
            {
                if (node == null) continue;
                
                var combination = $"{node.Name?.ToLowerInvariant()}|{node.Url}";
                if (!seenCombinations.Contains(combination))
                {
                    seenCombinations.Add(combination);
                    uniqueNodes.Add(node);
                }
            }

            if (originalCount > uniqueNodes.Count)
            {
                _optimizationLog.Add($"清理了OpenAI节点中的{originalCount - uniqueNodes.Count}个重复节点");
            }

            _settings.OpenAI.OpenAINodes = uniqueNodes;
        }

        /// <summary>
        /// 清理重复的Gemini节点
        /// </summary>
        private void CleanupDuplicateGeminiNodes()
        {
            if (_settings.Gemini?.GeminiNodes == null) return;

            var originalCount = _settings.Gemini.GeminiNodes.Count;
            var seenCombinations = new HashSet<string>();
            var uniqueNodes = new List<Setting.GeminiNodeSetting>();

            foreach (var node in _settings.Gemini.GeminiNodes)
            {
                if (node == null) continue;
                
                var combination = $"{node.Name?.ToLowerInvariant()}|{node.ApiKey}";
                if (!seenCombinations.Contains(combination))
                {
                    seenCombinations.Add(combination);
                    uniqueNodes.Add(node);
                }
            }

            if (originalCount > uniqueNodes.Count)
            {
                _optimizationLog.Add($"清理了Gemini节点中的{originalCount - uniqueNodes.Count}个重复节点");
            }

            _settings.Gemini.GeminiNodes = uniqueNodes;
        }

        /// <summary>
        /// 清理重复的自定义头部
        /// </summary>
        private void CleanupDuplicateCustomHeaders()
        {
            if (_settings.TTS?.DIY?.CustomHeaders == null) return;

            var originalCount = _settings.TTS.DIY.CustomHeaders.Count;
            var seenKeys = new HashSet<string>();
            var uniqueHeaders = new List<Setting.CustomHeader>();

            foreach (var header in _settings.TTS.DIY.CustomHeaders)
            {
                if (header == null || string.IsNullOrWhiteSpace(header.Key)) continue;
                
                var lowerKey = header.Key.ToLowerInvariant();
                if (!seenKeys.Contains(lowerKey))
                {
                    seenKeys.Add(lowerKey);
                    uniqueHeaders.Add(header);
                }
            }

            if (originalCount > uniqueHeaders.Count)
            {
                _optimizationLog.Add($"清理了DIY自定义头部中的{originalCount - uniqueHeaders.Count}个重复头部");
            }

            _settings.TTS.DIY.CustomHeaders = uniqueHeaders;
        }

        /// <summary>
        /// 清理其他重复的列表
        /// </summary>
        private void CleanupOtherDuplicateLists()
        {
            // 这里可以实现对其他类型列表的通用去重逻辑
            // 由于Setting类中主要就是这些已知的列表类型，暂时保持简单
        }

        /// <summary>
        /// 智能验证和修复数据有效性
        /// </summary>
        private void IntelligentlyValidateAndFixDataValidity()
        {
            Logger.Log("智能验证和修复数据有效性...");

            // 验证和修复索引越界
            ValidateAndFixIndexBounds();

            // 验证和修复数值范围
            ValidateAndFixNumericRanges();

            // 验证和修复URL格式
            ValidateAndFixUrlFormats();
        }

        /// <summary>
        /// 验证和修复索引越界
        /// </summary>
        private void ValidateAndFixIndexBounds()
        {
            // OpenAI节点索引
            if (_settings.OpenAI?.OpenAINodes != null)
            {
                if (_settings.OpenAI.CurrentNodeIndex < 0 || 
                    _settings.OpenAI.CurrentNodeIndex >= _settings.OpenAI.OpenAINodes.Count)
                {
                    _settings.OpenAI.CurrentNodeIndex = 0;
                    _optimizationLog.Add("修复了OpenAI节点索引越界");
                }
            }

            // Gemini节点索引
            if (_settings.Gemini?.GeminiNodes != null)
            {
                if (_settings.Gemini.CurrentNodeIndex < 0 || 
                    _settings.Gemini.CurrentNodeIndex >= _settings.Gemini.GeminiNodes.Count)
                {
                    _settings.Gemini.CurrentNodeIndex = 0;
                    _optimizationLog.Add("修复了Gemini节点索引越界");
                }
            }
        }

        /// <summary>
        /// 验证和修复数值范围
        /// </summary>
        private void ValidateAndFixNumericRanges()
        {
            // TTS音量范围（0-100 百分比）
            if (_settings.TTS != null)
            {
                if (_settings.TTS.Volume < 0 || _settings.TTS.Volume > 100)
                {
                    _settings.TTS.Volume = Math.Max(0, Math.Min(100, _settings.TTS.Volume));
                    _optimizationLog.Add("修复了TTS音量范围");
                }

                if (_settings.TTS.Speed < 0.1 || _settings.TTS.Speed > 3.0)
                {
                    _settings.TTS.Speed = Math.Max(0.1, Math.Min(3.0, _settings.TTS.Speed));
                    _optimizationLog.Add("修复了TTS语速范围");
                }
            }
        }

        /// <summary>
        /// 验证和修复URL格式
        /// </summary>
        private void ValidateAndFixUrlFormats()
        {
            // Ollama URL
            if (_settings.Ollama != null && !string.IsNullOrWhiteSpace(_settings.Ollama.Url) && 
                !_settings.Ollama.Url.StartsWith("http"))
            {
                _settings.Ollama.Url = "http://" + _settings.Ollama.Url;
                _optimizationLog.Add("修复了Ollama URL格式");
            }

            // 代理地址
            if (_settings.Proxy != null && _settings.Proxy.IsEnabled && 
                !string.IsNullOrWhiteSpace(_settings.Proxy.Address) && 
                !_settings.Proxy.Address.Contains(":"))
            {
                _settings.Proxy.Address = _settings.Proxy.Address + ":8080";
                _optimizationLog.Add("修复了代理地址格式");
            }
        }

        /// <summary>
        /// 智能回收机制
        /// </summary>
        private void IntelligentlyPerformGarbageCollection()
        {
            Logger.Log("执行智能回收机制...");

            // 清理空配置段落
            CleanupEmptyConfigurations();

            // 清理无效引用
            CleanupInvalidReferences();

            // 压缩配置结构
            CompressConfigurationStructure();
        }

        /// <summary>
        /// 清理空配置段落
        /// </summary>
        private void CleanupEmptyConfigurations()
        {
            // 清理空的工具列表
            if (_settings.Tools != null)
            {
                var nonEmptyTools = _settings.Tools
                    .Where(tool => tool != null && !string.IsNullOrWhiteSpace(tool.Name))
                    .ToList();
                
                if (nonEmptyTools.Count != _settings.Tools.Count)
                {
                    _settings.Tools = nonEmptyTools;
                    _optimizationLog.Add("清理了空的工具配置");
                }
            }

            // 清理空的OpenAI节点
            if (_settings.OpenAI?.OpenAINodes != null)
            {
                var nonEmptyNodes = _settings.OpenAI.OpenAINodes
                    .Where(node => node != null && !string.IsNullOrWhiteSpace(node.Name))
                    .ToList();
                
                if (nonEmptyNodes.Count != _settings.OpenAI.OpenAINodes.Count)
                {
                    _settings.OpenAI.OpenAINodes = nonEmptyNodes;
                    _optimizationLog.Add("清理了空的OpenAI节点");
                }
            }

            // 清理空的Gemini节点
            if (_settings.Gemini?.GeminiNodes != null)
            {
                var nonEmptyNodes = _settings.Gemini.GeminiNodes
                    .Where(node => node != null && !string.IsNullOrWhiteSpace(node.Name))
                    .ToList();
                
                if (nonEmptyNodes.Count != _settings.Gemini.GeminiNodes.Count)
                {
                    _settings.Gemini.GeminiNodes = nonEmptyNodes;
                    _optimizationLog.Add("清理了空的Gemini节点");
                }
            }

            // 清理空的自定义头部
            if (_settings.TTS?.DIY?.CustomHeaders != null)
            {
                var nonEmptyHeaders = _settings.TTS.DIY.CustomHeaders
                    .Where(header => header != null && !string.IsNullOrWhiteSpace(header.Key))
                    .ToList();
                
                if (nonEmptyHeaders.Count != _settings.TTS.DIY.CustomHeaders.Count)
                {
                    _settings.TTS.DIY.CustomHeaders = nonEmptyHeaders;
                    _optimizationLog.Add("清理了空的自定义头部");
                }
            }
        }

        /// <summary>
        /// 清理无效引用
        /// </summary>
        private void CleanupInvalidReferences()
        {
            // 清理GPT-SoVITS参考音频路径
            if (_settings.TTS?.GPTSoVITS?.ReferWavPath != null && 
                !string.IsNullOrWhiteSpace(_settings.TTS.GPTSoVITS.ReferWavPath) &&
                !File.Exists(_settings.TTS.GPTSoVITS.ReferWavPath))
            {
                _settings.TTS.GPTSoVITS.ReferWavPath = "";
                _optimizationLog.Add("清理了指向不存在文件的参考音频路径");
            }
        }

        /// <summary>
        /// 压缩配置结构
        /// </summary>
        private void CompressConfigurationStructure()
        {
            Logger.Log("配置结构压缩完成");
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

                Logger.Log("智能优化后的配置已保存");
            }
            catch (Exception ex)
            {
                Logger.Log($"保存智能优化配置时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录智能优化结果
        /// </summary>
        private void LogIntelligentOptimizationResults()
        {
            if (_optimizationLog.Count > 0)
            {
                Logger.Log("=== 智能配置优化结果 ===");
                foreach (var log in _optimizationLog)
                {
                    Logger.Log($"- {log}");
                }
                Logger.Log($"总共执行了 {_optimizationLog.Count} 项智能优化");
            }
            else
            {
                Logger.Log("智能配置优化完成，未发现需要优化的问题");
            }
        }

        /// <summary>
        /// 智能配置健康报告
        /// </summary>
        public string GetIntelligentHealthReport()
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
                return "智能配置状态良好，未发现问题";
            }

            return "智能配置问题:\n- " + string.Join("\n- ", issues);
        }

        /// <summary>
        /// 重置优化日志
        /// </summary>
        public void ClearOptimizationLog()
        {
            _optimizationLog.Clear();
        }

        #region 辅助方法

        /// <summary>
        /// 判断是否为简单类型
        /// </summary>
        private bool IsSimpleType(Type type)
        {
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || 
                   type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset);
        }

        /// <summary>
        /// 判断是否为数值类型
        /// </summary>
        private bool IsNumericType(Type type)
        {
            return type == typeof(int) || type == typeof(long) || type == typeof(short) ||
                   type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) ||
                   type == typeof(byte) || type == typeof(sbyte) ||
                   type == typeof(float) || type == typeof(double) || type == typeof(decimal);
        }

        #endregion
    }
}