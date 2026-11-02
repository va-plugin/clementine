using System.IO.Compression;
using System.Text;

namespace Clementine.Engines.Documents.Office;

/// <summary>
/// Spots password-protected Office files before LibreOffice is started, so the user gets a clear message at once instead
/// of a generic failure (or a wait) from a headless LibreOffice that can't ask for the password.
/// </summary>
internal static class ProtectedDocuments
{
    public const string Message = "This document is password-protected. Remove the password in the program that made it, then try again.";

    static readonly string[] OpenXmlExtensions =
        [".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm", ".xlsb", ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm"];

    static readonly string[] OpenDocumentExtensions = [".odt", ".ott", ".ods", ".ots", ".odp", ".otp", ".odg", ".odf"];

    public static void ThrowIfProtected(string path)
    {
        if (IsProtected(path))
            throw new OperationException(Message);
    }

    /// <summary>True for encrypted OOXML (an OLE file holding an "EncryptedPackage") and encrypted OpenDocument packages.</summary>
    public static bool IsProtected(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (OpenXmlExtensions.Contains(extension))
                return IsEncryptedOpenXml(path);
            if (OpenDocumentExtensions.Contains(extension))
                return IsEncryptedOpenDocument(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // Unreadable or not what it claims to be: let the converter report it.
        }
        return false;
    }

    /// <summary>
    /// Encrypted Word/Excel/PowerPoint files are OLE compound files with "EncryptionInfo" and "EncryptedPackage" streams.
    /// Reads the compound file header and the first directory sector only.
    /// </summary>
    static bool IsEncryptedOpenXml(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[512];
        if (stream.Read(header, 0, header.Length) < header.Length)
            return false;
        if (header[0] != 0xD0 || header[1] != 0xCF || header[2] != 0x11 || header[3] != 0xE0)
            return false;
        var sectorShift = BitConverter.ToUInt16(header, 0x1E);
        if (sectorShift is < 9 or > 16)
            return false;
        var sectorSize = 1 << sectorShift;
        var directory = new byte[sectorSize];
        var sector = BitConverter.ToUInt32(header, 0x30);
        // Walk the directory's sector chain (Office puts the DataSpaces storage first, so the encryption streams may
        // sit in the second or third directory sector). A few sectors is plenty: these files have only a handful of entries.
        for (var visited = 0; visited < 32 && sector < 0xFFFFFFFA; visited++)
        {
            var offset = (sector + 1L) * sectorSize;
            if (offset + sectorSize > stream.Length)
                return false;
            stream.Position = offset;
            stream.ReadExactly(directory);
            for (var entry = 0; entry + 128 <= directory.Length; entry += 128)
            {
                var nameBytes = Math.Clamp((int)BitConverter.ToUInt16(directory, entry + 0x40), 0, 64);
                var name = Encoding.Unicode.GetString(directory, entry, Math.Max(0, nameBytes - 2));
                if (name is "EncryptedPackage" or "EncryptionInfo")
                    return true;
            }
            sector = NextSector(stream, header, sectorSize, sector);
        }
        return false;
    }

    /// <summary>Follows the FAT (located through the header's 109 DIFAT slots, enough for files up to several GB).</summary>
    static uint NextSector(Stream stream, byte[] header, int sectorSize, uint sector)
    {
        var perFatSector = sectorSize / 4;
        var fatIndex = sector / perFatSector;
        if (fatIndex >= 109)
            return 0xFFFFFFFE;
        var fatSector = BitConverter.ToUInt32(header, 0x4C + (int)fatIndex * 4);
        var offset = (fatSector + 1L) * sectorSize + sector % perFatSector * 4;
        if (fatSector >= 0xFFFFFFFA || offset + 4 > stream.Length)
            return 0xFFFFFFFE;
        stream.Position = offset;
        Span<byte> next = stackalloc byte[4];
        stream.ReadExactly(next);
        return BitConverter.ToUInt32(next);
    }

    /// <summary>Encrypted OpenDocument files declare &lt;manifest:encryption-data&gt; in their manifest.</summary>
    static bool IsEncryptedOpenDocument(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifest = zip.GetEntry("META-INF/manifest.xml");
        if (manifest is null || manifest.Length > 4 * 1024 * 1024)
            return false;
        using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
        return reader.ReadToEnd().Contains("encryption-data", StringComparison.Ordinal);
    }
}
