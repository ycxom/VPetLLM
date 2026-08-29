using Newtonsoft.Json.Linq;

namespace VPetLLM.Utils.Common
{
    /// <summary>
    /// 目标 API 的推理参数风格。同一个"思考强度"档位在不同家写法完全不同。
    /// </summary>
    public enum ReasoningApiStyle
    {
        /// <summary>OpenAI Chat Completions（含 LMStudio / Free / 各类兼容网关）：顶层 reasoning_effort 字符串</summary>
        OpenAIChat,
        /// <summary>OpenAI Responses API：顶层 reasoning 对象的 effort 字段</summary>
        OpenAIResponses,
        /// <summary>Gemini 原生：generationConfig.thinkingConfig.thinkingBudget</summary>
        Gemini,
        /// <summary>Ollama /api/chat：顶层 think 字段</summary>
        Ollama
    }

    /// <summary>
    /// 把节点上的「思考强度」<see cref="Setting.ThinkingEffort"/> 翻译成各家 API 的请求字段，
    /// 原地写进已构造好的 payload JObject。
    ///
    /// <see cref="Setting.ThinkingEffort.Default"/> 时什么都不做 —— 保持"请求里根本没有这个参数"
    /// 的现状，避免给不支持推理的模型平白加一个会被拒收的字段。
    /// </summary>
    public static class ReasoningEffortHelper
    {
        public static void Apply(JObject? payload, Setting.ThinkingEffort effort, ReasoningApiStyle style)
        {
            if (payload is null || effort == Setting.ThinkingEffort.Default)
                return;

            switch (style)
            {
                case ReasoningApiStyle.OpenAIChat:
                    payload["reasoning_effort"] = OpenAiToken(effort);
                    break;

                case ReasoningApiStyle.OpenAIResponses:
                    payload["reasoning"] = new JObject { ["effort"] = OpenAiToken(effort) };
                    break;

                case ReasoningApiStyle.Gemini:
                {
                    if (payload["generationConfig"] is not JObject genConfig)
                    {
                        genConfig = new JObject();
                        payload["generationConfig"] = genConfig;
                    }
                    genConfig["thinkingConfig"] = new JObject
                    {
                        ["thinkingBudget"] = GeminiBudget(effort)
                    };
                    break;
                }

                case ReasoningApiStyle.Ollama:
                    // 新版 Ollama 认字符串档位；老版只认 bool，字符串对它等价于 true（仍然开思考）
                    payload["think"] = OllamaToken(effort);
                    break;
            }
        }

        /// <summary>reasoning_effort 的取值：minimal 仅 GPT-5 系列支持，其余家把未知值按 medium 处理。</summary>
        private static string OpenAiToken(Setting.ThinkingEffort e) => e switch
        {
            Setting.ThinkingEffort.Minimal => "minimal",
            Setting.ThinkingEffort.Low => "low",
            Setting.ThinkingEffort.Medium => "medium",
            Setting.ThinkingEffort.High => "high",
            _ => "medium"
        };

        /// <summary>Ollama 没有 minimal 档，向下并到 low。</summary>
        private static string OllamaToken(Setting.ThinkingEffort e) => e switch
        {
            Setting.ThinkingEffort.Minimal => "low",
            Setting.ThinkingEffort.Low => "low",
            Setting.ThinkingEffort.Medium => "medium",
            Setting.ThinkingEffort.High => "high",
            _ => "medium"
        };

        /// <summary>
        /// Gemini 的 thinkingBudget 是 token 数。各模型可取范围不同（2.5 Flash 可 0 关闭，
        /// 2.5 Pro 不能关且最低 128），这里给一组中庸值；-1 表示交给模型动态决定。
        /// </summary>
        private static int GeminiBudget(Setting.ThinkingEffort e) => e switch
        {
            Setting.ThinkingEffort.Minimal => 0,
            Setting.ThinkingEffort.Low => 2048,
            Setting.ThinkingEffort.Medium => 8192,
            Setting.ThinkingEffort.High => 24576,
            _ => -1
        };
    }
}
