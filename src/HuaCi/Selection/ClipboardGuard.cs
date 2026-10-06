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

    /// <summary>Returns null if the clipboard could not be read (e.g. locked by another app) — then never restore,
    /// because "restoring" an empty snapshot would wipe the user's clipboard.</summary>
    public static Snapshot? Capture()
    {
        var snapshot = new Snapshot();
        bool hadUnsupportedContent = false;
        bool ok = Retry(() =>
        {
            snapshot.Data.Clear();
            var obj = Clipboard.GetDataObject();
            if (obj == null) return;
            hadUnsupportedContent = obj.GetFormats(false).Length > 0;
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
        // 剪贴板里有内容但都是我们备份不了的私有格式：同样不要“恢复”成空
        if (!ok || (snapshot.IsEmpty && hadUnsupportedContent)) return null;
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

    private static bool Retry(Action action)
    {
        for (int i = 0; i < 8; i++)
        {
            try
            {
                action();
                return true;
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
        return false;
    }
}
