using System.Globalization;
using System.Text;
using System.Text.Json;
using Path = System.IO.Path;

namespace Ripshout3;

/// <summary>What RipShout knows about the track playing: from the station's announcement, and what it looked up.</summary>
internal sealed record TrackDetails(string Artist, string Title)
{
    public string Album { get; init; } = "";

    public string Year { get; init; } = "";

    public int TrackNumber { get; init; }

    public string Genre { get; init; } = "";

    /// <summary>The track's length in seconds as the station states it, or 0.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>The station says this is an advertisement or station ID rather than music.</summary>
    public bool IsAdvertisement { get; init; }

    /// <summary>The album cover on disk, or null when none was found.</summary>
    public string? CoverPath { get; init; }

    /// <summary>Pictures of the artist on disk, for the backdrop. Empty when none were found.</summary>
    public IReadOnlyList<string> BackdropPaths { get; init; } = [];
}

/// <summary>
/// Finds a track's album, cover and artist backdrops. AudioAddict says exactly what each channel is playing, with
/// its cover; otherwise iTunes supplies album and cover. Backdrops come from TheAudioDB. Matching is strict: the
/// artist must be the same artist and the title the same song (ignoring bracketed notes like "(Remastered)"), so a
/// wrong picture is never shown. Pictures are saved in the folders chosen in Settings and reused from there.
/// </summary>
internal static class TrackLookup
{
    // TheAudioDB's public test key; no account needed.
    private const string AudioDbKey = "123";

    // How long an artist with no backdrops anywhere is left alone before asking again.
    private static readonly TimeSpan NoBackdropRetry = TimeSpan.FromDays(7);

    private const int RememberedLookups = 40;
    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<string, Task<TrackDetails>> Lookups = new(StringComparer.Ordinal);
    private static readonly Queue<string> LookupOrder = new();

    /// <summary>
    /// The details for a track, looked up once and shared: the recorder asks when the track starts (while the
    /// station still lists it), and the Now Playing page asks the same, so neither waits on or repeats the other.
    /// </summary>
    public static Task<TrackDetails> GetAsync(Station station, string artist, string title)
    {
        string key = $"{station.Id}\n{artist}\n{title}";
        lock (CacheGate)
        {
            if (Lookups.TryGetValue(key, out Task<TrackDetails>? known))
            {
                return known;
            }
            Task<TrackDetails> lookup = Task.Run(() => LookupAsync(station, artist, title, CancellationToken.None));
            Lookups[key] = lookup;
            LookupOrder.Enqueue(key);
            while (LookupOrder.Count > RememberedLookups)
            {
                Lookups.Remove(LookupOrder.Dequeue());
            }
            return lookup;
        }
    }

    public static async Task<TrackDetails> LookupAsync(Station station, string artist, string title, CancellationToken cancellationToken)
    {
        var details = new TrackDetails(artist, title);
        if (artist.Length == 0 || title.Length == 0)
        {
            return details;
        }

        details = await FromAudioAddictAsync(station, details, cancellationToken) ?? details;
        if (details.IsAdvertisement)
        {
            return details;
        }
        if (details.CoverPath is null || details.Album.Length == 0)
        {
            details = await FromITunesAsync(details, cancellationToken) ?? details;
        }
        return details with { BackdropPaths = await BackdropsAsync(artist, cancellationToken) };
    }

    // ── AudioAddict: the channel's own record of the track ─────────────

    private static async Task<TrackDetails?> FromAudioAddictAsync(Station station, TrackDetails details, CancellationToken cancellationToken)
    {
        if (StationCatalog.AudioAddictApiName(station.Network) is not { } api || station.ChannelId == 0)
        {
            return null;
        }

        try
        {
            using Stream body = await Web.Client.GetStreamAsync(new Uri($"https://api.audioaddict.com/v1/{api}/track_history"), cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty(station.ChannelId.ToString(CultureInfo.InvariantCulture), out JsonElement entry))
            {
                return null;
            }

            string type = Text(entry, "type");
            if (type.Length > 0 && type != "track")
            {
                return details with { IsAdvertisement = true };
            }

            // The channel may have moved on since the title was announced; only use the entry if it is this track.
            if (!SameArtist(Text(entry, "artist"), details.Artist) || !SameTitle(Text(entry, "title"), details.Title))
            {
                return null;
            }

            int duration = entry.TryGetProperty("length", out JsonElement length) && length.TryGetInt32(out int seconds) ? seconds : 0;
            details = details with { Album = Text(entry, "release"), DurationSeconds = duration };
            string art = Text(entry, "art_url");
            if (art.Length > 0)
            {
                string url = (art.StartsWith("//", StringComparison.Ordinal) ? "https:" + art : art) + "?size=600x600";
                details = details with { CoverPath = await SaveCoverAsync(details, url, cancellationToken) };
            }
            return details;
        }
        catch (Exception ex) when (IsLookupFailure(ex))
        {
            Log.Error($"AudioAddict track lookup for {details.Artist} - {details.Title}", ex);
            return null;
        }
    }

    // ── iTunes: album, year and cover ─────────────────────────────────

    private static async Task<TrackDetails?> FromITunesAsync(TrackDetails details, CancellationToken cancellationToken)
    {
        try
        {
            string term = Uri.EscapeDataString($"{details.Artist} {StripNotes(details.Title)}");
            using Stream body = await Web.Client.GetStreamAsync(
                new Uri($"https://itunes.apple.com/search?media=music&entity=song&limit=50&term={term}"), cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            // The album the song first came out on: a studio album before a single, a compilation or a live
            // album, and the earliest of those.
            JsonElement? best = null;
            string bestDate = "";
            int bestRank = int.MaxValue;
            foreach (JsonElement song in document.RootElement.GetProperty("results").EnumerateArray())
            {
                if (!SameArtist(Text(song, "artistName"), details.Artist) || !SameTitle(Text(song, "trackName"), details.Title))
                {
                    continue;
                }
                int rank = AlbumRank(Text(song, "collectionName"), Text(song, "collectionArtistName"));
                string date = Text(song, "releaseDate");
                if (rank < bestRank || (rank == bestRank && string.CompareOrdinal(date, bestDate) < 0))
                {
                    best = song;
                    bestDate = date;
                    bestRank = rank;
                }
            }
            if (best is not { } match)
            {
                Log.Info($"No iTunes match for {details.Artist} - {details.Title}");
                return null;
            }

            details = details with
            {
                Album = details.Album.Length > 0 ? details.Album : Text(match, "collectionName"),
                Year = bestDate.Length >= 4 ? bestDate[..4] : "",
                Genre = Text(match, "primaryGenreName"),
                TrackNumber = match.TryGetProperty("trackNumber", out JsonElement number) && number.TryGetInt32(out int n) ? n : 0,
                DurationSeconds = details.DurationSeconds > 0 ? details.DurationSeconds
                    : match.TryGetProperty("trackTimeMillis", out JsonElement ms) && ms.TryGetInt64(out long millis) ? (int)(millis / 1000) : 0,
            };
            string art = Text(match, "artworkUrl100");
            if (details.CoverPath is null && art.Length > 0)
            {
                // The 100-pixel URL names its size; the same picture comes at any size asked for.
                details = details with { CoverPath = await SaveCoverAsync(details, art.Replace("100x100bb", "600x600bb", StringComparison.Ordinal), cancellationToken) };
            }
            return details;
        }
        catch (Exception ex) when (IsLookupFailure(ex))
        {
            Log.Error($"iTunes lookup for {details.Artist} - {details.Title}", ex);
            return null;
        }
    }

    private static async Task<string?> SaveCoverAsync(TrackDetails details, string url, CancellationToken cancellationToken)
    {
        string name = details.Album.Length > 0 ? details.Album : details.Title;
        string path = Path.Combine(RipshoutSettings.CoverFolder, SafeName(details.Artist), SafeName(name) + ".jpg");
        if (File.Exists(path))
        {
            return path;
        }
        return await DownloadAsync(url, path, cancellationToken) ? path : null;
    }

    // ── Artist backdrops: TheAudioDB, topped up from fanart.tv ──────────

    /// <summary>The most backdrops kept per artist; Now Playing rotates through them.</summary>
    internal const int MaxBackdrops = 12;

    // How long before an artist's fanart.tv pictures are looked at again (new ones get added there).
    private static readonly TimeSpan FanartRefresh = TimeSpan.FromDays(30);

    private static async Task<IReadOnlyList<string>> BackdropsAsync(string artist, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(RipshoutSettings.BackdropFolder, SafeName(artist));
        string noneMarker = Path.Combine(folder, ".none");
        string fanartMarker = Path.Combine(folder, ".fanarttv");
        string fanartKey = RipshoutSettings.FanartTvKey;
        List<string> saved = Pictures(folder);

        bool wantAudioDb = saved.Count == 0
            && !(File.Exists(noneMarker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(noneMarker) < NoBackdropRetry);
        bool wantFanart = fanartKey.Length > 0 && saved.Count < MaxBackdrops
            && !(File.Exists(fanartMarker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(fanartMarker) < FanartRefresh);
        if (!wantAudioDb && !wantFanart)
        {
            return saved;
        }

        try
        {
            // TheAudioDB has up to four fan-art pictures and the artist's MusicBrainz id, which fanart.tv is keyed by.
            (string[] audioDbUrls, string musicBrainzId) = await AudioDbArtistAsync(artist, cancellationToken);
            var seen = new HashSet<string>(saved.Select(FileHash), StringComparer.Ordinal);
            if (wantAudioDb)
            {
                for (int i = 0; i < audioDbUrls.Length; i++)
                {
                    await DownloadUniqueAsync(audioDbUrls[i], Path.Combine(folder, $"backdrop{i + 1}.jpg"), seen, cancellationToken);
                }
            }

            if (fanartKey.Length > 0 && musicBrainzId.Length > 0)
            {
                foreach ((string id, string url) in await FanartBackgroundsAsync(musicBrainzId, fanartKey, cancellationToken))
                {
                    if (seen.Count >= MaxBackdrops)
                    {
                        break;
                    }
                    string path = Path.Combine(folder, $"fanart-{SafeName(id)}.jpg");
                    if (!File.Exists(path))
                    {
                        await DownloadUniqueAsync(url, path, seen, cancellationToken);
                    }
                }
                Directory.CreateDirectory(folder);
                File.WriteAllText(fanartMarker, "");
            }

            List<string> found = Pictures(folder);
            if (found.Count > saved.Count)
            {
                Log.Info($"Found {found.Count - saved.Count} more backdrops for {artist} ({found.Count} in all)");
            }
            if (found.Count == 0)
            {
                Log.Info($"No backdrops for {artist}");
                Directory.CreateDirectory(folder);
                File.WriteAllText(noneMarker, "");
            }
            return found;
        }
        catch (Exception ex) when (IsLookupFailure(ex))
        {
            Log.Error($"Backdrop lookup for {artist}", ex);
        }
        return Pictures(folder);
    }

    // The artist's TheAudioDB fan-art URLs and MusicBrainz id (empty when unknown).
    private static async Task<(string[] Urls, string MusicBrainzId)> AudioDbArtistAsync(string artist, CancellationToken cancellationToken)
    {
        // A collaboration ("A & B", "A feat. B") is pictured by its first artist.
        foreach (string name in new[] { artist, PrimaryArtist(artist) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using Stream body = await Web.Client.GetStreamAsync(
                new Uri($"https://www.theaudiodb.com/api/v1/json/{AudioDbKey}/search.php?s={Uri.EscapeDataString(name)}"), cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("artists", out JsonElement artists) || artists.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement found in artists.EnumerateArray())
            {
                if (SameArtist(Text(found, "strArtist"), name))
                {
                    string[] urls = [.. new[] { "strArtistFanart", "strArtistFanart2", "strArtistFanart3", "strArtistFanart4" }
                        .Select(field => Text(found, field)).Where(url => url.Length > 0)];
                    return (urls, Text(found, "strMusicBrainzID"));
                }
            }
        }
        return ([], "");
    }

    // fanart.tv's artist backgrounds, best liked first, as (id, url).
    private static async Task<List<(string Id, string Url)>> FanartBackgroundsAsync(string musicBrainzId, string key, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Web.Client.GetAsync(
            new Uri($"https://webservice.fanart.tv/v3/music/{Uri.EscapeDataString(musicBrainzId)}?api_key={Uri.EscapeDataString(key)}"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 404: fanart.tv has nothing for this artist. 401: the personal key is wrong.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log.Error("fanart.tv refused the personal API key (check it in Settings)");
            }
            return [];
        }
        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("artistbackground", out JsonElement backgrounds) || backgrounds.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. backgrounds.EnumerateArray()
            .Select(b => (Id: Text(b, "id"), Url: Text(b, "url"), Likes: int.TryParse(Text(b, "likes"), out int likes) ? likes : 0))
            .Where(b => b.Id.Length > 0 && b.Url.StartsWith("https://", StringComparison.Ordinal))
            .OrderByDescending(b => b.Likes)
            .Select(b => (b.Id, b.Url))];
    }

    // Downloads unless the same picture is already in the folder (sites share pictures); keeps the hash set current.
    private static async Task DownloadUniqueAsync(string url, string path, HashSet<string> seen, CancellationToken cancellationToken)
    {
        if (!await DownloadAsync(url, path, cancellationToken))
        {
            return;
        }
        if (!seen.Add(FileHash(path)))
        {
            File.Delete(path);
        }
    }

    private static string FileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static List<string> Pictures(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)]
            : [];

    private static async Task<bool> DownloadAsync(string url, string path, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await Web.Client.GetByteArrayAsync(new Uri(url), cancellationToken);
            if (bytes.Length == 0)
            {
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (IsLookupFailure(ex))
        {
            Log.Error($"Download of {url}", ex);
            return false;
        }
    }

    private static bool IsLookupFailure(Exception ex) =>
        ex is HttpRequestException or JsonException or TaskCanceledException or IOException or UnauthorizedAccessException
            or InvalidOperationException or KeyNotFoundException or UriFormatException;

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // ── Matching ──────────────────────────────────────────────────────

    /// <summary>Same artist: equal once case, accents, punctuation, "&amp;"/"and" and a leading "The" are set aside.</summary>
    internal static bool SameArtist(string a, string b)
    {
        string x = NormalizeArtist(a);
        string y = NormalizeArtist(b);
        return x.Length > 0 && (x == y || x == NormalizeArtist(PrimaryArtist(b)) || NormalizeArtist(PrimaryArtist(a)) == y);
    }

    /// <summary>
    /// Same recording: equal titles once notes are set aside ("(2003 Remaster)", "- Single Version"), as long as
    /// neither names a different version the other doesn't: live, remix, acoustic, an edit and so on.
    /// </summary>
    internal static bool SameTitle(string a, string b)
    {
        string x = Normalize(StripNotes(a));
        string y = Normalize(StripNotes(b));
        return x.Length > 0 && x == y && VersionMarkers(a).SetEquals(VersionMarkers(b));
    }

    // Words in a title's notes that make it a different recording from the original.
    private static readonly string[] VersionWords =
        ["live", "remix", "mix", "acoustic", "demo", "instrumental", "extended", "unplugged", "karaoke", "edit", "rerecorded", "cover", "orchestral"];

    private static HashSet<string> VersionMarkers(string title)
    {
        string notes = Notes(title).ToLowerInvariant();
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string word in notes.Replace("-", "", StringComparison.Ordinal).Split([' ', '(', ')', '[', ']', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (VersionWords.Contains(word))
            {
                found.Add(word);
            }
        }
        return found;
    }

    // What StripNotes removes: the bracketed parts and an iTunes-style " - Remastered" tail.
    private static string Notes(string title)
    {
        var notes = new StringBuilder();
        var outside = new StringBuilder();
        int depth = 0;
        foreach (char c in title)
        {
            if (c is '(' or '[')
            {
                depth++;
                notes.Append(' ');
            }
            else if (c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth > 0)
            {
                notes.Append(c);
            }
            else
            {
                outside.Append(c);
            }
        }

        // StripNotes also cut a " - Remastered" style tail from the text outside the brackets.
        string unbracketed = outside.ToString().Trim();
        string kept = StripNotes(title);
        if (kept.Length < unbracketed.Length)
        {
            notes.Append(' ').Append(unbracketed[kept.Length..]);
        }
        return notes.ToString();
    }

    /// <summary>
    /// How well an album represents where a song came from, lower is better: the artist's studio album, then a
    /// single or EP, then a compilation, then a live album.
    /// </summary>
    internal static int AlbumRank(string album, string albumArtist)
    {
        string name = album.ToLowerInvariant();
        if (name.Contains("live", StringComparison.Ordinal) || name.Contains("concert", StringComparison.Ordinal)
            || name.Contains("unplugged", StringComparison.Ordinal))
        {
            return 3;
        }
        if (albumArtist.Length > 0 || name.Contains("greatest hits", StringComparison.Ordinal) || name.Contains("best of", StringComparison.Ordinal)
            || name.Contains("collection", StringComparison.Ordinal) || name.Contains("anthology", StringComparison.Ordinal)
            || name.Contains("essential", StringComparison.Ordinal) || name.Contains("hits", StringComparison.Ordinal))
        {
            return 2;
        }
        if (name.EndsWith(" - single", StringComparison.Ordinal) || name.EndsWith(" - ep", StringComparison.Ordinal))
        {
            return 1;
        }
        return 0;
    }

    /// <summary>The first artist of a collaboration: "A feat. B", "A ft B", "A &amp; B", "A, B", "A x B", "A vs. B".</summary>
    internal static string PrimaryArtist(string artist)
    {
        string[] joins = [" feat. ", " feat ", " ft. ", " ft ", " featuring ", " & ", ", ", " x ", " vs. ", " vs ", " with "];
        int cut = artist.Length;
        foreach (string join in joins)
        {
            int at = artist.IndexOf(join, StringComparison.OrdinalIgnoreCase);
            if (at > 0 && at < cut)
            {
                cut = at;
            }
        }
        return artist[..cut].Trim();
    }

    /// <summary>The title without bracketed notes: "It's My Life (Remastered 2010)" is "It's My Life".</summary>
    internal static string StripNotes(string title)
    {
        var builder = new StringBuilder(title.Length);
        int depth = 0;
        foreach (char c in title)
        {
            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }
        string stripped = builder.ToString();
        int dash = stripped.IndexOf(" - ", StringComparison.Ordinal);
        // "Song - Remastered 2010" / "Song - Live" style notes, as iTunes writes them.
        string[] dashNotes = ["remaster", "version", "edit", "mono", "stereo", "live", "mix", "acoustic", "demo", "instrumental"];
        if (dash > 0 && dashNotes.Any(note => stripped[dash..].Contains(note, StringComparison.OrdinalIgnoreCase)))
        {
            stripped = stripped[..dash];
        }
        return stripped.Trim();
    }

    private static string NormalizeArtist(string artist)
    {
        string text = Normalize(artist.Replace("&", " and ", StringComparison.Ordinal));
        return text.StartsWith("the", StringComparison.Ordinal) && artist.TrimStart().StartsWith("the ", StringComparison.OrdinalIgnoreCase)
            ? text[3..]
            : text;
    }

    // Lower case letters and digits only, accents removed.
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }
        return builder.ToString();
    }

    /// <summary>A name made safe for a file or folder name.</summary>
    internal static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in name.Trim())
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        string safe = builder.ToString().Trim(' ', '.');
        return safe.Length == 0 ? "Unknown" : safe.Length > 120 ? safe[..120].TrimEnd(' ', '.') : safe;
    }
}
