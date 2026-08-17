using VPetLLM.Utils.Localization;

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
            if (imageData is null || imageData.Length == 0)
            {
                return PreprocessingResult.CreateFailure("图片数据为空");
            }

            var config = _settings.Screenshot?.MultimodalProvider ?? new MultimodalProviderConfig();
            // 获取当前语言设置
            var lang = _settings.PromptLanguage ?? "zh";
            var prompt = string.IsNullOrWhiteSpace(customPrompt)
                ? config.GetEffectivePrompt(lang)
                : customPrompt;

            Logger.Log($"PreprocessingMultimodal: 开始分析图片，大小: {imageData.Length} bytes");

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
        /// 用「主聊天渠道」分析图片，供原生多模态模式使用。
        ///
        /// 与 AnalyzeImageAsync 的区别：后者读的是 Screenshot.MultimodalProvider 这套
        /// 前置视觉渠道配置（默认 Free）；本方法直接用用户正在聊天的那个 provider。
        /// 原生多模态的语义就是「图片交给主模型自己看」，不该绕道前置视觉渠道。
        /// </summary>
        public async Task<PreprocessingResult> AnalyzeWithMainProviderAsync(byte[] imageData, string? customPrompt = null)
        {
            if (imageData is null || imageData.Length == 0)
            {
                return PreprocessingResult.CreateFailure("图片数据为空");
            }

            var providerType = _settings.Provider switch
            {
                Setting.LLMType.Free => "Free",
                Setting.LLMType.OpenAI => "OpenAI",
                Setting.LLMType.Gemini => "Gemini",
                Setting.LLMType.Ollama => "Ollama",
                Setting.LLMType.LMStudio => "LMStudio",
                _ => ""
            };

            if (string.IsNullOrEmpty(providerType))
            {
                return PreprocessingResult.CreateFailure($"未知的主渠道类型: {_settings.Provider}");
            }

            var lang = _settings.PromptLanguage ?? "zh";
            var prompt = string.IsNullOrWhiteSpace(customPrompt)
                ? (_settings.Screenshot?.MultimodalProvider ?? new MultimodalProviderConfig()).GetEffectivePrompt(lang)
                : customPrompt;

            try
            {
                Logger.Log($"PreprocessingMultimodal: 原生多模态 - 使用主渠道 {providerType} 分析图片，大小: {imageData.Length} bytes");

                // 主渠道走 OpenAI/Gemini 这类多节点配置时，节点由各自 ChatCore 自行挑选，
                // 这里不指定 node，交给 provider 用它当前生效的那个
                var description = await CallChatWithImageForDescription(providerType, null, imageData, prompt);

                if (string.IsNullOrWhiteSpace(description))
                {
                    return PreprocessingResult.CreateFailure($"主渠道 {providerType} 未返回图片描述（可能未启用视觉能力）");
                }

                return PreprocessingResult.CreateSuccess(description, providerType);
            }
            catch (Exception ex)
            {
                Logger.Log($"PreprocessingMultimodal: 主渠道 {providerType} 分析失败: {ex.Message}");
                return PreprocessingResult.CreateFailure($"主渠道 {providerType} 分析失败: {ex.Message}");
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
                    var errorMessage = LanguageHelper.Get("Screenshot.Validation.FreeVisionRequired", _settings.Language)
                        ?? "前置多模态需要 Free 渠道启用视觉能力。请在 LLM 设置 -> Free 接口 中启用 EnableVision 选项。";
                    return PreprocessingResult.CreateFailure(errorMessage);
                }

                Logger.Log("PreprocessingMultimodal: 使用 Free 渠道进行图片分析");

                // 创建临时的 FreeChatCore 用于图片分析
                var description = await CallChatWithImageForDescription(
                    "Free", null, imageData, prompt);

                if (string.IsNullOrWhiteSpace(description))
                {
                    var errorMessage = LanguageHelper.Get("Screenshot.Validation.FreeChannelEmptyResponse", _settings.Language)
                        ?? "Free 渠道返回空描述";
                    return PreprocessingResult.CreateFailure(errorMessage);
                }

                return PreprocessingResult.CreateSuccess(description, "Free");
            }
            catch (Exception ex)
            {
                Logger.Log($"PreprocessingMultimodal: Free 渠道分析失败: {ex.Message}");
                var errorPrefix = LanguageHelper.Get("Screenshot.Validation.FreeChannelAnalysisFailed", _settings.Language)
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
                var errorMessage = LanguageHelper.Get("Screenshot.Validation.NoVisionNodesAvailable", _settings.Language)
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

                Logger.Log($"PreprocessingMultimodal: 尝试使用节点 {selectedNode.DisplayName}");

                try
                {
                    var description = await CallChatWithImageForDescription(
                        selectedNode.ProviderType, selectedNode, imageData, prompt);

                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        return PreprocessingResult.CreateSuccess(description, selectedNode.DisplayName);
                    }

                    // 空描述视为失败
                    var emptyResponseMessage = LanguageHelper.Get("Screenshot.Validation.EmptyResponse", _settings.Language)
                        ?? "返回空描述";
                    failedNodes.Add($"{selectedNode.DisplayName}: {emptyResponseMessage}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"PreprocessingMultimodal: 节点 {selectedNode.DisplayName} 失败: {ex.Message}");
                    failedNodes.Add($"{selectedNode.DisplayName}: {ex.Message}");
                }

                // 从可用列表中移除失败的节点
                availableNodes.RemoveAt(selectedIndex);
            }

            // 所有节点都失败
            var allFailedMessage = LanguageHelper.Get("Screenshot.Validation.AllVisionNodesFailed", _settings.Language)
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

            // 创建临时的响应处理器来捕获结果（非流式分支：整段一次性回调）
            Action<string> responseHandler = (response) =>
            {
                capturedResponse = response;
            };

            // 流式分支不走 ResponseHandler —— 那条路由 StreamingCommandProcessor 按
            // 「检测到完整命令」逐条回调，而图片描述是纯文本、不含 <|xxx_begin|>，
            // 很可能一次都不触发，只靠 capturedResponse 会拿到空描述。
            // StreamingChunkHandler 是逐 delta 的原始回调，两种分支都能兜住。
            var streamedText = new System.Text.StringBuilder();

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
                        if (_settings.Free is not null)
                        {
                            chatCore = new FreeChatCore(_settings.Free, _settings, mainWindow, null!);
                        }
                        break;

                    // 下面几个渠道：node 为 null 表示「不指定节点」（原生多模态走主渠道时就是这样），
                    // 此时退回该渠道当前生效的节点，而不是直接失败
                    case "OpenAI":
                        if (_settings.OpenAI?.OpenAINodes is not null)
                        {
                            var openAINode = node is not null
                                ? _settings.OpenAI.OpenAINodes.FirstOrDefault(n => n.Name == node.NodeName && n.Enabled && n.EnableVision)
                                : _settings.OpenAI.GetCurrentOpenAISetting();

                            if (openAINode is not null && openAINode.EnableVision)
                            {
                                chatCore = new OpenAIChatCore(openAINode, _settings, mainWindow, null!);
                            }
                        }
                        break;

                    case "Gemini":
                        if (_settings.Gemini?.GeminiNodes is not null)
                        {
                            var geminiNode = node is not null
                                ? _settings.Gemini.GeminiNodes.FirstOrDefault(n => n.Name == node.NodeName && n.Enabled && n.EnableVision)
                                : _settings.Gemini.GetCurrentGeminiSetting();

                            if (geminiNode is not null && geminiNode.EnableVision)
                            {
                                // 必须传 geminiNode 本身：传整份 _settings.Gemini 的话，
                                // GeminiChatCore 会用 GetCurrentGeminiSetting() 另选主聊天当前的节点，
                                // 上面挑出来的视觉节点就只剩「校验」作用，请求实际打去了别处
                                chatCore = new GeminiChatCore(geminiNode, _settings, mainWindow, null!);
                            }
                        }
                        break;

                    case "Ollama":
                        var ollamaVisionNode = _settings.Ollama?.GetCurrentOllamaSetting();
                        if (ollamaVisionNode != null && ollamaVisionNode.EnableVision)
                        {
                            chatCore = new OllamaChatCore(ollamaVisionNode, _settings, mainWindow, null!);
                        }
                        break;

                    case "LMStudio":
                        var lmNode = node is not null
                            ? _settings.LMStudio?.LMStudioNodes?.FirstOrDefault(n => n.Name == node.NodeName && n.Enabled && n.EnableVision)
                            : _settings.LMStudio?.GetCurrentLMStudioSetting();

                        if (lmNode is not null && lmNode.EnableVision)
                        {
                            chatCore = new LMStudioChatCore(lmNode, _settings, mainWindow, null!);
                        }
                        break;
                }

                if (chatCore is null)
                {
                    throw new InvalidOperationException($"无法创建 {providerType} 的 ChatCore 实例");
                }

                // 不使用主上下文的历史：靠上面的 KeepContext = false 即可 ——
                // GetCoreHistoryCommonAsync 在 KeepContext 为假时只发系统消息，压根不读历史。
                //
                // 这里绝不能调 chatCore.ClearContext()：HistoryManager / OverflowManager 是按
                // provider 名共享同一个 SQLite 库的，临时 core 和主 core 指向同一份数据，
                // 那一下会把用户真实的聊天历史和溢出总结全部删掉（日志里表现为每次识图都跟着
                // 一条「清除了所有历史记录，共 N 条」）。

                // 默认跟随所选节点的 EnableStreaming —— 那是用户的选择，不在这里替他改。
                // 只有用户显式打开「识图强制流式」时才覆盖：非流式请求在整段生成完之前不返回
                // 任何字节，途中按"等响应头"设超时的网关会把它掐掉（发出十几秒后收到空 body 的
                // 5xx）。该开关是给撞上这种网关的用户的出路，不必为此改动主对话的流式偏好。
                if (chatCore is Core.Abstractions.Base.ChatCoreBase coreBase)
                {
                    var forceStreaming = _settings.Screenshot?.MultimodalProvider?.ForceStreamingForVision ?? false;
                    if (forceStreaming)
                    {
                        coreBase.ForceStreaming = true;
                        Logger.Log("PreprocessingMultimodal: 识图强制流式（用户已开启该选项）");
                    }

                    // 无论走哪条分支都挂上：流式时它是唯一能拿到完整描述的通道，
                    // 非流式时它不会被调用、缓冲为空，取值处自然退回 ResponseHandler。
                    coreBase.SetStreamingChunkHandler(chunk => streamedText.Append(chunk));
                }

                // 设置响应处理器
                chatCore.SetResponseHandler(responseHandler);

                // 调用 ChatWithImage
                await chatCore.ChatWithImage(prompt, imageData);

                // 失败时错误文本会走 ResponseHandler（例如「API调用失败: Forbidden ...」）。
                // 它非空，若直接返回就会被当成图片描述判成功——节点容灾也就永远轮不到第二个节点。
                if (chatCore.LastCallFailed)
                {
                    Logger.Log($"PreprocessingMultimodal: {providerType} 调用失败: {capturedResponse}");
                    return "";
                }

                // 流式优先：整段 delta 拼出来的才是完整描述；非流式时它为空，退回 ResponseHandler。
                return streamedText.Length > 0 ? streamedText.ToString() : (capturedResponse ?? "");
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
            if (_settings.OpenAI?.OpenAINodes is not null)
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
            if (_settings.Gemini?.GeminiNodes is not null)
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

            // 收集 LMStudio 视觉节点
            if (_settings.LMStudio?.LMStudioNodes is not null)
            {
                foreach (var node in _settings.LMStudio.LMStudioNodes.Where(n => n.Enabled && n.EnableVision))
                {
                    nodes.Add(new VisionNodeIdentifier
                    {
                        ProviderType = "LMStudio",
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
            if (nodes is null || nodes.Count == 0)
            {
                return new List<VisionNodeIdentifier>();
            }

            var availableNodes = GetAvailableVisionNodes();
            var availableIds = availableNodes.Select(n => n.UniqueId).ToHashSet();

            var validNodes = nodes.Where(n => availableIds.Contains(n.UniqueId)).ToList();

            if (validNodes.Count < nodes.Count)
            {
                var removedCount = nodes.Count - validNodes.Count;
                Logger.Log($"PreprocessingMultimodal: 移除了 {removedCount} 个无效的视觉节点");
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
