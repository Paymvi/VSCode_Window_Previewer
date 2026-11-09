using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectPreviews.Core;

public sealed class ProjectPref
{
    public string? Alias { get; set; }
    public string? Color { get; set; }
    /// <summary>Original-case path, for display only (the dictionary key is normalized).</summary>
    public string? Path { get; set; }
}

public enum ThemeChoice { System, Light, Dark }

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool HoverEnabled { get; set; } = true;
    public bool ShortcutEnabled { get; set; } = true;
    public string Shortcut { get; set; } = "Ctrl+Alt+Space";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>Blur radius in DIPs, 0–40.</summary>
    public double Blur { get; set; } = 10;
    /// <summary>Black overlay opacity, 0–0.9.</summary>
    public double Darkness { get; set; } = 0.45;
    public Dictionary<string, ProjectPref> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Sanitize()
    {
        Blur = double.IsFinite(Blur) ? Math.Clamp(Blur, 0, 40) : 10;
        Darkness = double.IsFinite(Darkness) ? Math.Clamp(Darkness, 0, 0.9) : 0.45;
        if (!Enum.IsDefined(Theme)) Theme = ThemeChoice.System;
        if (string.IsNullOrWhiteSpace(Shortcut)) Shortcut = "Ctrl+Alt+Space";
        // Re-key through NormalizeKey so hand edits ("C:/Foo/") still match, and drop empty entries.
        var projects = new Dictionary<string, ProjectPref>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Projects ?? [])
        {
            if (string.IsNullOrWhiteSpace(k) || v is null) continue;
            if (v.Color is not null && !IsColor(v.Color)) v.Color = null;
            if (string.IsNullOrWhiteSpace(v.Alias)) v.Alias = null;
            if (v.Alias is null && v.Color is null) continue;
            projects[Aliases.NormalizeKey(k)] = v;
        }
        Projects = projects;
    }

    public static bool IsColor(string s) =>
        s.Length == 7 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
}

public static class SettingsStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPreviews", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "Ctrl+Alt" readable; never served as HTML
    };

    /// <summary>
    /// Loads settings. A damaged file is copied aside (settings.corrupt-*.json) before defaults are used, so a
    /// later save can't destroy the only copy. <paramref name="problem"/> says what happened, for the UI.
    /// </summary>
    public static AppSettings Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return new AppSettings();
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"Couldn't read settings ({e.Message}). Using defaults for now; the file was left untouched.";
            return new AppSettings { Version = -1 }; // -1: don't save over a file we couldn't read
        }
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(text, Options) ?? throw new JsonException("empty");
            s.Projects = new Dictionary<string, ProjectPref>(s.Projects ?? [], StringComparer.OrdinalIgnoreCase);
            s.Sanitize();
            return s;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            string backup = Path.Combine(Path.GetDirectoryName(path)!, $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(path, backup, overwrite: false); problem = $"Settings file was damaged. A copy was kept at {backup}; defaults are in use."; }
            catch (Exception ce) when (ce is IOException or UnauthorizedAccessException)
            {
                problem = "Settings file was damaged and couldn't be backed up, so it won't be overwritten. Defaults are in use.";
                return new AppSettings { Version = -1 };
            }
            return new AppSettings();
        }
    }

    /// <summary>Writes atomically (temp file, then replace) so a crash mid-write never leaves a half file.</summary>
    public static void Save(string path, AppSettings s)
    {
        if (s.Version < 0) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
