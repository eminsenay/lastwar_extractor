using System.ClientModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LastWarExtractor.Models;
using OpenAI.Chat;

namespace LastWarExtractor.Services;

public sealed class ExtractionConfig
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string BaseUrl { get; init; }
    public required string ApiKey { get; init; }
    public required string ApiStyle { get; init; }
    public required int RequestsPerMinute { get; init; }
}

/// <summary>Screenshot extraction orchestration ported from extractor.py.</summary>
public sealed class ExtractorService
{
    private static readonly HashSet<int> RetryableStatuses = new() { 408, 409, 429, 500, 502, 503, 504 };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(4) };

    public async Task<List<ExtractionResult>> ExtractManyAsync(
        IReadOnlyList<string> imagePaths,
        ExtractionConfig config,
        Action<int, int, ExtractionResult>? progress,
        CancellationToken cancellationToken)
    {
        var backend = CreateBackend(config);
        var limiter = new RequestRateLimiter(config.RequestsPerMinute);
        var results = new List<ExtractionResult>();

        for (int index = 0; index < imagePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = imagePaths[index];
            var result = await RunAttemptOnceAsync(backend, config.Model, path, config.ApiStyle, limiter, cancellationToken)
                .ConfigureAwait(false);
            results.Add(result);
            progress?.Invoke(index + 1, imagePaths.Count, result);
        }
        return results;
    }

    private async Task<ExtractionResult> RunAttemptOnceAsync(
        IExtractionBackend backend, string model, string path, string apiStyle,
        RequestRateLimiter limiter, CancellationToken ct)
    {
        try
        {
            var extraction = await ExtractOneAsync(backend, model, path, apiStyle, limiter, ct).ConfigureAwait(false);
            return new ExtractionResult(path, extraction, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                var extraction = await ExtractOneAsync(backend, model, path, apiStyle, limiter, ct).ConfigureAwait(false);
                return new ExtractionResult(path, extraction, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception retryEx)
            {
                return new ExtractionResult(path, null, $"{ex.Message}\nRetry failed: {retryEx.Message}");
            }
        }
    }

    public async Task<ScreenshotExtraction> ExtractOneAsync(
        IExtractionBackend backend, string model, string imagePath, string apiStyle,
        RequestRateLimiter limiter, CancellationToken ct, int maxAttempts = 4)
    {
        var dataUrl = EncodeImageAsDataUrl(imagePath);
        Exception? last = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var outputText = await backend.CreateAsync(model, dataUrl, apiStyle, ct).ConfigureAwait(false);
                return ParseAndValidate(outputText, Path.GetFileName(imagePath));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= maxAttempts - 1 || !IsRetryable(ex))
                    throw;
                var delay = RetryDelay(ex, attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            }
        }
        throw last!;
    }

    // --- Parsing / sanitizing ---

    private static readonly Dictionary<string, int> DayIndex = new()
    {
        ["monday"] = 1, ["tuesday"] = 2, ["wednesday"] = 3,
        ["thursday"] = 4, ["friday"] = 5, ["saturday"] = 6,
    };

    private static ScreenshotExtraction ParseAndValidate(string outputText, string fileName)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(outputText);
        }
        catch (JsonException ex)
        {
            var snippet = outputText.Length > 500 ? outputText[..500] : outputText;
            throw new InvalidOperationException($"Model returned non-JSON output for {fileName}: {snippet}", ex);
        }
        if (root is not JsonObject payload)
            throw new InvalidOperationException($"Model returned non-object JSON for {fileName}.");

        SanitizePayload(payload);

        ScreenshotExtraction? extraction;
        try
        {
            extraction = payload.Deserialize<ScreenshotExtraction>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Local validation failed for {fileName}: {ex.Message}", ex);
        }
        if (extraction is null)
            throw new InvalidOperationException($"Local validation failed for {fileName}: null result.");

        ValidateRanges(extraction, fileName);
        return extraction;
    }

    private static void ValidateRanges(ScreenshotExtraction e, string fileName)
    {
        if (e.DayConfidence is < 0 or > 1)
            throw new InvalidOperationException($"Local validation failed for {fileName}: day_confidence out of range.");
        foreach (var row in e.Rows.Concat(e.PinnedRow is null ? Array.Empty<ExtractedRow>() : new[] { e.PinnedRow }))
        {
            if (row.Rank < 1 || row.Points < 0 || string.IsNullOrWhiteSpace(row.RawName)
                || row.ExtractionConfidence is < 0 or > 1)
                throw new InvalidOperationException($"Local validation failed for {fileName}: invalid row values.");
        }
    }

    private static void SanitizePayload(JsonObject payload)
    {
        if (payload["warnings"] is not JsonArray warnings)
        {
            warnings = new JsonArray();
            payload["warnings"] = warnings;
        }

        double conf = 0.0;
        var rawConf = payload["day_confidence"];
        bool parsed = rawConf is not null && double.TryParse(rawConf.ToString(), out conf);
        if (!parsed)
        {
            conf = 0.0;
            warnings.Add($"Invalid day_confidence {rawConf}; normalized to 0.0.");
        }
        else if (conf > 1.0)
        {
            var day = (payload["detected_day"]?.ToString() ?? "").ToLowerInvariant();
            DayIndex.TryGetValue(day, out int expectedIndex);
            if (conf == Math.Floor(conf) && conf >= 1 && conf <= 6)
            {
                double normalized;
                string reason;
                if (expectedIndex == (int)conf)
                {
                    normalized = 1.0;
                    reason = "weekday index matching detected_day";
                }
                else
                {
                    normalized = 0.0;
                    reason = "weekday index conflicting with detected_day";
                }
                warnings.Add($"day_confidence {rawConf} looked like a {reason}; normalized to {normalized:0.0}.");
                conf = normalized;
            }
            else
            {
                warnings.Add($"day_confidence {rawConf} was outside 0..1; clamped to 1.0.");
                conf = 1.0;
            }
        }
        else if (conf < 0.0)
        {
            warnings.Add($"day_confidence {rawConf} was below 0; clamped to 0.0.");
            conf = 0.0;
        }
        payload["day_confidence"] = conf;

        if (payload["rows"] is JsonArray rows)
        {
            int i = 1;
            foreach (var row in rows)
            {
                SanitizeRow(row as JsonObject, $"row {i}", warnings);
                i++;
            }
        }
        if (payload["pinned_row"] is JsonObject pinned)
            SanitizeRow(pinned, "pinned_row", warnings);
    }

    private static void SanitizeRow(JsonObject? row, string label, JsonArray warnings)
    {
        if (row is null)
            return;
        if (!row.ContainsKey("alliance_name"))
            row["alliance_name"] = null;
        var rawName = row["raw_name"]?.ToString();
        var allianceNode = row["alliance_name"];
        var allianceName = allianceNode?.ToString();
        bool allianceIsString = allianceNode is JsonValue av && av.TryGetValue<string>(out _);

        if (LooksLikeAllianceText(rawName) && allianceIsString && !LooksLikeAllianceText(allianceName))
        {
            row["raw_name"] = allianceName;
            row["alliance_name"] = rawName;
            warnings.Add($"{label}: swapped player/alliance text lines returned by the model.");
        }
        else if (LooksLikeAllianceText(rawName))
        {
            warnings.Add($"{label}: raw_name still looks like alliance/role text; player name could not be recovered automatically.");
        }
    }

    private static bool LooksLikeAllianceText(string? value)
    {
        if (value is null)
            return false;
        var text = string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Contains("elite force commander")
            || text.Contains("alliance")
            || (text.StartsWith('[') && text.Contains(']'));
    }

    public static List<string> BasicSanityChecks(ScreenshotExtraction result)
    {
        var warnings = new List<string>();
        var ranks = result.Rows.Select(r => r.Rank).ToList();
        if (ranks.Count != ranks.Distinct().Count())
            warnings.Add("Duplicate leaderboard ranks detected in the same screenshot.");
        if (ranks.Count > 1 && ranks.Zip(ranks.Skip(1), (a, b) => b <= a).Any(x => x))
            warnings.Add("Leaderboard ranks are not strictly increasing.");

        var seen = new HashSet<int>();
        foreach (var row in result.Rows)
        {
            if (LooksLikeAllianceText(row.RawName))
                warnings.Add($"Rank {row.Rank} raw_name still looks like alliance/role text: {row.RawName}.");
            if (row.PlayerId is int id)
            {
                if (!seen.Add(id))
                    warnings.Add($"Player ID {id} appears more than once in the screenshot.");
            }
        }
        return warnings;
    }

    // --- Retry helpers ---

    private static bool IsRetryable(Exception ex)
    {
        if (ex is ExtractionHttpException http && http.StatusCode is int code && RetryableStatuses.Contains(code))
            return true;
        var name = ex.GetType().Name.ToLowerInvariant();
        if (ex is HttpRequestException || ex is TimeoutException || ex is TaskCanceledException)
            return true;
        return name.Contains("ratelimit") || name.Contains("timeout") || name.Contains("connection") || name.Contains("internalserver");
    }

    private static double RetryDelay(Exception ex, int attempt)
    {
        if (ex is ExtractionHttpException http && http.RetryAfterSeconds is double ra)
            return Math.Max(1.0, ra);
        return Math.Min(30.0, Math.Pow(2.0, attempt));
    }

    private static string EncodeImageAsDataUrl(string path)
    {
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => throw new InvalidOperationException($"Unsupported image type: {path}"),
        };
        var data = Convert.ToBase64String(File.ReadAllBytes(path));
        return $"data:{mime};base64,{data}";
    }

    private static IExtractionBackend CreateBackend(ExtractionConfig config)
    {
        var key = config.ApiKey;
        if (string.IsNullOrEmpty(key))
            key = !RequiresApiKey(config.Provider, config.BaseUrl) ? "local" : throw new InvalidOperationException("API key is not set");

        if (config.Provider == "openai" && config.ApiStyle == "chat")
            return new OpenAiSdkChatBackend(config.Model, config.BaseUrl, key);
        return new HttpExtractionBackend(config.BaseUrl, key);
    }

    internal static bool IsLocalEndpoint(string baseUrl)
    {
        var lower = baseUrl.ToLowerInvariant();
        return lower.Contains("localhost") || lower.Contains("127.0.0.1") || lower.Contains("::1");
    }

    /// <summary>Whether an API key is expected for this provider/endpoint. "custom" always requires one,
    /// since (unlike "local") it has no built-in no-auth preset — even if its URL happens to look local.</summary>
    internal static bool RequiresApiKey(string provider, string baseUrl) =>
        provider.Trim().ToLowerInvariant() == "custom" || !IsLocalEndpoint(baseUrl);

    internal static HttpClient SharedHttp => Http;
}

public interface IExtractionBackend
{
    Task<string> CreateAsync(string model, string dataUrl, string apiStyle, CancellationToken ct);
}

public sealed class ExtractionHttpException : Exception
{
    public ExtractionHttpException(int? statusCode, double? retryAfterSeconds, string message) : base(message)
    {
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int? StatusCode { get; }
    public double? RetryAfterSeconds { get; }
}

/// <summary>Raw HTTP backend for any OpenAI-compatible endpoint (chat completions and responses).</summary>
public sealed class HttpExtractionBackend : IExtractionBackend
{
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public HttpExtractionBackend(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task<string> CreateAsync(string model, string dataUrl, string apiStyle, CancellationToken ct)
    {
        return apiStyle == "responses"
            ? await CreateResponsesAsync(model, dataUrl, ct).ConfigureAwait(false)
            : await CreateChatAsync(model, dataUrl, ct).ConfigureAwait(false);
    }

    private async Task<string> CreateChatAsync(string model, string dataUrl, CancellationToken ct)
    {
        var schema = JsonNode.Parse(ExtractionPrompt.SchemaJson);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = ExtractionPrompt.Prompt },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUrl, ["detail"] = "high" },
                        },
                    },
                },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = ExtractionPrompt.SchemaName,
                    ["strict"] = true,
                    ["schema"] = schema,
                },
            },
        };

        var doc = await PostAsync($"{_baseUrl}/chat/completions", body, ct).ConfigureAwait(false);
        return doc["choices"]?[0]?["message"]?["content"]?.ToString()
            ?? throw new InvalidOperationException("Chat response missing message content.");
    }

    private async Task<string> CreateResponsesAsync(string model, string dataUrl, CancellationToken ct)
    {
        var schema = JsonNode.Parse(ExtractionPrompt.SchemaJson);
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "input_text", ["text"] = ExtractionPrompt.Prompt },
                        new JsonObject { ["type"] = "input_image", ["image_url"] = dataUrl, ["detail"] = "high" },
                    },
                },
            },
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = ExtractionPrompt.SchemaName,
                    ["strict"] = true,
                    ["schema"] = schema,
                },
            },
        };

        var doc = await PostAsync($"{_baseUrl}/responses", body, ct).ConfigureAwait(false);
        return ExtractResponsesOutputText(doc);
    }

    private static string ExtractResponsesOutputText(JsonNode doc)
    {
        if (doc["output_text"] is JsonValue direct && direct.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
            return text;

        var parts = new List<string>();
        if (doc["output"] is JsonArray output)
        {
            foreach (var item in output)
            {
                if (item?["content"] is JsonArray content)
                {
                    foreach (var part in content)
                    {
                        if (part?["type"]?.ToString() == "output_text" && part["text"] is not null)
                            parts.Add(part["text"]!.ToString());
                    }
                }
            }
        }
        if (parts.Count == 0)
            throw new InvalidOperationException("Responses payload contained no output_text.");
        return string.Concat(parts);
    }

    private async Task<JsonNode> PostAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        HttpResponseMessage response;
        try
        {
            response = await ExtractorService.SharedHttp.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ExtractionHttpException(null, null, ex.Message);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                double? retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                throw new ExtractionHttpException((int)response.StatusCode, retryAfter,
                    $"HTTP {(int)response.StatusCode} from {url}: {Truncate(payload, 500)}");
            }
            return JsonNode.Parse(payload) ?? throw new InvalidOperationException("Empty response body.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>OpenAI provider chat-completions backend using the official OpenAI .NET SDK.</summary>
public sealed class OpenAiSdkChatBackend : IExtractionBackend
{
    private readonly ChatClient _client;

    public OpenAiSdkChatBackend(string model, string baseUrl, string apiKey)
    {
        var options = new OpenAI.OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(baseUrl))
            options.Endpoint = new Uri(baseUrl);
        _client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
    }

    public async Task<string> CreateAsync(string model, string dataUrl, string apiStyle, CancellationToken ct)
    {
        var (mediaType, bytes) = DecodeDataUrl(dataUrl);
        var userMessage = new UserChatMessage(
            ChatMessageContentPart.CreateTextPart(ExtractionPrompt.Prompt),
            ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(bytes), mediaType, ChatImageDetailLevel.High));

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                ExtractionPrompt.SchemaName,
                BinaryData.FromString(ExtractionPrompt.SchemaJson),
                jsonSchemaIsStrict: true),
        };

        try
        {
            var completion = await _client.CompleteChatAsync(new[] { userMessage }, options, ct).ConfigureAwait(false);
            return completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text : "";
        }
        catch (ClientResultException ex)
        {
            double? retryAfter = null;
            throw new ExtractionHttpException(ex.Status, retryAfter, ex.Message);
        }
    }

    private static (string MediaType, byte[] Bytes) DecodeDataUrl(string dataUrl)
    {
        int comma = dataUrl.IndexOf(',');
        var header = dataUrl[5..dataUrl.IndexOf(';')];
        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        return (header, bytes);
    }
}
