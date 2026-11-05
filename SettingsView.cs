using System.Diagnostics;
using Cascade.UI;

namespace Ripshout3;

/// <summary>The Settings page. Every change applies and saves at once; there is no OK button to forget.</summary>
internal sealed class SettingsView : Component
{
    private static readonly IReadOnlyList<SelectOption<StreamQuality>> QualityOptions =
    [
        new(StreamQuality.Mp3High, "320 kbps MP3 (best)"),
        new(StreamQuality.Aac128, "128 kbps AAC"),
        new(StreamQuality.Aac64, "64 kbps AAC (least data)"),
    ];

    private readonly Action onChanged;
    private string listenKey = RipshoutSettings.ListenKey;
    private string fanartKey = RipshoutSettings.FanartTvKey;
    private StreamQuality quality = RipshoutSettings.StreamQuality;
    private bool recording = RipshoutSettings.RecordingEnabled;
    private bool activityLog = RipshoutSettings.ActivityLogEnabled;
    private bool errorLog = RipshoutSettings.ErrorLogEnabled;

    public SettingsView(Action onChanged)
    {
        this.onChanged = onChanged;
    }

    protected override Node Render() =>
        new ScrollView(
            new Column(
                spacing: 22,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                children:
                [
                    new Label("settings").Style(new TextStyle(34, FontWeight.Light, 1.1f)).Color(MainView.TextPrimary),
                    Section("DI.FM, RadioTunes and their sister stations",
                        new Label("These need a premium listen key. You'll find yours on the network's website, under Settings → Listening.")
                            .FontSize(13).Color(MainView.TextSecondary).Wrap(TextWrap.Wrap),
                        new PasswordInput(Bind(listenKey, value =>
                        {
                            listenKey = value;
                            RipshoutSettings.ListenKey = value;
                            onChanged();
                        }), placeholder: "Listen key").ShowToggle(true).AccessibleLabel("Listen key"),
                        new Label("Stream quality").Color(MainView.TextPrimary),
                        new Select<StreamQuality>(Bind(quality, value =>
                        {
                            quality = value;
                            RipshoutSettings.StreamQuality = value;
                            onChanged();
                        }), QualityOptions).AccessibleLabel("Stream quality"),
                        new Label("Takes effect the next time a station starts.").FontSize(12).Color(MainView.TextSecondary)),
                    Section("1.FM",
                        new Label("1.FM's stations are free and need no key.").FontSize(13).Color(MainView.TextSecondary)),
                    Section("Recording",
                        new Toggle(Bind(recording, value =>
                        {
                            recording = value;
                            RipshoutSettings.RecordingEnabled = value;
                        }), "Record tracks as they play",
                            "Each track is saved as the station sends it, once it has played from start to finish. Ads and clips under a minute aren't kept.")
                            .AccessibleLabel("Record tracks as they play"),
                        FolderRow("While recording", "Tracks are written here while they play.",
                            () => RipshoutSettings.RecordingFolder, path => RipshoutSettings.RecordingFolder = path),
                        FolderRow("Finished tracks", "Tagged, with the album cover, in a folder per genre.",
                            () => RipshoutSettings.MusicFolder, path => RipshoutSettings.MusicFolder = path)),
                    Section("Pictures",
                        FolderRow("Artist backdrops", "Pictures of the artist shown behind Now Playing, a folder per artist.",
                            () => RipshoutSettings.BackdropFolder, path => RipshoutSettings.BackdropFolder = path),
                        FolderRow("Album covers", "A folder per artist.",
                            () => RipshoutSettings.CoverFolder, path => RipshoutSettings.CoverFolder = path),
                        new Label("More backdrops").Color(MainView.TextPrimary),
                        new Label($"TheAudioDB has up to four pictures of an artist. With a free fanart.tv personal API key (fanart.tv → your profile → API), RipShout adds theirs, up to {TrackLookup.MaxBackdrops} per artist, most liked first.")
                            .FontSize(12).Color(MainView.TextSecondary).Wrap(TextWrap.Wrap),
                        new PasswordInput(Bind(fanartKey, value =>
                        {
                            fanartKey = value;
                            RipshoutSettings.FanartTvKey = value;
                        }), placeholder: "fanart.tv personal API key").ShowToggle(true).AccessibleLabel("fanart.tv personal API key")),
                    Section("Logs",
                        new Toggle(Bind(activityLog, value =>
                        {
                            activityLog = value;
                            RipshoutSettings.ActivityLogEnabled = value;
                        }), "Keep an activity log", "What played, what was recorded and which pictures were found.")
                            .AccessibleLabel("Keep an activity log"),
                        new Toggle(Bind(errorLog, value =>
                        {
                            errorLog = value;
                            RipshoutSettings.ErrorLogEnabled = value;
                        }), "Keep an error log").AccessibleLabel("Keep an error log"),
                        new Row(
                            spacing: 12,
                            crossAxisAlignment: CrossAxisAlignment.Center,
                            children:
                            [
                                new Label($"Each log stops at {Log.MaxBytes / (1024 * 1024)} MB; the older half is kept as *.old.log.")
                                    .FontSize(12).Color(MainView.TextSecondary).Expand(),
                                new Button("Open", () => OpenFolder(AppPaths.LogFolder)).AccessibleLabel("Open the log folder"),
                            ])),
                    new Label($"RipShout {AppVersion}").FontSize(12).Color(MainView.TextSecondary),
                ])
                .MaxWidth(620)
                .Padding(EdgeInsets.Only(top: 22, left: 28, right: 28, bottom: 28)));

    // The released version CI stamps into the build, as major.minor.patch.
    private static string AppVersion =>
        typeof(SettingsView).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "";

    // A folder setting: its name and purpose, where it is now, and buttons to change or open it.
    private Node FolderRow(string title, string hint, Func<string> current, Action<string> set) =>
        new Column(
            spacing: 4,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children:
            [
                new Label(title).Color(MainView.TextPrimary),
                new Label(hint).FontSize(12).Color(MainView.TextSecondary).Wrap(TextWrap.Wrap),
                new Row(
                    spacing: 8,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children:
                    [
                        new Label(current()).FontSize(13).Color(MainView.TextSecondary).MaxLines(1).Overflow(TextOverflow.Ellipsis)
                            .Selectable(true).Expand(),
                        new Button("Choose…", () => { _ = ChooseFolderAsync(title, current, set); }).AccessibleLabel($"Choose the {title.ToLowerInvariant()} folder"),
                        new Button("Open", () => OpenFolder(current())).AccessibleLabel($"Open the {title.ToLowerInvariant()} folder"),
                    ]),
            ]);

    private async Task ChooseFolderAsync(string title, Func<string> current, Action<string> set)
    {
        string? chosen = await FolderPicker.OpenAsync(title, Directory.Exists(current()) ? current() : null);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return;
        }
        set(chosen);
        Log.Info($"{title} folder set to {chosen}");
        Invalidate();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error($"Couldn't open {path}", ex);
        }
    }

    private static Node Section(string title, params Node[] content) =>
        new Column(
            spacing: 10,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children:
            [
                new Label(title).FontSize(15).Bold().Color(MainView.TextPrimary),
                new Column(spacing: 10, crossAxisAlignment: CrossAxisAlignment.Stretch, children: content)
                    .Padding(EdgeInsets.All(16))
                    .Background(MainView.PanelBackground)
                    .CornerRadius(12)
                    .Border(MainView.Hairline, 1, 12),
            ]);
}
