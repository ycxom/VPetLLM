using System;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPetLLM.Services;

namespace VPetLLM.Controllers
{
    /// <summary>
    /// LLM后处理控制器 - 负责处理LLM模型调用和宠物状态交互
    /// </summary>
    public class LLMProcessorController
    {
        private readonly LLMStateService _llmStateService;
        private readonly GameCore _gameCore;

        public LLMProcessorController(GameCore gameCore)
        {
            _gameCore = gameCore;
            _llmStateService = new LLMStateService(gameCore);
        }

        /// <summary>
        /// 处理用户输入并生成LLM响应
        /// </summary>
        public async Task<SayInfo> ProcessUserInputAsync(string userInput)
        {
            try
            {
                // 获取宠物状态信息
                var statusSummary = _llmStateService.GetStatusSummary();
                
                // 生成LLM提示
                var prompt = _llmStateService.GenerateLLMPrompt(userInput);
                
                // 调用LLM处理（这里可以替换为实际的LLM API调用）
                var llmResponse = await ProcessWithLLM(prompt);
                
                // 创建说话信息
                return CreateSayInfo(llmResponse, statusSummary);
            }
            catch (Exception ex)
            {
                return new SayInfoWithOutStream($"LLM处理出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取宠物状态信息（供外部调用）
        /// </summary>
        public string GetPetStatusInfo()
        {
            return _llmStateService.GetStatusSummary();
        }

        /// <summary>
        /// 模拟LLM处理（实际项目中应替换为真实的LLM API调用）
        /// </summary>
        private async Task<string> ProcessWithLLM(string prompt)
        {
            // 模拟LLM处理延迟
            await Task.Delay(100);
            
            // 这里可以集成OpenAI、Azure OpenAI或其他LLM服务
            // 示例：返回基于宠物状态的响应
            return $"基于当前宠物状态，我建议多关注宠物的需求。{Environment.NewLine}" +
                   $"（实际项目中这里会调用真实的LLM API）";
        }

        /// <summary>
        /// 创建说话信息对象
        /// </summary>
        private SayInfo CreateSayInfo(string response, string statusContext)
        {
            // 使用流式传输的说话信息以支持更好的交互体验
            var sayInfo = new SayInfoWithStream("think", false, "LLM分析中...")
            {
                Desc = $"宠物状态: {statusContext}"
            };

            // 模拟流式输出
            SimulateStreamingResponse(sayInfo, response);
            
            return sayInfo;
        }

        /// <summary>
        /// 模拟流式响应输出
        /// </summary>
        private async void SimulateStreamingResponse(SayInfoWithStream sayInfo, string fullResponse)
        {
            await Task.Delay(500);
            
            // 分段输出响应
            var words = fullResponse.Split(' ');
            foreach (var word in words)
            {
                sayInfo.UpdateText(word + " ");
                await Task.Delay(100);
            }
            
            sayInfo.FinishGenerate();
        }

        /// <summary>
        /// 直接获取宠物状态对象
        /// </summary>
        public PetStatus GetPetStatus()
        {
            return _llmStateService.GetPetStatus();
        }

        /// <summary>
        /// 检查是否可以执行特定动作
        /// </summary>
        public bool CanPerformAction(string actionType)
        {
            var status = GetPetStatus();
            
            return actionType switch
            {
                "feed" => status.StrengthFood < 80,
                "drink" => status.StrengthDrink < 80,
                "play" => status.Feeling < 70,
                "rest" => status.Strength < 50,
                _ => false
            };
        }
    }
}