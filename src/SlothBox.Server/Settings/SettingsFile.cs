using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using SlothBox.Core;

namespace SlothBox.Server.Settings;

// Spec 4.3 step 2 and 11. Read on first use. A missing file is written with the defaults and a new phone token;
// a file without a token gets one added and saved. The database password never goes in this file.
public sealed class SettingsFile(string path) : ISettingsStore
{
    // Hand-edited files may use comments, trailing commas and any name case.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private SlothBoxSettings? _current;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlothBox",
            "settings.json");

    // A file that cannot be read is reported on every read until it is fixed; it is never overwritten.
    public SlothBoxSettings Current
    {
        get
        {
            lock (_gate)
                return _current ??= LoadOrCreate(path);
        }
    }

    // Setup, the Settings page and New link save through here. A file that cannot be read is reported as on every read,
    // and stays as it is.
    public SlothBoxSettings Update(Func<SlothBoxSettings, SlothBoxSettings> change)
    {
        lock (_gate)
        {
            var updated = change(_current ??= LoadOrCreate(path));
            Save(path, updated);
            return _current = updated;
        }
    }

    // 128 random bits, URL-safe: the phone link carries it (spec 7.9).
    public static string NewPhoneToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static SlothBoxSettings LoadOrCreate(string path)
    {
        var settings = new SlothBoxSettings();
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<SlothBoxSettings>(File.ReadAllText(path), ReadOptions)
                    ?? settings;
            }
            catch (JsonException e)
            {
                throw new InvalidOperationException($"{path} could not be read: {e.Message}", e);
            }
            if (settings.Phone.Token.Length > 0)
                return settings;
        }
        settings = settings with { Phone = settings.Phone with { Token = NewPhoneToken() } };
        Save(path, settings);
        return settings;
    }

    // Written beside the file, then moved over it, so a crash never leaves half a settings file.
    private static void Save(string path, SlothBoxSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, WriteOptions));
        File.Move(temp, path, overwrite: true);
    }
}
