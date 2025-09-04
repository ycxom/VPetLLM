using System;
using System.Threading.Tasks;
using VPet_Simulator.Core;
using VPetLLM.Services;

namespace VPetLLM.Examples
{
    /// <summary>
    /// LLM功能使用示例
    /// </summary>
    public class LLMUsageExample
    {
        private readonly GameCore _gameCore;
        private readonly LLMStateService _stateService;

        public LLMUsageExample(GameCore gameCore)
        {
            _gameCore = gameCore;
            _stateService = new LLMStateService(gameCore);
        }

        /// <summary>
        /// 示例1: 获取宠物状态信息
        /// </summary>
        public void Example_GetPetStatus()
        {
            var status = _stateService.GetPetStatus();
            Console.WriteLine($"宠物名称: {status.Name}");
            Console.WriteLine($"等级: {status.Level}");
            Console.WriteLine($"健康值: {status.Health:F1}");
            Console.WriteLine($"体力值: {status.Strength:F1}");
            Console.WriteLine($"饱腹度: {status.StrengthFood:F1}");
            Console.WriteLine($"口渴度: {status.StrengthDrink:F1}");
            Console.WriteLine($"心情值: {status.Feeling:F1}");
            Console.WriteLine($"好感度: {status.Likability:F1}");
        }

        /// <summary>
        /// 示例2: 生成LLM提示
        /// </summary>
        public void Example_GenerateLLMPrompt()
        {
            var prompt = _stateService.GenerateLLMPrompt("我今天应该怎么照顾宠物？");
            Console.WriteLine("生成的LLM提示:");
            Console.WriteLine(prompt);
        }

        /// <summary>
        /// 示例3: 状态检查和建议
        /// </summary>
        public void Example_StatusCheckAndAdvice()
        {
            var status = _stateService.GetPetStatus();
            
            Console.WriteLine("=== 宠物状态分析 ===");
            Console.WriteLine(_stateService.GetStatusSummary());
            
            // 健康检查
            if (status.Health < 30)
            {
                Console.WriteLine("⚠️ 警告: 宠物健康值过低，需要立即关注！");
            }
            
            if (status.StrengthFood < 20)
            {
                Console.WriteLine("🍗 建议: 宠物很饿，需要喂食");
            }
            
            if (status.StrengthDrink < 20)
            {
                Console.WriteLine("💧 建议: 宠物很渴，需要喝水");
            }
            
            if (status.Feeling < 30)
            {
                Console.WriteLine("😢 建议: 宠物心情不好，需要玩耍");
            }
        }

        /// <summary>
        /// 示例4: 模拟LLM响应处理
        /// </summary>
        public async Task Example_ProcessLLMResponse()
        {
            var userInput = "我的宠物看起来怎么样？";
            
            // 生成提示
            var prompt = _stateService.GenerateLLMPrompt(userInput);
            Console.WriteLine("发送给LLM的提示:");
            Console.WriteLine(prompt);
            
            // 处理响应（模拟）
            var response = await _stateService.ProcessLLMResponse("基于状态的分析响应");
            Console.WriteLine($"LLM响应: {response.ResponseText}");
        }

        /// <summary>
        /// 示例5: 集成到对话系统
        /// </summary>
        public async Task<string> Example_IntegratedResponse(string userInput)
        {
            try
            {
                // 获取状态信息
                var statusInfo = _stateService.GetStatusSummary();
                
                // 生成增强的提示
                var enhancedPrompt = $"{statusInfo}\n\n用户说: {userInput}\n\n请根据以上宠物状态信息给出回应。";
                
                // 这里可以调用实际的LLM API
                // var llmResponse = await _llmApi.CallAsync(enhancedPrompt);
                
                // 模拟响应
                return $"基于当前状态: {statusInfo}\n\n回应: 我会根据宠物状态提供更好的建议！";
            }
            catch (Exception ex)
            {
                return $"处理出错: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// 扩展功能示例
    /// </summary>
    public static class LLMExtensions
    {
        /// <summary>
        /// 获取状态表情符号
        /// </summary>
        public static string GetStatusEmoji(this PetStatus status)
        {
            return status.Health switch
            {
                > 80 => "😊",
                > 50 => "😐",
                > 30 => "😟",
                _ => "😵"
            };
        }

        /// <summary>
        /// 获取健康状态描述
        /// </summary>
        public static string GetHealthStatus(this PetStatus status)
        {
            return status.Health switch
            {
                > 90 => "非常健康",
                > 70 => "健康",
                > 50 => "一般",
                > 30 => "需要注意",
                _ => "危险状态"
            };
        }

        /// <summary>
        /// 生成状态报告
        /// </summary>
        public static string GenerateStatusReport(this PetStatus status)
        {
            return $@"=== 宠物状态报告 ===
名称: {status.Name}
等级: {status.Level}
健康: {status.Health:F1} ({status.GetHealthStatus()}) {status.GetStatusEmoji()}
体力: {status.Strength:F1}
饱腹: {status.StrengthFood:F1}
口渴: {status.StrengthDrink:F1}
心情: {status.Feeling:F1}
好感: {status.Likability:F1}
金钱: {status.Money:F1}
经验: {status.Exp:F1}
模式: {status.Mode}";
        }
    }
}