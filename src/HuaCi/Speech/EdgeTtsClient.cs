using System.IO;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace HuaCi.Speech;

/// <summary>
/// Client for the Microsoft Edge "Read Aloud" neural voices (same protocol as the open-source edge-tts project).
/// Keeps one WebSocket open and reuses it, because on slow networks the TLS handshake alone can take seconds.
/// Returns MP3 bytes.
/// </summary>
public sealed class EdgeTtsClient : IDisposable
{
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string ChromiumFullVersion = "143.0.3650.75";
    private const string ChromiumMajor = "143";
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const long WinEpochSeconds = 11644473600;

    /// <summary>The service may drop idle connections; Warmup() rebuilds connections idle longer than this.</summary>
    private static readonly TimeSpan RefreshAfterIdle = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ClientWebSocket? _ws;
    private DateTime _lastUsed;

    /// <summary>
    /// Sec-MS-GEC token: SHA-256 of (Windows file time rounded down to 5 minutes, in 100ns ticks) + client token, upper hex.
    /// </summary>
    public static string SecMsGec(DateTimeOffset now)
    {
        long seconds = now.ToUnixTimeSeconds() + WinEpochSeconds;
        seconds -= seconds % 300;
        var ticks = (decimal)seconds * 10_000_000m;
        var input = ticks.ToString("0") + TrustedClientToken;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(input)));
    }

    /// <summary>"en-US-AvaNeural" → "Microsoft Server Speech Text to Speech Voice (en-US, AvaNeural)".</summary>
    public static string FullVoiceName(string shortName)
    {
        var parts = shortName.Split('-');
        if (parts.Length < 3) return shortName;
        var locale = parts[0] + "-" + parts[1];
        var name = string.Join('-', parts.Skip(2));
        return $"Microsoft Server Speech Text to Speech Voice ({locale}, {name})";
    }

    public static string RateString(double rate)
    {
        var pct = (int)Math.Round((rate - 1.0) * 100);
        return (pct >= 0 ? "+" : "") + pct + "%";
    }

    public static string BuildSsml(string text, string voice, double rate)
    {
        var lang = string.Join('-', voice.Split('-').Take(2));
        return $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{lang}'>" +
               $"<voice name='{FullVoiceName(voice)}'><prosody pitch='+0Hz' rate='{RateString(rate)}' volume='+0%'>" +
               SecurityElement.Escape(text) +
               "</prosody></voice></speak>";
    }

    private static string Timestamp() =>
        DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Opens the connection in the background so the first click on a speaker icon is fast.</summary>
    public void Warmup()
    {
        _ = Task.Run(async () =>
        {
            if (!await _lock.WaitAsync(0)) return; // 正在使用中，本来就是热的
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                // 用户还没点朗读，趁这时把可能已失效的旧连接换掉
                if (DateTime.UtcNow - _lastUsed > RefreshAfterIdle) ResetConnection();
                await EnsureConnectedAsync(cts.Token);
            }
            catch
            {
                // best effort
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voice, double rate, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;

        await _lock.WaitAsync(token);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                var ws = await EnsureConnectedAsync(token);
                try
                {
                    var audio = await SynthesizeOnAsync(ws, text, voice, rate, token);
                    _lastUsed = DateTime.UtcNow;
                    return audio;
                }
                catch (Exception ex) when (attempt == 0 && !token.IsCancellationRequested && ex is WebSocketException or IOException)
                {
                    // 复用的连接已被服务器关掉：重连再试一次
                    ResetConnection();
                }
                catch
                {
                    // 被取消或出错时连接里可能残留上一段音频，直接丢弃连接
                    ResetConnection();
                    throw;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ClientWebSocket> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_ws is { State: WebSocketState.Open } open) return open;
        ResetConnection();

        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumMajor}.0.0.0 Safari/537.36 Edg/{ChromiumMajor}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", "muid=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ";");
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        var url = $"{WssUrl}?TrustedClientToken={TrustedClientToken}&ConnectionId={Guid.NewGuid():N}" +
                  $"&Sec-MS-GEC={SecMsGec(DateTimeOffset.UtcNow)}&Sec-MS-GEC-Version=1-{ChromiumFullVersion}";
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
            var config =
                $"X-Timestamp:{Timestamp()}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
                "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
            await SendTextAsync(ws, config, ct);
        }
        catch
        {
            ws.Dispose();
            throw;
        }

        _ws = ws;
        _lastUsed = DateTime.UtcNow;
        return ws;
    }

    private static async Task<byte[]> SynthesizeOnAsync(ClientWebSocket ws, string text, string voice, double rate, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var ssml =
            $"X-RequestId:{requestId}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{Timestamp()}Z\r\nPath:ssml\r\n\r\n" +
            BuildSsml(text, voice, rate);
        await SendTextAsync(ws, ssml, ct);

        using var audio = new MemoryStream();
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException("语音服务关闭了连接");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var data = message.GetBuffer();
            int length = (int)message.Length;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var textMsg = Encoding.UTF8.GetString(data, 0, length);
                if (textMsg.Contains("Path:turn.end", StringComparison.Ordinal) && textMsg.Contains(requestId, StringComparison.OrdinalIgnoreCase))
                    break;
            }
            else if (length >= 2)
            {
                // 二进制帧：2 字节大端头长度 + 头 + MP3 数据；只收本次请求的音频
                int headerLength = (data[0] << 8) | data[1];
                if (length < 2 + headerLength) continue;
                var header = Encoding.ASCII.GetString(data, 2, headerLength);
                if (header.Contains("Path:audio", StringComparison.Ordinal) && header.Contains(requestId, StringComparison.OrdinalIgnoreCase))
                    audio.Write(data, 2 + headerLength, length - 2 - headerLength);
            }
        }

        if (audio.Length == 0) throw new IOException("语音服务没有返回音频");
        return audio.ToArray();
    }

    private void ResetConnection()
    {
        var ws = _ws;
        _ws = null;
        if (ws == null) return;
        try { ws.Abort(); } catch { /* ignore */ }
        ws.Dispose();
    }

    private static Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    public void Dispose()
    {
        ResetConnection();
        _lock.Dispose();
    }
}
