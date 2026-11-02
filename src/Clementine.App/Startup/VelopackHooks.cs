using System.IO;

namespace Clementine.App.Startup;

/// <summary>When <see cref="Program.Main"/> hands the launch to Velopack first.</summary>
internal static class VelopackHooks
{
    /// <summary>The argument prefix of Velopack's install/update/uninstall hook launches (--veloapp-install …).</summary>
    public const string HookPrefix = "--veloapp-";

    /// <summary>The arguments Clementine restarts with after installing an update: back to the tray, no window.</summary>
    public static readonly IReadOnlyList<string> RestartArgs = ["--background"];

    /// <summary>
    /// The version Velopack just installed when this launch is the restart after an update (Velopack's OnRestarted hook);
    /// the shell then says so with a card. Null for every other launch.
    /// </summary>
    public static string? RestartedAfterUpdate { get; set; }

    /// <summary>
    /// True for Velopack's hook launches and for a Velopack install (see <see cref="IsVelopackInstall"/>). A development
    /// build or a copied folder has neither: Velopack would only log "not installed" to
    /// %LOCALAPPDATA%\velopack\velopack.log on every launch, so it isn't started at all.
    /// </summary>
    public static bool Applies(IReadOnlyList<string> args, string exeDirectory) =>
        args.Any(a => a.StartsWith(HookPrefix, StringComparison.OrdinalIgnoreCase)) || IsVelopackInstall(exeDirectory);

    /// <summary>
    /// Whether <paramref name="exeDirectory"/> is a Velopack install — Setup.exe's or the portable zip's — which has
    /// Update.exe in the folder above the exe (as Velopack's own locator checks).
    /// </summary>
    public static bool IsVelopackInstall(string exeDirectory)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(exeDirectory));
            return parent is not null && File.Exists(Path.Combine(parent, "Update.exe"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
