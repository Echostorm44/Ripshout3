using System.Text;
using Path = System.IO.Path;

namespace Ripshout3;

/// <summary>
/// RipShout's two logs, both plain text in <see cref="AppPaths.LogFolder"/>: the activity log (what played, what
/// was recorded, what art was found) and the error log. Each is switched on or off in Settings and kept small: past
/// <see cref="MaxBytes"/> the file becomes "*.old.log" (replacing the previous one), so a log never takes more than
/// twice that. The listen key is scrubbed from every line, since stream URLs carry it.
/// </summary>
internal static class Log
{
    /// <summary>The most one log file grows to before it rolls over.</summary>
    public const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();

    public static string ActivityPath => Path.Combine(AppPaths.LogFolder, "activity.log");

    public static string ErrorPath => Path.Combine(AppPaths.LogFolder, "errors.log");

    /// <summary>Records something that happened, when the activity log is on.</summary>
    public static void Info(string message)
    {
        if (RipshoutSettings.ActivityLogEnabled)
        {
            Write(ActivityPath, message);
        }
    }

    /// <summary>Records a failure, when the error log is on. Also noted in the activity log.</summary>
    public static void Error(string message, Exception? exception = null)
    {
        string text = exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}";
        if (RipshoutSettings.ErrorLogEnabled)
        {
            Write(ErrorPath, exception is null ? text : $"{text}{Environment.NewLine}{exception.StackTrace}");
        }
        Info("ERROR " + text);
    }

    /// <summary>Appends one timestamped line. Logging never throws: a full disk must not stop the music.</summary>
    internal static void Write(string path, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Redact(message)}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new FileInfo(path);
                if (file.Exists && file.Length + line.Length > MaxBytes)
                {
                    File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report it.
            }
        }
    }

    /// <summary>The text with the listen key replaced, wherever it appears.</summary>
    internal static string Redact(string text)
    {
        foreach ((string key, string name) in new[] { (RipshoutSettings.ListenKey, "[listen key]"), (RipshoutSettings.FanartTvKey, "[fanart.tv key]") })
        {
            if (key.Length >= 8)
            {
                text = text.Replace(key, name, StringComparison.OrdinalIgnoreCase);
            }
        }
        return text;
    }
}
