using System.Runtime.CompilerServices;
using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Tests;

static class TestAssemblySetup
{
    /// <summary>
    /// Applies the engine's ImageMagick setup before any fixture touches Magick.NET, so tests behave like the app
    /// (and ImageMagick's OpenCL cache folder goes to the temp area, not the user's profile).
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => MagickRuntime.EnsureInitialized();
}
