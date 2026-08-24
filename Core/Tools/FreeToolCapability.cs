using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using VPetLLM.Utils.System;

namespace VPetLLM.Core.Tools
{
    /// <summary>
    /// Free 渠道的原生工具调用能力仲裁。
    ///
    /// 其它渠道由用户在设置里勾 <c>EnableToolCall</c>，因为用户知道自己填的是哪个模型；
    /// Free 的模型由云端下发、随时可能被换掉，用户既看不见也控制不了，所以这里改成
    /// 「云端给策略 + 客户端自己探测」：
    ///
    /// <list type="bullet">
    /// <item><c>Off</c>  —— 永远不挂 tools，走标记协议。</item>
    /// <item><c>On</c>   —— 云端确认当前模型支持，直接挂 tools，不探测。</item>
    /// <item><c>Auto</c> —— 默认。先当作支持来发一次请求，根据真实响应判定；
    ///       判定为不支持后，本次运行内的后续请求退回标记协议。</item>
    /// </list>
    ///
    /// **判定结果只活在内存里**，进程重启、以及每次打开设置窗口都会清空
    /// （见 <see cref="Reset"/>）。这是刻意的：云端换了模型之后，一个被持久化的
    /// "不支持"结论会永远压住新模型的能力，而用户完全没有手段去纠正它。
    /// </summary>
    public static class FreeToolCapability
    {
        public enum Policy
        {
            /// <summary>永不启用。</summary>
            Off,
            /// <summary>自动探测（默认）。</summary>
            Auto,
            /// <summary>云端确认支持，强制启用。</summary>
            On
        }

        public enum Probe
        {
            /// <summary>尚未得出结论 —— 下一次请求就是探测请求。</summary>
            Unknown,
            /// <summary>已观察到规范的 tool_calls。</summary>
            Supported,
            /// <summary>已观察到明确的不支持证据。</summary>
            Unsupported
        }

        private static readonly object Gate = new();
        private static Policy _policy = Policy.Auto;
        private static Probe _probe = Probe.Unknown;
        /// <summary>是否已经至少应用过一次云端配置（用来让首次读取仍然留一行日志）。</summary>
        private static bool _everApplied;

        /// <summary>云端下发的策略。未下发时保持 <see cref="Policy.Auto"/>。</summary>
        public static Policy CurrentPolicy
        {
            get { lock (Gate) return _policy; }
        }

        public static Probe CurrentProbe
        {
            get { lock (Gate) return _probe; }
        }

        /// <summary>
        /// 从云端配置读取策略。字段缺失按 Auto 处理 —— 老配置文件不该把功能锁死。
        /// 同时容忍布尔写法（true/false），省得下发端纠结类型。
        /// </summary>
        public static void ApplyCloudConfig(JToken? token)
        {
            var policy = ParsePolicy(token);
            bool changed;
            lock (Gate)
            {
                changed = _policy != policy || !_everApplied;
                if (_policy != policy)
                {
                    // 策略变了，之前那次探测的结论就作废
                    _probe = Probe.Unknown;
                }
                _policy = policy;
                _everApplied = true;
            }

            // 云端配置每 5 分钟刷一次，绝大多数时候策略没变。
            // 无条件打日志会往用户的 Debug.log 里灌进每天近 300 行毫无信息量的重复。
            if (changed) Logger.Log($"FreeToolCapability: 云端工具调用策略={policy}");
        }

        public static Policy ParsePolicy(JToken? token)
        {
            if (token is null || token.Type == JTokenType.Null) return Policy.Auto;

            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>() ? Policy.On : Policy.Off;

            var raw = token.ToString().Trim();
            if (string.IsNullOrEmpty(raw)) return Policy.Auto;

            return raw.ToLowerInvariant() switch
            {
                "on" or "true" or "1" or "enable" or "enabled" or "force" => Policy.On,
                "off" or "false" or "0" or "disable" or "disabled" => Policy.Off,
                _ => Policy.Auto
            };
        }

        /// <summary>
        /// 回到"未探测"。进程启动天然如此；此外每次打开设置窗口也会调一次，
        /// 这样用户在云端换模型后只要开一下设置面板就能让新模型重新被认定。
        /// </summary>
        public static void Reset()
        {
            lock (Gate)
            {
                if (_probe == Probe.Unknown) return;
                _probe = Probe.Unknown;
            }
            Logger.Log("FreeToolCapability: 探测结论已清空，下次请求重新检测");
        }

        /// <summary>本次请求要不要挂 tools。</summary>
        public static bool ShouldAttachTools()
        {
            lock (Gate)
            {
                return _policy switch
                {
                    Policy.Off => false,
                    Policy.On => true,
                    _ => _probe != Probe.Unsupported
                };
            }
        }

        /// <summary>
        /// 能力是否**已经确证**（云端强制 On，或探测拿到过规范的 tool_calls）。
        ///
        /// 和 <see cref="ShouldAttachTools"/> 的区别很关键：挂 tools 可以乐观，
        /// 但"叫模型优先用工具调用"这句提示词不能。探测期就写这句的话，
        /// 一个通道不支持工具的模型会被这句话推着放弃标记协议，最后既没发出工具调用、
        /// 又只留下一句"我这就去查……"的空承诺 —— 这正是实测里 DeepSeek-R1 的表现。
        /// 所以提示词只在确证之后才加；未确证时保持标记协议为主，tools 照挂，
        /// 真支持的模型本来就会按 tools 数组发起调用，不依赖这句提示。
        /// </summary>
        public static bool IsProven()
        {
            lock (Gate)
            {
                return _policy switch
                {
                    Policy.Off => false,
                    Policy.On => true,
                    _ => _probe == Probe.Supported
                };
            }
        }

        /// <summary>
        /// 本次请求是不是探测请求。探测请求即使失败也要能优雅退回标记协议，
        /// 所以调用方需要据此决定"要不要为这一轮准备无工具的重试"。
        /// </summary>
        public static bool IsProbing()
        {
            lock (Gate)
            {
                return _policy == Policy.Auto && _probe == Probe.Unknown;
            }
        }

        public static void MarkSupported()
        {
            bool changed;
            lock (Gate)
            {
                changed = _probe != Probe.Supported;
                _probe = Probe.Supported;
            }
            if (changed) Logger.Log("FreeToolCapability: 探测到当前 Free 模型支持原生工具调用，本次运行改用工具模式");
        }

        public static void MarkUnsupported(string reason)
        {
            bool changed;
            lock (Gate)
            {
                changed = _probe != Probe.Unsupported;
                _probe = Probe.Unsupported;
            }
            if (changed) Logger.Log($"FreeToolCapability: 当前 Free 模型不支持原生工具调用，本次运行退回标记协议（{reason}）");
        }

        #region 判定

        /// <summary>
        /// HTTP 失败是不是"因为带了 tools"。只有这种失败才该判定为不支持 ——
        /// 限流、鉴权、服务维护都跟工具能力无关，误判会让工具模式被无谓地关掉。
        /// </summary>
        public static bool LooksLikeToolRejection(int statusCode, string? body)
        {
            // 5xx 一律不算：那是服务端自己的问题
            if (statusCode < 400 || statusCode >= 500) return false;
            if (string.IsNullOrEmpty(body)) return false;

            var lower = body.ToLowerInvariant();
            if (!lower.Contains("tool") && !lower.Contains("function")) return false;

            // "tool" 也可能只是出现在无关的错误文案里，再要求一个表示"不认识/不支持"的词
            return lower.Contains("not support")
                || lower.Contains("unsupported")
                || lower.Contains("not allowed")
                || lower.Contains("unrecognized")
                || lower.Contains("unknown")
                || lower.Contains("invalid")
                || lower.Contains("unexpected")
                || lower.Contains("does not")
                || lower.Contains("no support")
                || lower.Contains("不支持");
        }

        /// <summary>
        /// 模型"假装"调用工具的特征：HTTP 200、tool_calls 为空，却把调用意图当正文吐出来。
        ///
        /// 这些串都是实测抓到的（DeepSeek-R1 会漏出自己的内部 token、Qwen2.5-7B 会直接
        /// 打印函数名和 arguments），而不是凭空列的。宁可漏判也不要误判 ——
        /// 误判会把一个本来正常的模型踢回标记协议。
        /// </summary>
        public static bool LooksLikeToolCallLeakage(string? content, IEnumerable<string>? toolNames)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;

            // DeepSeek 系的内部分隔符（U+2581 下划块），正常文本里不可能出现
            if (content.Contains("tool▁call") || content.Contains("tool▁calls")) return true;

            // Qwen 原生的 XML 风格标签，以及常见的伪标签写法
            if (content.Contains("<tool_call>") || content.Contains("</tool_call>")) return true;
            if (content.Contains("<function_call>") || content.Contains("<|tool_call")) return true;

            // ```function / ```tool_call 代码块
            if (content.Contains("```function") || content.Contains("```tool_call")) return true;

            // 兜底：正文里同时出现了某个真实工具名和 arguments 字段
            if (toolNames is not null && content.Contains("arguments"))
            {
                foreach (var name in toolNames)
                {
                    if (!string.IsNullOrEmpty(name) &&
                        content.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion
    }
}
