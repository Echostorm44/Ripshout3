using Cascade.UI;
using MediaKernel.Core.Input;
using MediaKernel.Playback;

namespace Ripshout3;

/// <summary>Where a <see cref="RadioPlayer"/> is with its station.</summary>
internal enum PlayerState
{
    Stopped,
    Connecting,
    Playing,
    Reconnecting,
    Failed,
}

/// <summary>
/// Plays one station at a time over a single connection. The station's servers are tried in order, the stream is
/// decoded by MediaKernel and played through WASAPI, and the station's track announcements come back as
/// <see cref="StreamTitle"/>. If the stream drops it reconnects on its own, backing off, through the station's
/// other servers. Every change is reported on the UI thread through <see cref="Changed"/>.
/// </summary>
internal sealed class RadioPlayer : IDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private float volume = RipshoutSettings.Volume;
    private bool muted = RipshoutSettings.Muted;
    private Connection? connection;
    // Bumped by every Play and Stop, so work started for an earlier station never touches the current one.
    private int generation;

    /// <summary>Raised on the UI thread whenever the state, station, title or stream details change.</summary>
    public event Action? Changed;

    public PlayerState State { get; private set; }

    public Station? Station { get; private set; }

    /// <summary>The station's latest track announcement ("Artist - Title"), or null before the first one.</summary>
    public string? StreamTitle { get; private set; }

    /// <summary>Why playback stopped or failed, in words for the user, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>The stream's bitrate in kbps as the station states it, or 0 if it does not.</summary>
    public int BitrateKbps { get; private set; }

    /// <summary>The stream's codec as the decoder names it ("mp3", "aac"), or empty before it is open.</summary>
    public string Codec { get; private set; } = "";

    /// <summary>The title being recorded whole right now, or null (not recording, or waiting for the next track).</summary>
    public string? RecordingTitle => connection?.Recorder.RecordingTitle;

    /// <summary>Raised on the UI thread when a recorded track has been saved, with its path.</summary>
    public event Action<string>? TrackSaved;

    /// <summary>
    /// The volume as the user sees it on the slider, 0 to 1. The sound follows a perceptual curve (see
    /// <see cref="GainFor"/>), so the slider's middle sounds like the middle.
    /// </summary>
    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0f, 1f);
            ApplyGain();
        }
    }

    /// <summary>Silences the sound without forgetting the volume.</summary>
    public bool Muted
    {
        get => muted;
        set
        {
            muted = value;
            ApplyGain();
        }
    }

    private float Gain => muted ? 0f : GainFor(volume);

    private void ApplyGain()
    {
        if (connection?.Player is { } player)
        {
            player.Volume = Gain;
        }
    }

    /// <summary>
    /// Slider position to amplitude. Hearing is logarithmic, so a linear slider puts nearly all of its audible
    /// change in the bottom quarter; a cube gives about -18 dB at the halfway point and silence at zero.
    /// </summary>
    internal static float GainFor(float sliderValue) => sliderValue * sliderValue * sliderValue;

    /// <summary>Starts <paramref name="station"/>, replacing whatever was playing.</summary>
    public async Task PlayAsync(Station station)
    {
        int playGeneration = ++generation;
        CloseConnection();
        Station = station;
        StreamTitle = null;
        Problem = null;
        BitrateKbps = 0;
        Codec = "";
        State = PlayerState.Connecting;
        Changed?.Invoke();

        string listenKey = RipshoutSettings.ListenKey;
        if (StationCatalog.NeedsListenKey(station.Network) && listenKey.Length == 0)
        {
            Fail($"{StationCatalog.DisplayName(station.Network)} needs a listen key. Add yours in Settings.");
            return;
        }

        IReadOnlyList<string> urls;
        try
        {
            urls = await Task.Run(() => StationCatalog.ResolveStreamUrlsAsync(
                station, listenKey, RipshoutSettings.StreamQuality, CancellationToken.None));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (playGeneration == generation)
            {
                Fail(ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden }
                    ? "The station refused the listen key. Check it in Settings."
                    : "Couldn't reach the station. Check your connection.");
            }
            return;
        }

        if (playGeneration != generation)
        {
            return;
        }
        if (urls.Count == 0)
        {
            Fail("The station didn't list any servers to play from.");
            return;
        }

        await ConnectAsync(playGeneration, urls, attempt: 0);
    }

    /// <summary>Stops playing and closes the connection.</summary>
    public void Stop()
    {
        generation++;
        CloseConnection();
        State = PlayerState.Stopped;
        Problem = null;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        generation++;
        connection?.Dispose();
        connection = null;
    }

    // Tries each server in turn; the first that opens and decodes wins. Repeated drops walk the reconnect
    // delays, so a station that is down is retried gently rather than hammered.
    private async Task ConnectAsync(int playGeneration, IReadOnlyList<string> urls, int attempt)
    {
        foreach (string url in urls)
        {
            Station station = Station!;
            Connection? opened = await Task.Run(() => Connection.TryOpenAsync(url, Gain, station));
            if (playGeneration != generation)
            {
                opened?.Dispose();
                return;
            }
            if (opened is null)
            {
                continue;
            }

            connection = opened;
            opened.Input.StreamTitleChanged += (_, e) => Dispatcher.Post(() => OnTitleAnnounced(playGeneration, e.Title));
            opened.Recorder.TrackSaved += path => Dispatcher.Post(() => TrackSaved?.Invoke(path));
            Log.Info($"Playing {station.Name} from {url} ({opened.Input.Info.ContentType}, {opened.Input.Icy.BitrateKbps} kbps)");
            StreamTitle = Clean(opened.Input.Icy.StreamTitle) ?? StreamTitle;
            BitrateKbps = opened.Input.Icy.BitrateKbps;
            Codec = opened.Player.AudioCodec;
            State = PlayerState.Playing;
            Problem = null;
            Changed?.Invoke();
            _ = WatchAsync(playGeneration, urls);
            return;
        }

        if (attempt + 1 >= ReconnectDelays.Length)
        {
            Fail("The station isn't answering. Try again later.");
            return;
        }

        State = PlayerState.Reconnecting;
        Changed?.Invoke();
        await Task.Delay(ReconnectDelays[attempt]);
        if (playGeneration == generation)
        {
            await ConnectAsync(playGeneration, urls, attempt + 1);
        }
    }

    // Notices a stream that stopped (the server dropped it, the network went) and reconnects.
    private async Task WatchAsync(int playGeneration, IReadOnlyList<string> urls)
    {
        while (playGeneration == generation)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (playGeneration != generation || connection is not { } current)
            {
                return;
            }
            if (!current.Player.Ended)
            {
                continue;
            }

            CloseConnection();
            State = PlayerState.Reconnecting;
            Changed?.Invoke();
            await Task.Delay(ReconnectDelays[0]);
            if (playGeneration == generation)
            {
                await ConnectAsync(playGeneration, urls, attempt: 1);
            }
            return;
        }
    }

    private void OnTitleAnnounced(int playGeneration, string? title)
    {
        if (playGeneration != generation)
        {
            return;
        }
        StreamTitle = Clean(title);
        Log.Info($"Now playing: {StreamTitle}");
        Changed?.Invoke();
    }

    private void Fail(string problem)
    {
        Log.Error($"{Station?.Name}: {problem}");
        CloseConnection();
        State = PlayerState.Failed;
        Problem = problem;
        Changed?.Invoke();
    }

    private void CloseConnection()
    {
        Connection? closing = connection;
        connection = null;
        if (closing is not null)
        {
            // Closing joins the decoder's threads, which can take a moment; never on the UI thread.
            _ = Task.Run(closing.Dispose);
        }
    }

    private static string? Clean(string? title)
    {
        string trimmed = title?.Trim() ?? "";
        return trimmed.Length > 0 ? trimmed : null;
    }

    /// <summary>One open stream: the HTTP input carrying the station's bytes and the player decoding them.</summary>
    private sealed class Connection(HttpMediaInput input, MediaPlayer player, Recorder recorder) : IDisposable
    {
        public HttpMediaInput Input { get; } = input;

        public MediaPlayer Player { get; } = player;

        /// <summary>Saves the tracks as they play; every byte the player reads passes through it.</summary>
        public Recorder Recorder { get; } = recorder;

        /// <summary>Connects and starts playing at <paramref name="gain"/> from the first sample, or returns null.</summary>
        public static async Task<Connection?> TryOpenAsync(string url, float gain, Station station)
        {
            var input = new HttpMediaInput(url);
            Recorder? recorder = null;
            try
            {
                await input.OpenAsync();
                recorder = new Recorder(station, input.Info.ContentType, input.Icy.BitrateKbps, input.Icy.Genre, input.Icy.StreamTitle, Recorder.Options.FromSettings);
                // Subscribed before the player reads a byte, so no title is missed. Runs on the reading thread,
                // between the two tracks' bytes: that is where the recording splits.
                Recorder captured = recorder;
                input.StreamTitleChanged += (_, e) => captured.OnTitleChanged(e.Title);
                MediaPlayer player = MediaPlayer.Open(new RecordingStream(input.AsStream(), recorder), new MediaPlayerOptions { Volume = gain });
                if (!player.HasAudio)
                {
                    player.Dispose();
                    recorder.Dispose();
                    input.Dispose();
                    return null;
                }
                return new Connection(input, player, recorder);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error($"Couldn't open {url}", ex);
                recorder?.Dispose();
                input.Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            // The track in progress is incomplete: drop it before the decoder shuts down.
            Recorder.Dispose();
            // The player owns the stream wrapper, not the input under it; the input holds the socket.
            Player.Dispose();
            Input.Dispose();
        }
    }
}
