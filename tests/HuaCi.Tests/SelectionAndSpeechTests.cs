using System.Windows.Input;
using HuaCi.Selection;
using HuaCi.Speech;

namespace HuaCi.Tests;

public class SelectionGestureDetectorTests
{
    private static InputEvent Down(int x, int y, uint t, bool ibeam = true, bool shift = false) =>
        new(InputKind.LeftDown, x, y, t, ibeam, shift, 0);

    private static InputEvent Up(int x, int y, uint t, bool ibeam = true) =>
        new(InputKind.LeftUp, x, y, t, ibeam, false, 0);

    [Fact]
    public void Drag_over_text_is_a_selection()
    {
        var d = new SelectionGestureDetector();
        d.OnLeftDown(Down(100, 100, 1000));
        var g = d.OnLeftUp(Up(180, 102, 1300));
        Assert.Equal(SelectionGestureKind.Drag, g?.Kind);
        Assert.Equal(180, g?.X);
    }

    [Fact]
    public void Single_click_is_not_a_selection()
    {
        var d = new SelectionGestureDetector();
        d.OnLeftDown(Down(100, 100, 1000));
        Assert.Null(d.OnLeftUp(Up(101, 100, 1080)));
    }

    [Fact]
    public void Drag_with_arrow_cursor_is_reported_as_needing_cursor_recheck()
    {
        var d = new SelectionGestureDetector();
        d.OnLeftDown(Down(100, 100, 1000, ibeam: false));
        var g = d.OnLeftUp(Up(400, 300, 1300, ibeam: false));
        Assert.NotNull(g);
        Assert.False(g!.Value.TextCursorSeen);
    }

    [Fact]
    public void Drag_starting_in_margin_but_ending_on_text_counts()
    {
        var d = new SelectionGestureDetector();
        d.OnLeftDown(Down(10, 100, 1000, ibeam: false));
        Assert.True(d.OnLeftUp(Up(300, 140, 1300, ibeam: true))?.TextCursorSeen);
    }

    [Fact]
    public void Double_click_is_a_selection()
    {
        var d = new SelectionGestureDetector(doubleClickTime: 500);
        d.OnLeftDown(Down(100, 100, 1000));
        Assert.Null(d.OnLeftUp(Up(100, 100, 1050)));
        d.OnLeftDown(Down(101, 100, 1200));
        Assert.Equal(SelectionGestureKind.MultiClick, d.OnLeftUp(Up(101, 100, 1250))?.Kind);
    }

    [Fact]
    public void Two_slow_clicks_are_not_a_double_click()
    {
        var d = new SelectionGestureDetector(doubleClickTime: 500);
        d.OnLeftDown(Down(100, 100, 1000));
        d.OnLeftUp(Up(100, 100, 1050));
        d.OnLeftDown(Down(100, 100, 1800));
        Assert.Null(d.OnLeftUp(Up(100, 100, 1850)));
    }

    [Fact]
    public void Two_clicks_far_apart_are_not_a_double_click()
    {
        var d = new SelectionGestureDetector(doubleClickTime: 500);
        d.OnLeftDown(Down(100, 100, 1000));
        d.OnLeftUp(Up(100, 100, 1050));
        d.OnLeftDown(Down(160, 100, 1200));
        Assert.Null(d.OnLeftUp(Up(160, 100, 1250)));
    }

    [Fact]
    public void Shift_click_extends_selection()
    {
        var d = new SelectionGestureDetector();
        d.OnLeftDown(Down(100, 100, 1000, shift: true));
        Assert.Equal(SelectionGestureKind.ShiftClick, d.OnLeftUp(Up(100, 100, 1050))?.Kind);
    }

    [Fact]
    public void Up_without_down_is_ignored()
    {
        var d = new SelectionGestureDetector();
        Assert.Null(d.OnLeftUp(Up(300, 300, 1000)));
    }

    [Fact]
    public void Tick_count_wraparound_still_detects_double_click()
    {
        var d = new SelectionGestureDetector(doubleClickTime: 500);
        d.OnLeftDown(Down(100, 100, uint.MaxValue - 50));
        d.OnLeftUp(Up(100, 100, uint.MaxValue - 10));
        d.OnLeftDown(Down(100, 100, 100));
        Assert.NotNull(d.OnLeftUp(Up(100, 100, 150)));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Q", HotkeyModifiers.Alt, Key.Q)]
    [InlineData("ctrl + shift + t", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, Key.T)]
    [InlineData("Win+1", HotkeyModifiers.Win, Key.D1)]
    [InlineData("F8", HotkeyModifiers.None, Key.F8)]
    public void Parses_valid_hotkeys(string text, HotkeyModifiers mods, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(key, hk.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Q+W")]
    [InlineData("Ctrl+Nope")]
    public void Rejects_invalid_hotkeys(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void ToString_round_trips()
    {
        Assert.True(Hotkey.TryParse("shift+alt+ctrl+D2", out var hk));
        Assert.Equal("Ctrl+Alt+Shift+2", hk.ToString());
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again));
        Assert.Equal(hk, again);
    }
}

public class EdgeTtsTests
{
    [Fact]
    public void SecMsGec_matches_reference_implementation()
    {
        // 参考值由 edge-tts 的 Python 算法计算：unix 1700000000
        var token = EdgeTtsClient.SecMsGec(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        Assert.Equal("42301B335578FEFDAE2637DED1ABD614505D432559EC08032B82048483726AFF", token);
    }

    [Fact]
    public void SecMsGec_is_stable_within_five_minutes()
    {
        var a = EdgeTtsClient.SecMsGec(DateTimeOffset.FromUnixTimeSeconds(1700000000));
        var b = EdgeTtsClient.SecMsGec(DateTimeOffset.FromUnixTimeSeconds(1700000000 + 50)); // 1700000000 位于 5 分钟窗口的第 200 秒
        var c = EdgeTtsClient.SecMsGec(DateTimeOffset.FromUnixTimeSeconds(1700000000 + 100));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Full_voice_name()
    {
        Assert.Equal("Microsoft Server Speech Text to Speech Voice (en-US, AvaMultilingualNeural)",
            EdgeTtsClient.FullVoiceName("en-US-AvaMultilingualNeural"));
    }

    [Theory]
    [InlineData(1.0, "+0%")]
    [InlineData(1.25, "+25%")]
    [InlineData(0.8, "-20%")]
    public void Rate_string(double rate, string expected) => Assert.Equal(expected, EdgeTtsClient.RateString(rate));

    [Fact]
    public void Ssml_escapes_text()
    {
        var ssml = EdgeTtsClient.BuildSsml("A < B & \"C\"", "en-GB-SoniaNeural", 1.0);
        Assert.Contains("xml:lang='en-GB'", ssml);
        Assert.Contains("A &lt; B &amp; &quot;C&quot;", ssml);
    }
}
