using Clementine.Engines.Files.Archives;
using Clementine.Engines.Files.Operations;

namespace Clementine.Engines.Files;

/// <summary>
/// Archives (extract, list, test, repack; folder and selection compression), checksums, subtitles
/// (SRT/VTT/ASS/SSA/SBV conversion, timing, cleaning, encoding repair) and data files (JSON, YAML, CSV, XML).
/// </summary>
public sealed class FilesOperationProvider : IOperationProvider
{
    readonly ExtractionLimits _limits;

    public FilesOperationProvider()
        : this(ExtractionLimits.Default)
    {
    }

    internal FilesOperationProvider(ExtractionLimits limits) => _limits = limits;

    public string Name => "Files";

    public IEnumerable<IOperation> CreateOperations(EngineContext context) =>
    [
        .. ArchiveOperations.Create(context, _limits),
        .. CompressOperations.Create(context),
        new ChecksumOperation(ChecksumAlgorithm.Sha256, 20),
        new ChecksumOperation(ChecksumAlgorithm.Sha1, 21),
        new ChecksumOperation(ChecksumAlgorithm.Md5, 22),
        new ChecksumOperation(ChecksumAlgorithm.Crc32, 23),
        .. SubtitleOperations.Create(),
        .. DataOperations.Create(),
    ];
}
