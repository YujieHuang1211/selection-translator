using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using HuaCi.Speech;

namespace HuaCi.UI.Controls;

/// <summary>Speaker button that reads <see cref="Text"/> aloud; shows loading / stop states, click again to stop.</summary>
public partial class SpeakButton : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(SpeakButton), new PropertyMetadata(""));

    public static readonly DependencyProperty LangProperty =
        DependencyProperty.Register(nameof(Lang), typeof(string), typeof(SpeakButton), new PropertyMetadata(null));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(string), typeof(SpeakButton), new PropertyMetadata(null));

    private readonly DoubleAnimation _spin = new(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever };

    public SpeakButton()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (App.Speech == null) return;
            App.Speech.StateChanged += UpdateVisual;
            UpdateVisual();
        };
        Unloaded += (_, _) =>
        {
            if (App.Speech != null) App.Speech.StateChanged -= UpdateVisual;
        };
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Language code of <see cref="Text"/>; null = detect from the text.</summary>
    public string? Lang
    {
        get => (string?)GetValue(LangProperty);
        set => SetValue(LangProperty, value);
    }

    /// <summary>"US" / "UK" for English; null = the default accent from settings.</summary>
    public string? Accent
    {
        get => (string?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (App.Speech == null || string.IsNullOrWhiteSpace(Text)) return;
        await App.Speech.ToggleAsync(this, Text, Lang, Accent);
    }

    private void UpdateVisual()
    {
        var state = App.Speech?.StateFor(this) ?? SpeechState.Idle;
        SpeakerIcon.Visibility = state == SpeechState.Idle ? Visibility.Visible : Visibility.Collapsed;
        Spinner.Visibility = state == SpeechState.Loading ? Visibility.Visible : Visibility.Collapsed;
        StopIcon.Visibility = state == SpeechState.Playing ? Visibility.Visible : Visibility.Collapsed;
        Btn.ToolTip = state == SpeechState.Idle ? "朗读" : "停止";
        SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, state == SpeechState.Loading ? _spin : null);
    }
}
