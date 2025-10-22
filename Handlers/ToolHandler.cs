using System;
using System.Linq;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Core;
using VPetLLM.Utils;

namespace VPetLLM.Handlers
{
    public class ToolHandler : IActionHandler
    {
        public ActionType ActionType => ActionType.Tool;
        public string Keyword => "tool";
        public string Description => Utils.PromptHelper.Get("Handler_Tool_Description", VPetLLM.Instance.Settings.PromptLanguage);

        public Task Execute(IMainWindow main)
        {
            return Task.CompletedTask;
        }

        public async Task Execute(string value, IMainWindow main)
        {
            // 若处于单条 AI 回复的执行会话中，则豁免限流（允许该回复内多次工具联合调用）
            var inMessageSession = global::VPetLLM.Utils.ExecutionContext.CurrentMessageId.Value.HasValue;

            // 跨消息限流：使用配置的参数
            if (!inMessageSession)
            {
                var config = RateLimiter.GetConfig("tool");
                if (!RateLimiter.TryAcquire("tool", 5, TimeSpan.FromMinutes(2)))
                {
                    var stats = RateLimiter.GetStats("tool");
                    VPetLLM.Instance.Log($"ToolHandler: 触发熔断 - 工具调用超限（跨消息）");
                    if (VPetLLM.Instance?.Settings?.RateLimiter?.LogRateLimitEvents == true && stats != null)
                    {
                        VPetLLM.Instance.Log($"  当前: {stats.CurrentCount}/{config?.MaxCount}, 已阻止: {stats.BlockedRequests}次");
                    }
                    return;
                }
            }

            VPetLLM.Instance.Log($"ToolHandler: Received value: {value}");
            var firstParen = value.IndexOf('(');
            var lastParen = value.LastIndexOf(')');

            if (firstParen != -1 && lastParen != -1 && lastParen > firstParen)
            {
                var toolName = value.Substring(0, firstParen).Trim();
                var arguments = value.Substring(firstParen + 1, lastParen - firstParen - 1);
                VPetLLM.Instance.Log($"ToolHandler: Parsed tool name: {toolName}, arguments: {arguments}");
                var tool = VPetLLM.Instance.Plugins.Find(p => p.Name.Replace(" ", "_").ToLower() == toolName.ToLower());
                if (tool != null)
                {
                    if (!tool.Enabled)
                    {
                        VPetLLM.Instance.Log($"ToolHandler: Tool '{tool.Name}' is disabled.");
                        return;
                    }
                    VPetLLM.Instance.Log($"ToolHandler: Found tool: {tool.Name}");
                    if (tool is IActionPlugin actionPlugin)
                    {
                        var result = await actionPlugin.Function(arguments);
                        var formattedResult = $"[Tool Result: {toolName}] {result}";
                        VPetLLM.Instance.Log($"ToolHandler: Tool function returned: {result}, formatted: {formattedResult}");

                        // 聚合到2秒窗口，统一回灌，避免连续触发LLM
                        ResultAggregator.Enqueue(formattedResult);
                    }
                }
                else
                {
                    VPetLLM.Instance.Log($"ToolHandler: Tool not found: {toolName}");
                    var availableTools = string.Join(", ", VPetLLM.Instance.Plugins.Where(p => p.Enabled).Select(p => p.Name));
                    var errorMessage = $"[SYSTEM] Error: Tool '{toolName}' not found. Available tools are: {availableTools}";

                    // 错误信息也进入聚合，避免多次触发LLM
                    ResultAggregator.Enqueue(errorMessage);
                }
            }
            else
            {
                VPetLLM.Instance.Log($"ToolHandler: Parentheses not found or mismatched for value: {value}");
            }
        }

        public Task Execute(int value, IMainWindow main)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;

        /// <summary>
        /// 供工具直接调用的方法，确保工具消息经过正确的格式化
        /// </summary>
        public static void SendToolMessage(string toolName, string message)
        {
            if (string.IsNullOrEmpty(toolName) || string.IsNullOrEmpty(message))
            {
                VPetLLM.Instance.Log("Tool message skipped: tool name or message is empty");
                return;
            }

            var formattedResult = $"[Tool Result: {toolName}] {message}";
            VPetLLM.Instance.Log($"Tool message formatted: {formattedResult}");

            // 使用聚合器确保消息正确处理
            ResultAggregator.Enqueue(formattedResult);
        }
    }
}