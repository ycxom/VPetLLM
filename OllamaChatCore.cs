using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace VPetLLM
{
    public class OllamaChatCore : ChatCoreBase
    {
        public override string Name => "Ollama";
        private readonly HttpClient _httpClient;
        private readonly Setting.OllamaSetting _ollamaSetting;

        public OllamaChatCore(Setting.OllamaSetting ollamaSetting)
        {
            _ollamaSetting = ollamaSetting;
            _httpClient = new HttpClient()
            {
                BaseAddress = new System.Uri(ollamaSetting.Url)
            };
        }

        public override async Task<string> Chat(string prompt)
        {
            History.Add(new Message { Role = "user", Content = prompt });
            var data = new
            {
                model = _ollamaSetting.Model,
                messages = History,
                stream = false,
            };
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("/api/chat", content);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var responseObject = JObject.Parse(responseString);
            var message = responseObject["message"]["content"].ToString();
            History.Add(new Message { Role = "assistant", Content = message });
            return message;
        }

        public override List<string> GetModels()
        {
            var response = _httpClient.GetAsync("/api/tags").Result;
            response.EnsureSuccessStatusCode();
            var responseString = response.Content.ReadAsStringAsync().Result;
            var responseObject = JObject.Parse(responseString);
            var models = new List<string>();
            foreach (var model in responseObject["models"])
            {
                models.Add(model["name"].ToString());
            }
            return models;
        }
    }
}