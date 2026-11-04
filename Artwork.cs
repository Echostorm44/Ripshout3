using System.Security.Cryptography;
using System.Text;
using Cascade.UI;
using SharpImage.Formats;
using SharpImage.Image;
using SharpImage.Transform;
using ImageFrame = SharpImage.Image.ImageFrame;
using Path = System.IO.Path;

namespace Ripshout3;

/// <summary>
/// Station and album artwork for the grid, by URL. Each picture is downloaded once, shrunk to
/// <see cref="TilePixels"/> and saved under <see cref="AppPaths.ArtFolder"/>, so later runs decode a small file
/// rather than fetching and decoding the original. Only what the grid asks for is decoded, and only the most
/// recently used <see cref="Capacity"/> stay in memory: a few hundred full-size channel images would otherwise
/// hold gigabytes for pictures nobody is looking at.
/// </summary>
internal sealed class ArtworkCache
{
    /// <summary>Square size the tiles are stored and decoded at: sharp at 150% scaling for a 160-point tile.</summary>
    public const int TilePixels = 240;
    private const int Capacity = 160;
    private const int ParallelLoads = 4;

    private readonly Dictionary<string, LinkedListNode<(string Url, ImageSource? Image)>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Url, ImageSource? Image)> recency = new();
    private readonly HashSet<string> loading = new(StringComparer.Ordinal);
    private readonly Queue<string> wanted = new();
    private int running;

    /// <summary>Raised on the UI thread when a picture becomes available.</summary>
    public event Action? ArtworkReady;

    /// <summary>
    /// The picture for <paramref name="url"/> if it is ready. Otherwise null, and it is fetched in the
    /// background; <see cref="ArtworkReady"/> fires when it arrives. A URL that failed stays null.
    /// </summary>
    public ImageSource? Get(string url)
    {
        if (url.Length == 0)
        {
            return null;
        }

        if (entries.TryGetValue(url, out var node))
        {
            recency.Remove(node);
            recency.AddFirst(node);
            return node.Value.Image;
        }

        if (loading.Add(url))
        {
            wanted.Enqueue(url);
            while (running < ParallelLoads && wanted.Count > 0)
            {
                running++;
                _ = LoadNextAsync();
            }
        }
        return null;
    }

    // Takes queued URLs until none are left; several run at once, so a slow server holds up only its own tile.
    private async Task LoadNextAsync()
    {
        try
        {
            while (wanted.TryDequeue(out string? url))
            {
                (byte[] Pixels, int Width, int Height)? picture = await Task.Run(() => LoadAsync(url));
                loading.Remove(url);
                Store(url, picture is { } p ? ImageSource.FromPixels(p.Pixels, p.Width, p.Height) : null);
                ArtworkReady?.Invoke();
            }
        }
        finally
        {
            running--;
        }
    }

    private void Store(string url, ImageSource? image)
    {
        var node = recency.AddFirst((url, image));
        entries[url] = node;
        while (recency.Count > Capacity)
        {
            var oldest = recency.Last!;
            recency.RemoveLast();
            entries.Remove(oldest.Value.Url);
            oldest.Value.Image?.Dispose();
        }
    }

    private static async Task<(byte[] Pixels, int Width, int Height)?> LoadAsync(string url)
    {
        string cachedPath = CachedPathFor(url);
        try
        {
            if (File.Exists(cachedPath))
            {
                using ImageFrame stored = FormatRegistry.Read(cachedPath);
                return (ImagePixels.ToRgba(stored), (int)stored.Columns, (int)stored.Rows);
            }

            byte[] original = await Web.Client.GetByteArrayAsync(new Uri(url));
            using ImageFrame decoded = FormatRegistry.Read(original);
            using ImageFrame small = Resize.Thumbnail(decoded, TilePixels, TilePixels);
            SaveQuietly(small, cachedPath);
            return (ImagePixels.ToRgba(small), (int)small.Columns, (int)small.Rows);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No network, a dead link or an unreadable picture: the tile shows its fallback.
            return null;
        }
    }

    private static void SaveQuietly(ImageFrame frame, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp.png";
            FormatRegistry.Write(frame, temporary);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The disk copy only saves a download next time.
        }
    }

    private static string CachedPathFor(string url)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(AppPaths.ArtFolder, "tiles", hash[..2], hash + ".png");
    }
}

/// <summary>Turns SharpImage frames into the 8-bit RGBA that <see cref="ImageSource.FromPixels"/> takes.</summary>
internal static class ImagePixels
{
    public static byte[] ToRgba(ImageFrame frame)
    {
        int width = (int)frame.Columns;
        int height = (int)frame.Rows;
        var rgba = GC.AllocateUninitializedArray<byte>(width * height * 4);
        int channels = frame.NumberOfChannels;
        bool hasAlpha = frame.HasAlpha;
        int colorChannels = hasAlpha ? channels - 1 : channels;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ushort> row = frame.GetPixelRow(y);
            Span<byte> destination = rgba.AsSpan(y * width * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<ushort> pixel = row.Slice(x * channels, channels);
                // SharpImage stores 16-bit channels; the high byte is the 8-bit value (an 8-bit source is b * 257).
                byte red = (byte)(pixel[0] >> 8);
                byte green = colorChannels >= 3 ? (byte)(pixel[1] >> 8) : red;
                byte blue = colorChannels >= 3 ? (byte)(pixel[2] >> 8) : red;
                byte alpha = hasAlpha ? (byte)(pixel[channels - 1] >> 8) : (byte)255;
                int o = x * 4;
                destination[o] = red;
                destination[o + 1] = green;
                destination[o + 2] = blue;
                destination[o + 3] = alpha;
            }
        }
        return rgba;
    }
}
