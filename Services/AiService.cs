using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LinkPocket.Services;

public class AiService
{
    private readonly string? _openaiApiKey;
    private readonly string? _openaiModel;
    private readonly string? _anthropicApiKey;
    private readonly string? _anthropicModel;
    private readonly string _defaultProvider;

    public AiService()
    {
        // 从配置读取API密钥（实际项目中应从配置文件或环境变量读取）
        _openaiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _openaiModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
        _anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        _anthropicModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-4-20250514";
        _defaultProvider = Environment.GetEnvironmentVariable("AI_DEFAULT_PROVIDER") ?? "openai";
    }

    public bool IsAvailable => !string.IsNullOrEmpty(_openaiApiKey) || !string.IsNullOrEmpty(_anthropicApiKey);

    public async Task<AiResult<string>> GenerateMetadataAsync(string url, string? htmlContent = null)
    {
        try
        {
            var prompt = $"分析以下网页内容，生成简洁的标题（不超过50字）和描述（不超过200字）。\n\nURL: {url}\n\n";

            if (!string.IsNullOrEmpty(htmlContent))
            {
                prompt += $"内容预览:\n{htmlContent[..Math.Min(5000, htmlContent.Length)]}\n\n";
            }

            prompt += "请以JSON格式返回：{\"title\": \"...\", \"description\": \"...\"}";

            var response = await CallAiAsync(prompt, maxTokens: 500);

            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(response);

            return new AiResult<string>
            {
                Success = true,
                Data = result?["title"] ?? "",
                Message = "元数据生成成功",
                Provider = _defaultProvider
            };
        }
        catch (Exception ex)
        {
            return new AiResult<string>
            {
                Success = false,
                Error = $"AI元数据生成失败: {ex.Message}",
                Provider = _defaultProvider
            };
        }
    }

    public async Task<AiResult<List<string>>> GenerateTagsAsync(string title, string? description = null, List<string>? existingTags = null)
    {
        try
        {
            var existingTagsText = existingTags != null && existingTags.Any() 
                ? $"已有标签列表：{string.Join(", ", existingTags)}\n" 
                : "";

            var prompt = $"{existingTagsText}根据以下链接信息，从已有标签中选择最匹配的标签（可多选），如果没有合适的可以不选。\n\n";
            prompt += $"标题: {title}\n";
            prompt += $"描述: {description ?? "无"}\n\n";
            prompt += "请返回JSON数组：[\"标签1\", \"标签2\", ...]，如果没有匹配的返回空数组[]";

            var response = await CallAiAsync(prompt, maxTokens: 200);

            var suggestedTags = JsonSerializer.Deserialize<List<string>>(response);

            return new AiResult<List<string>>
            {
                Success = true,
                Data = suggestedTags ?? new(),
                Message = suggestedTags?.Count > 0 
                    ? $"建议添加 {suggestedTags.Count} 个标签"
                    : "未找到匹配的已有标签",
                Provider = _defaultProvider
            };
        }
        catch (Exception ex)
        {
            return new AiResult<List<string>>
            {
                Success = false,
                Error = $"AI标签生成失败: {ex.Message}",
                Provider = _defaultProvider
            };
        }
    }

    public async Task<AiResult<string>> GenerateSummaryAsync(string title, string url, string? content = null)
    {
        try
        {
            var prompt = $"为以下链接生成一个简短摘要（100-200字），突出核心内容和价值点。\n\n";
            prompt += $"标题: {title}\n";
            prompt += $"URL: {url}\n";

            if (!string.IsNullOrEmpty(content))
            {
                prompt += $"内容:\n{content[..Math.Min(3000, content.Length)]}\n\n";
            }

            prompt += "请直接输出摘要文本：";

            var summary = await CallAiAsync(prompt, maxTokens: 300);

            return new AiResult<string>
            {
                Success = true,
                Data = summary.Trim(),
                Message = "摘要生成成功",
                Provider = _defaultProvider
            };
        }
        catch (Exception ex)
        {
            return new AiResult<string>
            {
                Success = false,
                Error = $"AI摘要生成失败: {ex.Message}",
                Provider = _defaultProvider
            };
        }
    }

    public async Task<AiResult<string>> ChatAsync(List<ChatMessage> messages, string systemPrompt = "")
    {
        try
        {
            var fullMessages = new List<ChatMessage>();

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                fullMessages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
            }

            fullMessages.AddRange(messages);

            var reply = await CallAiWithMessagesAsync(fullMessages, maxTokens: 2000);

            return new AiResult<string>
            {
                Success = true,
                Data = reply,
                Message = "AI回复已生成",
                Provider = _defaultProvider
            };
        }
        catch (Exception ex)
        {
            return new AiResult<string>
            {
                Success = false,
                Error = $"AI对话失败: {ex.Message}",
                Provider = _defaultProvider
            };
        }
    }

    public object GetProviderInfo()
    {
        return new
        {
            available_providers = new
            {
                openai = !string.IsNullOrEmpty(_openaiApiKey),
                anthropic = !string.IsNullOrEmpty(_anthropicApiKey)
            },
            default_provider = _defaultProvider,
            models = new
            {
                openai = _openaiModel,
                anthropic = _anthropicModel
            }
        };
    }

    private async Task<string> CallAiAsync(string prompt, int maxTokens = 1000)
    {
        if (_defaultProvider == "anthropic" && !string.IsNullOrEmpty(_anthropicApiKey))
        {
            return await CallAnthropicAsync(prompt, maxTokens);
        }
        else if (!string.IsNullOrEmpty(_openaiApiKey))
        {
            return await CallOpenAiAsync(prompt, maxTokens);
        }
        else
        {
            throw new Exception("No AI provider available. Please configure API keys.");
        }
    }

    private async Task<string> CallOpenAiAsync(string prompt, int maxTokens)
    {
        using var httpClient = new HttpClient();
        
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_openaiApiKey}");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "LinkPocket/1.0");

        var requestBody = new
        {
            model = _openaiModel,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            max_tokens = maxTokens,
            temperature = 0.7
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", jsonContent);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(responseBody);
        
        return result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private async Task<string> CallAnthropicAsync(string prompt, int maxTokens)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("x-api-key", _anthropicApiKey);
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var requestBody = new
        {
            model = _anthropicModel,
            max_tokens = maxTokens,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages", jsonContent);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(responseBody);
        
        return result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private async Task<string> CallAiWithMessagesAsync(List<ChatMessage> messages, int maxTokens)
    {
        if (_defaultProvider == "openai" && !string.IsNullOrEmpty(_openaiApiKey))
        {
            using var httpClient = new HttpClient();
            
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_openaiApiKey}");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LinkPocket/1.0");

            var requestBody = new
            {
                model = _openaiModel,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                max_tokens = maxTokens,
                temperature = 0.7
            };

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", jsonContent);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(responseBody);
            
            return result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        else
        {
            throw new Exception("Only OpenAI is supported for multi-message conversations in this implementation.");
        }
    }
}

public class AiResult<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string Provider { get; set; } = "";
}

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}
