using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Clementine.Core;

namespace Clementine.Wheel.Tests;

/// <summary>
/// Loads the shipped engines (and the shell's actions provider) from the built app folder
/// (<c>%LOCALAPPDATA%\ClementineBuild\bin\Clementine.App\debug</c>) into a private load context, so wheel tests can
/// look at the real catalog without referencing the engine projects. Clementine.Core and Clementine.Wheel are shared
/// with this test process, so the providers implement the very <see cref="IOperationProvider"/> the wheel uses.
/// </summary>
internal static class RealCatalog
{
    public static string AppDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClementineBuild", "bin", "Clementine.App", "debug");

    static readonly Lazy<(IReadOnlyList<IOperationProvider> Providers, string? Problem)> Loaded = new(LoadProviders);

    /// <summary>A catalog of every real provider, or null (with the reason) when the app hasn't been built.</summary>
    public static OperationCatalog? TryCreate(EngineContext context, out string reason)
    {
        var (providers, problem) = Loaded.Value;
        reason = problem ?? "";
        return providers.Count == 0 ? null : new OperationCatalog(context, providers);
    }

    static (IReadOnlyList<IOperationProvider>, string?) LoadProviders()
    {
        var appDll = Path.Combine(AppDirectory, "Clementine.dll");
        if (!File.Exists(appDll))
            return ([], $"{appDll} doesn't exist (build the app first).");
        try
        {
            var context = new AppLoadContext(appDll);
            var app = context.LoadFromAssemblyPath(appDll);
            var bootstrap = app.GetType("Clementine.App.ShellBootstrap", throwOnError: true)!;
            var engines = (IOperationProvider[])bootstrap.GetMethod("EngineProviders", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
            var providers = new List<IOperationProvider>(engines);

            // The shell's file actions (copy path, reveal, shelf …) with a do-nothing desktop.
            var actions = app.GetType("Clementine.App.Actions.ActionsOperationProvider");
            var noShell = app.GetType("Clementine.App.Snapshots.SnapshotRunner+NoShellActions");
            if (actions is not null && noShell is not null)
                providers.Add((IOperationProvider)Activator.CreateInstance(actions, Activator.CreateInstance(noShell, nonPublic: true))!);
            return (providers, null);
        }
        catch (Exception ex)
        {
            return ([], $"Loading the engines from {AppDirectory} failed: {ex}");
        }
    }

    sealed class AppLoadContext(string mainAssembly) : AssemblyLoadContext("Clementine app engines")
    {
        static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase) { "Clementine.Core", "Clementine.Wheel" };

        readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is null || Shared.Contains(name.Name))
                return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

/// <summary>Runs only with CLEMENTINE_REAL_CATALOG=1 and a built app: checks and renders the wheel for the shipped catalog.</summary>
public sealed class RealCatalogFactAttribute : FactAttribute
{
    public RealCatalogFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLEMENTINE_REAL_CATALOG") != "1")
            Skip = "Set CLEMENTINE_REAL_CATALOG=1 (after building the app) to check the wheel against the real engines.";
    }
}
