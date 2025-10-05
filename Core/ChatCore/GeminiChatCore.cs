using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using VPet_Simulator.Windows.Interface;
using VPetLLM.Handlers;
using VPetLLM.Utils;

namespace VPetLLM.Core.ChatCore
{
    public class GeminiChatCore : ChatCoreBase
    {
        public override string Name => "Gemini";
        private readonly Setting.GeminiSetting _geminiSetting;
        private readonly Setting _setting;
        private static int _apiKeyIndex = 0;

        public GeminiChatCore(Setting.GeminiSetting geminiSetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _geminiSetting = geminiSetting;
            _setting = setting;
        }

        private string GetNextApiKey()
        {
            if (string.IsNullOrWhiteSpace(_geminiSetting.ApiKey)) return null;
            var apiKeys = _geminiSetting.ApiKey.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                              .Select(key => key.Trim())
                                              .Where(key => !string.IsNullOrWhiteSpace(key))
                                              .ToArray();
            if (apiKeys.Length == 0) return null;
            _apiKeyIndex = (_apiKeyIndex + 1) % apiKeys.Length;
            return apiKeys[_apiKeyIndex];
        }

        private async Task<string> SendRequestWithRetry(string url, HttpContent content, int estimatedTokens, bool isGet = false, int maxRetries = 3)
        {
            var availableKeys = _geminiSetting.ApiKey?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length ?? 1;
            var attemptLimit = Math.Max(maxRetries, availableKeys) + 1;

            for (int i = 0; i < attemptLimit; i++)
            {
                Logger.Log($"[{Name}] Request attempt {i + 1}/{attemptLimit}.");
                try
                {
                    Logger.Log($"[{Name}] Waiting for rate limiter...");
                    await RateLimiter.WaitForReady(estimatedTokens, availableKeys, _setting.RateLimiter).ConfigureAwait(false);
                    Logger.Log($"[{Name}] Rate limiter passed.");

                    using (var client = GetClient())
                    {
                        var apiKey = GetNextApiKey();
                        var requestUrl = url;
                        if (!string.IsNullOrEmpty(apiKey))
                        {
                            requestUrl += $"?key={apiKey}";
                        }

                        if (client.DefaultRequestHeaders.TryGetValues("User-Agent", out _))
                        {
                            client.DefaultRequestHeaders.Remove("User-Agent");
                        }
                        client.DefaultRequestHeaders.Add("User-Agent", "Lolisi_VPet_LLMAPI");

                        Logger.Log($"[{Name}] Sending HTTP request to {requestUrl}");
                        HttpResponseMessage response = isGet
                            ? await client.GetAsync(requestUrl).ConfigureAwait(false)
                            : await client.PostAsync(requestUrl, content).ConfigureAwait(false);

                        Logger.Log($"[{Name}] Received response with status code: {response.StatusCode}");

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            Logger.Log($"[{Name}] Request failed with status {response.StatusCode}. Response body: {errorBody}");
                            response.EnsureSuccessStatusCode(); // This will now throw a detailed exception
                        }
                        
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Logger.Log($"[{Name}] Rate limit error detected. Switching to next API key.");
                    if (i == attemptLimit - 1)
                    {
                        Logger.Log($"[{Name}] All API keys have been tried and failed due to rate limits.");
                        throw;
                    }
                    continue;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[{Name}] Error during request attempt {i + 1}: {ex.GetType().Name} - {ex.Message}");
                    if (i == attemptLimit - 1)
                    {
                        Logger.Log($"[{Name}] All retries failed. Rethrowing final exception.");
                        throw;
                    }
                    // For other errors, just continue to the next attempt immediately.
                    continue;
                }
            }
            throw new Exception("Request failed after all retries.");
        }

        public override Task<string> Chat(string prompt)
        {
            return Chat(prompt, false);
        }

        private List<Message> SanitizeHistory(List<Message> history)
        {
            var sanitized = new List<Message>();
            if (history == null || !history.Any()) return sanitized;

            var conversation = history.Where(m => m.Role != "system").ToList();

            // Find the last user message, as the conversation must end with a user turn.
            int lastUserIndex = conversation.FindLastIndex(m => m.NormalizedRole == "user");

            // If no user message, the conversation is invalid for a request.
            if (lastUserIndex == -1) return sanitized;

            // Trim the history to end with the last user message.
            var trimmedConversation = conversation.Take(lastUserIndex + 1).ToList();

            // Now, merge consecutive roles from this valid, user-terminated history.
            foreach (var message in trimmedConversation)
            {
                if (sanitized.Any() && sanitized.Last().NormalizedRole == message.NormalizedRole)
                {
                    sanitized.Last().Content += "\n" + message.Content;
                }
                else
                {
                    sanitized.Add(new Message { Role = message.Role, Content = message.Content });
                }
            }

            // A final check to ensure the conversation doesn't start with an assistant
            int firstValidIndex = sanitized.FindIndex(m => m.NormalizedRole == "user");
            if (firstValidIndex > 0)
            {
                return sanitized.Skip(firstValidIndex).ToList();
            }

            return sanitized;
        }

        public override async Task<string> Chat(string prompt, bool isFunctionCall = false)
        {
            var sanitizedPrompt = prompt?.Replace("\r", " ").Replace("\n", " ") ?? "";
            Logger.Log($"[{Name}] Chat process started. Prompt: '{(sanitizedPrompt.Length > 50 ? sanitizedPrompt.Substring(0, 50) + "..." : sanitizedPrompt)}");
            try
            {
                if (!Settings.KeepContext)
                {
                    Logger.Log($"[{Name}] Context is disabled, clearing history.");
                    ClearContext();
                }

                Logger.Log($"[{Name}] Checking for history summarization...");
                await CheckAndSummarizeHistoryIfNeeded().ConfigureAwait(false);
                Logger.Log($"[{Name}] History summarization check complete.");

                if (!string.IsNullOrEmpty(prompt))
                {
                    await HistoryManager.AddMessage(new Message { Role = "user", Content = prompt }).ConfigureAwait(false);
                }

                var systemMessage = GetSystemMessage();
                var history = HistoryManager.GetHistory().Skip(Math.Max(0, HistoryManager.GetHistory().Count - _setting.HistoryCompressionThreshold)).ToList();
                List<Message> sanitizedHistory = SanitizeHistory(history);

                var requestData = new
                {
                    contents = sanitizedHistory.Select(m => new { role = m.NormalizedRole == "assistant" ? "model" : m.NormalizedRole, parts = new[] { new { text = m.Content } } }),
                    generationConfig = new
                    {
                        maxOutputTokens = _geminiSetting.EnableAdvanced ? _geminiSetting.MaxTokens : 4096,
                        temperature = _geminiSetting.EnableAdvanced ? _geminiSetting.Temperature : 0.8
                    },
                    systemInstruction = new
                    {
                        parts = new[] { new { text = systemMessage } }
                    }
                };

                var requestJson = JsonConvert.SerializeObject(requestData, Formatting.Indented);
                Logger.Log($"[{Name}] Sending payload:\n{requestJson}"); // Log the payload
                var requestContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
                var estimatedTokens = requestJson.Length / 2;

                var apiEndpoint = GetApiUrl($"/models/{_geminiSetting.Model}:generateContent");
                Logger.Log($"[{Name}] Preparing to send request to {apiEndpoint}. Estimated tokens: {estimatedTokens}");

                var responseString = await SendRequestWithRetry(apiEndpoint, requestContent, estimatedTokens).ConfigureAwait(false);
                Logger.Log($"[{Name}] Received successful response string from server.");

                var responseObject = JObject.Parse(responseString);
                string message;
                try
                {
                    JToken firstCandidate = responseObject["candidates"]?.FirstOrDefault();
                    message = firstCandidate?["content"]?["parts"]?.FirstOrDefault()?["text"]?.ToString();

                    if (string.IsNullOrEmpty(message))
                    {
                        string blockReason = responseObject["promptFeedback"]?["blockReason"]?.ToString() ?? "Unknown";
                        Logger.Log($"[{Name}] Response was empty or blocked. Reason: {blockReason}. Full response: {responseString}");
                        message = $"抱歉，我的回复被安全系统拦截了。原因: {blockReason}";
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[{Name}] Error parsing response JSON: {ex.Message}. Full response: {responseString}");
                    message = "抱歉，解析服务器响应时发生错误。";
                }
                Logger.Log($"[{Name}] Parsed message content successfully.");

                if (Settings.KeepContext)
                {
                    await HistoryManager.AddMessage(new Message { Role = "assistant", Content = message }).ConfigureAwait(false);
                    SaveHistory();
                }

                Logger.Log($"[{Name}] Invoking response handler to display message.");
                ResponseHandler?.Invoke(message);
                return "";
            }
            catch (Exception ex)
            {
                Logger.Log($"[{Name}] A critical error occurred in the Chat method: {ex.GetType().Name} - {ex.Message}\nStackTrace: {ex.StackTrace}");
                ResponseHandler?.Invoke($"抱歉，处理您的请求时发生错误: {ex.Message}");
                return "";
            }
        }

        public override async Task<string> Summarize(string text)
        {
            Logger.Log($"[{Name}] Starting summarization with new creative prompt...");
            try
            {
                var creativePrompt = $@"You are a memory editor for a virtual pet. Your task is to refine and combine a [Previous Summary] with a [New Conversation to Integrate].
Read both sections carefully.
Your goal is to create a new, single, coherent summary from the first-person perspective of the virtual pet.
- Merge new events from the conversation into the summary.
- If the new conversation corrects or updates information in the previous summary, reflect that change.
- Remove redundant or less important details to save space.
- The final summary should be a continuous piece of text, not a list or dialogue.
- IMPORTANT: The total length of your final summary must not exceed {_setting.LongTermMemoryTokenLimit} tokens (approximately {_setting.LongTermMemoryTokenLimit / 2} characters).

Here is the data:
{text}";

                var requestData = new
                {
                    contents = new[]
                    {
                        new { role = "user", parts = new[] { new { text = creativePrompt } } }
                    },
                    generationConfig = new
                    {
                        maxOutputTokens = _setting.LongTermMemoryTokenLimit, // Limit summary tokens
                        temperature = 0.7
                    }
                };

                var requestContent = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");
                var estimatedTokens = text.Length / 2;
                var apiEndpoint = GetApiUrl($"/models/{_geminiSetting.Model}:generateContent");
                Logger.Log($"[{Name}] Sending summarization request to {apiEndpoint}.");

                var responseString = await SendRequestWithRetry(apiEndpoint, requestContent, estimatedTokens).ConfigureAwait(false);
                var responseObject = JObject.Parse(responseString);
                var summary = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
                Logger.Log($"[{Name}] Summarization successful.");
                return summary;
            }
            catch (Exception ex)
            {
                Logger.Log($"[{Name}] A critical error occurred during summarization: {ex.GetType().Name} - {ex.Message}\nStackTrace: {ex.StackTrace}");
                return "";
            }
        }

        private string GetApiUrl(string endpoint)
        {
            var baseUrl = _geminiSetting.Url.TrimEnd('/');
            if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
            {
                baseUrl += "/v1beta";
            }
            return baseUrl + endpoint;
        }


        public List<string> RefreshModels()
        {
            Logger.Log($"[{Name}] Starting to refresh models...");
            try
            {
                return Task.Run(async () =>
                {
                    string requestUrl;
                    if (_geminiSetting.Url.Contains("/models"))
                    {
                        requestUrl = _geminiSetting.Url;
                    }
                    else
                    {
                        var baseUrl = _geminiSetting.Url.TrimEnd('/');
                        if (!baseUrl.Contains("/v1") && !baseUrl.Contains("/v1beta"))
                        {
                            baseUrl += "/v1beta";
                        }
                        requestUrl = baseUrl.EndsWith("/") ? $"{baseUrl}models" : $"{baseUrl}/models";
                    }

                    var responseString = await SendRequestWithRetry(requestUrl, null, 0, isGet: true).ConfigureAwait(false);
                    var models = new List<string>();
                    var jsonToken = JToken.Parse(responseString);

                    if (jsonToken is JObject responseObject && responseObject["models"] is JArray modelsArray)
                    {
                        foreach (var model in modelsArray)
                        {
                            models.Add(model["name"].ToString().Replace("models/", ""));
                        }
                    }
                    else if (jsonToken is JArray responseArray)
                    {
                        foreach (var model in responseArray)
                        {
                            var modelName = model["id"]?.ToString() ?? model["name"]?.ToString();
                            if (!string.IsNullOrEmpty(modelName))
                            {
                                models.Add(modelName.Replace("models/", ""));
                            }
                        }
                    }
                    Logger.Log($"[{Name}] Found {models.Count} models.");
                    return models;
                }).Result;
            }
            catch (Exception ex)
            {
                Logger.Log($"[{Name}] Failed to refresh models: {ex.Message}");
                return new List<string>();
            }
        }

        public new List<string> GetModels()
        {
            return new List<string>();
        }
    }
}
