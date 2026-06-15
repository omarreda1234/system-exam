using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Exam.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class AiController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;

        public AiController(IConfiguration configuration)
        {
            _configuration = configuration;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] ChatRequest request)
        {
            if (request == null || (string.IsNullOrWhiteSpace(request.Message) && string.IsNullOrWhiteSpace(request.ImageBase64)))
            {
                return BadRequest(new { success = false, message = "Message or image is required." });
            }

            // Retrieve Gemini API Key from appsettings.json, appsettings.Development.json, or env variables
            string? apiKey = _configuration["GeminiApiKey"] ?? _configuration["Gemini:ApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Ok(new
                {
                    success = false,
                    message = "⚠️ **Gemini API Key Missing:** Please add your Gemini API Key in `appsettings.json` under `\"GeminiApiKey\"` to activate the live academic assistant! E.g.:\n`\"GeminiApiKey\": \"AIzaSy...\"`"
                });
            }

            try
            {
                // Construct Gemini API Payload
                var geminiRequest = new GeminiRequest();
                var userContent = new GeminiContent { Role = "user" };

                // Handle Image if present
                if (!string.IsNullOrWhiteSpace(request.ImageBase64))
                {
                    string base64Data = request.ImageBase64;
                    string mimeType = "image/png";

                    // Strip mime prefix if present (e.g. data:image/png;base64,)
                    if (base64Data.Contains(";base64,"))
                    {
                        var parts = base64Data.Split(";base64,");
                        if (parts.Length == 2)
                        {
                            mimeType = parts[0].Replace("data:", "");
                            base64Data = parts[1];
                        }
                    }

                    userContent.Parts.Add(new GeminiPart
                    {
                        InlineData = new GeminiInlineData
                        {
                            MimeType = mimeType,
                            Data = base64Data
                        }
                    });
                }

                // Handle Context/History and User prompt
                string systemPrompt = "You are El-Tarshoubi AI, an academic assistant for medical and clinical training. Answer questions clearly, accurately, and assist the user (Doctor/Assistant) with their study material, safety standards, or exams. If Arabic is used, reply in high-quality Arabic. Keep responses rich but concise and professional.";
                
                string prompt = $"{systemPrompt}\n\n";

                // Add history if present
                if (request.History != null && request.History.Count > 0)
                {
                    prompt += "Previous conversation context:\n";
                    foreach (var h in request.History)
                    {
                        string role = h.IsUser ? "User" : "AI";
                        prompt += $"{role}: {h.Text}\n";
                    }
                    prompt += "\n";
                }

                prompt += $"Current User Question: {request.Message}";

                userContent.Parts.Add(new GeminiPart { Text = prompt });
                geminiRequest.Contents.Add(userContent);

                // Serialize payload
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                string requestJson = JsonSerializer.Serialize(geminiRequest, jsonOptions);

                // Direct call — model list confirmed from API for this account
                // Order: fastest first, fallback to next if quota exhausted (429) or not found (404)
                string[] modelsToTry = new[] { "gemini-2.0-flash", "gemini-2.5-flash", "gemini-2.5-pro" };
                HttpResponseMessage? response = null;
                string responseJson = "";

                foreach (var model in modelsToTry)
                {
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    response = await _httpClient.PostAsync(url, content);
                    responseJson = await response.Content.ReadAsStringAsync();

                    // Success — use this model
                    if (response.IsSuccessStatusCode) break;

                    // Model not found — try next model
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;

                    // Quota exhausted for this model — try next model
                    if ((int)response.StatusCode == 429) continue;

                    // Any other error (auth failure, bad request, etc.) — stop immediately
                    break;
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    if (responseJson.Contains("API_KEY_INVALID") || responseJson.Contains("not valid") || responseJson.Contains("INVALID_ARGUMENT"))
                    {
                        return Ok(new { success = false, message = "⚠️ مفتاح الـ Gemini API غير صالح. تأكد من صحة المفتاح في appsettings.json" });
                    }
                    if ((int)(response?.StatusCode ?? 0) == 429)
                    {
                        return Ok(new { success = false, message = "⏳ **تجاوزت الحد المسموح به من الطلبات (Rate Limit):**\n\nتم استنزاف الـ Quota المجاني لهذا المفتاح على جميع الموديلات المتاحة.\n\n**الحلول المتاحة:**\n1. انتظر بضع دقائق ثم حاول مرة أخرى.\n2. أنشئ مفتاح API جديد من [Google AI Studio](https://aistudio.google.com) وضعه في `appsettings.json`.\n3. فعّل الدفع (Billing) على مشروعك في Google Cloud Console لرفع الحدود." });
                    }
                    return Ok(new { success = false, message = $"AI Service Error: {response?.StatusCode} - {responseJson}" });
                }

                var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseJson, jsonOptions);
                string? replyText = geminiResponse?.Candidates?[0]?.Content?.Parts?[0]?.Text;

                if (string.IsNullOrWhiteSpace(replyText))
                {
                    return Ok(new { success = false, message = "Could not generate response. Please try again." });
                }

                return Ok(new { success = true, reply = replyText });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = $"Error calling Gemini AI: {ex.Message}" });
            }
        }
    }

    public class ChatRequest
    {
        public string? Message { get; set; }
        public string? ImageBase64 { get; set; }
        public List<ChatHistoryItem>? History { get; set; }
    }

    public class ChatHistoryItem
    {
        public bool IsUser { get; set; }
        public string? Text { get; set; }
    }

    // Gemini API Request Models
    public class GeminiRequest
    {
        public List<GeminiContent> Contents { get; set; } = new();
    }

    public class GeminiContent
    {
        [JsonPropertyName("role")]
        public string? Role { get; set; }
        public List<GeminiPart> Parts { get; set; } = new();
    }

    public class GeminiPart
    {
        public string? Text { get; set; }
        [JsonPropertyName("inlineData")]
        public GeminiInlineData? InlineData { get; set; }
    }

    public class GeminiInlineData
    {
        [JsonPropertyName("mimeType")]
        public string? MimeType { get; set; }
        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    // Gemini API Response Models
    public class GeminiResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }
    }

    public class GeminiCandidate
    {
        public GeminiContentResponse? Content { get; set; }
    }

    public class GeminiContentResponse
    {
        public List<GeminiPartResponse>? Parts { get; set; }
    }

    public class GeminiPartResponse
    {
        public string? Text { get; set; }
    }
}
