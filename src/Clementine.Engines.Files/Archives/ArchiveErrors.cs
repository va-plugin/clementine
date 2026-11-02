using SharpCompress.Common;

namespace Clementine.Engines.Files.Archives;

/// <summary>Friendly, consistent messages for everything that can go wrong with an archive.</summary>
internal static class ArchiveErrors
{
    public const string ExtractHint = "Use Extract › With password… and enter the password.";
    public const string TestHint = "Use Test › With password… and enter the password.";
    public const string RepackHint = "Extract it with its password first, then compress the folder.";

    const int ErrorSharingViolation = unchecked((int)0x80070020);
    const int ErrorLockViolation = unchecked((int)0x80070021);
    const int ErrorDiskFull = unchecked((int)0x80070070);
    const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static OperationException PasswordRequired(string hint) =>
        new($"This archive is password-protected. {hint}");

    public static OperationException WrongPassword(Exception? inner = null) =>
        new("That password isn't right for this archive.", inner?.Message, inner);

    public static OperationException UnsafePath(string entryPath) =>
        new($"Clementine didn't extract this archive because an entry points outside its folder (\"{Shorten(entryPath)}\"). " +
            "Malicious archives use this trick to overwrite other files.");

    public static OperationException Bomb(long limit, long archiveBytes) =>
        new($"This archive expands to more than {ByteSize.Format(limit)} from {ByteSize.Format(archiveBytes)}, " +
            "which looks like a decompression bomb, so Clementine stopped.");

    public static OperationException TooManyEntries(long max) =>
        new($"This archive has more than {max:N0} entries, which is more than Clementine will extract at once.");

    public static OperationException NotEnoughSpace(long needed, long free) =>
        new($"Not enough free disk space: the archive needs about {ByteSize.Format(needed)} but only {ByteSize.Format(free)} is free.");

    public static OperationException RunningOutOfSpace() =>
        new("Clementine stopped extracting because the disk is nearly full.");

    public static OperationException SizeMismatch(string entryPath, bool encrypted = false) => encrypted
        ? DidNotDecrypt(entryPath)
        : new($"The archive is damaged: \"{Shorten(entryPath)}\" holds more data than its header says.");

    public static OperationException CrcMismatch(string entryPath, bool encrypted) => encrypted
        ? DidNotDecrypt(entryPath)
        : new($"The archive is damaged: \"{Shorten(entryPath)}\" failed its CRC check.");

    static OperationException DidNotDecrypt(string entryPath) =>
        new($"\"{Shorten(entryPath)}\" didn't decrypt correctly — the password is wrong or the archive is damaged.");

    const string WrongPasswordOrDamaged = "The password is wrong or the archive is damaged.";

    /// <summary>Exceptions that mean "this archive can't be read" (as opposed to bugs).</summary>
    public static bool IsReadFailure(Exception ex) => ex is SharpCompressException
        or InvalidDataException
        or EndOfStreamException
        or IOException
        or UnauthorizedAccessException
        or FormatException
        or NotSupportedException
        or NotImplementedException
        or OverflowException
        or IndexOutOfRangeException
        or ArgumentOutOfRangeException
        or InvalidOperationException
        || ex.GetType().FullName == "SharpCompress.Compressors.LZMA.DataErrorException";

    /// <summary>
    /// <see cref="IsReadFailure(Exception)"/>, plus the decoder crashes a wrong password causes while a password is
    /// in use and the data being decoded is (or may be) encrypted — see <see cref="IsGarbledDecryption"/>.
    /// </summary>
    public static bool IsReadFailure(Exception ex, string? password, ArchiveEntry? entry) =>
        IsReadFailure(ex) || IsGarbledDecryption(ex, password, entry);

    /// <summary>
    /// True when <paramref name="ex"/> is what a decoder throws on wrongly decrypted bytes. 7z has no password check at
    /// all and ZipCrypto's single check byte lets one wrong password in 256 through, so a wrong password reaches
    /// LZMA/Deflate/PPMd as random data, which fails in random ways: data errors, invalid parameters, index and even
    /// null-reference errors inside SharpCompress. 7-Zip picks a random IV for every archive, so which of those a given
    /// wrong password produces changes from archive to archive. Never applies to entries known to be unencrypted, so
    /// real damage (and bugs) in plain archives keep their own messages.
    /// </summary>
    public static bool IsGarbledDecryption(Exception ex, string? password, ArchiveEntry? entry) =>
        !string.IsNullOrEmpty(password)
        && entry is not { IsEncrypted: false }
        && ex is not OperationException and not OperationCanceledException and not NotSupportedException and not NotImplementedException
        && (ex is InvalidDataException or FormatException or IndexOutOfRangeException or ArgumentException or OverflowException
                or InvalidOperationException or NullReferenceException or InvalidCastException
            || ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true
            // An encrypted entry's stream "ending early" is garbage too; while opening (no entry yet) it means truncation.
            || (entry is { IsEncrypted: true } && ex is EndOfStreamException));

    /// <summary>
    /// Maps a library or I/O failure to a friendly <see cref="OperationException"/>. <paramref name="nameEntry"/> false
    /// leaves the entry's name out of the message (for callers that prefix it themselves).
    /// </summary>
    public static OperationException Translate(Exception ex, string? password, ArchiveEntry? entry, string hint = ExtractHint, bool nameEntry = true)
    {
        if (ex is OperationException friendly)
            return friendly;

        var details = $"{ex.GetType().Name}: {ex.Message}" + (entry is null ? "" : $"\nEntry: {entry.Path}");
        var hasPassword = !string.IsNullOrEmpty(password);
        var where = entry is null || !nameEntry ? "" : $" (\"{Shorten(entry.Path)}\")";

        switch (ex)
        {
            case IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull:
                return new OperationException("The disk is full.", details, ex);
            case IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation:
                return new OperationException("The archive is open in another program. Close it and try again.", details, ex);
            case PathTooLongException:
                return new OperationException($"A path in the archive is too long for Windows{where}.", details, ex);
            case UnauthorizedAccessException:
                return new OperationException("Windows denied access while reading or writing the archive.", details, ex);
            case CryptographicException:
                return hasPassword ? new OperationException("That password isn't right for this archive.", details, ex) : PasswordRequired(hint);
            case IncompleteArchiveException or MultiVolumeExtractionException or MultipartStreamRequiredException:
                return new OperationException(
                    "This is part of a multi-part archive and some parts are missing. Keep all parts in one folder and start from the first.", details, ex);
            case NotSupportedException or NotImplementedException:
                return new OperationException($"This archive uses a compression method or feature Clementine can't read{where}.", details, ex);
        }

        var message = ex.Message;
        if (message.Contains("password", StringComparison.OrdinalIgnoreCase))
            return hasPassword ? new OperationException("That password isn't right for this archive.", details, ex) : PasswordRequired(hint);
        if (IsGarbledDecryption(ex, password, entry))
            return new OperationException(WrongPasswordOrDamaged, details, ex);
        if (ex is EndOfStreamException)
            return new OperationException($"The archive is incomplete — it ends unexpectedly{where}. It may not have finished downloading.", details, ex);
        if (ex is IOException)
            return new OperationException($"Couldn't read the archive: {ex.Message}", details, ex);
        return new OperationException($"The archive is damaged or isn't a format Clementine can read{where}.", details, ex);
    }

    public static string Shorten(string path) => path.Length <= 80 ? path : path[..77] + "…";
}
