using System.Diagnostics;

namespace VPetLLM.Core
{
    /// <summary>
    /// LLM 调用入口点 - 为插件和外部应用提供统一的 LLM 调用接口
    /// 未来可扩展：调用者识别、权限管理、速率限制、调用统计等
    /// </summary>
    public class LLMEntryPoint
    {
        private readonly VPetLLM _vpetLLM;
        private readonly IStructuredLogger _logger;

        public LLMEntryPoint(VPetLLM vpetLLM, IStructuredLogger logger)
        {
            _vpetLLM = vpetLLM ?? throw new ArgumentNullException(nameof(vpetLLM));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 调用 LLM 处理消息
        /// </summary>
        /// <param name="message">要发送给 LLM 的消息</param>
        /// <param name="purpose">用途标识（如 "Chat"、"Compression" 或插件自定义），预留供插件指定用途</param>
        /// <returns>LLM 的文本响应</returns>
        public async Task<string> CallAsync(string message, string? purpose = null)
        {
            var startTime = DateTime.UtcNow;
            string caller = "Unknown";

            try
            {
                // 识别调用者
                caller = GetCallerInfo();

                if (_vpetLLM.ChatCore == null)
                {
                    Logger.Log($"[LLM Call] {caller} - ChatCore is null");
                    return "Error: LLM service not initialized";
                }

                // 记录调用
                Logger.Log($"[LLM Call] {caller} calling LLM");
                Logger.Log($"[LLM Call] Message: {TruncateForLog(message, 200)}");

                // 使用现有的 Chat 方法，isFunctionCall=true 表示不修改历史
                var response = await _vpetLLM.ChatCore.Chat(message, isFunctionCall: true);

                // 计算耗时
                var duration = DateTime.UtcNow - startTime;

                // 记录响应
                Logger.Log($"[LLM Call] {caller} - Response in {duration.TotalSeconds:F2}s");
                Logger.Log($"[LLM Call] Response: {TruncateForLog(response ?? "", 200)}");

                return response ?? "Error: No response from LLM";
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                Logger.Log($"[LLM Call] {caller} - Error after {duration.TotalSeconds:F2}s: {ex.Message}");
                _logger.LogError($"LLM call failed for {caller}", ex);
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// 获取调用者信息（插件或外部应用）
        /// 未来可扩展：更精确的调用者识别、权限验证等
        /// </summary>
        private string GetCallerInfo()
        {
            try
            {
                var stackTrace = new StackTrace();

                for (int i = 0; i < stackTrace.FrameCount; i++)
                {
                    var frame = stackTrace.GetFrame(i);
                    var method = frame?.GetMethod();
                    var declaringType = method?.DeclaringType;

                    if (declaringType != null)
                    {
                        // 检查是否是插件
                        if (typeof(IVPetLLMPlugin).IsAssignableFrom(declaringType))
                        {
                            var plugin = _vpetLLM.Plugins.FirstOrDefault(p =>
                                p.GetType() == declaringType ||
                                p.GetType().FullName == declaringType.FullName);

                            if (plugin != null)
                                return $"Plugin:{plugin.Name}";

                            return $"Plugin:{declaringType.Name}";
                        }

                        // 检查是否是外部应用
                        if (declaringType.Namespace != null &&
                            !declaringType.Namespace.StartsWith("VPetLLM") &&
                            !declaringType.Namespace.StartsWith("VPet_Simulator"))
                        {
                            return $"ExternalProgram:{declaringType.Namespace}.{declaringType.Name}";
                        }
                    }
                }

                return "Unknown";
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to get caller info: {ex.Message}");
                return "Unknown";
            }
        }

        /// <summary>
        /// 截断字符串用于日志
        /// </summary>
        private string TruncateForLog(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            if (text.Length <= maxLength)
                return text;

            return text.Substring(0, maxLength) + "...";
        }
    }
}
