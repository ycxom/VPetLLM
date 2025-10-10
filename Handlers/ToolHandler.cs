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

            // 跨消息限流：2分钟内最多5次，超限则直接终止
            if (!inMessageSession && !RateLimiter.TryAcquire("tool", 5, TimeSpan.FromMinutes(2)))
            {
                Logger.Log("ToolHandler: 超过工具调用上限（跨消息），终止调用");
                return;
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