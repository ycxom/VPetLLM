using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VPetLLM.Core.Tools
{
    /// <summary>
    /// 把一轮原生工具调用**还原成标记协议的消息序列**，供落库。
    ///
    /// 为什么要这么做：工具循环的中间消息原本只活在单次请求里，不进历史。
    /// 于是同一个插件调用，走标记模式在历史里白纸黑字，走工具模式却查无此事 ——
    /// 下一轮模型不知道自己已经查过了。这个不对称就是本类要消除的东西。
    ///
    /// 设计上只做**入站**（工具 → 标记），不做出站（标记 → 工具）：
    /// 历史里因此永远只有标记一种格式，"关掉工具模式后怎么把历史喂给 LLM"
    /// 这个问题就不存在了，也不必去猜历史里哪条结果消息对应哪次调用。
    ///
    /// 还原是无损的，因为 <see cref="NativeToolResult.MarkerArguments"/> 本来就是
    /// 拿去调插件的那段文本 —— 不是事后从 JSON 猜出来的。
    /// </summary>
    public static class NativeToolTranscript
    {
        // 注意：转录**只覆盖发生了工具调用的那些轮次**，不含模型最终的自然语言回复 ——
        // 最终回复由各 ChatCore 原有的落库逻辑负责，重复记录会让历史里出现两条一样的 assistant。

        /// <summary>转录中的一条消息。Role 用 VPetLLM 历史里的角色名。</summary>
        public readonly struct Entry
        {
            public Entry(string role, string content)
            {
                Role = role;
                Content = content;
            }

            public string Role { get; }
            public string Content { get; }
        }

        /// <summary>
        /// 渲染一次工具调用为标记文本。和标记模式里模型自己写出来的形式一致。
        /// </summary>
        public static string RenderCall(NativeToolResult result)
        {
            // 插件名缺失（未知工具 / 插件没加载）时退回模型给的函数名，
            // 至少让历史里留下"它试图调用什么"，而不是凭空消失
            var name = string.IsNullOrEmpty(result.PluginName) ? result.Call.Name : result.PluginName;
            return $"<|plugin_{name}_begin|> {result.MarkerArguments} <|plugin_{name}_end|>";
        }

        /// <summary>
        /// 渲染插件结果。**刻意复用标记模式那句一模一样的格式**
        /// （PluginHandler 里的 <c>[Plugin Result: {name}] {result}</c>），
        /// 这样两种模式产出的历史才能逐字节相同 —— 这是可以写成断言的等价性。
        /// </summary>
        public static string RenderResult(NativeToolResult result)
        {
            var name = string.IsNullOrEmpty(result.PluginName) ? result.Call.Name : result.PluginName;
            return $"[Plugin Result: {name}] {result.Content}";
        }

        /// <summary>
        /// 追加一轮：模型这一轮说的话 + 它发起的调用，然后是每个调用的结果。
        ///
        /// 顺序刻意和标记模式对齐：assistant（含标记）→ 结果回灌 → 下一轮 assistant。
        /// </summary>
        public static void AppendRound(
            List<Entry> transcript, string? assistantContent, IReadOnlyList<NativeToolResult> results)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(assistantContent))
            {
                sb.Append(assistantContent!.Trim());
            }

            foreach (var result in results)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(RenderCall(result));
            }

            if (sb.Length > 0)
            {
                transcript.Add(new Entry("assistant", sb.ToString()));
            }

            // 结果各自成条，和 ResultAggregator 回灌时的粒度一致
            foreach (var result in results)
            {
                // 空结果在标记模式下压根不会回灌（PluginHandler 里 if (!string.IsNullOrEmpty(result))），
                // 这里跟着跳过，否则两种模式的历史会差出一条
                if (string.IsNullOrEmpty(result.Content)) continue;
                transcript.Add(new Entry("user", RenderResult(result)));
            }
        }

        /// <summary>
        /// 调试用：把整段转录拼成可读文本。
        /// </summary>
        public static string Describe(IEnumerable<Entry> transcript)
            => string.Join("\n", transcript.Select(e => $"[{e.Role}] {e.Content}"));
    }
}
