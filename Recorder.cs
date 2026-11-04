using System.Buffers.Binary;
using System.Text;
using Path = System.IO.Path;

namespace Ripshout3;

/// <summary>
/// Saves a station's tracks as they play, byte for byte as the station sends them (no re-encoding). The stream the
/// player decodes passes through <see cref="RecordingStream"/>, which hands every byte here; a new track title
/// starts a new file at exactly that byte, since the title arrives in-band, between the two tracks' bytes.
/// <para>
/// A track is kept only when it was heard whole: the one already playing when the station was tuned in, and the
/// one cut off by stopping, switching or a dropped connection, are thrown away. Advertisements and anything shorter
/// than <see cref="MinimumSeconds"/> (station IDs, jingles) are too. A finished track is tagged (artist, title,
/// album, year, genre, cover) and moved to the music folder, under its genre. A track already there is replaced only
/// by a higher-bitrate recording of about the same length, never by a different version.
/// </para>
/// </summary>
internal sealed class Recorder : IDisposable
{
    public const int MinimumSeconds = 60;

    /// <summary>Where recordings go and whether to make them, read at the start of each track.</summary>
    internal sealed record Options(Func<bool> Enabled, Func<string> RecordingFolder, Func<string> MusicFolder,
        Func<Station, string, string, Task<TrackDetails>> Lookup)
    {
        /// <summary>The user's settings, and the shared track lookup.</summary>
        public static Options FromSettings { get; } = new(
            () => RipshoutSettings.RecordingEnabled,
            () => RipshoutSettings.RecordingFolder,
            () => RipshoutSettings.MusicFolder,
            TrackLookup.GetAsync);
    }

    private readonly Options options;

    private readonly Lock gate = new();
    private readonly Station station;
    private readonly string extension;
    private readonly int bitrateKbps;
    private readonly string genre;

    private FileStream? file;
    private string? path;
    private string? title;
    private long bytes;
    private bool whole;
    private bool seenTitle;
    private bool disposed;
    private Task pending = Task.CompletedTask;

    /// <summary>The most recent finished track still being saved (for tests).</summary>
    internal Task Pending => pending;

    /// <param name="station">The station being recorded.</param>
    /// <param name="contentType">The stream's MIME type, which decides the file extension.</param>
    /// <param name="bitrateKbps">The stream's bitrate as the station states it, or 0.</param>
    /// <param name="genre">The station's ICY genre, or null.</param>
    /// <param name="currentTitle">The title already playing when the stream opened: recorded only to be discarded.</param>
    /// <param name="options">Folders, the on/off setting and the track lookup; <see cref="Options.FromSettings"/> in the app.</param>
    public Recorder(Station station, string? contentType, int bitrateKbps, string? genre, string? currentTitle, Options options)
    {
        this.options = options;
        this.station = station;
        this.bitrateKbps = bitrateKbps;
        extension = ExtensionFor(contentType);
        this.genre = string.IsNullOrWhiteSpace(genre) ? station.Name : genre.Trim();
        title = currentTitle;
        seenTitle = currentTitle is not null;
        // Nothing is written for the partial first track; it would only be deleted.
    }

    /// <summary>Raised on a background thread when a finished track has been saved: its final path.</summary>
    public event Action<string>? TrackSaved;

    /// <summary>The title of the track being recorded whole, or null while waiting for the first full track.</summary>
    public string? RecordingTitle
    {
        get
        {
            lock (gate)
            {
                return whole && file is not null ? title : null;
            }
        }
    }

    /// <summary>The bytes the player just read. Called on the stream's reading thread.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (file is null)
            {
                return;
            }
            try
            {
                file.Write(data);
                bytes += data.Length;
            }
            catch (IOException ex)
            {
                Log.Error($"Recording {path}", ex);
                Abandon();
            }
        }
    }

    /// <summary>The station announced a new track: finish the one before and start this one.</summary>
    public void OnTitleChanged(string? newTitle)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            // Shortly after connecting, the station names the track already under way: still the partial one.
            if (!seenTitle)
            {
                seenTitle = true;
                title = string.IsNullOrWhiteSpace(newTitle) ? null : newTitle.Trim();
                return;
            }

            (string Path, string Title, long Bytes)? finished = whole && file is not null && path is not null && title is not null
                ? (path, title, bytes)
                : null;
            file?.Dispose();
            file = null;
            if (finished is null && path is not null)
            {
                TryDelete(path);
            }
            path = null;

            title = string.IsNullOrWhiteSpace(newTitle) ? null : newTitle.Trim();
            bytes = 0;
            whole = true;
            if (title is not null && options.Enabled())
            {
                Start(title);
            }

            if (finished is { } track)
            {
                pending = Task.Run(() => FinishAsync(track.Path, track.Title, track.Bytes));
            }
        }
    }

    /// <summary>Stops recording; the track in progress is incomplete, so its file is deleted.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Abandon();
        }
    }

    private void Start(string trackTitle)
    {
        try
        {
            string folder = Path.Combine(options.RecordingFolder(), TrackLookup.SafeName(station.Name));
            Directory.CreateDirectory(folder);
            path = UniquePath(Path.Combine(folder, TrackLookup.SafeName(trackTitle) + extension));
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
            // Look the track up now, while the station still lists it as playing.
            (string artist, string song) = MainView.SplitStreamTitle(trackTitle);
            _ = options.Lookup(station, artist, song);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Error($"Couldn't start recording {trackTitle} in {options.RecordingFolder()}", ex);
            file?.Dispose();
            file = null;
            path = null;
        }
    }

    private void Abandon()
    {
        file?.Dispose();
        file = null;
        if (path is not null)
        {
            TryDelete(path);
        }
        path = null;
        whole = false;
    }

    private async Task FinishAsync(string recordedPath, string trackTitle, long recordedBytes)
    {
        try
        {
            int seconds = bitrateKbps > 0 ? (int)(recordedBytes * 8 / (bitrateKbps * 1000L)) : int.MaxValue;
            (string artist, string song) = MainView.SplitStreamTitle(trackTitle);
            if (seconds < MinimumSeconds || artist.Length == 0)
            {
                Log.Info($"Not keeping \"{trackTitle}\" ({seconds} s{(artist.Length == 0 ? ", no artist" : "")})");
                TryDelete(recordedPath);
                return;
            }

            TrackDetails details = await options.Lookup(station, artist, song);
            if (details.IsAdvertisement)
            {
                Log.Info($"Not keeping \"{trackTitle}\": the station marks it as an advertisement");
                TryDelete(recordedPath);
                return;
            }

            string genreFolder = TrackLookup.SafeName(genre);
            string finalPath = Path.Combine(options.MusicFolder(), genreFolder,
                TrackLookup.SafeName($"{details.Artist} - {details.Title}") + extension);
            if (File.Exists(finalPath) && !IsBetterCopy(finalPath, seconds))
            {
                Log.Info($"Already have {finalPath}; not keeping this copy");
                TryDelete(recordedPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            string temporary = finalPath + ".saving";
            using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[]? cover = details.CoverPath is { } coverPath && File.Exists(coverPath) ? await File.ReadAllBytesAsync(coverPath) : null;
                output.Write(Id3Tag.Build(details, genre, station.Name, bitrateKbps, seconds, cover));
                using FileStream audio = File.OpenRead(recordedPath);
                await audio.CopyToAsync(output);
            }
            File.Move(temporary, finalPath, overwrite: true);
            TryDelete(recordedPath);
            Log.Info($"Saved {finalPath} ({seconds / 60}:{seconds % 60:00}, {bitrateKbps} kbps)");
            TrackSaved?.Invoke(finalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Error($"Couldn't save \"{trackTitle}\"", ex);
        }
    }

    // A copy already saved is replaced only by a higher bitrate of the same recording (within 10% of its length).
    private bool IsBetterCopy(string existingPath, int seconds)
    {
        (int existingKbps, int existingSeconds) = Id3Tag.ReadRipshoutInfo(existingPath);
        bool sameLength = existingSeconds > 0 && Math.Abs(existingSeconds - seconds) <= existingSeconds / 10;
        return bitrateKbps > existingKbps && sameLength;
    }

    private static string ExtensionFor(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        { } type when type.Contains("aac", StringComparison.Ordinal) => ".aac",
        { } type when type.Contains("ogg", StringComparison.Ordinal) => ".ogg",
        _ => ".mp3",
    };

    private static string UniquePath(string candidate)
    {
        if (!File.Exists(candidate))
        {
            return candidate;
        }
        string folder = Path.GetDirectoryName(candidate)!;
        string name = Path.GetFileNameWithoutExtension(candidate);
        string ext = Path.GetExtension(candidate);
        for (int i = 2; ; i++)
        {
            string next = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!File.Exists(next))
            {
                return next;
            }
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't delete {filePath}", ex);
        }
    }
}

/// <summary>The stream the player decodes, passing every byte it reads to a <see cref="Recorder"/> on the way.</summary>
internal sealed class RecordingStream(Stream inner, Recorder recorder) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        recorder.Write(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        recorder.Write(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Writes the ID3v2.3 tag a saved track starts with, and reads back what RipShout noted in it.</summary>
internal static class Id3Tag
{
    private const string KbpsField = "RIPSHOUT_KBPS";
    private const string SecondsField = "RIPSHOUT_SECONDS";

    public static byte[] Build(TrackDetails details, string genre, string stationName, int kbps, int seconds, byte[]? cover)
    {
        var frames = new MemoryStream();
        TextFrame(frames, "TPE1", details.Artist);
        TextFrame(frames, "TIT2", details.Title);
        TextFrame(frames, "TALB", details.Album);
        TextFrame(frames, "TYER", details.Year);
        TextFrame(frames, "TCON", details.Genre.Length > 0 ? details.Genre : genre);
        TextFrame(frames, "TRCK", details.TrackNumber > 0 ? details.TrackNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        UserFrame(frames, "Station", stationName);
        UserFrame(frames, KbpsField, kbps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        UserFrame(frames, SecondsField, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (cover is { Length: > 0 })
        {
            // Encoding 0, MIME type, picture type 3 (front cover), empty description.
            var body = new MemoryStream();
            body.WriteByte(0);
            body.Write(Encoding.Latin1.GetBytes(cover.Length > 4 && cover[0] == 0x89 && cover[1] == (byte)'P' ? "image/png" : "image/jpeg"));
            body.WriteByte(0);
            body.WriteByte(3);
            body.WriteByte(0);
            body.Write(cover);
            Frame(frames, "APIC", body.ToArray());
        }

        byte[] content = frames.ToArray();
        var tag = new byte[10 + content.Length];
        "ID3"u8.CopyTo(tag);
        tag[3] = 3; // v2.3
        tag[4] = 0;
        tag[5] = 0;
        WriteSyncSafe(tag.AsSpan(6, 4), content.Length);
        content.CopyTo(tag, 10);
        return tag;
    }

    /// <summary>The bitrate and length RipShout noted in a file it saved, or zeros.</summary>
    public static (int Kbps, int Seconds) ReadRipshoutInfo(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[10];
            if (file.Read(header) != 10 || !header[..3].SequenceEqual("ID3"u8) || header[3] != 3)
            {
                return (0, 0);
            }
            int size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            byte[] content = new byte[size];
            file.ReadExactly(content);

            int kbps = 0;
            int seconds = 0;
            for (int at = 0; at + 10 <= content.Length && content[at] != 0;)
            {
                string id = Encoding.Latin1.GetString(content, at, 4);
                int length = BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(at + 4));
                if (length < 0 || at + 10 + length > content.Length)
                {
                    break;
                }
                if (id == "TXXX")
                {
                    string[] parts = DecodeText(content.AsSpan(at + 10, length)).Split('\0', 2);
                    if (parts.Length == 2 && int.TryParse(parts[1].TrimEnd('\0'), out int value))
                    {
                        kbps = parts[0] == KbpsField ? value : kbps;
                        seconds = parts[0] == SecondsField ? value : seconds;
                    }
                }
                at += 10 + length;
            }
            return (kbps, seconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return (0, 0);
        }
    }

    // Text frames are UTF-16 with a BOM (encoding 1), which every ID3v2.3 reader understands.
    private static void TextFrame(Stream frames, string id, string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        var body = new MemoryStream();
        body.WriteByte(1);
        body.Write(Encoding.Unicode.GetPreamble());
        body.Write(Encoding.Unicode.GetBytes(text));
        Frame(frames, id, body.ToArray());
    }

    private static void UserFrame(Stream frames, string description, string value)
    {
        var body = new MemoryStream();
        body.WriteByte(1);
        body.Write(Encoding.Unicode.GetPreamble());
        body.Write(Encoding.Unicode.GetBytes(description));
        body.Write([0, 0]);
        body.Write(Encoding.Unicode.GetPreamble());
        body.Write(Encoding.Unicode.GetBytes(value));
        Frame(frames, "TXXX", body.ToArray());
    }

    private static string DecodeText(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return "";
        }
        if (body[0] == 0)
        {
            return Encoding.Latin1.GetString(body[1..]);
        }
        // UTF-16 strings, each with its own BOM, separated by a two-byte NUL.
        var text = new StringBuilder();
        ReadOnlySpan<byte> rest = body[1..];
        while (rest.Length >= 2)
        {
            int end = 0;
            while (end + 1 < rest.Length && !(rest[end] == 0 && rest[end + 1] == 0))
            {
                end += 2;
            }
            ReadOnlySpan<byte> part = rest[..end];
            if (part.Length >= 2 && part[0] == 0xFF && part[1] == 0xFE)
            {
                part = part[2..];
            }
            text.Append(Encoding.Unicode.GetString(part));
            rest = end + 2 <= rest.Length ? rest[(end + 2)..] : [];
            if (rest.Length > 0)
            {
                text.Append('\0');
            }
        }
        return text.ToString();
    }

    private static void Frame(Stream frames, string id, byte[] body)
    {
        Span<byte> header = stackalloc byte[10];
        Encoding.Latin1.GetBytes(id, header[..4]);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], body.Length);
        frames.Write(header);
        frames.Write(body);
    }

    private static void WriteSyncSafe(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 21) & 0x7F);
        destination[1] = (byte)((value >> 14) & 0x7F);
        destination[2] = (byte)((value >> 7) & 0x7F);
        destination[3] = (byte)(value & 0x7F);
    }
}
