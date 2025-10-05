using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
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
    public class OpenAIChatCore : ChatCoreBase
    {
        public override string Name => "OpenAI";
        private readonly Setting.OpenAISetting _openAISetting;
        private readonly Setting _setting;
        private static int _apiKeyIndex = 0;

        public OpenAIChatCore(Setting.OpenAISetting openAISetting, Setting setting, IMainWindow mainWindow, ActionProcessor actionProcessor)
            : base(setting, mainWindow, actionProcessor)
        {
            _openAISetting = openAISetting;
            _setting = setting;
        }

        private string GetNextApiKey()
        {
            if (string.IsNullOrWhiteSpace(_openAISetting.ApiKey)) return null;
            var apiKeys = _openAISetting.ApiKey.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                              .Select(key => key.Trim())
                                              .Where(key => !string.IsNullOrWhiteSpace(key))
                                              .ToArray();
            if (apiKeys.Length == 0) return null;
            _apiKeyIndex = (_apiKeyIndex + 1) % apiKeys.Length;
            return apiKeys[_apiKeyIndex];
        }

        private async Task<string> SendRequestWithRetry(string url, HttpContent content, int estimatedTokens, bool isGet = false, int maxRetries = 3)
        {
            var availableKeys = _openAISetting.ApiKey?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length ?? 1;
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
                        if (!string.IsNullOrEmpty(apiKey))
                        {
                            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                        }
                        Logger.Log($"[{Name}] Sending HTTP request to {url}");
                        HttpResponseMessage response;
                        if (isGet)
                        {
                            response = await client.GetAsync(url).ConfigureAwait(false);
                        }
                        else
                        {
                            response = await client.PostAsync(url, content).ConfigureAwait(false);
                        }
                        Logger.Log($"[{Name}] Received response with status code: {response.StatusCode}");

                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            throw new HttpRequestException($"Rate limit exceeded (429) for key ending with ...{apiKey?.Substring(Math.Max(0, apiKey.Length - 4))}", null, HttpStatusCode.TooManyRequests);
                        }
                        response.EnsureSuccessStatusCode();
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

        public override async Task<string> Chat(string prompt, bool isFunctionCall = false)
        {
            var sanitizedPrompt = prompt?.Replace("\r", " ").Replace("\n", " ") ?? "";
            Logger.Log($"[{Name}] Chat process started. Prompt: '{(sanitizedPrompt.Length > 50 ? sanitizedPrompt.Substring(0, 50) + "..." : sanitizedPrompt)}'");
            try
            {
                if (!Settings.KeepContext)
                {
                    Logger.Log($"[{Name}] Context is disabled, clearing history.");
                    ClearContext();
                }
                if (!string.IsNullOrEmpty(prompt))
                {
                    await HistoryManager.AddMessage(new Message { Role = "user", Content = prompt }).ConfigureAwait(false);
                }

                Logger.Log($"[{Name}] Checking for history summarization...");
                await CheckAndSummarizeHistoryIfNeeded().ConfigureAwait(false);
                Logger.Log($"[{Name}] History summarization check complete.");

                List<Message> history = GetCoreHistory();
                object data;
                if (_openAISetting.EnableAdvanced)
                {
                    data = new
                    {
                        model = _openAISetting.Model,
                        messages = history.Select(m => new { role = m.NormalizedRole, content = m.Content }),
                        temperature = _openAISetting.Temperature,
                        max_tokens = _openAISetting.MaxTokens
                    };
                }
                else
                {
                    data = new
                    {
                        model = _openAISetting.Model,
                        messages = history.Select(m => new { role = m.NormalizedRole, content = m.Content })
                    };
                }
                var requestJson = JsonConvert.SerializeObject(data);
                var requestContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
                var estimatedTokens = requestJson.Length / 2;

                string apiUrl = GetApiUrl("/chat/completions");
                Logger.Log($"[{Name}] Preparing to send request to {apiUrl}. Estimated tokens: {estimatedTokens}");

                var responseString = await SendRequestWithRetry(apiUrl, requestContent, estimatedTokens).ConfigureAwait(false);
                Logger.Log($"[{Name}] Received successful response string from server.");

                var responseObject = JObject.Parse(responseString);
                string message;
                try
                {
                    message = responseObject["choices"]?.FirstOrDefault()?["message"]?["content"]?.ToString();

                    if (string.IsNullOrEmpty(message))
                    {
                        string finishReason = responseObject["choices"]?.FirstOrDefault()?["finish_reason"]?.ToString() ?? "Unknown";
                        Logger.Log($"[{Name}] Response was empty or filtered. Finish Reason: {finishReason}. Full response: {responseString}");
                        message = $"抱歉，我的回复被内容过滤器拦截了。原因: {finishReason}";
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

                var messages = new[]
                {
                    new { role = "user", content = creativePrompt }
                };

                object data;
                if (_openAISetting.EnableAdvanced)
                {
                    data = new
                    {
                        model = _openAISetting.Model,
                        messages = messages,
                        temperature = _openAISetting.Temperature,
                        max_tokens = _setting.LongTermMemoryTokenLimit // Limit summary tokens
                    };
                }
                else
                {
                    data = new
                    {
                        model = _openAISetting.Model,
                        messages = messages,
                        max_tokens = _setting.LongTermMemoryTokenLimit // Limit summary tokens
                    };
                }

                var requestContent = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
                var estimatedTokens = text.Length / 2;
                string apiUrl = GetApiUrl("/chat/completions");
                Logger.Log($"[{Name}] Sending summarization request to {apiUrl}.");

                var responseString = await SendRequestWithRetry(apiUrl, requestContent, estimatedTokens).ConfigureAwait(false);
                var responseObject = JObject.Parse(responseString);
                var summary = responseObject["choices"][0]["message"]["content"].ToString();
                Logger.Log($"[{Name}] Summarization successful.");
                return summary;
            }
            catch (Exception ex)
            {
                Logger.Log($"[{Name}] A critical error occurred during summarization: {ex.GetType().Name} - {ex.Message}\nStackTrace: {ex.StackTrace}");
                return ""; // Return empty string on failure
            }
        }

        private string GetApiUrl(string endpoint)
        {
            string apiUrl = _openAISetting.Url;
            if (!apiUrl.Contains(endpoint))
            {
                var baseUrl = apiUrl.TrimEnd('/');
                if (!baseUrl.EndsWith("/v1") && !baseUrl.EndsWith("/v1/"))
                {
                    baseUrl += "/v1";
                }
                return baseUrl.TrimEnd('/') + endpoint;
            }
            return apiUrl;
        }

        private List<Message> GetCoreHistory()
        {
            var history = new List<Message>
            {
                new Message { Role = "system", Content = GetSystemMessage() }
            };
            history.AddRange(HistoryManager.GetHistory().Skip(Math.Max(0, HistoryManager.GetHistory().Count - _setting.HistoryCompressionThreshold)));
            return history;
        }

        public List<string> RefreshModels()
        {
            Logger.Log($"[{Name}] Starting to refresh models...");
            try
            {
                // Bridge the sync-over-async call safely to prevent deadlocks
                return Task.Run(async () =>
                {
                    string apiUrl = GetApiUrl("/models").Replace("/chat/completions", "/models");
                    var responseString = await SendRequestWithRetry(apiUrl, null, 0, isGet: true).ConfigureAwait(false);

                    JObject responseObject = JObject.Parse(responseString);
                    var models = new List<string>();
                    foreach (var model in responseObject["data"])
                    {
                        models.Add(model["id"].ToString());
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
