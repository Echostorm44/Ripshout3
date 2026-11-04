using System.Reflection;
using Cascade.UI;
using Updater = Cascade.UI.Installer.Update.Updater;
using SharpImage.Formats;
using ImageFrame = SharpImage.Image.ImageFrame;

namespace Ripshout3;

/// <summary>
/// RipShout's one window, laid out like the Zune player it grew from: a narrow rail on the left, and either the
/// station that is playing over a full-window backdrop, the grid of stations, or the settings.
/// </summary>
internal sealed class MainView : Component
{
    // ── Palette (Apple dark, with the RipShout icon's yellow for accents) ─────────────────
    internal static readonly ColorValue Accent = new("#F3B533");
    internal static readonly ColorValue WindowBackground = new("#1E1E1E");
    internal static readonly ColorValue RailBackground = new("#161618");
    internal static readonly ColorValue PanelBackground = new("#2C2C2E");
    internal static readonly ColorValue Hairline = new("#FFFFFF1A");
    internal static readonly ColorValue TextPrimary = new("#FFFFFF");
    internal static readonly ColorValue TextSecondary = new("#98989E");

    // Outlines text drawn over the backdrop, as the old RipShout did. Opaque: neighbouring
    // glyphs' outlines overlap, and a translucent outline would darken where they do.
    internal static readonly ColorValue TextOutlineColor = new("#000000");
    private static readonly ColorValue Scrim = new("#000000");

    private const float TileWidth = 168f;
    private const float TileArtSize = 152f;
    private const float TileHeight = 214f;
    private const float TileGap = 16f;

    private enum View
    {
        NowPlaying,
        Stations,
        Settings,
    }

    private readonly RadioPlayer player = new();
    private readonly ArtworkCache artwork = new();
    private readonly CancellationTokenSource lifetime = new();

    private View page = View.Stations;
    private List<Station> stations = StationCatalog.LoadCached();
    private bool refreshingStations;
    private string search = "";
    private bool favoritesOnly;
    private HashSet<string> favorites = RipshoutSettings.Favorites;
    private HashSet<StationNetwork> hiddenNetworks = RipshoutSettings.HiddenNetworks;
    private float volume = RipshoutSettings.Volume;
    private bool muted = RipshoutSettings.Muted;
    private ImageSource? defaultBackdrop;

    // The track on the Now Playing page: what was looked up for it, its cover and the backdrop showing.
    private const int BackdropSeconds = 30;
    private string? detailsFor;
    private TrackDetails? details;
    private ImageSource? cover;
    private ImageSource? backdrop;
    private int backdropIndex;

    // A newer release offered in the banner, and what the update is doing once accepted.
    private string? availableUpdate;
    private string? updateStatus;

    protected override Task OnMounted()
    {
        WindowPlacement.Restore();
        App.Window.OnCloseRequested = () =>
        {
            WindowPlacement.Save();
            RipshoutSettings.Volume = volume;
            return CloseResult.Propagate;
        };

        player.Muted = muted;
        player.Changed += OnPlayerChanged;
        artwork.ArtworkReady += Invalidate;
        _ = RotateBackdropsAsync();
        _ = RefreshStationsAsync();
        _ = LoadDefaultBackdropAsync();
        if (StartupOptions.PlayStationId is { } id && stations.Find(s => s.Id == id) is { } station)
        {
            _ = PlayStationAsync(station);
        }
        if (Updater.IsConfigured)
        {
            // This launch reached a healthy state: defuse the update shim's crash rollback for this version.
            Updater.MarkHealthy();
            _ = CheckForUpdateAsync();
        }
        return Task.CompletedTask;
    }

    protected override void OnUnmounted()
    {
        lifetime.Cancel();
        player.Dispose();
        defaultBackdrop?.Dispose();
        cover?.Dispose();
        backdrop?.Dispose();
    }

    // ── Track details: cover and backdrops ───────────────────────────

    private void OnPlayerChanged()
    {
        string? key = player.Station is { } station && player.StreamTitle is { } title ? $"{station.Id}\n{title}" : null;
        if (key != detailsFor)
        {
            detailsFor = key;
            details = null;
            SetCover(null);
            SetBackdrop(null);
            if (key is not null)
            {
                _ = LoadDetailsAsync(player.Station!, player.StreamTitle!, key);
            }
        }
        Invalidate();
    }

    private async Task LoadDetailsAsync(Station station, string streamTitle, string key)
    {
        (string artist, string title) = SplitStreamTitle(streamTitle);
        if (artist.Length == 0)
        {
            return;
        }

        TrackDetails found = await TrackLookup.GetAsync(station, artist, title);
        if (key != detailsFor)
        {
            return;
        }
        details = found;
        backdropIndex = 0;
        Invalidate();

        ImageSource? coverImage = found.CoverPath is { } coverPath ? await LoadPictureAsync(coverPath, 360) : null;
        ImageSource? backdropImage = found.BackdropPaths.Count > 0 ? await LoadPictureAsync(found.BackdropPaths[0], 1920) : null;
        if (key != detailsFor)
        {
            coverImage?.Dispose();
            backdropImage?.Dispose();
            return;
        }
        SetCover(coverImage);
        SetBackdrop(backdropImage);
        Invalidate();
    }

    // Shows each of the artist's backdrops in turn.
    private async Task RotateBackdropsAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(BackdropSeconds), lifetime.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (details is not { BackdropPaths.Count: > 1 } current || page != View.NowPlaying)
            {
                continue;
            }
            string? key = detailsFor;
            backdropIndex = (backdropIndex + 1) % current.BackdropPaths.Count;
            ImageSource? next = await LoadPictureAsync(current.BackdropPaths[backdropIndex], 1920);
            if (key != detailsFor)
            {
                next?.Dispose();
                continue;
            }
            SetBackdrop(next);
            Invalidate();
        }
    }

    private void SetCover(ImageSource? image)
    {
        cover?.Dispose();
        cover = image;
    }

    private void SetBackdrop(ImageSource? image)
    {
        backdrop?.Dispose();
        backdrop = image;
    }

    // Decodes a picture off the UI thread, no larger than it is shown, or null if it can't be read.
    private static async Task<ImageSource?> LoadPictureAsync(string path, int maxSize)
    {
        (byte[] Pixels, int Width, int Height)? decoded = await Task.Run<(byte[] Pixels, int Width, int Height)?>(() =>
        {
            try
            {
                using ImageFrame frame = FormatRegistry.Read(path);
                if (frame.Columns <= maxSize && frame.Rows <= maxSize)
                {
                    return (ImagePixels.ToRgba(frame), (int)frame.Columns, (int)frame.Rows);
                }
                using ImageFrame small = SharpImage.Transform.Resize.Thumbnail(frame, maxSize, maxSize);
                return (ImagePixels.ToRgba(small), (int)small.Columns, (int)small.Rows);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"Couldn't read the picture {path}", ex);
                return null;
            }
        });
        return decoded is { } picture ? ImageSource.FromPixels(picture.Pixels, picture.Width, picture.Height) : null;
    }

    protected override Node Render() =>
        new Stack(children:
        [
            new Row(crossAxisAlignment: CrossAxisAlignment.Stretch, children:
            [
                Rail(),
                page switch
                {
                    View.NowPlaying => NowPlaying(),
                    View.Settings => new SettingsView(OnSettingsChanged).Expand(),
                    _ => StationsPage(),
                },
            ]).Expand(),
            availableUpdate is null ? Node.Empty : UpdateBanner().PassThroughHits(),
        ]).Background(WindowBackground);

    // ── Updates ───────────────────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var result = await Task.Run(() => Updater.CheckNowAsync());
            if (result.IsAvailable && result.Version is { Length: > 0 })
            {
                availableUpdate = result.Version;
                Invalidate();
            }
        }
        catch
        {
            // A failed check is invisible: no network, no release yet.
        }
    }

    private async Task InstallUpdateAsync()
    {
        updateStatus = "Downloading the update…";
        Invalidate();
        try
        {
            await Task.Run(() => Updater.DownloadAsync());
            updateStatus = "Restarting RipShout…";
            Invalidate();
            // Stopping finishes any recording in progress before the files are swapped.
            player.Stop();
            // Hands over to the update shim, which waits for RipShout to exit, swaps the files and relaunches.
            Updater.ApplyAndRestart();
        }
        catch
        {
            // It will be offered again on the next check.
            updateStatus = null;
            availableUpdate = null;
            Invalidate();
        }
    }

    private Node UpdateBanner() =>
        new Column(
            crossAxisAlignment: CrossAxisAlignment.End,
            children:
            [
                new Row(
                    spacing: 12,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children: updateStatus is not null
                        ? [new Label(updateStatus).FontSize(13).Color(TextPrimary)]
                        :
                        [
                            new Label($"RipShout {availableUpdate} is available").FontSize(13).Color(TextPrimary),
                            new Button("Later", () => { availableUpdate = null; Invalidate(); }).Variant("ghost").Height(28),
                            new Button("Update", () => _ = InstallUpdateAsync()).Height(28),
                        ])
                    .Padding(EdgeInsets.Symmetric(horizontal: 14, vertical: 8))
                    .Background(PanelBackground)
                    .CornerRadius(12)
                    .Border(Hairline, 1, 12),
            ]).Padding(EdgeInsets.Only(top: 18, right: 18));

    // ── Rail ──────────────────────────────────────────────────────────

    private Node Rail() =>
        new Column(
            spacing: 6,
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                RailButton(Icons.Disc3, "playing", View.NowPlaying),
                RailButton(Icons.Radio, "stations", View.Stations),
                new Spacer(),
                RailButton(Icons.Settings, "settings", View.Settings),
            ])
            .Padding(EdgeInsets.Symmetric(vertical: 14))
            .Width(76)
            .Background(RailBackground)
            .BorderRight(Hairline, 1);

    private Node RailButton(Icon icon, string label, View target)
    {
        bool active = page == target;
        ColorValue color = active ? Accent : TextSecondary;
        return new Column(
            spacing: 4,
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                new IconView(icon, 24).Color(color),
                new Label(label).FontSize(11).Color(color),
            ])
            .Padding(EdgeInsets.Symmetric(vertical: 8))
            .Width(68)
            .CornerRadius(10)
            .Background(active ? new ColorValue("#FFFFFF0F") : new ColorValue("#00000000"))
            .OnTap(() => { page = target; Invalidate(); })
            .Focusable()
            .AccessibleRole(AccessibleRole.Button)
            .AccessibleLabel(label);
    }

    // ── Now playing ───────────────────────────────────────────────────

    private Node NowPlaying()
    {
        Station? station = player.Station;
        (string artist, string title) = SplitStreamTitle(player.StreamTitle);
        // The album cover when one was found, else the station's picture.
        ImageSource? art = cover ?? (station is null ? null : artwork.Get(station.ArtUrl));
        ImageSource? backdropImage = backdrop ?? defaultBackdrop;

        Node backdropNode = backdropImage is null
            ? Node.Empty
            : new Image(backdropImage).Fit(ImageFit.Cover).Opacity(0.85f).AccessibleLabel("");

        Node detailsColumn = new Column(
            spacing: 6,
            mainAxisAlignment: MainAxisAlignment.End,
            children:
            [
                art is null
                    ? Node.Empty
                    : new Image(art).Fit(ImageFit.Cover).Size(180).CornerRadius(6).Margin(EdgeInsets.Only(bottom: 14))
                        .AccessibleLabel(cover is not null ? $"{details?.Album} cover" : $"{station!.Name} artwork"),
                new Label(artist.Length > 0 ? artist : station?.Name ?? "Nothing playing")
                    .Style(new TextStyle(46, FontWeight.Light, 1.1f)).Color(TextPrimary).Outline(TextOutlineColor, 2f)
                    .MaxLines(1).Overflow(TextOverflow.Ellipsis),
                new Label(title.Length > 0 ? title : StatusLine())
                    .Style(new TextStyle(28, FontWeight.Light, 1.15f)).Color(TextPrimary).Outline(TextOutlineColor, 2f)
                    .MaxLines(2).Overflow(TextOverflow.Ellipsis),
                AlbumLine() is { Length: > 0 } album
                    ? new Label(album).FontSize(18).Color(TextPrimary).Outline(TextOutlineColor, 1.5f).MaxLines(1).Overflow(TextOverflow.Ellipsis)
                    : Node.Empty,
                new Row(
                    spacing: 12,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children:
                    [
                        new Label(StreamDetails()).FontSize(14).Color(TextSecondary).Outline(TextOutlineColor, 1.5f),
                        player.RecordingTitle is null
                            ? Node.Empty
                            : new Label("● REC").FontSize(12).Bold().Color(new ColorValue("#FF453A")).Outline(TextOutlineColor, 1.5f)
                                .AccessibleLabel("Recording this track"),
                    ]).Margin(EdgeInsets.Only(top: 4)),
                Transport().Margin(EdgeInsets.Only(top: 14)),
            ])
            .Padding(EdgeInsets.Only(left: 48, bottom: 40, right: 48));

        return new Stack(children:
        [
            backdropNode.Expand(),
            detailsColumn,
        ]).Background(Scrim).ClipToBounds(true).Expand();
    }

    // The album and year, when known.
    private string AlbumLine() => details is { Album.Length: > 0 } d
        ? d.Year.Length > 0 ? $"{d.Album} ({d.Year})" : d.Album
        : "";

    private Node Transport()
    {
        bool active = player.State is PlayerState.Playing or PlayerState.Connecting or PlayerState.Reconnecting;
        Node playStop = player.Station is null
            ? Node.Empty
            : new IconButton(active ? Icons.Square : Icons.Play, () => { _ = TogglePlaybackAsync(); })
                .Size(40).IconSize(20)
                .Tooltip(active ? "Stop" : "Play")
                .AccessibleLabel(active ? "Stop" : "Play");

        return new Row(
            spacing: 12,
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                playStop,
                new IconButton(muted || volume <= 0.001f ? Icons.VolumeX : volume < 0.5f ? Icons.Volume1 : Icons.Volume2, ToggleMute)
                    .Size(32).IconSize(20)
                    .Tooltip(muted ? "Unmute" : "Mute")
                    .AccessibleLabel(muted ? "Unmute" : "Mute"),
                new Slider(Bind(volume, value => { volume = value; player.Volume = value; }), 0f, 1f, label: "Volume")
                    .Width(220)
                    .AccessibleLabel("Volume"),
                new Label(muted ? "muted" : $"{MathF.Round(volume * 100f):0}%")
                    .FontSize(14).Color(TextSecondary).Outline(TextOutlineColor, 1.5f)
                    .Width(48),
            ]);
    }

    private void ToggleMute()
    {
        muted = !muted;
        player.Muted = muted;
        RipshoutSettings.Muted = muted;
        Invalidate();
    }

    private async Task TogglePlaybackAsync()
    {
        if (player.State is PlayerState.Playing or PlayerState.Connecting or PlayerState.Reconnecting)
        {
            player.Stop();
            return;
        }
        if (player.Station is { } station)
        {
            await player.PlayAsync(station);
        }
    }

    private string StatusLine() => player.State switch
    {
        PlayerState.Connecting => "Connecting…",
        PlayerState.Reconnecting => "Reconnecting…",
        PlayerState.Failed => player.Problem ?? "Couldn't play this station.",
        PlayerState.Stopped when player.Station is not null => "Stopped",
        PlayerState.Playing => "",
        _ => "Pick a station to start listening.",
    };

    private string StreamDetails()
    {
        if (player.Station is not { } station)
        {
            return "";
        }

        var parts = new List<string> { $"{StationCatalog.DisplayName(station.Network)} · {station.Name}" };
        if (player.State == PlayerState.Playing)
        {
            string codec = player.Codec.ToUpperInvariant();
            parts.Add(player.BitrateKbps > 0 ? $"{player.BitrateKbps} kbps {codec}".Trim() : codec);
        }
        return string.Join("   ", parts.Where(p => p.Length > 0));
    }

    /// <summary>Stations announce "Artist - Title"; the first " - " splits them, so titles may contain dashes.</summary>
    internal static (string Artist, string Title) SplitStreamTitle(string? streamTitle)
    {
        if (string.IsNullOrWhiteSpace(streamTitle))
        {
            return ("", "");
        }

        int separator = streamTitle.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0
            ? (streamTitle[..separator].Trim(), streamTitle[(separator + 3)..].Trim())
            : ("", streamTitle.Trim());
    }

    private async Task LoadDefaultBackdropAsync()
    {
        (byte[] Pixels, int Width, int Height) decoded = await Task.Run(() =>
        {
            using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("DefaultBackdrop.png")!;
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            using ImageFrame frame = FormatRegistry.Read(buffer.ToArray());
            return (ImagePixels.ToRgba(frame), (int)frame.Columns, (int)frame.Rows);
        });
        defaultBackdrop = ImageSource.FromPixels(decoded.Pixels, decoded.Width, decoded.Height);
        Invalidate();
    }

    // ── Stations ──────────────────────────────────────────────────────

    private Node StationsPage()
    {
        List<Station> visible = VisibleStations();
        return new Column(
            spacing: 14,
            children:
            [
                new Row(
                    spacing: 16,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children:
                    [
                        new Label("stations").Style(new TextStyle(34, FontWeight.Light, 1.1f)).Color(TextPrimary),
                        new Label(refreshingStations && stations.Count == 0 ? "Loading…" : $"{visible.Count}")
                            .FontSize(14).Color(TextSecondary),
                        new Spacer(),
                        new TextInput(Bind(search, value => search = value), placeholder: "Search stations", icon: Icons.Search)
                            .Width(260)
                            .AccessibleLabel("Search stations"),
                    ]),
                NetworkFilters(),
                visible.Count == 0
                    ? EmptyStations()
                    : new ContainerQuery(container => StationGrid(visible, container.Width)).Expand(),
            ])
            .Padding(EdgeInsets.Only(top: 22, left: 28, right: 12))
            .Expand();
    }

    private Node NetworkFilters()
    {
        var tags = new List<Node>
        {
            new Tag("★ Favorites", Bind(favoritesOnly, value => favoritesOnly = value)),
        };
        foreach (StationNetwork network in Enum.GetValues<StationNetwork>())
        {
            if (!stations.Any(s => s.Network == network))
            {
                continue;
            }
            StationNetwork captured = network;
            tags.Add(new Tag(StationCatalog.DisplayName(network), Bind(!hiddenNetworks.Contains(network), shown =>
            {
                if (shown)
                {
                    hiddenNetworks.Remove(captured);
                }
                else
                {
                    hiddenNetworks.Add(captured);
                }
                RipshoutSettings.HiddenNetworks = hiddenNetworks;
            })));
        }
        return new Row(spacing: 8, children: [.. tags]);
    }

    private List<Station> VisibleStations()
    {
        string query = search.Trim();
        return stations
            .Where(s => !hiddenNetworks.Contains(s.Network))
            .Where(s => !favoritesOnly || favorites.Contains(s.Id))
            .Where(s => query.Length == 0
                || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            // Favorites first, alphabetically; the rest keep their order.
            .OrderBy(s => favorites.Contains(s.Id) ? 0 : 1)
            .ThenBy(s => favorites.Contains(s.Id) ? s.Name : "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Node EmptyStations()
    {
        string message = stations.Count == 0
            ? refreshingStations ? "Fetching the station lists…" : "Couldn't load the station lists. Check your connection."
            : "No stations match.";
        return new Center(new Label(message).FontSize(15).Color(TextSecondary)).Expand();
    }

    // A virtualized list of rows, each a row of tiles: only the rows on screen are built, so only their artwork
    // is decoded.
    private Node StationGrid(List<Station> visible, float width)
    {
        int columns = Math.Max(1, (int)((width - 16) / (TileWidth + TileGap)));
        var rows = visible.Chunk(columns).ToList();
        return new ListView<Station[]>(rows, row => new Row(spacing: TileGap, children: [.. row.Select(StationTile)]))
            .ItemHeight(TileHeight + TileGap)
            .Plain(true)
            .SelectionHighlight(false);
    }

    private Node StationTile(Station station)
    {
        bool playing = player.Station?.Id == station.Id && player.State != PlayerState.Stopped;
        bool favorite = favorites.Contains(station.Id);
        ImageSource? art = artwork.Get(station.ArtUrl);

        Node picture = art is null
            ? new Center(new Label(StationCatalog.DisplayName(station.Network)).FontSize(15).Color(TextSecondary))
                .Size(TileArtSize).Background(PanelBackground).CornerRadius(10)
            : new Image(art).Fit(ImageFit.Cover).Size(TileArtSize).CornerRadius(10).AccessibleLabel(""); // the name is under it

        return new Column(
            spacing: 6,
            children:
            [
                picture
                    .Border(playing ? Accent : new ColorValue("#00000000"), playing ? 3 : 0, 10)
                    .OnTap(() => { _ = PlayStationAsync(station); })
                    .Focusable()
                    .AccessibleRole(AccessibleRole.Button)
                    .AccessibleLabel($"Play {station.Name}"),
                new Row(
                    spacing: 6,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children:
                    [
                        new Column(
                            spacing: 1,
                            children:
                            [
                                new Label(station.Name).FontSize(14).Color(playing ? Accent : TextPrimary).MaxLines(1).Overflow(TextOverflow.Ellipsis),
                                new Label(StationCatalog.DisplayName(station.Network)).FontSize(12).Color(TextSecondary),
                            ]).Expand(),
                        new IconView(Icons.Star, 18).Filled(favorite)
                            .Color(favorite ? Accent : new ColorValue("#FFFFFF40"))
                            .Padding(4)
                            .OnTap(() => ToggleFavorite(station))
                            .Focusable()
                            .AccessibleRole(AccessibleRole.Button)
                            .AccessibleLabel(favorite ? $"Remove {station.Name} from favorites" : $"Add {station.Name} to favorites"),
                    ]),
            ]).Width(TileWidth - 16);
    }

    private async Task PlayStationAsync(Station station)
    {
        page = View.NowPlaying;
        RipshoutSettings.LastStationId = station.Id;
        Invalidate();
        await player.PlayAsync(station);
    }

    private void ToggleFavorite(Station station)
    {
        if (!favorites.Remove(station.Id))
        {
            favorites.Add(station.Id);
        }
        RipshoutSettings.Favorites = favorites;
        Invalidate();
    }

    private async Task RefreshStationsAsync()
    {
        refreshingStations = true;
        Invalidate();
        List<Station> cached = stations;
        stations = await Task.Run(() => StationCatalog.RefreshAsync(cached, lifetime.Token));
        refreshingStations = false;
        Invalidate();
    }

    private void OnSettingsChanged()
    {
        hiddenNetworks = RipshoutSettings.HiddenNetworks;
        Invalidate();
    }
}

/// <summary>Apple's dark theme with RipShout's yellow in place of Apple's blue.</summary>
internal sealed class RipshoutTheme() : AppleTheme(ThemeMode.Dark)
{
    private ColorSet? darkColors;

    public override ColorSet DarkColors => darkColors ??= base.DarkColors with
    {
        Primary = MainView.Accent,
        // Text on a yellow-tinted selection (the chosen dropdown row): yellow on yellow is unreadable.
        PrimaryText = MainView.TextPrimary,
        // Text on solid yellow (the selected network pills): white, as the user asked.
        TextOnPrimary = MainView.TextPrimary,
    };
}
