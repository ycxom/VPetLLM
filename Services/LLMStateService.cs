using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPet_Simulator.Core;

namespace VPetLLM.Services
{
    /// <summary>
    /// LLM状态服务 - 提供宠物状态查询和LLM交互功能
    /// </summary>
    public class LLMStateService
    {
        private readonly GameCore _gameCore;
        
        public LLMStateService(GameCore gameCore)
        {
            _gameCore = gameCore;
        }

        /// <summary>
        /// 获取宠物当前状态信息
        /// </summary>
        public PetStatus GetPetStatus()
        {
            var save = _gameCore.Save;
            return new PetStatus
            {
                Name = save.Name,
                Level = save.Level,
                Health = save.Health,
                Strength = save.Strength,
                StrengthFood = save.StrengthFood,
                StrengthDrink = save.StrengthDrink,
                Feeling = save.Feeling,
                Likability = save.Likability,
                Money = save.Money,
                Exp = save.Exp,
                Mode = save.Mode.ToString(),
                // 当前状态需要根据具体实现获取
            };
        }

        /// <summary>
        /// 获取宠物状态摘要（用于LLM提示）
        /// </summary>
        public string GetStatusSummary()
        {
            var status = GetPetStatus();
            return $"宠物[{status.Name}]状态: " +
                   $"等级{status.Level}, " +
                   $"健康{status.Health:F1}, " +
                   $"体力{status.Strength:F1}, " +
                   $"饱腹{status.StrengthFood:F1}, " +
                   $"口渴{status.StrengthDrink:F1}, " +
                   $"心情{status.Feeling:F1}, " +
                   $"好感{status.Likability:F1}, " +
                   $"金钱{status.Money:F1}, " +
                   $"经验{status.Exp:F1}, " +
                   $"模式[{status.Mode}]";
        }

        /// <summary>
        /// 处理LLM响应并执行相应动作
        /// </summary>
        public async Task<LLMResponse> ProcessLLMResponse(string llmResponse)
        {
            // 这里可以集成实际的LLM API调用
            // 目前返回模拟响应
            return await Task.FromResult(new LLMResponse
            {
                ResponseText = $"已收到LLM响应: {llmResponse}",
                ShouldPerformAction = false,
                ActionType = ""
            });
        }

        /// <summary>
        /// 根据状态生成LLM提示
        /// </summary>
        public string GenerateLLMPrompt(string userInput)
        {
            var status = GetStatusSummary();
            return $"{status}\n\n用户输入: {userInput}\n\n请根据宠物状态给出合适的回应和建议。";
        }
    }

    /// <summary>
    /// 宠物状态信息
    /// </summary>
    public class PetStatus
    {
        public string Name { get; set; }
        public int Level { get; set; }
        public double Health { get; set; }
        public double Strength { get; set; }
        public double StrengthFood { get; set; }
        public double StrengthDrink { get; set; }
        public double Feeling { get; set; }
        public double Likability { get; set; }
        public double Money { get; set; }
        public double Exp { get; set; }
        public string Mode { get; set; }
        public string CurrentState { get; set; }
    }

    /// <summary>
    /// LLM响应结果
    /// </summary>
    public class LLMResponse
    {
        public string ResponseText { get; set; }
        public bool ShouldPerformAction { get; set; }
        public string ActionType { get; set; }
        public Dictionary<string, object> ActionParameters { get; set; }
    }
}