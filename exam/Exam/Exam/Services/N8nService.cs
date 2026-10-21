using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Exam.Services
{
    public class N8nService : IN8nService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<N8nService> _logger;

        public N8nService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<N8nService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        public async Task<bool> TriggerCompanySessionNotificationAsync(object payload)
        {
            string url = _config["N8nSettings:CompanySessionWebhookUrl"];
            bool isEnabled = _config.GetValue<bool>("N8nSettings:Enabled", true);
            if (!isEnabled || string.IsNullOrEmpty(url)) return false;

            return await PostJsonAsync(url, payload);
        }

        public async Task<bool> TriggerWaveNominationNotificationAsync(object payload)
        {
            string url = _config["N8nSettings:WaveNominationWebhookUrl"];
            bool isEnabled = _config.GetValue<bool>("N8nSettings:Enabled", true);
            if (!isEnabled || string.IsNullOrEmpty(url)) return false;

            return await PostJsonAsync(url, payload);
        }

        private async Task<bool> PostJsonAsync(string url, object payload)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully dispatched payload to n8n at {Url}", url);
                    return true;
                }
                else
                {
                    _logger.LogWarning("n8n Webhook returned non-success code {StatusCode} from {Url}", response.StatusCode, url);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send webhook to n8n ({Url}): {Message}", url, ex.Message);
                return false;
            }
        }
    }
}
