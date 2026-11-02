using Clementine.Engines.Documents.External;

namespace Clementine.Engines.Documents.Common;

/// <summary>What document operations share: the engine context and the external-tool bridges.</summary>
internal sealed class DocumentServices(EngineContext context, bool useLibreOffice = true)
{
    public EngineContext Context { get; } = context;

    public LibreOffice LibreOffice { get; } = new(context.Tools, context.Log, useLibreOffice);

    public EdgePrinter Edge { get; } = new(context.Tools);
}
