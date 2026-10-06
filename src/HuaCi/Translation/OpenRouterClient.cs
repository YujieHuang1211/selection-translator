using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HuaCi.Translation;

public sealed class OpenRouterException : Exception
{
    public OpenRouterException(string message, HttpStatusCode? status = null, bool isAuthProblem = false)
        : base(message)
    {
        Status = status;
        IsAuthProblem = isAuthProblem;
    }

    public HttpStatusCode? Status { get; }

    /// <summary>Missing/invalid key or no credits — the fix is in Settings, not "retry".</summary>
    public bool IsAuthProblem { get; }
}

public sealed class OpenRouterClient
{
    private static readonly HttpClient Http = CreateHttp();
    private static long _lastActivityTicks;
    private readonly Func<string> _apiKey;

    public OpenRouterClient(Func<string> apiKey) => _apiKey = apiKey;

    private static HttpClient CreateHttp()
    {
        // 部分网络下 TLS 握手要 1.5 秒以上，所以尽量复用一条长连接：
        // HTTP/2 让译文和词汇解析两个请求共用一条连接，心跳 ping 让空闲连接不被断开
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(15),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/huaci-translator");
        http.DefaultRequestHeaders.Add("X-Title", "HuaCi");
        return http;
    }

    /// <summary>HTTP version of the last response (diagnostics).</summary>
    public static Version? LastHttpVersion { get; private set; }

    /// <summary>
    /// Opens/refreshes the pooled connection in the background so the next real request skips the TLS handshake.
    /// Called at startup and whenever the selection icon appears; throttled to avoid useless traffic.
    /// </summary>
    public static void Warmup()
    {
        var last = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
        if (DateTime.UtcNow - last < TimeSpan.FromSeconds(20)) return;
        MarkActivity();
        _ = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "models") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                using var response = await Http.SendAsync(request);
                LastHttpVersion = response.Version;
            }
            catch { /* best effort */ }
        });
    }

    private static void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    public IAsyncEnumerable<string> StreamChatAsync(string model, string system, string user, double temperature, CancellationToken ct)
    {
        var body = BuildBody(model, system, user, temperature, stream: true, responseFormat: null, disableReasoning: true);
        body["provider"] = new JsonObject { ["sort"] = "latency" };
        return StreamAsync(body, ct);
    }

    /// <summary>
    /// Streams a structured-output (JSON schema) response so the caller can render partial results.
    /// Falls back to "JSON in the prompt" when the model rejects structured outputs.
    /// </summary>
    public async IAsyncEnumerable<string> StreamJsonAsync(
        string model, string system, string user, JsonObject schema,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var format = new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject { ["name"] = "analysis", ["strict"] = true, ["schema"] = schema },
        };
        var body = BuildBody(model, system, user, 0.2, stream: true, format, disableReasoning: true);
        // 流式显示词卡，首包延迟比吞吐更重要
        body["provider"] = new JsonObject { ["sort"] = "latency" };

        HttpResponseMessage response;
        try
        {
            response = await SendWithReasoningFallbackAsync(body, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OpenRouterException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            var fallbackSystem = system + "\n\nRespond with a single JSON object only, matching this JSON schema:\n" + schema.ToJsonString();
            response = await SendWithReasoningFallbackAsync(
                BuildBody(model, fallbackSystem, user, 0.2, stream: true, null, disableReasoning: true),
                HttpCompletionOption.ResponseHeadersRead, ct);
        }

        await foreach (var part in ReadSseAsync(response, ct)) yield return part;
    }

    private async IAsyncEnumerable<string> StreamAsync(JsonObject body, [EnumeratorCancellation] CancellationToken ct)
    {
        var response = await SendWithReasoningFallbackAsync(body, HttpCompletionOption.ResponseHeadersRead, ct);
        await foreach (var part in ReadSseAsync(response, ct)) yield return part;
    }

    private static async IAsyncEnumerable<string> ReadSseAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (line == null) yield break;
                var parsed = SseParser.Parse(line);
                switch (parsed.Kind)
                {
                    case SseLineKind.Content: yield return parsed.Text!; break;
                    case SseLineKind.Done: yield break;
                    case SseLineKind.Error: throw new OpenRouterException("模型返回错误：" + parsed.Text);
                }
            }
        }
    }

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync("models", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var list = new List<ModelInfo>();
        foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var outputs = m.TryGetProperty("architecture", out var arch) && arch.TryGetProperty("output_modalities", out var om)
                ? om.EnumerateArray().Select(x => x.GetString()).ToList()
                : new List<string?> { "text" };
            if (!outputs.Contains("text")) continue;

            decimal Price(string name) =>
                m.TryGetProperty("pricing", out var p) && p.TryGetProperty(name, out var v)
                && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? Math.Max(0, d) * 1_000_000m
                    : 0m;

            list.Add(new ModelInfo(
                m.GetProperty("id").GetString() ?? "",
                m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Price("prompt"),
                Price("completion"),
                m.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0));
        }
        return list;
    }

    /// <summary>Validates the key with a tiny request. Returns null when OK, otherwise a user-facing message.</summary>
    public async Task<string?> TestAsync(string model, CancellationToken ct)
    {
        try
        {
            var sb = new StringBuilder();
            await foreach (var part in StreamChatAsync(model, "Reply with OK.", "ping", 0, ct)) sb.Append(part);
            return null;
        }
        catch (OpenRouterException ex) { return ex.Message; }
        catch (HttpRequestException ex) { return "网络连接失败：" + ex.Message; }
    }

    private static JsonObject BuildBody(string model, string system, string user, double temperature, bool stream, JsonObject? responseFormat, bool disableReasoning)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["temperature"] = temperature,
            ["stream"] = stream,
        };
        if (disableReasoning) body["reasoning"] = new JsonObject { ["enabled"] = false };
        if (responseFormat != null) body["response_format"] = responseFormat;
        return body;
    }

    private async Task<HttpResponseMessage> SendWithReasoningFallbackAsync(JsonObject body, HttpCompletionOption option, CancellationToken ct)
    {
        try
        {
            return await SendAsync(body, option, ct);
        }
        catch (OpenRouterException ex) when (ex.Status == HttpStatusCode.BadRequest && body.ContainsKey("reasoning")
                                              && ex.Message.Contains("reason", StringComparison.OrdinalIgnoreCase))
        {
            // 某些模型强制推理，不允许关闭
            body.Remove("reasoning");
            return await SendAsync(body, option, ct);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(JsonObject body, HttpCompletionOption option, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new OpenRouterException("还没有设置 OpenRouter API Key", isAuthProblem: true);

        var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        MarkActivity();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            response = await Http.SendAsync(request, option, timeout.Token);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OpenRouterException("请求超时，请检查网络后重试");
        }
        catch (HttpRequestException ex)
        {
            throw new OpenRouterException("网络连接失败，请检查网络或代理设置（" + ex.Message + "）");
        }

        LastHttpVersion = response.Version;
        if (response.IsSuccessStatusCode) return response;

        var text = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        throw new OpenRouterException(FriendlyError(response.StatusCode, ExtractMessage(text)), response.StatusCode,
            isAuthProblem: response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden);
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return m.GetString() ?? body;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }

    public static string FriendlyError(HttpStatusCode status, string detail) => status switch
    {
        HttpStatusCode.Unauthorized => "API Key 无效或已失效，请在设置中检查",
        HttpStatusCode.PaymentRequired => "OpenRouter 账户余额不足，请充值或换用免费模型",
        HttpStatusCode.Forbidden => "请求被拒绝（可能是 Key 权限或内容审核）：" + detail,
        HttpStatusCode.NotFound => "找不到该模型，请在设置中重新选择：" + detail,
        HttpStatusCode.TooManyRequests => "请求太频繁，被限流了，请稍后重试",
        HttpStatusCode.BadRequest => "请求参数有误：" + detail,
        _ when (int)status >= 500 => "OpenRouter 或模型服务暂时不可用，请稍后重试",
        _ => $"请求失败（{(int)status}）：{detail}",
    };
}

