using System.Diagnostics;

namespace ProjectPreviews.Platform;

/// <summary>Opt-in diagnostics: set PROJECT_PREVIEWS_DEBUG=1 to append to %LOCALAPPDATA%\ProjectPreviews\debug.log. Off by default.</summary>
public static class Log
{
    private static readonly string? FilePath = Environment.GetEnvironmentVariable("PROJECT_PREVIEWS_DEBUG") == "1"
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectPreviews", "debug.log")
        : null;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Write(string message)
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} +{Clock.ElapsedMilliseconds}ms {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}
