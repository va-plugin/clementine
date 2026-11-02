using Clementine.Core;

namespace Clementine.Engines.Documents.Tests.Fixtures;

/// <summary>A fact that needs LibreOffice; skipped (not failed) on machines without it.</summary>
public sealed class LibreOfficeFactAttribute : FactAttribute
{
    public LibreOfficeFactAttribute()
    {
        if (Tools.LibreOffice is null)
            Skip = "LibreOffice isn't installed on this machine.";
    }

    internal static readonly ToolLocator Tools = new(() => new ClementineSettings());
}

/// <summary>A fact that needs Microsoft Edge; skipped (not failed) on machines without it.</summary>
public sealed class EdgeFactAttribute : FactAttribute
{
    public EdgeFactAttribute()
    {
        if (LibreOfficeFactAttribute.Tools.Edge is null)
            Skip = "Microsoft Edge isn't installed on this machine.";
    }
}
