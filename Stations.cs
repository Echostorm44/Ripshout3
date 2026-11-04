using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ripshout3;

/// <summary>The station families RipShout knows how to list, play and look up track details for.</summary>
internal enum StationNetwork
{
    DigitallyImported,
    RadioTunes,
    JazzRadio,
    RockRadio,
    ZenRadio,
    ClassicalRadio,
    OneFm,
}

/// <summary>
/// The stream quality to ask an AudioAddict network for. Each is a tier of the premium service, so each needs a
/// listen key; the public, key-less streams these networks once offered are gone.
/// </summary>
internal enum StreamQuality
{
    /// <summary>320 kbps MP3 (the <c>premium_high</c> tier).</summary>
    Mp3High,

    /// <summary>128 kbps AAC (the <c>premium</c> tier).</summary>
    Aac128,

    /// <summary>64 kbps AAC (the <c>premium_medium</c> tier).</summary>
    Aac64,
}

/// <summary>
/// One channel. <see cref="Key"/> is the network's own name for it: the channel key on AudioAddict, the stream
/// link on 1.FM. <see cref="ChannelId"/> is AudioAddict's numeric id, used for its track history, or 0.
/// </summary>
internal sealed record Station(
    StationNetwork Network,
    string Key,
    int ChannelId,
    string Name,
    string Description,
    string ArtUrl)
{
    /// <summary>Stable across catalog refreshes, so favorites survive them.</summary>
    [JsonIgnore]
    public string Id => $"{Network}:{Key}";
}

/// <summary>
/// Lists every station on the networks RipShout supports. The last list is kept on disk so the grid appears
/// instantly at start-up; <see cref="RefreshAsync"/> then fetches every network at once and replaces it.
/// </summary>
internal static class StationCatalog
{
    private static readonly string CachePath = Path.Combine(AppPaths.CacheFolder, "stations.json");

    /// <summary>The AudioAddict networks: their API name and the domain their streams are served from.</summary>
    private static readonly (StationNetwork Network, string ApiName, string Domain)[] AudioAddictNetworks =
    [
        (StationNetwork.DigitallyImported, "di", "di.fm"),
        (StationNetwork.RadioTunes, "radiotunes", "radiotunes.com"),
        (StationNetwork.JazzRadio, "jazzradio", "jazzradio.com"),
        (StationNetwork.RockRadio, "rockradio", "rockradio.com"),
        (StationNetwork.ZenRadio, "zenradio", "zenradio.com"),
        (StationNetwork.ClassicalRadio, "classicalradio", "classicalradio.com"),
    ];

    public static string DisplayName(StationNetwork network) => network switch
    {
        StationNetwork.DigitallyImported => "DI.FM",
        StationNetwork.RadioTunes => "RadioTunes",
        StationNetwork.JazzRadio => "JAZZRADIO",
        StationNetwork.RockRadio => "ROCKRADIO",
        StationNetwork.ZenRadio => "Zen Radio",
        StationNetwork.ClassicalRadio => "ClassicalRadio",
        StationNetwork.OneFm => "1.FM",
        _ => throw new ArgumentOutOfRangeException(nameof(network), network, null),
    };

    public static bool NeedsListenKey(StationNetwork network) => network != StationNetwork.OneFm;

    /// <summary>The network's name in AudioAddict's API ("di", "rockradio"), or null for a network not on AudioAddict.</summary>
    public static string? AudioAddictApiName(StationNetwork network) =>
        AudioAddictNetworks.Where(n => n.Network == network).Select(n => n.ApiName).FirstOrDefault();

    /// <summary>The list saved by the last refresh, or an empty list on first run or if the file is unreadable.</summary>
    public static List<Station> LoadCached()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return [];
            }
            using FileStream file = File.OpenRead(CachePath);
            return JsonSerializer.Deserialize(file, StationJsonContext.Default.ListStation) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Fetches every network in parallel. A network that fails keeps its cached stations, so one site being down
    /// never empties its part of the grid. The merged list is saved for next time.
    /// </summary>
    public static async Task<List<Station>> RefreshAsync(IReadOnlyList<Station> cached, CancellationToken cancellationToken)
    {
        var fetches = AudioAddictNetworks
            .Select(n => FetchAudioAddictAsync(n.Network, n.ApiName, cancellationToken))
            .Append(FetchOneFmAsync(cancellationToken))
            .ToArray();
        List<Station>?[] results = await Task.WhenAll(fetches);

        var stations = new List<Station>();
        var networks = AudioAddictNetworks.Select(n => n.Network).Append(StationNetwork.OneFm).ToArray();
        for (int i = 0; i < networks.Length; i++)
        {
            stations.AddRange(results[i] ?? cached.Where(s => s.Network == networks[i]));
        }

        if (results.Any(r => r is not null))
        {
            Save(stations);
        }
        return stations;
    }

    /// <summary>
    /// The URLs to try for a station, best first. AudioAddict's come from the network's playlist for the chosen
    /// quality, which names the servers currently serving it (primary then backups); 1.FM's is the link itself.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveStreamUrlsAsync(
        Station station, string listenKey, StreamQuality quality, CancellationToken cancellationToken)
    {
        if (station.Network == StationNetwork.OneFm)
        {
            return [station.Key];
        }

        string domain = AudioAddictNetworks.First(n => n.Network == station.Network).Domain;
        string tier = quality switch
        {
            StreamQuality.Mp3High => "premium_high",
            StreamQuality.Aac128 => "premium",
            StreamQuality.Aac64 => "premium_medium",
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, null),
        };

        var playlistUrl = new Uri($"http://listen.{domain}/{tier}/{station.Key}.pls?listen_key={Uri.EscapeDataString(listenKey)}");
        string playlist = await Web.Client.GetStringAsync(playlistUrl, cancellationToken);
        return ParsePlaylist(playlist);
    }

    /// <summary>The <c>FileN=</c> entries of a .pls playlist, in order.</summary>
    internal static List<string> ParsePlaylist(string playlist)
    {
        var urls = new List<(int Index, string Url)>();
        foreach (string rawLine in playlist.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0 || !int.TryParse(line.AsSpan(4, equals - 4), out int index))
            {
                continue;
            }

            string url = line[(equals + 1)..].Trim();
            if (url.Length > 0)
            {
                urls.Add((index, url));
            }
        }
        return urls.OrderBy(u => u.Index).Select(u => u.Url).ToList();
    }

    private static async Task<List<Station>?> FetchAudioAddictAsync(
        StationNetwork network, string apiName, CancellationToken cancellationToken)
    {
        try
        {
            using Stream body = await Web.Client.GetStreamAsync(
                new Uri($"https://api.audioaddict.com/v1/{apiName}/channels"), cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            var stations = new List<Station>();
            foreach (JsonElement channel in document.RootElement.EnumerateArray())
            {
                string key = Text(channel, "key");
                string name = Text(channel, "name");
                if (key.Length == 0 || name.Length == 0)
                {
                    continue;
                }

                int id = channel.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt32(out int parsed) ? parsed : 0;
                string description = Text(channel, "description_short");
                if (description.Length == 0)
                {
                    description = Text(channel, "description");
                }

                // The square artwork, else the channel's general asset. Both are URI templates ("…png{?size,…}").
                string art = channel.TryGetProperty("images", out JsonElement images) ? Text(images, "square") : "";
                if (art.Length == 0)
                {
                    art = Text(channel, "asset_url");
                }

                stations.Add(new Station(network, key, id, name, description.Trim(), AudioAddictImageUrl(art)));
            }
            return stations.Count > 0 ? stations.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<List<Station>?> FetchOneFmAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.1cloud.fm/api/channels?optimized=true"));
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await Web.Client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            var stations = new List<Station>();
            foreach (JsonElement channel in document.RootElement.GetProperty("data").EnumerateArray())
            {
                string name = Text(channel, "display_name");
                if (name.Length == 0)
                {
                    name = Text(channel, "channel_title");
                }

                // Some links carry stray trailing punctuation ("…gdpr=true.").
                string link = Text(channel, "broadcast_mp3_link").TrimEnd('.');
                if (name.Length == 0 || !Uri.TryCreate(link, UriKind.Absolute, out _))
                {
                    continue;
                }

                stations.Add(new Station(StationNetwork.OneFm, link, 0, name, Text(channel, "description"), Text(channel, "cover_art")));
            }
            return stations.Count > 0 ? stations.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns AudioAddict's protocol-relative URI template into a concrete URL for a square of the size the grid
    /// shows, so a 1000-pixel original is never downloaded to draw a 170-pixel tile.
    /// </summary>
    internal static string AudioAddictImageUrl(string template)
    {
        if (template.Length == 0)
        {
            return "";
        }

        int brace = template.IndexOf('{');
        string url = brace >= 0 ? template[..brace] : template;
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        return url + "?size=360x360";
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static void Save(List<Station> stations)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheFolder);
            string temporary = CachePath + ".tmp";
            using (FileStream file = File.Create(temporary))
            {
                JsonSerializer.Serialize(file, stations, StationJsonContext.Default.ListStation);
            }
            File.Move(temporary, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is a convenience: next start fetches again.
        }
    }
}

/// <summary>The one HTTP client the app shares, so connections to the same hosts are reused.</summary>
internal static class Web
{
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RipShout/3.0");
        return client;
    }
}

[JsonSerializable(typeof(List<Station>))]
internal sealed partial class StationJsonContext : JsonSerializerContext;
