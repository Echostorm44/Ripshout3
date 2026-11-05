using Cascade.UI.Installer;

namespace Ripshout3;

/// <summary>
/// The install/uninstall declaration for RipShout, consumed by <c>cascade package --installer</c> to build the
/// single-file setup exe. A per-user install (no admin): files under LocalAppData and shortcuts on the Start Menu
/// and Desktop. The bundled uninstaller removes all of it.
/// </summary>
// Must be PUBLIC: `cascade package --installer` finds it by the public modifier and the generated wizard
// instantiates it from a separate assembly.
[Installer]
public sealed class RipshoutInstaller : CascadeInstaller
{
    public override InstallerConfig Configure() => new()
    {
        AppId = "C595345A-BA2A-4AFD-8929-A5DFA979A5D1",
        AppName = "Ripshout3",                   // drives the exe name (Ripshout3.exe) and install folder
        Version = ResolveVersion(),
        Publisher = "Echostorm",
        Description = "An internet radio player that shows what's playing and can record it.",
        // Not %LOCALAPPDATA%\Ripshout3 itself: that is where RipShout keeps its data (AppPaths.DataFolder).
        InstallDir = InstallDir.LocalAppData(@"Programs\Ripshout3"),
        Output = "Ripshout3-Setup",
        RequiresAdmin = false,                   // per-user: LocalAppData + HKCU only
        Shortcuts =
        [
            new Shortcut { Name = "RipShout", TargetPath = "Ripshout3.exe", Location = ShortcutLocation.StartMenu },
            new Shortcut { Name = "RipShout", TargetPath = "Ripshout3.exe", Location = ShortcutLocation.Desktop },
        ],
    };

    public override IReadOnlyList<InstallFile> Files =>
    [
        // The whole published app (Ripshout3.exe + the native renderer libraries and fonts) staged by the packager.
        InstallFile.Directory("publish/*", dest: Dir.App, recursive: true),
    ];

    /// <summary>
    /// The app assembly's version as <c>major.minor.patch</c>. CI stamps the tag version into the build, so the
    /// setup, the Apps list entry and upgrade detection all report the released version.
    /// </summary>
    private static string ResolveVersion()
    {
        Version? version = typeof(RipshoutInstaller).Assembly.GetName().Version;
        return version is null ? "3.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
