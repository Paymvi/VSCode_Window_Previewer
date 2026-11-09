using System.Text.Json;

namespace ProjectPreviews.Core;

public enum ProjectKind { Folder, Workspace, Empty, Unknown }

/// <summary>
/// What a VS Code window has open. <see cref="Key"/> is the normalized full path (or remote URI) and is
/// null when we only know a name; local alias overrides need a key, so they never attach to a guess.
/// </summary>
public sealed record ProjectIdentity(ProjectKind Kind, string? Key, string? Path, string Name, bool Certain)
{
    public static ProjectIdentity Empty { get; } = new(ProjectKind.Empty, null, null, "", true);
}

public enum AliasSource { Override, ClaudeWorkers, ProjectName, Fallback }

public static class Aliases
{
    /// <summary>Full path, no trailing separator, lower-case (Windows paths are case-insensitive). Non-file URIs are just lower-cased.</summary>
    public static string NormalizeKey(string pathOrUri)
    {
        string s = pathOrUri.Trim();
        if (s.Contains("://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsFile) s = FileUriToPath(u);
            else return s.TrimEnd('/').ToLowerInvariant();
        }
        try { s = System.IO.Path.GetFullPath(s.Replace('/', '\\')); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        if (s.Length > 3) s = s.TrimEnd('\\');
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// VS Code writes "file:///c%3A/Users/x" (colon escaped), which Uri.LocalPath turns into "c:\c:\Users\x";
    /// unescape the path ourselves. UNC hosts become \\host\share.
    /// </summary>
    public static string FileUriToPath(Uri u)
    {
        string p = Uri.UnescapeDataString(u.AbsolutePath).Replace('/', '\\');
        if (!string.IsNullOrEmpty(u.Host)) return @"\\" + u.Host + p;
        return p.Length >= 3 && p[0] == '\\' && p[2] == ':' ? p[1..] : p;
    }

    /// <summary>Same rule Claude Workers uses (Board.FriendlyName): underscores become spaces.</summary>
    public static string Readable(string name)
    {
        string s = name.Replace('_', ' ');
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        s = s.Trim();
        return s.Length == 0 ? name : s;
    }

    /// <summary>Precedence: local override (by full path) → Claude Workers alias (by folder name, as Claude Workers keys them) → readable name.</summary>
    public static (string Alias, AliasSource Source) Resolve(ProjectIdentity id, IReadOnlyDictionary<string, ProjectPref> prefs, IReadOnlyDictionary<string, string> claudeWorkers)
    {
        if (id.Key is not null && prefs.TryGetValue(id.Key, out var pref) && !string.IsNullOrWhiteSpace(pref.Alias))
            return (pref.Alias.Trim(), AliasSource.Override);
        if (id.Kind == ProjectKind.Empty)
            return ("Empty window", AliasSource.Fallback);
        if (id.Name.Length == 0)
            return ("Visual Studio Code", AliasSource.Fallback);
        if (claudeWorkers.TryGetValue(id.Name, out var cw) && !string.IsNullOrWhiteSpace(cw))
            return (cw.Trim(), AliasSource.ClaudeWorkers);
        return (Readable(id.Name), id.Certain || id.Kind != ProjectKind.Unknown ? AliasSource.ProjectName : AliasSource.Fallback);
    }

    /// <summary>A stable, pleasant hue per project for the default accent, so colors don't shuffle between launches.</summary>
    public static string DefaultAccent(string seed)
    {
        string[] palette = ["#3B82F6", "#8B5CF6", "#EC4899", "#F97316", "#10B981", "#06B6D4", "#EAB308", "#EF4444", "#6366F1", "#14B8A6"];
        uint h = 2166136261;
        foreach (char c in seed.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return palette[h % (uint)palette.Length];
    }
}

/// <summary>Read-only view of Claude Workers' (AI Status Indicator) "projectAliases", re-read only when the file changes.</summary>
public sealed class ClaudeWorkersAliases(string configPath)
{
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".claude\status-indicator\config.json");

    private DateTime _stamp = DateTime.MinValue;
    private IReadOnlyDictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string ConfigPath => configPath;
    public bool Found { get; private set; }

    public IReadOnlyDictionary<string, string> Current
    {
        get
        {
            try
            {
                var info = new FileInfo(configPath);
                Found = info.Exists;
                if (!info.Exists) return _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (info.LastWriteTimeUtc == _stamp) return _cache;
                // FileShare.ReadWrite|Delete: never block Claude Workers from saving its own file.
                using var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                _cache = Parse(reader.ReadToEnd());
                _stamp = info.LastWriteTimeUtc;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Mid-save or locked: keep the last good copy.
            }
            return _cache;
        }
    }

    internal static Dictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("projectAliases", out var aliases) && aliases.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in aliases.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()) && p.Name.Trim().Length > 0)
                        result[p.Name.Trim()] = p.Value.GetString()!.Trim();
            }
        }
        catch (JsonException) { }
        return result;
    }
}

/// <summary>Pieces of VS Code's default window title: "[●] activeEditorShort - rootName [- profile] - Visual Studio Code".</summary>
public sealed record TitleParts(string? ActiveFile, string? Root, bool IsWorkspace, bool RootConfirmed);

public static class VsTitle
{
    private const string Sep = " - ";

    /// <summary>
    /// Splits a title. <paramref name="knownRoots"/> (folder names VS Code itself lists as open) decides which
    /// part is the root when the title alone is ambiguous; without a match the default layout is assumed but not confirmed.
    /// </summary>
    public static TitleParts? Parse(string title, ICollection<string> knownRoots)
    {
        var parts = title.Split(Sep).Select(p => p.Trim()).ToList();
        int app = parts.FindLastIndex(p => p.StartsWith("Visual Studio Code", StringComparison.Ordinal));
        if (app < 0) return null; // custom window.title without the product name: can't parse
        parts = parts.Take(app).Select(p => p.TrimStart('●', '•', ' ')).Where(p => p.Length > 0).ToList();
        if (parts.Count > 0 && parts[0].StartsWith("[Extension Development Host]", StringComparison.Ordinal))
            parts[0] = parts[0]["[Extension Development Host]".Length..].Trim();
        if (parts.Count == 0) return new TitleParts(null, null, false, true);

        // The rightmost part that is a workspace or a folder VS Code says is open is the root.
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            bool ws = parts[i].EndsWith("(Workspace)", StringComparison.Ordinal);
            if (ws || knownRoots.Contains(parts[i]))
            {
                string? file = i > 0 ? string.Join(Sep, parts.Take(i)) : null;
                string root = ws ? parts[i][..^"(Workspace)".Length].Trim() : parts[i];
                return new TitleParts(file, root, ws, true);
            }
        }
        // Unconfirmed: default layout is file - root [- profile].
        return parts.Count == 1
            ? new TitleParts(null, parts[0], false, false)
            : new TitleParts(parts[0], parts[1], false, false);
    }
}
