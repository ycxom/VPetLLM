using System;
using System.Text.RegularExpressions;
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
                    Logger.Log($"ToolHandler: 触发熔断 - 工具调用超限（跨消息）");
                    if (VPetLLM.Instance?.Settings?.RateLimiter?.LogRateLimitEvents == true && stats != null)
                    {
                        Logger.Log($"  当前: {stats.CurrentCount}/{config?.MaxCount}, 已阻止: {stats.BlockedRequests}次");
                    }
                    return;
                }
            }

            var match = new Regex(@"(.*?)\((.*)\)").Match(value);
            if (match.Success)
            {
                var toolName = match.Groups[1].Value;
                var arguments = match.Groups[2].Value;
                var tool = VPetLLM.Instance.Plugins.Find(p => p.Name.Replace(" ", "_").ToLower() == toolName);
                if (tool is IActionPlugin actionPlugin)
                {
                    var result = await actionPlugin.Function(arguments);

                    // 旧逻辑：直接写入History的tool消息会触发二次会话，这里改为进入2秒聚合，统一回灌
                    var formatted = $"[Tool.{toolName}: \"{result}\"]";
                    ResultAggregator.Enqueue(formatted);
                }
            }
        }

        public Task Execute(int value, IMainWindow main)
        {
            return Task.CompletedTask;
        }
        public int GetAnimationDuration(string animationName) => 0;
    }
}