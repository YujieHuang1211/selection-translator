using System.IO;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace HuaCi.Speech;

/// <summary>
/// Client for the Microsoft Edge "Read Aloud" neural voices (same protocol as the open-source edge-tts project).
/// Returns MP3 bytes.
/// </summary>
public sealed class EdgeTtsClient
{
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string ChromiumFullVersion = "143.0.3650.75";
    private const string ChromiumMajor = "143";
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const long WinEpochSeconds = 11644473600;

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

    public async Task<byte[]> SynthesizeAsync(string text, string voice, double rate, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumMajor}.0.0.0 Safari/537.36 Edg/{ChromiumMajor}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", "muid=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ";");

        var connectionId = Guid.NewGuid().ToString("N");
        var url = $"{WssUrl}?TrustedClientToken={TrustedClientToken}&ConnectionId={connectionId}" +
                  $"&Sec-MS-GEC={SecMsGec(DateTimeOffset.UtcNow)}&Sec-MS-GEC-Version=1-{ChromiumFullVersion}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;

        await ws.ConnectAsync(new Uri(url), token);

        var config =
            $"X-Timestamp:{Timestamp()}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
        await SendTextAsync(ws, config, token);

        var ssml =
            $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{Timestamp()}Z\r\nPath:ssml\r\n\r\n" +
            BuildSsml(text, voice, rate);
        await SendTextAsync(ws, ssml, token);

        using var audio = new MemoryStream();
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException("语音服务提前关闭了连接");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var data = message.GetBuffer();
            int length = (int)message.Length;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var textMsg = Encoding.UTF8.GetString(data, 0, length);
                if (textMsg.Contains("Path:turn.end", StringComparison.Ordinal)) break;
            }
            else if (length >= 2)
            {
                // 二进制帧：2 字节大端头长度 + 头 + MP3 数据
                int headerLength = (data[0] << 8) | data[1];
                if (length < 2 + headerLength) continue;
                var header = Encoding.ASCII.GetString(data, 2, headerLength);
                if (header.Contains("Path:audio", StringComparison.Ordinal))
                    audio.Write(data, 2 + headerLength, length - 2 - headerLength);
            }
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { /* ignore */ }

        if (audio.Length == 0) throw new IOException("语音服务没有返回音频");
        return audio.ToArray();
    }

    private static Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
}
