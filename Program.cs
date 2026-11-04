using Cascade.UI;
using Cascade.UI.Installer.Update;
using Ripshout3;

// Updates come from GitHub Releases. Nothing is downloaded or applied without asking: MainView checks in the
// background and offers a newer version in a banner. Crash rollback after an update belongs to the update shim;
// the app's only part is Updater.MarkHealthy() once it is up. Best effort: a failure here never stops RipShout.
try
{
    string installDir = AppContext.BaseDirectory;
    string appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "3.0.0";

    Updater.Configure(
        new UpdateConfig
        {
            ManifestUrl = "https://github.com/Echostorm44/Ripshout3/releases/latest/download/manifest.json",
            CheckOnStartup = true,
            CheckInterval = TimeSpan.FromHours(6),
            AutoDownload = false,
            Channel = "stable",
        },
        appVersion,
        "win-x64",
        installDir,
        Environment.ProcessPath ?? System.IO.Path.Combine(installDir, "Ripshout3.exe"));
}
catch
{
    // The app runs regardless.
}

StartupOptions.Parse(args);

App.Run<MainView>(config =>
{
    config.Theme = new RipshoutTheme();
    config.SingleInstance = true;
    config.WindowSize = WindowPlacement.SavedSize ?? new Size(1280, 800);
    // A mostly still window: the CPU renderer draws it with a third of the memory the GPU path
    // needs and less CPU (measured 2026-11-02, AOT, playing: CPU 81 MB / 0.3% of a core,
    // GPU 225 MB / 1.2%). CASCADE_GPU=auto still selects the GPU.
    config.Gpu = GpuPreference.Cpu;
});
