using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace VPetLLM.Core
{
    public class GeminiChatCore : ChatCoreBase
    {
        public override string Name => "Gemini";
        private readonly HttpClient _httpClient;
        private readonly Setting.GeminiSetting _geminiSetting;

        public GeminiChatCore(Setting.GeminiSetting geminiSetting)
        {
            _geminiSetting = geminiSetting;
            _httpClient = new HttpClient();
        }

        public override async Task<string> Chat(string prompt)
        {
            History.Add(new Message { Role = "user", Content = prompt });
            var data = new
            {
                contents = History.Select(m => new { role = m.Role, parts = new[] { new { text = m.Content } } })
            };
            var content = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");
            var url = _geminiSetting.Url;
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var responseObject = JObject.Parse(responseString);
            var message = responseObject["candidates"][0]["content"]["parts"][0]["text"].ToString();
            History.Add(new Message { Role = "model", Content = message });
            return message;
        }

        public override List<string> GetModels()
        {
            var url = new System.Uri(new System.Uri(_geminiSetting.Url), $"/v1beta/models?key={_geminiSetting.ApiKey}");
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
            foreach (var model in responseObject["models"])
            {
                models.Add(model["name"].ToString().Replace("models/", ""));
            }
            return models;
        }
    }
}