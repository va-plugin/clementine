using System.Runtime.CompilerServices;
using System.Text;

namespace Clementine.Engines.Files.Tests;

internal static class TestSetup
{
    /// <summary>Legacy code pages (1250–1258, Shift-JIS …) for building fixtures in other encodings.</summary>
    [ModuleInitializer]
    internal static void RegisterCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
