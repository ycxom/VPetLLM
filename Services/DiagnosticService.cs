using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace VPetLLM.Services
{
    public class DiagnosticResult
    {
        public bool NetworkConnectivityOk { get; set; }
        public string NetworkDetails { get; set; } = "";
        public bool ProxyOk { get; set; }
        public string ProxyDetails { get; set; } = "";
        public bool ProxyEnabled { get; set; }
        public bool GlobalApiProxyEnabled { get; set; }
        public List<ChannelDiagnosticResult> ChannelResults { get; set; } = new();
        public bool AllPassed { get; set; }
        public string Summary { get; set; } = "";
    }

    public class ChannelDiagnosticResult
    {
        public string ChannelType { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public string ApiUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public bool Enabled { get; set; }
        public Setting.ChannelProxyMode ProxyMode { get; set; } = Setting.ChannelProxyMode.FollowDefault;
        public bool UsesProxy { get; set; }
        public bool ApiAvailable { get; set; }
        public string ApiMessage { get; set; } = "";
        public List<string> AvailableModels { get; set; } = new();
        public bool LlmTested { get; set; }
        public bool LlmResponded { get; set; }
        public string LlmMessage { get; set; } = "";
    }

    public class RecommendedSetting
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string CurrentValue { get; set; } = "";
        public string RecommendedValue { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Category { get; set; } = "recommended";
        public bool IsApplied { get; set; }
    }

    public class DiagnosticService
    {
        private readonly Setting _settings;
        private readonly string _language;

        public DiagnosticService(Setting settings, string language)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _language = language ?? "zh-hans";
        }

        public async Task<DiagnosticResult> RunFullDiagnosticAsync(Action<string>? statusCallback = null)
        {
            var result = new DiagnosticResult();
            var sb = new StringBuilder();

            statusCallback?.Invoke(GetLocalizedText("Diagnostic.StartingNetwork"));

            var networkOk = await CheckNetworkConnectivityAsync();
            result.NetworkConnectivityOk = networkOk;
            result.NetworkDetails = networkOk
                ? GetLocalizedText("Diagnostic.NetworkOk")
                : GetLocalizedText("Diagnostic.NetworkFail");
            sb.AppendLine(result.NetworkDetails);

            if (!networkOk)
            {
                result.AllPassed = false;
                result.Summary = GetLocalizedText("Diagnostic.NetworkBlocked");
                return result;
            }

            statusCallback?.Invoke(GetLocalizedText("Diagnostic.CheckingProxy"));

            var proxyOk = await CheckProxyAvailabilityAsync();
            result.ProxyOk = proxyOk;
            result.ProxyEnabled = _settings.Proxy?.IsEnabled ?? false;
            result.GlobalApiProxyEnabled = _settings.Proxy?.ForAllAPI ?? false;
            result.ProxyDetails = proxyOk
                ? GetLocalizedText("Diagnostic.ProxyOk")
                : (_settings.Proxy?.IsEnabled == true
                    ? GetLocalizedText("Diagnostic.ProxyFail")
                    : GetLocalizedText("Diagnostic.ProxyNotEnabled"));
            sb.AppendLine(result.ProxyDetails);

            statusCallback?.Invoke(GetLocalizedText("Diagnostic.CheckingChannels"));

            var channelResults = await CheckAllChannelsAsync(statusCallback);
            result.ChannelResults = channelResults;

            bool allChannelsOk = true;
            foreach (var cr in channelResults)
            {
                if (!cr.ApiAvailable)
                    allChannelsOk = false;
            }

            result.AllPassed = networkOk && (proxyOk || !result.ProxyEnabled) && allChannelsOk;

            if (result.AllPassed)
                sb.AppendLine(GetLocalizedText("Diagnostic.AllPassed"));
            else
                sb.AppendLine(GetLocalizedText("Diagnostic.SomeFailed"));

            result.Summary = sb.ToString();
            return result;
        }

        private async Task<bool> CheckNetworkConnectivityAsync()
        {
            string[] pingTargets = { "8.8.8.8", "1.1.1.1", "114.114.114.114" };

            foreach (var target in pingTargets)
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(target, 3000);
                    if (reply.Status == IPStatus.Success)
                    {
                        Logger.Log($"Diagnostic: Ping to {target} succeeded ({reply.RoundtripTime}ms)");
                        return true;
                    }
                    Logger.Log($"Diagnostic: Ping to {target} failed: {reply.Status}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"Diagnostic: Ping to {target} exception: {ex.Message}");
                }
            }

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await client.GetAsync("https://www.google.com/generate_204");
                Logger.Log($"Diagnostic: HTTP connectivity check: {response.StatusCode}");
                return true;
            }
            catch
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var response = await client.GetAsync("https://www.baidu.com");
                    Logger.Log($"Diagnostic: HTTP fallback check: {response.StatusCode}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Diagnostic: HTTP connectivity failed: {ex.Message}");
                }
            }

            return false;
        }

        private async Task<bool> CheckProxyAvailabilityAsync()
        {
            var proxy = _settings.Proxy;
            if (proxy == null || !proxy.IsEnabled)
                return true;

            try
            {
                var handler = new HttpClientHandler();
                if (proxy.FollowSystemProxy)
                {
                    handler.Proxy = WebRequest.GetSystemWebProxy();
                    handler.UseProxy = true;
                }
                else if (!string.IsNullOrEmpty(proxy.Address))
                {
                    var protocol = proxy.Protocol?.ToLower() == "socks" ? "socks5" : "http";
                    var proxyUri = $"{protocol}://{proxy.Address}";
                    handler.Proxy = new WebProxy(new Uri(proxyUri));
                    handler.UseProxy = true;
                }

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var response = await client.GetAsync("https://www.google.com/generate_204");
                Logger.Log($"Diagnostic: Proxy check HTTP status: {response.StatusCode}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Logger.Log($"Diagnostic: Proxy check failed: {ex.Message}");
                return false;
            }
        }

        private async Task<List<ChannelDiagnosticResult>> CheckAllChannelsAsync(Action<string>? statusCallback = null)
        {
            var results = new List<ChannelDiagnosticResult>();

            statusCallback?.Invoke(GetLocalizedText("Diagnostic.CheckingOpenAI"));
            foreach (var node in _settings.OpenAI.OpenAINodes.Where(n => n.Enabled))
            {
                var r = await CheckOpenAIChannelAsync(node);
                results.Add(r);
            }
            if (!_settings.OpenAI.OpenAINodes.Any(n => n.Enabled) && !string.IsNullOrEmpty(_settings.OpenAI.ApiKey))
            {
                var legacyNode = new Setting.OpenAINodeSetting
                {
                    Name = "OpenAI (兼容)",
                    ApiKey = _settings.OpenAI.ApiKey,
                    Url = _settings.OpenAI.Url,
                    Model = _settings.OpenAI.Model ?? "gpt-3.5-turbo",
                    Enabled = _settings.OpenAI.Enabled,
                    ProxyMode = Setting.ChannelProxyMode.FollowDefault
                };
                var r = await CheckOpenAIChannelAsync(legacyNode);
                results.Add(r);
            }

            statusCallback?.Invoke(GetLocalizedText("Diagnostic.CheckingGemini"));
            foreach (var node in _settings.Gemini.GeminiNodes.Where(n => n.Enabled))
            {
                var r = await CheckGeminiChannelAsync(node);
                results.Add(r);
            }
            if (!_settings.Gemini.GeminiNodes.Any(n => n.Enabled) && !string.IsNullOrEmpty(_settings.Gemini.ApiKey))
            {
                var legacyNode = new Setting.GeminiNodeSetting
                {
                    Name = "Gemini (兼容)",
                    ApiKey = _settings.Gemini.ApiKey,
                    Url = _settings.Gemini.Url,
                    Model = _settings.Gemini.Model ?? "gemini-pro",
                    Enabled = true,
                    ProxyMode = Setting.ChannelProxyMode.FollowDefault
                };
                var r = await CheckGeminiChannelAsync(legacyNode);
                results.Add(r);
            }

            statusCallback?.Invoke(GetLocalizedText("Diagnostic.CheckingOllama"));
            foreach (var node in _settings.Ollama.OllamaNodes.Where(n => n.Enabled))
            {
                var r = await CheckOllamaChannelAsync(node);
                results.Add(r);
            }
            if (!_settings.Ollama.OllamaNodes.Any(n => n.Enabled) && !string.IsNullOrEmpty(_settings.Ollama.Url))
            {
                var legacyNode = new Setting.OllamaNodeSetting
                {
                    Name = "Ollama (兼容)",
                    Url = _settings.Ollama.Url,
                    Model = _settings.Ollama.Model ?? "",
                    Enabled = true
                };
                var r = await CheckOllamaChannelAsync(legacyNode);
                results.Add(r);
            }

            statusCallback?.Invoke(GetLocalizedText("Diagnostic.CheckingLMStudio"));
            foreach (var node in _settings.LMStudio.LMStudioNodes.Where(n => n.Enabled))
            {
                var r = await CheckLMStudioChannelAsync(node);
                results.Add(r);
            }
            if (!_settings.LMStudio.LMStudioNodes.Any(n => n.Enabled) && !string.IsNullOrEmpty(_settings.LMStudio.Url))
            {
                var legacyNode = new Setting.LMStudioNodeSetting
                {
                    Name = "LMStudio (兼容)",
                    Url = _settings.LMStudio.Url,
                    Model = _settings.LMStudio.Model ?? "",
                    Enabled = true
                };
                var r = await CheckLMStudioChannelAsync(legacyNode);
                results.Add(r);
            }

            return results;
        }

        private bool ShouldUseProxyForChannel(Setting.ChannelProxyMode proxyMode, string channelType)
        {
            if (proxyMode == Setting.ChannelProxyMode.Direct)
                return false;

            if (proxyMode == Setting.ChannelProxyMode.ForceProxy)
                return true;

            if (_settings.Proxy == null || !_settings.Proxy.IsEnabled)
                return false;

            if (_settings.Proxy.ForAllAPI)
                return true;

            return channelType switch
            {
                "OpenAI" => _settings.Proxy.ForOpenAI,
                "Gemini" => _settings.Proxy.ForGemini,
                "Ollama" => _settings.Proxy.ForOllama,
                "Free" => _settings.Proxy.ForFree,
                _ => false
            };
        }

        private HttpClientHandler CreateHandlerForChannel(Setting.ChannelProxyMode proxyMode, string channelType)
        {
            var handler = new HttpClientHandler();

            if (!ShouldUseProxyForChannel(proxyMode, channelType))
            {
                handler.UseProxy = false;
                handler.Proxy = null;
                return handler;
            }

            var proxy = _settings.Proxy;
            if (proxy == null) return handler;

            if (proxy.FollowSystemProxy)
            {
                handler.Proxy = WebRequest.GetSystemWebProxy();
                handler.UseProxy = true;
            }
            else if (!string.IsNullOrEmpty(proxy.Address))
            {
                var protocol = proxy.Protocol?.ToLower() == "socks" ? "socks5" : "http";
                handler.Proxy = new WebProxy(new Uri($"{protocol}://{proxy.Address}"));
                handler.UseProxy = true;
            }

            return handler;
        }

        private async Task<ChannelDiagnosticResult> CheckOpenAIChannelAsync(Setting.OpenAINodeSetting node)
        {
            var result = new ChannelDiagnosticResult
            {
                ChannelType = "OpenAI",
                ChannelName = node.Name ?? "OpenAI",
                ApiUrl = node.Url ?? "https://api.openai.com/v1",
                Model = node.Model ?? "",
                Enabled = node.Enabled,
                ProxyMode = node.ProxyMode,
                UsesProxy = ShouldUseProxyForChannel(node.ProxyMode, "OpenAI")
            };

            try
            {
                var handler = CreateHandlerForChannel(node.ProxyMode, "OpenAI");
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", node.ApiKey);

                var response = await client.GetAsync($"{result.ApiUrl.TrimEnd('/')}/models");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id))
                                result.AvailableModels.Add(id.GetString() ?? "");
                        }
                    }
                    result.ApiAvailable = result.AvailableModels.Count > 0;
                    result.ApiMessage = result.ApiAvailable
                        ? string.Format(GetLocalizedText("Diagnostic.ModelsFound"), result.AvailableModels.Count)
                        : GetLocalizedText("Diagnostic.NoModelsFound");
                }
                else
                {
                    result.ApiAvailable = false;
                    result.ApiMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                }
            }
            catch (Exception ex)
            {
                result.ApiAvailable = false;
                result.ApiMessage = ex.Message;
            }

            return result;
        }

        private async Task<ChannelDiagnosticResult> CheckGeminiChannelAsync(Setting.GeminiNodeSetting node)
        {
            var result = new ChannelDiagnosticResult
            {
                ChannelType = "Gemini",
                ChannelName = node.Name ?? "Gemini",
                ApiUrl = node.Url ?? "https://generativelanguage.googleapis.com/v1beta",
                Model = node.Model ?? "",
                Enabled = node.Enabled,
                ProxyMode = node.ProxyMode,
                UsesProxy = ShouldUseProxyForChannel(node.ProxyMode, "Gemini")
            };

            try
            {
                var handler = CreateHandlerForChannel(node.ProxyMode, "Gemini");
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={node.ApiKey}";
                if (node.UseOpenAIAuth && !string.IsNullOrEmpty(result.ApiUrl))
                {
                    url = $"{result.ApiUrl.TrimEnd('/')}/models";
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", node.ApiKey);
                }

                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("models", out var modelList))
                    {
                        foreach (var item in modelList.EnumerateArray())
                        {
                            if (item.TryGetProperty("name", out var name))
                            {
                                var nameStr = name.GetString() ?? "";
                                if (nameStr.Contains("gemini"))
                                {
                                    nameStr = nameStr.Replace("models/", "");
                                    result.AvailableModels.Add(nameStr);
                                }
                            }
                        }
                    }
                    result.ApiAvailable = result.AvailableModels.Count > 0;
                    result.ApiMessage = result.ApiAvailable
                        ? string.Format(GetLocalizedText("Diagnostic.ModelsFound"), result.AvailableModels.Count)
                        : GetLocalizedText("Diagnostic.NoGeminiModels");
                }
                else
                {
                    result.ApiAvailable = false;
                    result.ApiMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                }
            }
            catch (Exception ex)
            {
                result.ApiAvailable = false;
                result.ApiMessage = ex.Message;
            }

            return result;
        }

        private async Task<ChannelDiagnosticResult> CheckOllamaChannelAsync(Setting.OllamaNodeSetting node)
        {
            var result = new ChannelDiagnosticResult
            {
                ChannelType = "Ollama",
                ChannelName = node.Name ?? "Ollama",
                ApiUrl = node.Url ?? "http://localhost:11434",
                Model = node.Model ?? "",
                Enabled = node.Enabled,
                ProxyMode = Setting.ChannelProxyMode.FollowDefault,
                UsesProxy = ShouldUseProxyForChannel(Setting.ChannelProxyMode.FollowDefault, "Ollama")
            };

            try
            {
                var handler = CreateHandlerForChannel(Setting.ChannelProxyMode.FollowDefault, "Ollama");
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

                var baseUrl = result.ApiUrl.TrimEnd('/');
                var response = await client.GetAsync($"{baseUrl}/api/tags");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("models", out var modelList))
                    {
                        foreach (var item in modelList.EnumerateArray())
                        {
                            if (item.TryGetProperty("name", out var name))
                            {
                                var nameStr = name.GetString() ?? "";
                                if (!string.IsNullOrEmpty(nameStr))
                                    result.AvailableModels.Add(nameStr);
                            }
                        }
                    }
                    result.ApiAvailable = result.AvailableModels.Count > 0;
                    result.ApiMessage = result.ApiAvailable
                        ? string.Format(GetLocalizedText("Diagnostic.ModelsFound"), result.AvailableModels.Count)
                        : GetLocalizedText("Diagnostic.NoModelsFound");
                }
                else
                {
                    result.ApiAvailable = false;
                    result.ApiMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                }
            }
            catch (Exception ex)
            {
                result.ApiAvailable = false;
                result.ApiMessage = ex.Message;
            }

            return result;
        }

        private async Task<ChannelDiagnosticResult> CheckLMStudioChannelAsync(Setting.LMStudioNodeSetting node)
        {
            var result = new ChannelDiagnosticResult
            {
                ChannelType = "LMStudio",
                ChannelName = node.Name ?? "LMStudio",
                ApiUrl = node.Url ?? "http://localhost:1234",
                Model = node.Model ?? "",
                Enabled = node.Enabled,
                ProxyMode = Setting.ChannelProxyMode.FollowDefault,
                UsesProxy = ShouldUseProxyForChannel(Setting.ChannelProxyMode.FollowDefault, "LMStudio")
            };

            try
            {
                var handler = CreateHandlerForChannel(Setting.ChannelProxyMode.FollowDefault, "LMStudio");
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

                var baseUrl = result.ApiUrl.TrimEnd('/');
                var response = await client.GetAsync($"{baseUrl}/v1/models");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id))
                                result.AvailableModels.Add(id.GetString() ?? "");
                        }
                    }
                    result.ApiAvailable = result.AvailableModels.Count > 0;
                    result.ApiMessage = result.ApiAvailable
                        ? string.Format(GetLocalizedText("Diagnostic.ModelsFound"), result.AvailableModels.Count)
                        : GetLocalizedText("Diagnostic.NoModelsFound");
                }
                else
                {
                    result.ApiAvailable = false;
                    result.ApiMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                }
            }
            catch (Exception ex)
            {
                result.ApiAvailable = false;
                result.ApiMessage = ex.Message;
            }

            return result;
        }

        public async Task<bool> CheckChannelLLMAsync(ChannelDiagnosticResult channelResult)
        {
            const string testPrompt = "This is a test message. You don't need to return anything, just return TRUE.";

            try
            {
                channelResult.LlmTested = true;

                switch (channelResult.ChannelType)
                {
                    case "OpenAI":
                        return await CheckOpenAILLMAsync(channelResult, testPrompt);
                    case "Gemini":
                        return await CheckGeminiLLMAsync(channelResult, testPrompt);
                    case "Ollama":
                        return await CheckOllamaLLMAsync(channelResult, testPrompt);
                    case "LMStudio":
                        return await CheckLMStudioLLMAsync(channelResult, testPrompt);
                }
            }
            catch (Exception ex)
            {
                channelResult.LlmTested = true;
                channelResult.LlmResponded = false;
                channelResult.LlmMessage = ex.Message;
            }

            return false;
        }

        private async Task<bool> CheckOpenAILLMAsync(ChannelDiagnosticResult channelResult, string prompt)
        {
            var node = _settings.OpenAI.OpenAINodes.FirstOrDefault(n =>
                (n.Name ?? "") == channelResult.ChannelName);
            if (node == null)
            {
                node = new Setting.OpenAINodeSetting
                {
                    ApiKey = _settings.OpenAI.ApiKey,
                    Url = channelResult.ApiUrl,
                    Model = channelResult.Model,
                    ProxyMode = channelResult.ProxyMode
                };
            }

            var handler = CreateHandlerForChannel(node.ProxyMode, "OpenAI");
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", node.ApiKey);

            var model = !string.IsNullOrEmpty(channelResult.Model) ? channelResult.Model : "gpt-3.5-turbo";
            var requestBody = new
            {
                model,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                max_tokens = 10,
                temperature = 0
            };
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{channelResult.ApiUrl.TrimEnd('/')}/chat/completions", content);
            if (response.IsSuccessStatusCode)
            {
                channelResult.LlmResponded = true;
                channelResult.LlmMessage = GetLocalizedText("Diagnostic.LLMResponseOk");
                return true;
            }

            channelResult.LlmResponded = false;
            channelResult.LlmMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            return false;
        }

        private async Task<bool> CheckGeminiLLMAsync(ChannelDiagnosticResult channelResult, string prompt)
        {
            var node = _settings.Gemini.GeminiNodes.FirstOrDefault(n =>
                (n.Name ?? "") == channelResult.ChannelName);
            if (node == null)
            {
                node = new Setting.GeminiNodeSetting
                {
                    ApiKey = _settings.Gemini.ApiKey,
                    Url = channelResult.ApiUrl,
                    Model = channelResult.Model,
                    ProxyMode = channelResult.ProxyMode
                };
            }

            var handler = CreateHandlerForChannel(node.ProxyMode, "Gemini");
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var model = !string.IsNullOrEmpty(channelResult.Model) ? channelResult.Model : "gemini-pro";
            string url;
            StringContent requestContent;

            if (node.UseOpenAIAuth && !string.IsNullOrEmpty(channelResult.ApiUrl))
            {
                url = $"{channelResult.ApiUrl.TrimEnd('/')}/chat/completions";
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", node.ApiKey);
                var requestBody = new
                {
                    model,
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    },
                    max_tokens = 10,
                    temperature = 0
                };
                requestContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            }
            else
            {
                url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={node.ApiKey}";
                var requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        maxOutputTokens = 10,
                        temperature = 0
                    }
                };
                requestContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            }

            var response = await client.PostAsync(url, requestContent);
            if (response.IsSuccessStatusCode)
            {
                channelResult.LlmResponded = true;
                channelResult.LlmMessage = GetLocalizedText("Diagnostic.LLMResponseOk");
                return true;
            }

            channelResult.LlmResponded = false;
            channelResult.LlmMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            return false;
        }

        private async Task<bool> CheckOllamaLLMAsync(ChannelDiagnosticResult channelResult, string prompt)
        {
            var node = _settings.Ollama.OllamaNodes.FirstOrDefault(n =>
                (n.Name ?? "") == channelResult.ChannelName);
            if (node == null)
            {
                node = new Setting.OllamaNodeSetting
                {
                    Url = channelResult.ApiUrl,
                    Model = channelResult.Model
                };
            }

            var handler = CreateHandlerForChannel(Setting.ChannelProxyMode.FollowDefault, "Ollama");
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var model = !string.IsNullOrEmpty(channelResult.Model) ? channelResult.Model : "llama2";
            var requestBody = new
            {
                model,
                prompt,
                stream = false,
                options = new { num_predict = 10 }
            };
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{channelResult.ApiUrl.TrimEnd('/')}/api/generate", content);
            if (response.IsSuccessStatusCode)
            {
                channelResult.LlmResponded = true;
                channelResult.LlmMessage = GetLocalizedText("Diagnostic.LLMResponseOk");
                return true;
            }

            channelResult.LlmResponded = false;
            channelResult.LlmMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            return false;
        }

        private async Task<bool> CheckLMStudioLLMAsync(ChannelDiagnosticResult channelResult, string prompt)
        {
            var node = _settings.LMStudio.LMStudioNodes.FirstOrDefault(n =>
                (n.Name ?? "") == channelResult.ChannelName);
            if (node == null)
            {
                node = new Setting.LMStudioNodeSetting
                {
                    Url = channelResult.ApiUrl,
                    Model = channelResult.Model
                };
            }

            var handler = CreateHandlerForChannel(Setting.ChannelProxyMode.FollowDefault, "LMStudio");
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var model = !string.IsNullOrEmpty(channelResult.Model) ? channelResult.Model : "local-model";
            var requestBody = new
            {
                model,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                max_tokens = 10,
                temperature = 0
            };
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{channelResult.ApiUrl.TrimEnd('/')}/v1/chat/completions", content);
            if (response.IsSuccessStatusCode)
            {
                channelResult.LlmResponded = true;
                channelResult.LlmMessage = GetLocalizedText("Diagnostic.LLMResponseOk");
                return true;
            }

            channelResult.LlmResponded = false;
            channelResult.LlmMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            return false;
        }

        public string FormatDiagnosticReport(DiagnosticResult result)
        {
            var sb = new StringBuilder();
            var lang = _language;

            sb.AppendLine("═══════════════════════════════════");
            sb.AppendLine($"  {GetLocalizedText("Diagnostic.ReportTitle")}");
            sb.AppendLine("═══════════════════════════════════");
            sb.AppendLine();

            sb.AppendLine($"【{GetLocalizedText("Diagnostic.SectionNetwork")}】");
            sb.AppendLine($"  {(result.NetworkConnectivityOk ? "✅" : "❌")} {result.NetworkDetails}");
            sb.AppendLine();

            sb.AppendLine($"【{GetLocalizedText("Diagnostic.SectionProxy")}】");
            sb.Append(result.ProxyEnabled
                ? $"  {GetLocalizedText("Diagnostic.ProxyStatusEnabled")}"
                : $"  {GetLocalizedText("Diagnostic.ProxyStatusDisabled")}");
            sb.AppendLine(result.GlobalApiProxyEnabled
                ? $" ({GetLocalizedText("Diagnostic.GlobalApiProxyOn")})"
                : "");
            sb.AppendLine();
            sb.AppendLine($"  {(result.ProxyOk ? "✅" : "❌")} {result.ProxyDetails}");
            sb.AppendLine();

            sb.AppendLine($"【{GetLocalizedText("Diagnostic.SectionChannels")}】");
            if (result.ChannelResults.Count == 0)
            {
                sb.AppendLine($"  {GetLocalizedText("Diagnostic.NoEnabledChannels")}");
            }
            else
            {
                foreach (var cr in result.ChannelResults)
                {
                    sb.AppendLine($"  ── {cr.ChannelType}: {cr.ChannelName} ──");
                    sb.AppendLine($"  {GetLocalizedText("Diagnostic.ApiUrl")}: {cr.ApiUrl}");
                    sb.AppendLine($"  {GetLocalizedText("Diagnostic.Model")}: {(string.IsNullOrEmpty(cr.Model) ? GetLocalizedText("Diagnostic.NotSet") : cr.Model)}");
                    sb.AppendLine($"  {GetLocalizedText("Diagnostic.ProxyMode")}: {cr.ProxyMode}");
                    sb.AppendLine($"  {GetLocalizedText("Diagnostic.UsesProxy")}: {(cr.UsesProxy ? GetLocalizedText("Diagnostic.Yes") : GetLocalizedText("Diagnostic.No"))}");
                    sb.AppendLine($"  {GetLocalizedText("Diagnostic.ApiStatus")}: {(cr.ApiAvailable ? "✅" : "❌")} {cr.ApiMessage}");
                    if (cr.ApiAvailable && cr.AvailableModels.Count > 0)
                    {
                        sb.AppendLine($"  {GetLocalizedText("Diagnostic.AvailableModels")}: {string.Join(", ", cr.AvailableModels.Take(10))}");
                    }
                    if (cr.LlmTested)
                    {
                        sb.AppendLine($"  {GetLocalizedText("Diagnostic.LLMResponse")}: {(cr.LlmResponded ? "✅" : "❌")} {cr.LlmMessage}");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("═══════════════════════════════════");
            sb.AppendLine($"  {(result.AllPassed ? "✅" : "❌")} {GetLocalizedText("Diagnostic.OverallResult")}: {(result.AllPassed ? GetLocalizedText("Diagnostic.AllPassed") : GetLocalizedText("Diagnostic.SomeFailed"))}");
            sb.AppendLine("═══════════════════════════════════");

            return sb.ToString();
        }

        public List<RecommendedSetting> GenerateRecommendedSettings(DiagnosticResult result)
        {
            var recommendations = new List<RecommendedSetting>();

            var hasAnyApiConfig = HasAnyApiConfigured();
            var freeFallbackExists = _settings.EnableFallback
                && _settings.FallbackProviders.Any(fp =>
                    fp.ProviderType == "Free" && fp.IsEnabled);

            if (!hasAnyApiConfig)
            {
                recommendations.Add(new RecommendedSetting
                {
                    Key = "Provider",
                    DisplayName = GetLocalizedText("Diagnostic.RecProvider"),
                    CurrentValue = _settings.Provider.ToString(),
                    RecommendedValue = "Free",
                    Reason = GetLocalizedText("Diagnostic.RecNoApiReason"),
                    Category = "critical"
                });

                if (_settings.Free.EnableAdvanced == false)
                {
                    recommendations.Add(new RecommendedSetting
                    {
                        Key = "Free.EnableAdvanced",
                        DisplayName = GetLocalizedText("Diagnostic.RecFreeAdvanced"),
                        CurrentValue = "false",
                        RecommendedValue = "true",
                        Reason = GetLocalizedText("Diagnostic.RecFreeAdvancedReason"),
                        Category = "recommended"
                    });
                }

                if (_settings.Free.EnableLoadBalancing == false)
                {
                    recommendations.Add(new RecommendedSetting
                    {
                        Key = "Free.EnableLoadBalancing",
                        DisplayName = GetLocalizedText("Diagnostic.RecFreeLoadBalance"),
                        CurrentValue = "false",
                        RecommendedValue = "true",
                        Reason = GetLocalizedText("Diagnostic.RecFreeLoadBalanceReason"),
                        Category = "recommended"
                    });
                }
            }

            if (!freeFallbackExists)
            {
                recommendations.Add(new RecommendedSetting
                {
                    Key = "EnableFallback",
                    DisplayName = GetLocalizedText("Diagnostic.RecEnableFallback"),
                    CurrentValue = _settings.EnableFallback ? "true" : "false",
                    RecommendedValue = "true",
                    Reason = GetLocalizedText("Diagnostic.RecEnableFallbackReason"),
                    Category = "critical"
                });

                recommendations.Add(new RecommendedSetting
                {
                    Key = "Fallback.Free",
                    DisplayName = GetLocalizedText("Diagnostic.RecFallbackFree"),
                    CurrentValue = "not configured",
                    RecommendedValue = "priority=0",
                    Reason = GetLocalizedText("Diagnostic.RecFallbackFreeReason"),
                    Category = "critical"
                });
            }

            if (_settings.Proxy != null && _settings.Proxy.IsEnabled && !result.ProxyOk)
            {
                recommendations.Add(new RecommendedSetting
                {
                    Key = "Proxy.IsEnabled",
                    DisplayName = GetLocalizedText("Diagnostic.RecDisableProxy"),
                    CurrentValue = "true",
                    RecommendedValue = "false",
                    Reason = GetLocalizedText("Diagnostic.RecDisableProxyReason"),
                    Category = "critical"
                });
            }

            if (_settings.Proxy != null && _settings.Proxy.IsEnabled && _settings.Proxy.ForAllAPI && !result.ProxyOk)
            {
                recommendations.Add(new RecommendedSetting
                {
                    Key = "Proxy.ForAllAPI",
                    DisplayName = GetLocalizedText("Diagnostic.RecDisableGlobalApiProxy"),
                    CurrentValue = "true",
                    RecommendedValue = "false",
                    Reason = GetLocalizedText("Diagnostic.RecDisableGlobalApiProxyReason"),
                    Category = "critical"
                });
            }

            var failingChannels = result.ChannelResults
                .Where(cr => cr.Enabled && !cr.ApiAvailable)
                .ToList();
            foreach (var fc in failingChannels)
            {
                if (fc.UsesProxy && !result.ProxyOk)
                {
                    recommendations.Add(new RecommendedSetting
                    {
                        Key = $"Channel.{fc.ChannelType}.{fc.ChannelName}.ProxyMode",
                        DisplayName = string.Format(GetLocalizedText("Diagnostic.RecChannelProxy"),
                            fc.ChannelType, fc.ChannelName),
                        CurrentValue = fc.ProxyMode.ToString(),
                        RecommendedValue = "Direct",
                        Reason = GetLocalizedText("Diagnostic.RecChannelProxyReason"),
                        Category = "critical"
                    });
                }
            }

            var testedChannels = result.ChannelResults
                .Where(cr => cr.LlmTested && !cr.LlmResponded)
                .ToList();
            if (testedChannels.Count > 0 && !freeFallbackExists && !_settings.EnableFallback)
            {
                recommendations.Add(new RecommendedSetting
                {
                    Key = "EnableFallback",
                    DisplayName = GetLocalizedText("Diagnostic.RecEnableFallback"),
                    CurrentValue = "false",
                    RecommendedValue = "true",
                    Reason = GetLocalizedText("Diagnostic.RecLlmFailFallbackReason"),
                    Category = "critical"
                });
            }

            return recommendations;
        }

        public void ApplyRecommendedSettings(List<RecommendedSetting> recommendations)
        {
            foreach (var rec in recommendations)
            {
                switch (rec.Key)
                {
                    case "Provider":
                        if (Enum.TryParse<Setting.LLMType>(rec.RecommendedValue, out var providerType))
                            _settings.Provider = providerType;
                        break;

                    case "Free.EnableAdvanced":
                        _settings.Free.EnableAdvanced = rec.RecommendedValue == "true";
                        break;

                    case "Free.EnableLoadBalancing":
                        _settings.Free.EnableLoadBalancing = rec.RecommendedValue == "true";
                        break;

                    case "EnableFallback":
                        _settings.EnableFallback = rec.RecommendedValue == "true";
                        break;

                    case "Fallback.Free":
                        var existingFree = _settings.FallbackProviders
                            .FirstOrDefault(fp => fp.ProviderType == "Free");
                        if (existingFree == null)
                        {
                            _settings.FallbackProviders.Add(new Setting.ProviderFallbackConfig
                            {
                                ProviderType = "Free",
                                IsEnabled = true,
                                Priority = 0
                            });
                        }
                        else
                        {
                            existingFree.IsEnabled = true;
                            existingFree.Priority = 0;
                        }
                        break;

                    case "Proxy.IsEnabled":
                        if (_settings.Proxy != null)
                            _settings.Proxy.IsEnabled = false;
                        break;

                    case "Proxy.ForAllAPI":
                        if (_settings.Proxy != null)
                            _settings.Proxy.ForAllAPI = false;
                        break;

                    default:
                        if (rec.Key.StartsWith("Channel."))
                        {
                            var parts = rec.Key.Split('.');
                            if (parts.Length >= 4)
                            {
                                var channelType = parts[1];
                                var channelName = parts[2];

                                switch (channelType)
                                {
                                    case "OpenAI":
                                        var oaNode = _settings.OpenAI.OpenAINodes
                                            .FirstOrDefault(n => n.Name == channelName);
                                        if (oaNode != null)
                                            oaNode.ProxyMode = Setting.ChannelProxyMode.Direct;
                                        break;
                                    case "Gemini":
                                        var gmNode = _settings.Gemini.GeminiNodes
                                            .FirstOrDefault(n => n.Name == channelName);
                                        if (gmNode != null)
                                            gmNode.ProxyMode = Setting.ChannelProxyMode.Direct;
                                        break;
                                }
                            }
                        }
                        break;
                }
            }

            _settings.Save();
            Logger.Log($"Applied {recommendations.Count} recommended settings");
        }

        public string FormatRecommendationsReport(List<RecommendedSetting> recommendations)
        {
            var sb = new StringBuilder();

            sb.AppendLine("═══════════════════════════════════");
            sb.AppendLine($"  {GetLocalizedText("Diagnostic.RecReportTitle")}");
            sb.AppendLine("═══════════════════════════════════");
            sb.AppendLine();

            var critical = recommendations.Where(r => r.Category == "critical").ToList();
            var recommended = recommendations.Where(r => r.Category == "recommended").ToList();

            if (critical.Count > 0)
            {
                sb.AppendLine($"【{GetLocalizedText("Diagnostic.RecCritical")}】");
                foreach (var rec in critical)
                {
                    sb.AppendLine($"  🔴 {rec.DisplayName}");
                    sb.AppendLine($"     {GetLocalizedText("Diagnostic.RecCurrent")}: {rec.CurrentValue}");
                    sb.AppendLine($"     {GetLocalizedText("Diagnostic.RecSuggest")}: {rec.RecommendedValue}");
                    sb.AppendLine($"     {GetLocalizedText("Diagnostic.RecReason")}: {rec.Reason}");
                    sb.AppendLine();
                }
            }

            if (recommended.Count > 0)
            {
                sb.AppendLine($"【{GetLocalizedText("Diagnostic.RecRecommended")}】");
                foreach (var rec in recommended)
                {
                    sb.AppendLine($"  🟡 {rec.DisplayName}");
                    sb.AppendLine($"     {GetLocalizedText("Diagnostic.RecCurrent")}: {rec.CurrentValue}");
                    sb.AppendLine($"     {GetLocalizedText("Diagnostic.RecSuggest")}: {rec.RecommendedValue}");
                    sb.AppendLine($"     {GetLocalizedText("Diagnostic.RecReason")}: {rec.Reason}");
                    sb.AppendLine();
                }
            }

            if (recommendations.Count == 0)
            {
                sb.AppendLine($"  {GetLocalizedText("Diagnostic.RecNoIssues")}");
                sb.AppendLine();
            }

            sb.AppendLine("═══════════════════════════════════");

            return sb.ToString();
        }

        private bool HasAnyApiConfigured()
        {
            foreach (var node in _settings.OpenAI.OpenAINodes)
            {
                if (!string.IsNullOrWhiteSpace(node.ApiKey))
                    return true;
            }
            if (!string.IsNullOrWhiteSpace(_settings.OpenAI.ApiKey))
                return true;

            foreach (var node in _settings.Gemini.GeminiNodes)
            {
                if (!string.IsNullOrWhiteSpace(node.ApiKey))
                    return true;
            }
            if (!string.IsNullOrWhiteSpace(_settings.Gemini.ApiKey))
                return true;

            foreach (var node in _settings.Ollama.OllamaNodes)
            {
                if (node.Enabled && !string.IsNullOrWhiteSpace(node.Url))
                    return true;
            }
            if (!string.IsNullOrWhiteSpace(_settings.Ollama.Url))
                return true;

            foreach (var node in _settings.LMStudio.LMStudioNodes)
            {
                if (node.Enabled && !string.IsNullOrWhiteSpace(node.Url))
                    return true;
            }
            if (!string.IsNullOrWhiteSpace(_settings.LMStudio.Url))
                return true;

            return false;
        }

        private string GetLocalizedText(string key)
        {
            try
            {
                var text = Utils.Localization.LanguageHelper.Get(key, _language);
                if (!string.IsNullOrEmpty(text) && text != key)
                    return text;
            }
            catch { }

            return key switch
            {
                "Diagnostic.StartingNetwork" => _language.StartsWith("zh") ? "正在检查网络连接..." : "Checking network connectivity...",
                "Diagnostic.CheckingProxy" => _language.StartsWith("zh") ? "正在检查代理设置..." : "Checking proxy settings...",
                "Diagnostic.CheckingChannels" => _language.StartsWith("zh") ? "正在检查各渠道 API..." : "Checking channel APIs...",
                "Diagnostic.CheckingOpenAI" => _language.StartsWith("zh") ? "检查 OpenAI 渠道..." : "Checking OpenAI channels...",
                "Diagnostic.CheckingGemini" => _language.StartsWith("zh") ? "检查 Gemini 渠道..." : "Checking Gemini channels...",
                "Diagnostic.CheckingOllama" => _language.StartsWith("zh") ? "检查 Ollama 渠道..." : "Checking Ollama channels...",
                "Diagnostic.CheckingLMStudio" => _language.StartsWith("zh") ? "检查 LMStudio 渠道..." : "Checking LMStudio channels...",
                "Diagnostic.NetworkOk" => _language.StartsWith("zh") ? "网络连接正常" : "Network connectivity OK",
                "Diagnostic.NetworkFail" => _language.StartsWith("zh") ? "网络连接失败，请检查网络设置" : "Network connectivity failed, check network settings",
                "Diagnostic.NetworkBlocked" => _language.StartsWith("zh") ? "网络不可用，无法继续诊断" : "Network unavailable, cannot continue diagnostics",
                "Diagnostic.ProxyOk" => _language.StartsWith("zh") ? "代理连接正常" : "Proxy connection OK",
                "Diagnostic.ProxyFail" => _language.StartsWith("zh") ? "代理连接失败，请检查代理设置" : "Proxy connection failed, check proxy settings",
                "Diagnostic.ProxyNotEnabled" => _language.StartsWith("zh") ? "代理未启用" : "Proxy not enabled",
                "Diagnostic.AllPassed" => _language.StartsWith("zh") ? "所有检查通过" : "All checks passed",
                "Diagnostic.SomeFailed" => _language.StartsWith("zh") ? "部分检查未通过" : "Some checks failed",
                "Diagnostic.ModelsFound" => _language.StartsWith("zh") ? "获取到 {0} 个模型" : "Found {0} models",
                "Diagnostic.NoModelsFound" => _language.StartsWith("zh") ? "未获取到模型列表" : "No models found",
                "Diagnostic.NoGeminiModels" => _language.StartsWith("zh") ? "未找到 Gemini 模型" : "No Gemini models found",
                "Diagnostic.LLMResponseOk" => _language.StartsWith("zh") ? "LLM 响应正常" : "LLM response OK",
                "Diagnostic.ReportTitle" => _language.StartsWith("zh") ? "VPetLLM 诊断报告" : "VPetLLM Diagnostic Report",
                "Diagnostic.SectionNetwork" => _language.StartsWith("zh") ? "网络连接" : "Network",
                "Diagnostic.SectionProxy" => _language.StartsWith("zh") ? "代理设置" : "Proxy",
                "Diagnostic.SectionChannels" => _language.StartsWith("zh") ? "API 渠道" : "API Channels",
                "Diagnostic.NoEnabledChannels" => _language.StartsWith("zh") ? "没有启用的渠道" : "No enabled channels",
                "Diagnostic.ApiUrl" => _language.StartsWith("zh") ? "API地址" : "API URL",
                "Diagnostic.Model" => _language.StartsWith("zh") ? "模型" : "Model",
                "Diagnostic.NotSet" => _language.StartsWith("zh") ? "未设置" : "Not set",
                "Diagnostic.ProxyMode" => _language.StartsWith("zh") ? "代理模式" : "Proxy Mode",
                "Diagnostic.UsesProxy" => _language.StartsWith("zh") ? "使用代理" : "Uses Proxy",
                "Diagnostic.Yes" => _language.StartsWith("zh") ? "是" : "Yes",
                "Diagnostic.No" => _language.StartsWith("zh") ? "否" : "No",
                "Diagnostic.ApiStatus" => _language.StartsWith("zh") ? "API状态" : "API Status",
                "Diagnostic.AvailableModels" => _language.StartsWith("zh") ? "可用模型" : "Available Models",
                "Diagnostic.LLMResponse" => _language.StartsWith("zh") ? "LLM响应" : "LLM Response",
                "Diagnostic.OverallResult" => _language.StartsWith("zh") ? "总体结果" : "Overall Result",
                "Diagnostic.ProxyStatusEnabled" => _language.StartsWith("zh") ? "代理已启用" : "Proxy enabled",
                "Diagnostic.ProxyStatusDisabled" => _language.StartsWith("zh") ? "代理未启用" : "Proxy disabled",
                "Diagnostic.GlobalApiProxyOn" => _language.StartsWith("zh") ? "全部API代理已启用" : "Global API proxy enabled",
                "Diagnostic.RecReportTitle" => _language.StartsWith("zh") ? "推荐设置调整报告" : "Recommended Settings Report",
                "Diagnostic.RecCritical" => _language.StartsWith("zh") ? "关键问题" : "Critical Issues",
                "Diagnostic.RecRecommended" => _language.StartsWith("zh") ? "建议优化" : "Suggested Optimizations",
                "Diagnostic.RecCurrent" => _language.StartsWith("zh") ? "当前值" : "Current",
                "Diagnostic.RecSuggest" => _language.StartsWith("zh") ? "建议值" : "Suggested",
                "Diagnostic.RecReason" => _language.StartsWith("zh") ? "原因" : "Reason",
                "Diagnostic.RecNoIssues" => _language.StartsWith("zh") ? "未发现设置问题，配置正常。" : "No issues found, settings are properly configured.",
                "Diagnostic.RecProvider" => _language.StartsWith("zh") ? "切换主提供商为 Free" : "Switch primary provider to Free",
                "Diagnostic.RecNoApiReason" => _language.StartsWith("zh") ? "未配置任何 API 密钥或接口。Free 模式无需 API 密钥即可使用，推荐新手使用。" : "No API key or endpoint configured. Free mode works without any API key and is recommended for new users.",
                "Diagnostic.RecFreeAdvanced" => _language.StartsWith("zh") ? "启用 Free 高级模式" : "Enable Free advanced mode",
                "Diagnostic.RecFreeAdvancedReason" => _language.StartsWith("zh") ? "高级模式可在 Free 模式下提供更好的模型选择和回复质量。" : "Advanced mode enables better model selection and response quality in Free mode.",
                "Diagnostic.RecFreeLoadBalance" => _language.StartsWith("zh") ? "启用 Free 负载均衡" : "Enable Free load balancing",
                "Diagnostic.RecFreeLoadBalanceReason" => _language.StartsWith("zh") ? "负载均衡可将请求分发到多个 Free 提供商，提高可靠性。" : "Load balancing distributes requests across multiple Free providers for better reliability.",
                "Diagnostic.RecEnableFallback" => _language.StartsWith("zh") ? "启用提供商降级流程" : "Enable provider fallback (degradation)",
                "Diagnostic.RecEnableFallbackReason" => _language.StartsWith("zh") ? "当主提供商请求失败时，系统将自动降级到 Free 以确保服务不中断。" : "When the primary provider fails, the system will automatically fall back to Free to ensure uninterrupted service.",
                "Diagnostic.RecFallbackFree" => _language.StartsWith("zh") ? "添加 Free 作为降级提供商" : "Add Free as fallback provider",
                "Diagnostic.RecFallbackFreeReason" => _language.StartsWith("zh") ? "Free 无需 API 密钥，是其他提供商不可用时的理想兜底方案。" : "Free requires no API key and serves as an ideal fallback when other providers are unavailable.",
                "Diagnostic.RecDisableProxy" => _language.StartsWith("zh") ? "关闭代理" : "Disable proxy",
                "Diagnostic.RecDisableProxyReason" => _language.StartsWith("zh") ? "代理连接失败，关闭代理可能恢复直接网络访问。" : "Proxy connection failed. Disabling proxy may restore direct network access.",
                "Diagnostic.RecDisableGlobalApiProxy" => _language.StartsWith("zh") ? "关闭全部API代理" : "Disable global API proxy",
                "Diagnostic.RecDisableGlobalApiProxyReason" => _language.StartsWith("zh") ? "全部API代理导致连接失败，关闭后可能恢复直连。" : "Global API proxy is causing connection failures. Disabling it may allow direct access to work.",
                "Diagnostic.RecChannelProxy" => _language.StartsWith("zh") ? "{0} 渠道: {1} - 关闭代理" : "{0} channel: {1} - disable proxy",
                "Diagnostic.RecChannelProxyReason" => _language.StartsWith("zh") ? "该渠道配置了代理但代理不可用，建议切换为直连。" : "This channel is using proxy but proxy is not working. Switching to direct connection is recommended.",
                "Diagnostic.RecLlmFailFallbackReason" => _language.StartsWith("zh") ? "部分渠道 LLM 测试失败。启用 Free 降级可确保主提供商失败时服务仍然可用。" : "Some channel LLM tests failed. Enabling fallback with Free ensures service availability even when primary providers fail.",
                _ => key
            };
        }
    }
}