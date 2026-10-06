using System.IO;
using HuaCi.Core;
using NAudio.Wave;

namespace HuaCi.Speech;

public enum SpeechState { Idle, Loading, Playing }

/// <summary>
/// Plays text aloud: Edge neural voice first, Windows offline voice as fallback. Only one utterance at a time;
/// the "owner" (usually a speak button) lets the UI show which button is loading/playing.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private const int CacheCapacity = 80;

    private readonly Func<AppSettings> _settings;
    private readonly EdgeTtsClient _edge = new();
    private readonly SynchronizationContext? _ui;
    private readonly LinkedList<(string Key, byte[] Audio, bool IsMp3)> _cache = new();
    private readonly Dictionary<string, Task<(byte[] Audio, bool IsMp3)>> _inflight = new();
    private int _prefetchGeneration;

    private WaveOutEvent? _output;
    private IDisposable? _reader;
    private CancellationTokenSource? _cts;
    private int _session;
    private bool _fallbackNotified;
    private DateTime _edgeFailedAt = DateTime.MinValue;

    public SpeechService(Func<AppSettings> settings)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current;
    }

    public object? CurrentOwner { get; private set; }
    public SpeechState State { get; private set; }

    /// <summary>Raised on the UI thread whenever the owner or state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised once when the online voice failed and offline voice was used.</summary>
    public event Action<string>? FellBackToOffline;

    public SpeechState StateFor(object owner) => ReferenceEquals(owner, CurrentOwner) ? State : SpeechState.Idle;

    /// <summary>Pre-opens the online voice connection (called when the translation popup appears).</summary>
    public void Warmup()
    {
        if (DateTime.UtcNow - _edgeFailedAt > TimeSpan.FromMinutes(1)) _edge.Warmup();
    }

    /// <summary>Starts speaking, or stops if this owner is already loading/playing.</summary>
    public async Task ToggleAsync(object owner, string text, string? languageCode, string? accent = null)
    {
        if (ReferenceEquals(owner, CurrentOwner) && State != SpeechState.Idle)
        {
            Stop();
            return;
        }
        await SpeakAsync(owner, text, languageCode, accent);
    }

    public async Task SpeakAsync(object owner, string text, string? languageCode, string? accent = null)
    {
        Stop();
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 3000) text = text[..3000];

        var session = ++_session;
        var cts = _cts = new CancellationTokenSource();
        SetState(owner, SpeechState.Loading);

        var s = _settings();
        var lang = string.IsNullOrWhiteSpace(languageCode) ? LanguageInfo.DetectScript(text) : languageCode;
        var voice = LanguageInfo.VoiceFor(lang, accent ?? s.EnglishAccent);
        var rate = Math.Clamp(s.SpeechRate, 0.5, 2.0);

        try
        {
            var (audio, isMp3) = await GetAudioAsync(text, voice, lang, rate, s.OfflineSpeechFallback, cts.Token);
            if (session != _session) return;
            Play(audio, isMp3, session, owner);
        }
        catch (OperationCanceledException)
        {
            // 被新的朗读打断
        }
        catch (Exception ex)
        {
            App.Log("朗读失败", ex);
            if (session == _session) SetState(null, SpeechState.Idle);
        }
    }

    public void Stop()
    {
        _session++;
        _cts?.Cancel();
        _cts = null;
        StopOutput();
        if (CurrentOwner != null || State != SpeechState.Idle) SetState(null, SpeechState.Idle);
    }

    /// <summary>
    /// Synthesizes in the background so later clicks play instantly (original sentence, key words).
    /// A newer prefetch request supersedes the previous one.
    /// </summary>
    public void Prefetch(IEnumerable<(string Text, string? Lang, string? Accent)> items)
    {
        var s = _settings();
        var generation = ++_prefetchGeneration;
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i.Text) && i.Text.Length <= 600).Take(8).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var (text, lang, accent) in list)
            {
                if (generation != _prefetchGeneration) return;
                var l = string.IsNullOrWhiteSpace(lang) ? LanguageInfo.DetectScript(text) : lang;
                var voice = LanguageInfo.VoiceFor(l, accent ?? s.EnglishAccent);
                try
                {
                    // 预取只走在线语音；失败了等用户点击时再兜底
                    await RunOnUiAsync(() => GetAudioAsync(text.Trim(), voice, l, Math.Clamp(s.SpeechRate, 0.5, 2.0), allowOffline: false, CancellationToken.None));
                }
                catch
                {
                    return;
                }
            }
        });
    }

    private Task<T> RunOnUiAsync<T>(Func<Task<T>> func)
    {
        if (_ui == null) return func();
        var tcs = new TaskCompletionSource<T>();
        _ui.Post(async _ =>
        {
            try { tcs.SetResult(await func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    /// <summary>Cache → in-flight request → new synthesis. Must be called on the UI thread.</summary>
    private async Task<(byte[] Audio, bool IsMp3)> GetAudioAsync(string text, string voice, string lang, double rate, bool allowOffline, CancellationToken ct)
    {
        var key = $"{voice}|{rate:0.00}|{text}";
        var hit = _cache.FirstOrDefault(c => c.Key == key);
        if (hit.Audio != null)
        {
            _cache.Remove(hit);
            _cache.AddFirst(hit);
            return (hit.Audio, hit.IsMp3);
        }

        if (_inflight.TryGetValue(key, out var pending))
        {
            try
            {
                return await pending.WaitAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested && allowOffline)
            {
                // 预取失败（例如在线语音不可用）：下面重新走带离线兜底的流程
            }
        }

        var task = FetchAsync(text, voice, rate, allowOffline, ct);
        _inflight[key] = task;
        try
        {
            var result = await task;
            _cache.AddFirst((key, result.Audio, result.IsMp3));
            while (_cache.Count > CacheCapacity) _cache.RemoveLast();
            return result;
        }
        finally
        {
            if (_inflight.TryGetValue(key, out var t) && t == task) _inflight.Remove(key);
        }
    }

    private async Task<(byte[] Audio, bool IsMp3)> FetchAsync(string text, string voice, double rate, bool allowOffline, CancellationToken ct)
    {
        byte[] audio;
        bool isMp3;
        // 在线语音刚失败过的 1 分钟内直接走离线，避免每次都等超时
        bool skipOnline = allowOffline && DateTime.UtcNow - _edgeFailedAt < TimeSpan.FromMinutes(1);
        try
        {
            if (skipOnline) throw new IOException("在线语音暂不可用");
            audio = await _edge.SynthesizeAsync(text, voice, rate, ct);
            isMp3 = true;
        }
        catch (Exception ex) when (allowOffline && !ct.IsCancellationRequested)
        {
            if (!skipOnline)
            {
                _edgeFailedAt = DateTime.UtcNow;
                App.Log("在线语音失败，改用离线语音", ex);
                if (!_fallbackNotified)
                {
                    _fallbackNotified = true;
                    Post(() => FellBackToOffline?.Invoke(ex.Message));
                }
            }
            var tag = string.Join('-', voice.Split('-').Take(2));
            audio = await WindowsTts.SynthesizeAsync(text, tag, rate, ct);
            isMp3 = false;
        }
        return (audio, isMp3);
    }

    private void Play(byte[] audio, bool isMp3, int session, object owner)
    {
        StopOutput();
        WaveStream reader = isMp3
            ? new StreamMediaFoundationReader(new MemoryStream(audio))
            : new WaveFileReader(new MemoryStream(audio));
        var output = new WaveOutEvent { DesiredLatency = 120 };
        output.Init(reader);
        output.PlaybackStopped += (_, _) => Post(() =>
        {
            if (session == _session)
            {
                StopOutput();
                SetState(null, SpeechState.Idle);
            }
        });
        _reader = reader;
        _output = output;
        output.Play();
        SetState(owner, SpeechState.Playing);
    }

    private void StopOutput()
    {
        var output = _output;
        var reader = _reader;
        _output = null;
        _reader = null;
        try { output?.Stop(); } catch { /* ignore */ }
        output?.Dispose();
        reader?.Dispose();
    }

    private void SetState(object? owner, SpeechState state)
    {
        CurrentOwner = owner;
        State = state;
        Post(() => StateChanged?.Invoke());
    }

    private void Post(Action action)
    {
        if (_ui == null || SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        Stop();
        _edge.Dispose();
    }
}
