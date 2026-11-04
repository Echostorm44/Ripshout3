namespace Ripshout3;

/// <summary>
/// Command-line options. <c>--play &lt;station id&gt;</c> starts playing that station
/// (<c>DigitallyImported:chillout</c>), and <c>--play last</c> the station played last.
/// </summary>
internal static class StartupOptions
{
    /// <summary>The station to start playing, or null.</summary>
    public static string? PlayStationId { get; private set; }

    public static void Parse(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--play", StringComparison.OrdinalIgnoreCase))
            {
                string value = args[i + 1];
                PlayStationId = string.Equals(value, "last", StringComparison.OrdinalIgnoreCase)
                    ? RipshoutSettings.LastStationId
                    : value;
            }
        }
    }
}
