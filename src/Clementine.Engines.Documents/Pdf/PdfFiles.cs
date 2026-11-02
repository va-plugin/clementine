using Clementine.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>
/// Opens PDFs with PDFsharp from an in-memory copy (the input file is never held open or written) and turns
/// library exceptions into friendly messages.
/// </summary>
internal static class PdfFiles
{
    /// <summary>Opens <paramref name="path"/>. Throws a friendly <see cref="OperationException"/> for protected or damaged files.</summary>
    public static PdfDocument Open(string path, PdfDocumentOpenMode mode, string? password = null)
    {
        var bytes = ReadBytes(path);
        var name = Path.GetFileName(path);
        var passwordRequested = false;
        void Provider(PdfPasswordProviderArgs args)
        {
            passwordRequested = true;
            args.Abort = true;
        }

        PdfDocument? document;
        try
        {
            var stream = new MemoryStream(bytes, writable: false);
            document = string.IsNullOrEmpty(password)
                ? PdfReader.Open(stream, mode, Provider)
                : PdfReader.Open(stream, password, mode, Provider);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OperationException)
        {
            if (passwordRequested || LooksLikePasswordProblem(ex))
                throw PasswordProblem(name, password, mode, ex);
            // Wrong xref offsets are the commonest damage, and PDFsharp (unlike PDFium and PdfPig) can't recover on its own.
            document = TryRepaired(bytes, password, mode, Provider);
            if (document is null)
                throw new OperationException($"Couldn't read \"{name}\" — it may be damaged or not a real PDF.", ex.ToString(), ex);
        }

        // PDFsharp returns null (rather than throwing) when the password callback aborts.
        if (document is null || passwordRequested)
        {
            document?.Dispose();
            throw PasswordProblem(name, password, mode, null);
        }
        return document;
    }

    static PdfDocument? TryRepaired(byte[] bytes, string? password, PdfDocumentOpenMode mode, PdfPasswordProvider provider)
    {
        if (bytes.LongLength > 512L * 1024 * 1024 || PdfRepair.RebuildXref(bytes) is not { } repaired)
            return null;
        PdfDocument? document = null;
        try
        {
            var stream = new MemoryStream(repaired, writable: false);
            document = string.IsNullOrEmpty(password)
                ? PdfReader.Open(stream, mode, provider)
                : PdfReader.Open(stream, password, mode, provider);
            // Only accept the repair if every page is really there (a truncated file can rebuild into a hollow shell).
            if (document is null || document.PageCount == 0)
                throw new InvalidDataException("No pages after repair.");
            foreach (var page in document.Pages)
                _ = page.MediaBox;
            return document;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OperationException)
        {
            document?.Dispose();
            return null;
        }
    }

    static OperationException PasswordProblem(string name, string? password, PdfDocumentOpenMode mode, Exception? inner)
    {
        if (string.IsNullOrEmpty(password))
        {
            return mode == PdfDocumentOpenMode.Modify
                ? new OperationException($"\"{name}\" is protected against changes. Use Unlock to remove the protection first.", inner?.Message, inner)
                : new OperationException($"\"{name}\" is password-protected. Use Unlock to remove the password first.", inner?.Message, inner);
        }
        return mode == PdfDocumentOpenMode.Modify
            ? new OperationException(OwnerPasswordNeeded, inner?.Message, inner)
            : new OperationException("That password isn't right for this PDF.", inner?.Message, inner);
    }

    /// <summary>Message when a password opens the file but doesn't allow editing (it has a separate owner password).</summary>
    public const string OwnerPasswordNeeded = "That password opens the PDF but doesn't allow changing it.";

    /// <summary>Opens for copying pages into a new document.</summary>
    public static PdfDocument OpenForImport(string path, string? password = null) => Open(path, PdfDocumentOpenMode.Import, password);

    /// <summary>Opens for editing in memory; the result must be saved to a new path.</summary>
    public static PdfDocument OpenForEdit(string path, string? password = null) => Open(path, PdfDocumentOpenMode.Modify, password);

    /// <summary>Whether the file needs a password to open (cheap check through PDFsharp).</summary>
    public static bool RequiresPassword(string path)
    {
        try
        {
            using var _ = Open(path, PdfDocumentOpenMode.Import);
            return false;
        }
        catch (OperationException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    public static byte[] ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it may be open in another program.", ex.Message, ex);
        }
    }

    /// <summary>Saves with sensible compression; PDFsharp writes only objects reachable from the catalog.</summary>
    public static void Save(PdfDocument document, string path)
    {
        document.Options.CompressContentStreams = true;
        document.Options.NoCompression = false;
        try
        {
            document.Save(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new OperationException("Couldn't write the PDF — the source may use features PDFsharp can't rewrite.", ex.ToString(), ex);
        }
    }

    /// <summary>Copies the document information (title, author…) from one document to another.</summary>
    public static void CopyInfo(PdfDocument from, PdfDocument to)
    {
        var info = from.Info;
        to.Info.Title = info.Title;
        to.Info.Author = info.Author;
        to.Info.Subject = info.Subject;
        to.Info.Keywords = info.Keywords;
        to.Info.Creator = info.Creator;
        if (info.Elements.ContainsKey("/CreationDate"))
            to.Info.CreationDate = info.CreationDate;
    }

    static bool LooksLikePasswordProblem(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var message = e.Message;
            if (message.Contains("password", StringComparison.OrdinalIgnoreCase) || message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
