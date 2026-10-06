using System.Runtime.InteropServices;
using System.Windows;

namespace HuaCi.Selection;

/// <summary>
/// Snapshot/restore of the user's clipboard around a simulated copy, so grabbing a selection
/// never destroys what the user had copied. Must be called on an STA (UI) thread.
/// </summary>
public static class ClipboardGuard
{
    private static readonly string[] PreservedFormats =
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html,
        DataFormats.CommaSeparatedValue, DataFormats.FileDrop, DataFormats.Bitmap, "PNG",
    };

    public sealed class Snapshot
    {
        internal Dictionary<string, object> Data { get; } = new();
        public bool IsEmpty => Data.Count == 0;
    }

    public static Snapshot Capture()
    {
        var snapshot = new Snapshot();
        Retry(() =>
        {
            snapshot.Data.Clear();
            var obj = Clipboard.GetDataObject();
            if (obj == null) return;
            foreach (var format in PreservedFormats)
            {
                try
                {
                    if (!obj.GetDataPresent(format, false)) continue;
                    var data = obj.GetData(format, false);
                    if (data != null) snapshot.Data[format] = data;
                }
                catch (Exception ex) when (ex is COMException or OutOfMemoryException or InvalidOperationException)
                {
                    // 某些私有格式读不出来，跳过
                }
            }
        });
        return snapshot;
    }

    public static void Restore(Snapshot snapshot)
    {
        Retry(() =>
        {
            if (snapshot.IsEmpty)
            {
                Clipboard.Clear();
                return;
            }
            var obj = new DataObject();
            foreach (var (format, data) in snapshot.Data) obj.SetData(format, data, false);
            Clipboard.SetDataObject(obj, copy: true);
        });
    }

    public static string? GetText()
    {
        string? text = null;
        Retry(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : null);
        return text;
    }

    private static void Retry(Action action)
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException)
            {
                // 剪贴板正被别的程序占用（CLIPBRD_E_CANT_OPEN），稍等重试
                Thread.Sleep(25);
            }
            catch (ExternalException)
            {
                Thread.Sleep(25);
            }
        }
    }
}
