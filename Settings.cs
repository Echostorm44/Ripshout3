using System.Text.Json.Serialization;
using Cascade.UI;
using Path = System.IO.Path;

namespace Ripshout3;

/// <summary>
/// The user's preferences, persisted through Cascade's <see cref="LocalStorage"/> (per user, under AppData) as plain
/// values with source-generated JSON, so they survive Native AOT. The AudioAddict listen key lives here and nowhere
/// else: it is the user's, so it never appears in code, logs or the repository.
/// </summary>
internal static class RipshoutSettings
{
    private static readonly SettingsJsonContext Json = SettingsJsonContext.Default;

    /// <summary>Playback volume, 0 to 1, as the user sees it on the slider.</summary>
    public static float Volume
    {
        get => Math.Clamp(LocalStorage.Get("volume", 0.5f, Json.Single), 0f, 1f);
        set => LocalStorage.Set("volume", Math.Clamp(value, 0f, 1f), Json.Single);
    }

    /// <summary>The DI.fm / RadioTunes premium listen key, or empty when the user has none.</summary>
    public static string ListenKey
    {
        get => LocalStorage.Get("listenKey", "", Json.String).Trim();
        set => LocalStorage.Set("listenKey", value.Trim(), Json.String);
    }

    public static StreamQuality StreamQuality
    {
        get => (StreamQuality)Math.Clamp(LocalStorage.Get("streamQuality", (int)StreamQuality.Mp3High, Json.Int32), 0, (int)StreamQuality.Aac64);
        set => LocalStorage.Set("streamQuality", (int)value, Json.Int32);
    }

    /// <summary>The <see cref="Station.Id"/>s the user has starred.</summary>
    public static HashSet<string> Favorites
    {
        get => [.. LocalStorage.Get("favorites", Array.Empty<string>(), Json.StringArray)];
        set => LocalStorage.Set("favorites", value.Order(StringComparer.Ordinal).ToArray(), Json.StringArray);
    }

    /// <summary>The networks the user has hidden from the station grid.</summary>
    public static HashSet<StationNetwork> HiddenNetworks
    {
        get => [.. LocalStorage.Get("hiddenNetworks", Array.Empty<int>(), Json.Int32Array).Select(n => (StationNetwork)n)];
        set => LocalStorage.Set("hiddenNetworks", value.Select(n => (int)n).Order().ToArray(), Json.Int32Array);
    }

    /// <summary>Whether the sound is muted. The volume is kept, so unmuting returns to it.</summary>
    public static bool Muted
    {
        get => LocalStorage.Get("muted", false, Json.Boolean);
        set => LocalStorage.Set("muted", value, Json.Boolean);
    }

    /// <summary>Whether tracks are saved while they play.</summary>
    public static bool RecordingEnabled
    {
        get => LocalStorage.Get("recordingEnabled", true, Json.Boolean);
        set => LocalStorage.Set("recordingEnabled", value, Json.Boolean);
    }

    /// <summary>Where a track is written while it plays. A finished track moves to <see cref="MusicFolder"/>.</summary>
    public static string RecordingFolder
    {
        get => Folder("recordingFolder", AppPaths.DefaultRecordingFolder);
        set => LocalStorage.Set("recordingFolder", value, Json.String);
    }

    /// <summary>Where finished, tagged tracks go, in a folder per genre.</summary>
    public static string MusicFolder
    {
        get => Folder("musicFolder", AppPaths.DefaultMusicFolder);
        set => LocalStorage.Set("musicFolder", value, Json.String);
    }

    /// <summary>Where artist backdrops are kept, a folder per artist.</summary>
    public static string BackdropFolder
    {
        get => Folder("backdropFolder", AppPaths.DefaultBackdropFolder);
        set => LocalStorage.Set("backdropFolder", value, Json.String);
    }

    /// <summary>Where album covers are kept, a folder per artist.</summary>
    public static string CoverFolder
    {
        get => Folder("coverFolder", AppPaths.DefaultCoverFolder);
        set => LocalStorage.Set("coverFolder", value, Json.String);
    }

    /// <summary>
    /// The user's own fanart.tv personal API key, or empty. With one, artists get up to
    /// <see cref="TrackLookup.MaxBackdrops"/> backdrops instead of TheAudioDB's four. Kept only in the
    /// user's settings, never in the source.
    /// </summary>
    public static string FanartTvKey
    {
        get => LocalStorage.Get("fanartTvKey", "", Json.String).Trim();
        set => LocalStorage.Set("fanartTvKey", value.Trim(), Json.String);
    }

    /// <summary>Whether the activity log is kept (what played, was recorded, which art was found).</summary>
    public static bool ActivityLogEnabled
    {
        get => LocalStorage.Get("activityLog", false, Json.Boolean);
        set => LocalStorage.Set("activityLog", value, Json.Boolean);
    }

    /// <summary>Whether the error log is kept.</summary>
    public static bool ErrorLogEnabled
    {
        get => LocalStorage.Get("errorLog", true, Json.Boolean);
        set => LocalStorage.Set("errorLog", value, Json.Boolean);
    }

    // A folder setting, falling back to the default when unset or blank.
    private static string Folder(string key, string fallback)
    {
        string value = LocalStorage.Get(key, "", Json.String).Trim();
        return value.Length > 0 ? value : fallback;
    }

    /// <summary>The station playing when RipShout last closed, so it can offer it again.</summary>
    public static string LastStationId
    {
        get => LocalStorage.Get("lastStationId", "", Json.String);
        set => LocalStorage.Set("lastStationId", value, Json.String);
    }
}

/// <summary>Where RipShout keeps its own files: the station list, downloaded artwork and logs.</summary>
internal static class AppPaths
{
    public static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ripshout3");

    public static readonly string CacheFolder = Path.Combine(DataFolder, "Cache");

    public static readonly string ArtFolder = Path.Combine(DataFolder, "Art");

    public static readonly string LogFolder = Path.Combine(DataFolder, "Logs");

    public static readonly string DefaultRecordingFolder = Path.Combine(DataFolder, "Recording");

    public static readonly string DefaultMusicFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "RipShout");

    public static readonly string DefaultBackdropFolder = Path.Combine(DataFolder, "Backdrops");

    public static readonly string DefaultCoverFolder = Path.Combine(DataFolder, "Covers");
}

/// <summary>
/// Remembers where the window was: its size (logical pixels), its top-left (physical, virtual-desktop pixels, which
/// survive monitors with different DPI), whether it was maximized and on which monitor. A position no longer on any
/// monitor is ignored, so a disconnected screen never strands the window off-screen.
/// </summary>
internal static class WindowPlacement
{
    private static readonly SettingsJsonContext Json = SettingsJsonContext.Default;

    /// <summary>The size to open at, or null for the default.</summary>
    public static Size? SavedSize
    {
        get
        {
            int width = LocalStorage.Get("windowWidth", 0, Json.Int32);
            int height = LocalStorage.Get("windowHeight", 0, Json.Int32);
            return width >= 400 && height >= 300 ? new Size(width, height) : null;
        }
    }

    public static void Restore()
    {
        if (!LocalStorage.Exists("windowX"))
        {
            return;
        }

        int x = LocalStorage.Get("windowX", 0, Json.Int32);
        int y = LocalStorage.Get("windowY", 0, Json.Int32);
        // The title bar's left end must land on a monitor that is still there.
        var titleBar = new Point(x + 40, y + 20);
        bool onScreen = App.Screens.All.Any(screen => screen.Bounds.Contains(titleBar));
        if (onScreen)
        {
            App.Window.SetScreenPosition(x, y);
        }

        if (!LocalStorage.Get("windowMaximized", false, Json.Boolean))
        {
            return;
        }

        // Maximized on another monitor than the restored place: open on that monitor, near its top-left, so it
        // maximizes there and un-maximizes there too.
        var maximizedAt = new Point(
            LocalStorage.Get("windowMaximizedX", x + 40, Json.Int32), LocalStorage.Get("windowMaximizedY", y + 20, Json.Int32));
        ScreenInfo? monitor = App.Screens.All.FirstOrDefault(screen => screen.Bounds.Contains(maximizedAt));
        if (monitor is not null && !(onScreen && monitor.Bounds.Contains(titleBar)))
        {
            App.Window.SetScreenPosition((int)monitor.WorkArea.X + 40, (int)monitor.WorkArea.Y + 40);
        }
        App.Window.Maximize();
    }

    public static void Save()
    {
        // Fullscreen and minimized are moments, not places to come back to.
        if (App.Window.IsFullScreen || App.Window.IsMinimized)
        {
            return;
        }

        bool maximized = App.Window.IsMaximized;
        LocalStorage.Set("windowMaximized", maximized, Json.Boolean);
        if (maximized)
        {
            // Keep the restored size and place from before it was maximized; note only which monitor it fills.
            Rect filled = App.Window.ScreenBounds;
            LocalStorage.Set("windowMaximizedX", (int)(filled.X + filled.Width / 2), Json.Int32);
            LocalStorage.Set("windowMaximizedY", (int)(filled.Y + filled.Height / 2), Json.Int32);
            return;
        }

        // The client size, which is what the window is created at; the outer size would grow by the frame each run.
        Rect client = App.Window.ClientBounds;
        Rect screen = App.Window.ScreenBounds;
        // Rounded: at a fractional DPI scale the logical size isn't whole, and truncating shrank it a pixel a run.
        LocalStorage.Set("windowWidth", (int)MathF.Round(client.Width), Json.Int32);
        LocalStorage.Set("windowHeight", (int)MathF.Round(client.Height), Json.Int32);
        LocalStorage.Set("windowX", (int)screen.X, Json.Int32);
        LocalStorage.Set("windowY", (int)screen.Y, Json.Int32);
    }
}

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
