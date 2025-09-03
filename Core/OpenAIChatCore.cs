using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace VPetLLM.Core
{
    public class OpenAIChatCore : ChatCoreBase
    {
        public override string Name => "OpenAI";
        private readonly HttpClient _httpClient;
        private readonly Setting.OpenAISetting _openAISetting;

        public OpenAIChatCore(Setting.OpenAISetting openAISetting)
        {
            _openAISetting = openAISetting;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAISetting.ApiKey}");
        }

        public override async Task<string> Chat(string prompt)
        {
            History.Add(new Message { Role = "user", Content = prompt });
            var data = new
            {
                model = _openAISetting.Model,
                messages = History
            };
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_openAISetting.Url, content);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var responseObject = JObject.Parse(responseString);
            var message = responseObject["choices"][0]["message"]["content"].ToString();
            History.Add(new Message { Role = "assistant", Content = message });
            return message;
        }

        public override List<string> GetModels()
        {
            var url = new System.Uri(new System.Uri(_openAISetting.Url), "/v1/models");
            var response = _httpClient.GetAsync(url).Result;
            response.EnsureSuccessStatusCode();
            var responseString = response.Content.ReadAsStringAsync().Result;
            JObject responseObject;
            try
            {
                responseObject = JObject.Parse(responseString);
            }
            catch (JsonReaderException)
            {
                throw new System.Exception($"Failed to parse JSON response: {responseString.Substring(0, System.Math.Min(responseString.Length, 100))}");
            }
            var models = new List<string>();
            foreach (var model in responseObject["data"])
            {
                models.Add(model["id"].ToString());
            }
            return models;
        }
    }
}