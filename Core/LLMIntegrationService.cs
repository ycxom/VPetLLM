using System;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPetLLM.Controllers;
using VPetLLM.Services;

namespace VPetLLM.Core
{
    /// <summary>
    /// LLM集成服务 - 连接VPet状态和LLM处理功能
    /// </summary>
    public class LLMIntegrationService
    {
        private readonly LLMProcessorController _llmProcessor;
        private readonly GameCore _gameCore;

        public LLMIntegrationService(GameCore gameCore)
        {
            _gameCore = gameCore;
            _llmProcessor = new LLMProcessorController(gameCore);
        }

        /// <summary>
        /// 处理用户输入并返回LLM增强的响应
        /// </summary>
        public async Task<SayInfo> ProcessWithLLMAsync(string userInput)
        {
            try
            {
                // 获取当前宠物状态
                var statusInfo = _llmProcessor.GetPetStatusInfo();
                
                // 处理LLM响应
                return await _llmProcessor.ProcessUserInputAsync(userInput);
            }
            catch (Exception ex)
            {
                return new SayInfoWithOutStream($"LLM处理错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取宠物状态信息
        /// </summary>
        public string GetPetStatus()
        {
            return _llmProcessor.GetPetStatusInfo();
        }

        /// <summary>
        /// 检查是否可以执行特定动作
        /// </summary>
        public bool CanPerformAction(string actionType)
        {
            return _llmProcessor.CanPerformAction(actionType);
        }

        /// <summary>
        /// 获取详细的宠物状态对象
        /// </summary>
        public PetStatus GetDetailedPetStatus()
        {
            return _llmProcessor.GetPetStatus();
        }

        /// <summary>
        /// 生成LLM提示模板
        /// </summary>
        public string GeneratePromptTemplate(string userInput)
        {
            var status = GetPetStatus();
            return $"{status}\n\n用户说: {userInput}\n\n请根据宠物当前状态给出合适的回应和建议。";
        }

        /// <summary>
        /// 模拟LLM调用（实际项目中应替换为真实API）
        /// </summary>
        public async Task<string> CallLLMAsync(string prompt)
        {
            // 模拟API调用延迟
            await Task.Delay(200);
            
            // 这里应该调用真实的LLM API
            // 返回基于宠物状态的模拟响应
            return $"基于当前宠物状态分析: {prompt}\n\n建议: 注意宠物的基本需求，保持健康状态。";
        }
    }
}