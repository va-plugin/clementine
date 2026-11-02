using Clementine.Core;
using Clementine.Engines.Media.Catalog;

namespace Clementine.Engines.Media;

/// <summary>Video and audio operations (FFmpeg): conversions incl. the VJ codecs DXV, HAP and ProRes, plus video and audio tools.</summary>
public sealed class MediaOperationProvider : IOperationProvider
{
    public string Name => "Media";

    public IEnumerable<IOperation> CreateOperations(EngineContext context)
    {
        var kit = new MediaToolkit(context);
        return
        [
            .. VideoConversions.Create(kit),
            .. AudioConversions.Create(kit),
            .. VideoTools.Create(kit),
            .. AudioTools.Create(kit),
        ];
    }
}
