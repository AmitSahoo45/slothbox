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
        settings = settings with
        {
            Phone = settings.Phone with { Token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)) }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, WriteOptions));
        return settings;
    }
}
