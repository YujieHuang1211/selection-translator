using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HuaCi.Core;

/// <summary>Loads/saves settings.json under the data directory; the API key is DPAPI-encrypted for the current user.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "settings.json");
    }

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HuaCi");

    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            settings = new AppSettings();
        }

        // JSON 反序列化会丢掉字典的忽略大小写比较器
        settings.CopyKeyOverrides = new Dictionary<string, string>(settings.CopyKeyOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        settings.BlacklistProcesses ??= new();
        if (string.IsNullOrWhiteSpace(settings.Model)) settings.Model = AppSettings.DefaultModel;
        settings.ApiKey = Unprotect(settings.ApiKeyProtected);
        Current = settings;
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.ApiKeyProtected = Protect(settings.ApiKey);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
        Current = settings;
        Changed?.Invoke(settings);
    }

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedB64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
