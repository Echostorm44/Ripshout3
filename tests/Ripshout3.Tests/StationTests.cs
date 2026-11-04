namespace Ripshout3.Tests;

internal sealed class StationPlaylistTests
{
    // What listen.di.fm serves for premium_high/trance.pls, with the listen key replaced.
    private const string DiPlaylist =
        "[playlist]\r\nNumberOfEntries=2\r\nFile1=http://prem1.di.fm:80/trance_hi?KEY\r\nTitle1=DI.FM - Trance\r\nLength1=0\r\n"
        + "File2=http://prem4.di.fm:80/trance_hi?KEY\r\nTitle2=DI.FM - Trance\r\nLength2=0\r\nVersion=2\r\n";

    [Test]
    public async Task Playlist_GivesEveryFileInOrder()
    {
        List<string> urls = StationCatalog.ParsePlaylist(DiPlaylist);

        await Assert.That(urls).IsEquivalentTo(
            ["http://prem1.di.fm:80/trance_hi?KEY", "http://prem4.di.fm:80/trance_hi?KEY"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Playlist_OrdersByNumberNotByLine()
    {
        List<string> urls = StationCatalog.ParsePlaylist("[playlist]\nFile2=b\nFile10=c\nFile1=a\n");

        await Assert.That(urls).IsEquivalentTo(["a", "b", "c"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task AudioAddictArt_TemplateBecomesASizedHttpsUrl()
    {
        string url = StationCatalog.AudioAddictImageUrl(
            "//cdn-images.audioaddict.com/b/1/0/5/6/7/b10567777ad265dcc63816fa32396654.png{?size,height,width,quality,pad}");

        await Assert.That(url).IsEqualTo(
            "https://cdn-images.audioaddict.com/b/1/0/5/6/7/b10567777ad265dcc63816fa32396654.png?size=360x360");
    }
}

/// <summary>Against the live station APIs: these are what the app depends on, so they are what to test.</summary>
internal sealed class LiveStationTests
{
    [Test]
    [Timeout(60_000)]
    public async Task Refresh_ListsEveryNetwork(CancellationToken cancellationToken)
    {
        List<Station> stations = await StationCatalog.RefreshAsync([], cancellationToken);

        foreach (StationNetwork network in Enum.GetValues<StationNetwork>())
        {
            int count = stations.Count(s => s.Network == network);
            await Assert.That(count).IsGreaterThan(5).Because($"{network} listed {count} stations.");
        }

        await Assert.That(stations.Select(s => s.Id).Distinct().Count()).IsEqualTo(stations.Count)
            .Because("station ids must be unique, or favorites would point at two stations.");
        string missingIds = string.Join(", ", stations.Where(s => s.Network != StationNetwork.OneFm && s.ChannelId <= 0).Select(s => s.Id));
        await Assert.That(missingIds).IsEqualTo("").Because("AudioAddict's track history is looked up by channel id.");

        // A few channels (often brand new ones) have no artwork at all; the grid draws a fallback tile for them.
        // What must never happen is a URL the grid cannot fetch.
        string badArt = string.Join(", ", stations
            .Where(s => s.ArtUrl.Length > 0 && !s.ArtUrl.StartsWith("https://", StringComparison.Ordinal))
            .Select(s => $"{s.Id} [{s.ArtUrl}]"));
        await Assert.That(badArt).IsEqualTo("").Because("artwork must be an https URL or absent.");

        int withoutArt = stations.Count(s => s.ArtUrl.Length == 0);
        await Assert.That(withoutArt).IsLessThan(stations.Count / 20)
            .Because($"{withoutArt} of {stations.Count} stations came back with no artwork, which suggests the artwork field moved.");
    }

    [Test]
    [Timeout(60_000)]
    public async Task Resolve_GivesPremiumServersForEachQuality(CancellationToken cancellationToken)
    {
        // The listen key is the user's: it comes from the environment, never from the repository.
        string? listenKey = Environment.GetEnvironmentVariable("RIPSHOUT_LISTEN_KEY");
        if (string.IsNullOrWhiteSpace(listenKey))
        {
            Skip.Test("Set RIPSHOUT_LISTEN_KEY to an AudioAddict listen key to run this.");
        }

        var trance = new Station(StationNetwork.DigitallyImported, "trance", 1, "Trance", "", "");
        IReadOnlyList<string> high = await StationCatalog.ResolveStreamUrlsAsync(trance, listenKey!, StreamQuality.Mp3High, cancellationToken);
        IReadOnlyList<string> aac = await StationCatalog.ResolveStreamUrlsAsync(trance, listenKey!, StreamQuality.Aac64, cancellationToken);

        await Assert.That(high.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(high.All(u => u.Contains("/trance_hi?", StringComparison.Ordinal))).IsTrue();
        await Assert.That(aac.All(u => u.Contains("/trance_aac?", StringComparison.Ordinal))).IsTrue();
    }
}
