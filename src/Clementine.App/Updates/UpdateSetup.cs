using System.Globalization;
using System.Reflection;
using Clementine.Core;

namespace Clementine.App.Updates;

/// <summary>
/// Where updates come from: the GitHub repository named by <c>&lt;ClementineUpdateRepo&gt;</c> in Directory.Build.props,
/// baked into the assembly as metadata at build time. Empty means this build never checks.
/// </summary>
internal static class UpdateSetup
{
    public const string RepositoryMetadataKey = "ClementineUpdateRepo";

    /// <summary>Shown on the About page when the build names no repository.</summary>
    public const string DefaultProjectUrl = "https://github.com/va-plugin/clementine";

#if DEBUG
    /// <summary>
    /// DEBUG builds only (compiled out of Release, so never shipped): a folder holding a Velopack feed (vpk pack output)
    /// that replaces GitHub, for end-to-end tests of the update flow on this machine.
    /// </summary>
    public const string LocalFeedVariable = "CLEMENTINE_UPDATE_FEED";

    /// <summary>DEBUG builds only: seconds before the first scheduled check (default 60), for the same tests.</summary>
    public const string FirstCheckVariable = "CLEMENTINE_UPDATE_FIRST_CHECK_SECONDS";
#endif

    /// <summary>The update repository, normalised to https://github.com/&lt;owner&gt;/&lt;repo&gt;; null when there is none.</summary>
    public static string? RepositoryUrl { get; } = NormalizeRepository(ReadMetadata(typeof(UpdateSetup).Assembly, RepositoryMetadataKey));

    /// <summary>The project page for the About page and "What's new".</summary>
    public static string ProjectUrl => RepositoryUrl ?? DefaultProjectUrl;

    /// <summary>The release page of <paramref name="version"/> (tags are v&lt;version&gt;).</summary>
    public static string ReleaseUrl(string version) => $"{ProjectUrl}/releases/tag/v{version}";

    /// <summary>The updater for this app: Velopack against GitHub Releases, or nothing to check when no repository is set.</summary>
    public static Updater CreateUpdater(SettingsStore settings, ILog log)
    {
        IUpdateBackend? backend = null;
#if DEBUG
        if (Environment.GetEnvironmentVariable(LocalFeedVariable) is { Length: > 0 } feed)
        {
            log.Warn($"Update: DEBUG build reading the local feed {feed} ({LocalFeedVariable}) instead of GitHub");
            backend = VelopackUpdateBackend.ForLocalFeed(feed, log);
        }
#endif
        if (backend is null && RepositoryUrl is { } repository)
        {
            log.Info($"Update source: {repository} (GitHub Releases, stable only)");
            backend = VelopackUpdateBackend.ForGitHub(repository, log);
        }
        return new Updater(backend, () => settings.Current.CheckForUpdates, log);
    }

    /// <summary>When the first scheduled check runs (60 s after startup; DEBUG builds can shorten it for tests).</summary>
    public static TimeSpan FirstCheckDelay
    {
        get
        {
#if DEBUG
            if (double.TryParse(Environment.GetEnvironmentVariable(FirstCheckVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);
#endif
            return Updater.DefaultFirstCheckDelay;
        }
    }

    /// <summary>
    /// "https://github.com/owner/repo" (trailing slash and ".git" removed) for a GitHub repository URL; null for anything
    /// else, including an empty value.
    /// </summary>
    public static string? NormalizeRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            return null;
        return $"https://github.com/{parts[0]}/{parts[1]}";
    }

    static string? ReadMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}
