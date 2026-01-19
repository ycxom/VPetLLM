using System.Net;
using System.Net.Http;
using VPetLLM.Utils.Localization;
using SystemIO = System.IO;
using SystemNet = System.Net;

namespace VPetLLM.Utils.System
{
    public static class ErrorMessageHelper
    {
        /// <summary>
        /// 检查是否为调试模式（角色设定中包含 VPetLLM_DeBug）
        /// </summary>
        public static bool IsDebugMode(Setting settings)
        {
            if (settings == null || string.IsNullOrEmpty(settings.Role))
                return false;

            // 模糊匹配，不区分大小写
            return settings.Role.IndexOf("VPetLLM_DeBug", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 根据HTTP状态码获取人性化的错误信息（多语言支持）
        /// </summary>
        public static string GetFriendlyHttpError(HttpStatusCode statusCode, string rawError, Setting settings)
        {
            // 如果是调试模式，返回原始错误
            if (IsDebugMode(settings))
            {
                return rawError;
            }

            var langCode = settings?.Language ?? "zh-hans";

            // 根据状态码返回人性化错误信息
            var errorKey = statusCode switch
            {
                HttpStatusCode.Unauthorized => "API.Error.Unauthorized",
                HttpStatusCode.Forbidden => "API.Error.Forbidden",
                HttpStatusCode.NotFound => "API.Error.NotFound",
                HttpStatusCode.TooManyRequests => "API.Error.TooManyRequests",
                HttpStatusCode.BadRequest => "API.Error.BadRequest",
                HttpStatusCode.InternalServerError => "API.Error.InternalServerError",
                HttpStatusCode.BadGateway => "API.Error.BadGateway",
                HttpStatusCode.ServiceUnavailable => "API.Error.ServiceUnavailable",
                HttpStatusCode.GatewayTimeout => "API.Error.GatewayTimeout",
                _ => "API.Error.Unknown"
            };

            var message = LanguageHelper.GetError(errorKey, langCode);

            // 如果是未知错误，添加状态码
            if (errorKey == "API.Error.Unknown" && message != $"[{errorKey}]")
            {
                message = $"{message} ({(int)statusCode})";
            }

            return message != $"[{errorKey}]" ? message : $"Request failed ({(int)statusCode}). Please try again later.";
        }

        /// <summary>
        /// 根据异常类型获取人性化的错误信息（多语言支持）
        /// </summary>
        public static string GetFriendlyExceptionError(Exception ex, Setting settings, string providerName = "API")
        {
            // 如果是调试模式，返回原始错误
            if (IsDebugMode(settings))
            {
                return $"{providerName} 错误: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            }

            var langCode = settings?.Language ?? "zh-hans";

            // 根据异常类型返回人性化错误信息
            return ex switch
            {
                HttpRequestException httpEx => GetHttpRequestExceptionMessage(httpEx, settings, providerName),
                TaskCanceledException => GetLocalizedErrorWithProvider("API.Error.Timeout", langCode, providerName),
                SystemNet.Sockets.SocketException => GetLocalizedErrorWithProvider("API.Error.ConnectionRefused", langCode, providerName),
                SystemIO.IOException => LanguageHelper.GetError("API.Error.NetworkError", langCode),
                Newtonsoft.Json.JsonException => GetLocalizedErrorWithProvider("API.Error.InvalidResponse", langCode, providerName),
                _ => GetLocalizedErrorWithProvider("API.Error.ServiceException", langCode, providerName)
            };
        }

        private static string GetHttpRequestExceptionMessage(HttpRequestException ex, Setting settings, string providerName)
        {
            var langCode = settings?.Language ?? "zh-hans";
            var message = ex.Message.ToLower();

            if (message.Contains("connection refused") || message.Contains("无法连接"))
                return GetLocalizedErrorWithProvider("API.Error.ConnectionRefused", langCode, providerName);

            if (message.Contains("name resolution") || message.Contains("dns") || message.Contains("主机"))
                return GetLocalizedErrorWithProvider("API.Error.DnsResolution", langCode, providerName);

            if (message.Contains("ssl") || message.Contains("tls") || message.Contains("certificate"))
                return LanguageHelper.GetError("API.Error.SslError", langCode);

            return LanguageHelper.GetError("API.Error.NetworkError", langCode);
        }

        /// <summary>
        /// 获取带有提供商名称的本地化错误信息
        /// </summary>
        private static string GetLocalizedErrorWithProvider(string errorKey, string langCode, string providerName)
        {
            var message = LanguageHelper.GetError(errorKey, langCode);
            if (message == $"[{errorKey}]")
            {
                return $"{providerName} service error. Please try again later.";
            }
            return message.Replace("{Provider}", providerName);
        }

        /// <summary>
        /// 处理HTTP响应错误，返回适当的错误信息
        /// </summary>
        public static async Task<string> HandleHttpResponseError(
            HttpResponseMessage response,
            Setting settings,
            string providerName = "API")
        {
            var statusCode = response.StatusCode;
            var rawError = await response.Content.ReadAsStringAsync();

            Logger.Log($"{providerName} API 错误: {(int)statusCode} {statusCode} - {rawError}");

            // 如果是调试模式，返回详细的原始错误
            if (IsDebugMode(settings))
            {
                return $"{providerName} API 错误 [{(int)statusCode} {statusCode}]: {rawError}";
            }

            return GetFriendlyHttpError(statusCode, rawError, settings);
        }

        /// <summary>
        /// 获取Ollama特定的超时错误信息
        /// </summary>
        public static string GetOllamaTimeoutError(Setting settings)
        {
            if (IsDebugMode(settings))
                return null; // 返回null表示使用原始错误

            var langCode = settings?.Language ?? "zh-hans";
            return LanguageHelper.GetError("Ollama.Error.Timeout", langCode);
        }

        /// <summary>
        /// 获取Ollama特定的连接失败错误信息
        /// </summary>
        public static string GetOllamaConnectionError(Setting settings)
        {
            if (IsDebugMode(settings))
                return null; // 返回null表示使用原始错误

            var langCode = settings?.Language ?? "zh-hans";
            return LanguageHelper.GetError("Ollama.Error.ConnectionFailed", langCode);
        }

        /// <summary>
        /// 获取Free API特定的错误信息
        /// </summary>
        public static string GetFreeApiError(Setting settings, string errorType)
        {
            if (IsDebugMode(settings))
                return null; // 返回null表示使用原始错误

            var langCode = settings?.Language ?? "zh-hans";
            return errorType switch
            {
                "ConfigNotLoaded" => LanguageHelper.GetError("Free.Error.ConfigNotLoaded", langCode),
                "ServiceMaintenance" => LanguageHelper.GetError("Free.Error.ServiceMaintenance", langCode),
                "ServiceUnavailable" => LanguageHelper.GetError("Free.Error.ServiceUnavailable", langCode),
                _ => LanguageHelper.GetError("API.Error.ServiceException", langCode).Replace("{Provider}", "Free")
            };
        }

        /// <summary>
        /// 获取总结功能失败的错误信息
        /// </summary>
        public static string GetSummarizeError(Setting settings)
        {
            if (IsDebugMode(settings))
                return null; // 返回null表示使用原始错误

            var langCode = settings?.Language ?? "zh-hans";
            return LanguageHelper.GetError("API.Error.SummarizeFailed", langCode);
        }

        /// <summary>
        /// 获取模型列表获取失败的错误信息
        /// </summary>
        public static string GetModelsError(Setting settings)
        {
            if (IsDebugMode(settings))
                return null; // 返回null表示使用原始错误

            var langCode = settings?.Language ?? "zh-hans";
            return LanguageHelper.GetError("API.Error.GetModelsFailed", langCode);
        }

        public static string GetLocalizedError(string errorKey, string langCode, string fallbackMessage, Exception ex)
        {
            var localizedMessage = LanguageHelper.GetError(errorKey, langCode);
            var localizedTitle = LanguageHelper.GetError("Error", langCode);

            if (localizedMessage == $"[{errorKey}]")
            {
                localizedMessage = fallbackMessage;
            }

            return $"{localizedMessage}: {ex.Message}";
        }

        public static string GetLocalizedMessage(string messageKey, string langCode, string fallbackMessage)
        {
            var localizedMessage = LanguageHelper.GetError(messageKey, langCode);
            if (localizedMessage == $"[{messageKey}]")
            {
                localizedMessage = fallbackMessage;
            }
            return localizedMessage;
        }

        public static string GetLocalizedTitle(string titleKey, string langCode, string fallbackTitle)
        {
            var localizedTitle = LanguageHelper.GetError(titleKey, langCode);
            if (localizedTitle == $"[{titleKey}]")
            {
                localizedTitle = fallbackTitle;
            }
            return localizedTitle;
        }
    }
}
