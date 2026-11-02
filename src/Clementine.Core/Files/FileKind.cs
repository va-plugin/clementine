namespace Clementine.Core;

/// <summary>Broad family a file belongs to. Drives which operations the wheel offers.</summary>
public enum FileKind
{
    Unknown,
    Folder,
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Spreadsheet,
    Presentation,
    Ebook,
    Text,
    Subtitle,
    Archive,
}
