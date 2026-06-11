using VPet_Simulator.Windows.Interface;
using VPetLLM.Core.Services;

namespace VPetLLM.Handlers.Actions
{
    /// <summary>
    /// Memory retrieval tool handler.
    /// AI invokes via <|retrieve_memories_begin|> query <|retrieve_memories_end|>
    /// when it cannot find needed information in the current conversation context.
    /// 
    /// Flow: keyword → local search (with ±5 surrounding context per hit)
    ///       → expert model summarization  → feed back via ResultAggregator
    /// </summary>
    public class MemoryRetrievalHandler : IActionHandler
    {
        private readonly MemoryRetrievalService _retrievalService;

        /// <summary>
        /// Global enable flag. Set by FreeChatCore.LoadConfig() from cloud config.
        /// When disabled, the Description is empty (hidden from AI) and Execute() is a no-op.
        /// </summary>
        public static bool IsEnabled { get; set; } = false;

        public MemoryRetrievalHandler(MemoryRetrievalService retrievalService)
        {
            _retrievalService = retrievalService;
        }

        public string Keyword => "retrieve_memories";
        public ActionType ActionType => ActionType.Tool;
        public ActionCategory Category => ActionCategory.Unknown;

        public string Description => IsEnabled
            ? PromptHelper.Get("Handler_RetrieveMemories_Description", VPetLLM.Instance.Settings.PromptLanguage)
            : "";

        public async Task Execute(string value, IMainWindow mainWindow)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(value))
                return;

            try
            {
                var query = value.Trim();
                Logger.Log($"MemoryRetrievalHandler: AI 发起记忆检索: {query}");

                var result = await _retrievalService.SearchWithExpertAsync(query, contextWindow: 5);

                if (!string.IsNullOrEmpty(result))
                {
                    var formattedResult = $"[记忆检索结果]\n{result}\n[/记忆检索结果]";
                    Logger.Log($"MemoryRetrievalHandler: 专家总结完成，回灌给 AI");
                    ResultAggregator.Enqueue(formattedResult);
                }
                else
                {
                    Logger.Log($"MemoryRetrievalHandler: 未找到相关记忆: {query}");
                    ResultAggregator.Enqueue("[记忆检索结果] 未找到相关记忆。");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"MemoryRetrievalHandler: Error: {ex.Message}");
                ResultAggregator.Enqueue($"[记忆检索结果] 检索失败: {ex.Message}");
            }
        }

        public Task Execute(int value, IMainWindow mainWindow) => Task.CompletedTask;
        public Task Execute(IMainWindow mainWindow) => Task.CompletedTask;
        public int GetAnimationDuration(string animationName) => 0;
    }
}
