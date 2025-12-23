using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPetLLM.Configuration;
using VPetLLM.Core;
using VPetLLM.Core.ChatCore;
using VPet_Simulator.Windows.Interface;

namespace VPetLLM.Services
{
    /// <summary>
    /// 前置多模态处理服务实现
    /// </summary>
    public class PreprocessingMultimodal : IPreprocessingMultimodal
    {
        private readonly Setting _settings;
        private readonly VPetLLM _plugin;
        private readonly Random _random = new();

        public PreprocessingMultimodal(Setting settings, VPetLLM plugin)
        {
            _settings = settings;
            _plugin = plugin;
        }

        /// <inheritdoc/>
        public async Task<PreprocessingResult> AnalyzeImageAsync(byte[] imageData, string? customPrompt = null)
        {
            if (imageData == null || imageData.Length == 0)
            {
                return PreprocessingResult.CreateFailure("图片数据为空");
            }

            var config = _settings.Screenshot?.MultimodalProvider ?? new MultimodalProviderConfig();
            // 获取当前语言设置
            var lang = _settings.PromptLanguage ?? "zh";
            var prompt = string.IsNullOrWhiteSpace(customPrompt)
                ? config.GetEffectivePrompt(lang)
                : customPrompt;

            Utils.Logger.Log($"PreprocessingMultimodal: 开始分析图片，大小: {imageData.Length} bytes");

            if (config.ProviderType == MultimodalProviderType.Free)
            {
                return await AnalyzeWithFree(imageData, prompt);
            }
            else
            {
                return await AnalyzeWithVisionChannels(imageData, prompt);
            }
        }

        /// <summary>
        /// 使用 Free 渠道分析图片
        /// </summary>
        private async Task<PreprocessingResult> AnalyzeWithFree(byte[] imageData, string prompt)
        {
            try
            {
                // 检查 Free 渠道是否启用视觉（前置多模态需要 Free 渠道具有视觉能力来分析图片）
                if (_settings.Free?.EnableVision != true)
                {
                    var errorMessage = Utils.LanguageHelper.Get("Screenshot.Validation.FreeVisionRequired", _settings.Language) 
                        ?? "前置多模态需要 Free 渠道启用视觉能力。请在 LLM 设置 -> Free 接口 中启用 EnableVision 选项。";
                    return PreprocessingResult.CreateFailure(errorMessage);
                }

                Utils.Logger.Log("PreprocessingMultimodal: 使用 Free 渠道进行图片分析");

                // 创建临时的 FreeChatCore 用于图片分析
                var description = await CallChatWithImageForDescription(
                    "Free", null, imageData, prompt);

                if (string.IsNullOrWhiteSpace(description))
                {
                    var errorMessage = Utils.LanguageHelper.Get("Screenshot.Validation.FreeChannelEmptyResponse", _settings.Language) 
                        ?? "Free 渠道返回空描述";
                    return PreprocessingResult.CreateFailure(errorMessage);
                }

                return PreprocessingResult.CreateSuccess(description, "Free");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"PreprocessingMultimodal: Free 渠道分析失败: {ex.Message}");
                var errorPrefix = Utils.LanguageHelper.Get("Screenshot.Validation.FreeChannelAnalysisFailed", _settings.Language) 
                    ?? "Free 渠道分析失败";
                return PreprocessingResult.CreateFailure($"{errorPrefix}: {ex.Message}");
            }
        }

        /// <summary>
        /// 使用视觉渠道分析图片（带负载均衡和容灾）
        /// </summary>
        private async Task<PreprocessingResult> AnalyzeWithVisionChannels(byte[] imageData, string prompt)
        {
            var config = _settings.Screenshot?.MultimodalProvider ?? new MultimodalProviderConfig();
            var selectedNodes = config.SelectedNodes ?? new List<VisionNodeIdentifier>();

            // 验证并过滤有效节点
            var validNodes = ValidateSelectedNodes(selectedNodes);
            if (validNodes.Count == 0)
            {
                var errorMessage = Utils.LanguageHelper.Get("Screenshot.Validation.NoVisionNodesAvailable", _settings.Language) 
                    ?? "没有可用的视觉节点，请在设置中选择至少一个启用视觉的节点";
                return PreprocessingResult.CreateFailure(errorMessage);
            }

            // 创建节点列表副本用于容灾
            var availableNodes = new List<VisionNodeIdentifier>(validNodes);
            var failedNodes = new List<string>();

            while (availableNodes.Count > 0)
            {
                // 随机选择一个节点（负载均衡）
                var selectedIndex = _random.Next(availableNodes.Count);
                var selectedNode = availableNodes[selectedIndex];

                Utils.Logger.Log($"PreprocessingMultimodal: 尝试使用节点 {selectedNode.DisplayName}");

                try
                {
                    var description = await CallChatWithImageForDescription(
                        selectedNode.ProviderType, selectedNode, imageData, prompt);

                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        return PreprocessingResult.CreateSuccess(description, selectedNode.DisplayName);
                    }

                    // 空描述视为失败
                    var emptyResponseMessage = Utils.LanguageHelper.Get("Screenshot.Validation.EmptyResponse", _settings.Language) 
                        ?? "返回空描述";
                    failedNodes.Add($"{selectedNode.DisplayName}: {emptyResponseMessage}");
                }
                catch (Exception ex)
                {
                    Utils.Logger.Log($"PreprocessingMultimodal: 节点 {selectedNode.DisplayName} 失败: {ex.Message}");
                    failedNodes.Add($"{selectedNode.DisplayName}: {ex.Message}");
                }

                // 从可用列表中移除失败的节点
                availableNodes.RemoveAt(selectedIndex);
            }

            // 所有节点都失败
            var allFailedMessage = Utils.LanguageHelper.Get("Screenshot.Validation.AllVisionNodesFailed", _settings.Language) 
                ?? "所有视觉节点都失败";
            var errorDetails = string.Join("\n", failedNodes);
            return PreprocessingResult.CreateFailure($"{allFailedMessage}:\n{errorDetails}");
        }


        /// <summary>
        /// 调用 ChatWithImage 获取图片描述
        /// 注意：前置多模态处理不应该保存历史记录到主上下文
        /// </summary>
        private async Task<string> CallChatWithImageForDescription(
            string providerType, VisionNodeIdentifier? node, byte[] imageData, string prompt)
        {
            string? capturedResponse = null;

            // 创建临时的响应处理器来捕获结果
            Action<string> responseHandler = (response) =>
            {
                capturedResponse = response;
            };

            IChatCore? chatCore = null;

            // 保存原始的 KeepContext 设置
            var originalKeepContext = _settings.KeepContext;

            try
            {
                // 临时禁用上下文保存，防止前置多模态的 prompt 被写入主上下文
                _settings.KeepContext = false;

                // 获取 MainWindow 引用
                var mainWindow = _plugin.MW;

                switch (providerType)
                {
                    case "Free":
                        if (_settings.Free != null)
                        {
                            chatCore = new FreeChatCore(_settings.Free, _settings, mainWindow, null!);
                        }
                        break;

                    case "OpenAI":
                        if (node != null && _settings.OpenAI?.OpenAINodes != null)
                        {
                            var openAINode = _settings.OpenAI.OpenAINodes
                                .FirstOrDefault(n => n.Name == node.NodeName && n.Enabled && n.EnableVision);
                            if (openAINode != null)
                            {
                                chatCore = new OpenAIChatCore(openAINode, _settings, mainWindow, null!);
                            }
                        }
                        break;

                    case "Gemini":
                        if (node != null && _settings.Gemini?.GeminiNodes != null)
                        {
                            var geminiNode = _settings.Gemini.GeminiNodes
                                .FirstOrDefault(n => n.Name == node.NodeName && n.Enabled && n.EnableVision);
                            if (geminiNode != null)
                            {
                                chatCore = new GeminiChatCore(_settings.Gemini, _settings, mainWindow, null!);
                            }
                        }
                        break;

                    case "Ollama":
                        // Ollama 目前不支持多节点，直接使用设置
                        if (_settings.Ollama?.EnableVision == true)
                        {
                            chatCore = new OllamaChatCore(_settings.Ollama, _settings, mainWindow, null!);
                        }
                        break;
                }

                if (chatCore == null)
                {
                    throw new InvalidOperationException($"无法创建 {providerType} 的 ChatCore 实例");
                }

                // 清除临时 ChatCore 的上下文，确保不使用主上下文的历史
                chatCore.ClearContext();

                // 设置响应处理器
                chatCore.SetResponseHandler(responseHandler);

                // 调用 ChatWithImage
                await chatCore.ChatWithImage(prompt, imageData);

                return capturedResponse ?? "";
            }
            finally
            {
                // 恢复原始的 KeepContext 设置
                _settings.KeepContext = originalKeepContext;

                // 清理资源
                if (chatCore is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        /// <inheritdoc/>
        public List<VisionNodeIdentifier> GetAvailableVisionNodes()
        {
            var nodes = new List<VisionNodeIdentifier>();

            // 收集 OpenAI 视觉节点
            if (_settings.OpenAI?.OpenAINodes != null)
            {
                foreach (var node in _settings.OpenAI.OpenAINodes.Where(n => n.Enabled && n.EnableVision))
                {
                    nodes.Add(new VisionNodeIdentifier
                    {
                        ProviderType = "OpenAI",
                        NodeName = node.Name,
                        Model = node.Model ?? ""
                    });
                }
            }

            // 收集 Gemini 视觉节点
            if (_settings.Gemini?.GeminiNodes != null)
            {
                foreach (var node in _settings.Gemini.GeminiNodes.Where(n => n.Enabled && n.EnableVision))
                {
                    nodes.Add(new VisionNodeIdentifier
                    {
                        ProviderType = "Gemini",
                        NodeName = node.Name,
                        Model = node.Model ?? ""
                    });
                }
            }

            // 收集 Ollama 视觉节点
            if (_settings.Ollama?.EnableVision == true)
            {
                nodes.Add(new VisionNodeIdentifier
                {
                    ProviderType = "Ollama",
                    NodeName = "Default",
                    Model = _settings.Ollama.Model ?? ""
                });
            }

            return nodes;
        }

        /// <inheritdoc/>
        public List<VisionNodeIdentifier> ValidateSelectedNodes(List<VisionNodeIdentifier> nodes)
        {
            if (nodes == null || nodes.Count == 0)
            {
                return new List<VisionNodeIdentifier>();
            }

            var availableNodes = GetAvailableVisionNodes();
            var availableIds = availableNodes.Select(n => n.UniqueId).ToHashSet();

            var validNodes = nodes.Where(n => availableIds.Contains(n.UniqueId)).ToList();

            if (validNodes.Count < nodes.Count)
            {
                var removedCount = nodes.Count - validNodes.Count;
                Utils.Logger.Log($"PreprocessingMultimodal: 移除了 {removedCount} 个无效的视觉节点");
            }

            return validNodes;
        }

        /// <inheritdoc/>
        public bool HasAvailableProvider()
        {
            var config = _settings.Screenshot?.MultimodalProvider ?? new MultimodalProviderConfig();

            if (config.ProviderType == MultimodalProviderType.Free)
            {
                return _settings.Free?.EnableVision == true;
            }
            else
            {
                var validNodes = ValidateSelectedNodes(config.SelectedNodes ?? new List<VisionNodeIdentifier>());
                return validNodes.Count > 0;
            }
        }
    }
}
