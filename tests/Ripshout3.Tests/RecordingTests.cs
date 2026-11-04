namespace Ripshout3.Tests;

/// <summary>The recorder, against temporary folders and a stand-in track lookup (no settings, no network).</summary>
internal sealed class RecorderTests
{
    // 1 kbps: 125 bytes a second, so a minute is 7,500 bytes.
    private const int Kbps = 1;
    private const int BytesPerSecond = 125;

    private static readonly Station Station = new(StationNetwork.RockRadio, "classicrock", 1, "Classic Rock", "", "");

    [Test]
    public async Task KeepsOnlyWholeTracks_TaggedAndFiledByGenre()
    {
        using var folders = new TempFolders();
        using var recorder = folders.Recorder(currentTitle: "Joined - Mid Song");

        recorder.Write(Bytes(10));                       // the partial first track: never written
        recorder.OnTitleChanged("Bon Jovi - It's My Life");
        await Assert.That(recorder.RecordingTitle).IsEqualTo("Bon Jovi - It's My Life");
        recorder.Write(Bytes(90 * BytesPerSecond));
        recorder.OnTitleChanged("Van Halen - Feels So Good");
        recorder.Write(Bytes(30 * BytesPerSecond));     // cut off when the station is stopped
        await recorder.Pending;
        recorder.Dispose();

        string saved = Path.Combine(folders.Music, "Classic Rock", "Bon Jovi - It's My Life.mp3");
        await Assert.That(File.Exists(saved)).IsTrue();
        await Assert.That(Directory.GetFiles(folders.Music, "*", SearchOption.AllDirectories).Length).IsEqualTo(1);
        await Assert.That(Directory.GetFiles(folders.Recording, "*", SearchOption.AllDirectories).Length).IsEqualTo(0);

        // Tag first, then the station's bytes unchanged.
        byte[] file = await File.ReadAllBytesAsync(saved);
        await Assert.That(file.AsSpan(0, 3).SequenceEqual("ID3"u8)).IsTrue();
        await Assert.That(file.AsSpan(file.Length - 90 * BytesPerSecond).SequenceEqual(Bytes(90 * BytesPerSecond))).IsTrue();
        await Assert.That(Id3Tag.ReadRipshoutInfo(saved)).IsEqualTo((Kbps, 90));
    }

    [Test]
    public async Task TheFirstAnnouncedTitle_IsTheTrackAlreadyPlaying()
    {
        using var folders = new TempFolders();
        using var recorder = folders.Recorder(currentTitle: null);

        // The station names what is already playing a moment after connecting: that track is partial.
        recorder.OnTitleChanged("Joined - Mid Song");
        await Assert.That(recorder.RecordingTitle).IsNull();
        recorder.Write(Bytes(90 * BytesPerSecond));
        recorder.OnTitleChanged("Bon Jovi - It's My Life");
        await recorder.Pending;

        await Assert.That(Directory.Exists(folders.Music)).IsFalse();
        await Assert.That(recorder.RecordingTitle).IsEqualTo("Bon Jovi - It's My Life");
    }

    [Test]
    public async Task ShortClipsAndAdverts_AreNotKept()
    {
        using var folders = new TempFolders();
        using var recorder = folders.Recorder(currentTitle: "Joined - Mid Song", advert: "Sponsor");

        recorder.OnTitleChanged("Station - Jingle");
        recorder.Write(Bytes(20 * BytesPerSecond));
        recorder.OnTitleChanged("Sponsor - Buy Things");
        await recorder.Pending;
        recorder.Write(Bytes(90 * BytesPerSecond));
        recorder.OnTitleChanged("Next - Track");
        await recorder.Pending;

        await Assert.That(Directory.Exists(folders.Music)).IsFalse();
        await Assert.That(Directory.GetFiles(folders.Recording, "*", SearchOption.AllDirectories).Single()).EndsWith("Next - Track.mp3");
    }

    [Test]
    public async Task AnExistingCopy_IsReplacedOnlyByAHigherBitrateOfTheSameLength()
    {
        using var folders = new TempFolders();
        string saved = Path.Combine(folders.Music, "Classic Rock", "Bon Jovi - It's My Life.mp3");

        await Record(folders, kbps: 1, seconds: 90);
        await Assert.That(Id3Tag.ReadRipshoutInfo(saved)).IsEqualTo((1, 90));

        await Record(folders, kbps: 2, seconds: 90);      // better: replaces
        await Assert.That(Id3Tag.ReadRipshoutInfo(saved)).IsEqualTo((2, 90));

        await Record(folders, kbps: 1, seconds: 90);      // worse: ignored
        await Record(folders, kbps: 4, seconds: 300);     // better bitrate, but a different (extended) recording
        await Assert.That(Id3Tag.ReadRipshoutInfo(saved)).IsEqualTo((2, 90));
    }

    [Test]
    public async Task RecordingOff_WritesNothing()
    {
        using var folders = new TempFolders();
        using var recorder = folders.Recorder(currentTitle: "Joined - Mid Song", enabled: false);

        recorder.OnTitleChanged("Bon Jovi - It's My Life");
        recorder.Write(Bytes(90 * BytesPerSecond));
        recorder.OnTitleChanged("Next - Track");
        await recorder.Pending;

        await Assert.That(recorder.RecordingTitle).IsNull();
        await Assert.That(Directory.Exists(folders.Recording) && Directory.EnumerateFiles(folders.Recording, "*", SearchOption.AllDirectories).Any()).IsFalse();
        await Assert.That(Directory.Exists(folders.Music)).IsFalse();
    }

    [Test]
    public async Task RecordingStream_PassesEveryByteThrough()
    {
        using var folders = new TempFolders();
        using var recorder = folders.Recorder(currentTitle: "Joined - Mid Song");
        recorder.OnTitleChanged("Bon Jovi - It's My Life");
        byte[] source = Bytes(5000);
        using var stream = new RecordingStream(new MemoryStream(source), recorder);

        byte[] read = new byte[source.Length];
        int total = 0;
        while (total < read.Length)
        {
            total += await stream.ReadAsync(read.AsMemory(total, Math.Min(777, read.Length - total)));
        }
        recorder.Dispose();

        await Assert.That(read.AsSpan().SequenceEqual(source)).IsTrue();
    }

    private static async Task Record(TempFolders folders, int kbps, int seconds)
    {
        using var recorder = folders.Recorder(currentTitle: "Joined - Mid Song", kbps: kbps);
        recorder.OnTitleChanged("Bon Jovi - It's My Life");
        recorder.Write(Bytes(seconds * kbps * 125));
        recorder.OnTitleChanged("Next - Track");
        await recorder.Pending;
    }

    private static byte[] Bytes(int count)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)(i * 31 + 7);
        }
        return bytes;
    }

    private sealed class TempFolders : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ripshout-tests", Guid.NewGuid().ToString("N"));

        public string Recording => Path.Combine(root, "recording");

        public string Music => Path.Combine(root, "music");

        public Recorder Recorder(string? currentTitle, int kbps = Kbps, bool enabled = true, string? advert = null) =>
            new(Station, "audio/mpeg", kbps, "Classic Rock", currentTitle, new Recorder.Options(
                () => enabled,
                () => Recording,
                () => Music,
                (_, artist, title) => Task.FromResult(new TrackDetails(artist, title)
                {
                    Album = "Crush",
                    Year = "2000",
                    IsAdvertisement = artist == advert,
                })));

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}

internal sealed class TrackMatchingTests
{
    [Test]
    [Arguments("Bon Jovi", "Bon Jovi", true)]
    [Arguments("The Rolling Stones", "Rolling Stones", true)]
    [Arguments("Simon & Garfunkel", "Simon and Garfunkel", true)]
    [Arguments("Beyoncé", "Beyonce", true)]
    [Arguments("Bon Jovi feat. LeAnn Rimes", "Bon Jovi", true)]
    [Arguments("Jon Bon Jovi", "Bon Jovi", false)]
    [Arguments("Van Halen", "Van Morrison", false)]
    [Arguments("Them", "The M", false)]
    public async Task SameArtist(string a, string b, bool same)
    {
        await Assert.That(TrackLookup.SameArtist(a, b)).IsEqualTo(same);
    }

    [Test]
    [Arguments("It's My Life", "It's My Life (Remastered 2010)", true)]
    [Arguments("Feels So Good", "Feels So Good - 2015 Remaster", true)]
    [Arguments("Hunger Strike", "Hunger Strike [Live]", false)]
    [Arguments("It's My Life", "Livin' on a Prayer", false)]
    [Arguments("Jump", "Jumpin' Jack Flash", false)]
    public async Task SameTitle(string a, string b, bool same)
    {
        await Assert.That(TrackLookup.SameTitle(a, b)).IsEqualTo(same);
    }

    [Test]
    public async Task SafeName_KeepsFileNamesLegal()
    {
        await Assert.That(TrackLookup.SafeName("AC/DC: Back in Black?")).IsEqualTo("AC_DC_ Back in Black_");
        await Assert.That(TrackLookup.SafeName("  ...  ")).IsEqualTo("Unknown");
    }
}

internal sealed class LogTests
{
    [Test]
    public async Task ALogRollsOver_SoItNeverGrowsPastTwiceItsLimit()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ripshout-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "activity.log");
        try
        {
            string line = new('x', 1000);
            for (int i = 0; i < 2500; i++)
            {
                Log.Write(path, line);
            }

            await Assert.That(new FileInfo(path).Length).IsLessThanOrEqualTo(Log.MaxBytes);
            await Assert.That(new FileInfo(Path.Combine(folder, "activity.old.log")).Length).IsLessThanOrEqualTo(Log.MaxBytes);
            await Assert.That(Directory.GetFiles(folder).Length).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>Against the live services, for tracks the user saw come up without pictures.</summary>
internal sealed class LiveTrackLookupTests
{
    [Test]
    [Arguments("Bon Jovi", "It's My Life")]
    [Arguments("Temple of the Dog", "Hunger Strike")]
    [Arguments("Van Halen", "Feels So Good")]
    public async Task FindsCoverAndBackdrops(string artist, string title)
    {
        // 1.FM: no AudioAddict history, so the cover comes from iTunes.
        var station = new Station(StationNetwork.OneFm, "https://example.invalid/stream", 0, "Classic Rock Replay", "", "");
        TrackDetails details = await TrackLookup.LookupAsync(station, artist, title, CancellationToken.None);

        await Assert.That(details.Album).IsNotEmpty();
        await Assert.That(details.CoverPath is not null && File.Exists(details.CoverPath)).IsTrue();
        await Assert.That(details.BackdropPaths.Count).IsGreaterThan(0);
    }
}

internal sealed class TrackVersionTests
{
    [Test]
    [Arguments("It's My Life", "It's My Life (Live from the Bounce Tour)", false)]
    [Arguments("It's My Life", "It's My Life - Live", false)]
    [Arguments("Hunger Strike", "Hunger Strike (Remix)", false)]
    [Arguments("Hunger Strike (Live)", "Hunger Strike [Live]", true)]
    [Arguments("Live and Let Die", "Live and Let Die (Remastered 2018)", true)]
    [Arguments("Feels So Good", "Feels So Good (Radio Edit)", false)]
    public async Task VersionsAreNotTheSameRecording(string a, string b, bool same)
    {
        await Assert.That(TrackLookup.SameTitle(a, b)).IsEqualTo(same);
    }

    [Test]
    [Arguments("Crush", "", 0)]
    [Arguments("It's My Life (2003) - Single", "", 1)]
    [Arguments("Greatest Hits: The Ultimate Collection (Deluxe Edition)", "", 2)]
    [Arguments("Now That's What I Call Music", "Various Artists", 2)]
    [Arguments("One Wild Night Live 1985-2001", "", 3)]
    public async Task StudioAlbumsRankFirst(string album, string albumArtist, int rank)
    {
        await Assert.That(TrackLookup.AlbumRank(album, albumArtist)).IsEqualTo(rank);
    }
}

/// <summary>
/// A real station through the recorder, as the player reads it: one whole track must come out tagged and playable.
/// Slow (it waits for a track to finish), so it runs only when RIPSHOUT_LIVE_RECORDING is set.
/// </summary>
internal sealed class LiveRecordingTests
{
    [Test]
    [Timeout(15 * 60 * 1000)]
    public async Task RecordsAWholeTrackFromALiveStation(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("RIPSHOUT_LIVE_RECORDING") is null)
        {
            Skip.Test("Set RIPSHOUT_LIVE_RECORDING=1 to record a track from a live 1.FM station (takes several minutes).");
        }

        Station station = StationCatalog.LoadCached().First(s => s.Network == StationNetwork.OneFm
            && s.Name.Contains("Top 40", StringComparison.OrdinalIgnoreCase));
        string root = Path.Combine(Path.GetTempPath(), "ripshout-live", Guid.NewGuid().ToString("N"));
        string music = Path.Combine(root, "music");
        var options = new Recorder.Options(() => true, () => Path.Combine(root, "recording"), () => music, TrackLookup.GetAsync);

        using var input = new MediaKernel.Core.Input.HttpMediaInput(station.Key);
        await input.OpenAsync(cancellationToken);
        using var recorder = new Recorder(station, input.Info.ContentType, input.Icy.BitrateKbps, input.Icy.Genre, input.Icy.StreamTitle, options);
        int changes = 0;
        input.StreamTitleChanged += (_, e) =>
        {
            recorder.OnTitleChanged(e.Title);
            changes++;
        };
        string? saved = null;
        recorder.TrackSaved += path => saved = path;

        // Read as the player would until a whole track (between two announced changes, after the first) is saved.
        using Stream stream = new RecordingStream(input.AsStream(), recorder);
        byte[] buffer = new byte[16 * 1024];
        while (saved is null && !cancellationToken.IsCancellationRequested)
        {
            await stream.ReadAsync(buffer, cancellationToken);
            if (changes >= 3)
            {
                await recorder.Pending;
            }
        }

        await Assert.That(saved).IsNotNull();
        byte[] head = new byte[3];
        await using (FileStream file = File.OpenRead(saved!))
        {
            await file.ReadExactlyAsync(head, cancellationToken);
        }
        await Assert.That(head.AsSpan().SequenceEqual("ID3"u8)).IsTrue();
        using var player = MediaKernel.Playback.MediaPlayer.Open(saved!, new MediaKernel.Playback.MediaPlayerOptions { Silent = true });
        await Assert.That(player.HasAudio).IsTrue();
        Console.WriteLine($"Saved {saved} ({new FileInfo(saved!).Length / 1024} KB, {Id3Tag.ReadRipshoutInfo(saved!)})");
    }
}
