using System.IO;
using Windows.Media.SpeechSynthesis;

namespace HuaCi.Speech;

/// <summary>Offline fallback using the voices installed in Windows. Returns WAV bytes.</summary>
public static class WindowsTts
{
    public static async Task<byte[]> SynthesizeAsync(string text, string languageTag, double rate, CancellationToken ct)
    {
        using var synth = new SpeechSynthesizer();
        var lang = languageTag.Split('-')[0];
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.Equals(languageTag, StringComparison.OrdinalIgnoreCase))
                    ?? SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
        if (voice != null) synth.Voice = voice;
        synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 3.0);

        using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
        using var input = stream.AsStreamForRead();
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
