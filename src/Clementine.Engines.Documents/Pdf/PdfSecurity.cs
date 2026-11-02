using System.Security.Cryptography;
using PdfSharp.Pdf;

namespace Clementine.Engines.Documents.Pdf;

/// <summary>Protect with a password (AES-256) and remove a password.</summary>
internal static class PdfSecurity
{
    public static IEnumerable<IOperation> Create()
    {
        var pdf = DocumentFormats.Pdf;
        yield return new DocumentOperation(
            Info.Tool("pdf.protect", "Protect", null, "Protect PDF with a password",
                "Encrypts a copy (AES-256) so it only opens with the password you choose.", "lock", ".pdf", 60),
            pdf, ProtectAsync)
        {
            Schema = new OptionSchema("Protect with password", [
                new TextField("password", "Password", "") { IsPassword = true, Help = "Anyone who wants to open the copy will need this password." },
                new BoolField("allowPrinting", "Allow printing", true),
                new BoolField("allowCopying", "Allow copying text and images", true),
            ]) { ConfirmLabel = "Protect", Description = "The original stays unprotected; a protected copy is saved next to it." },
        };
        yield return new DocumentOperation(
            Info.Tool("pdf.unlock", "Unlock", null, "Remove PDF password",
                "Saves an unprotected copy. You need the current password.", "unlock", ".pdf", 61),
            pdf, UnlockAsync)
        {
            Schema = new OptionSchema("Remove password", [
                new TextField("password", "Current password", "") { IsPassword = true },
            ]) { ConfirmLabel = "Unlock" },
        };
    }

    static Task<OperationResult> ProtectAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var password = request.Options.GetString("password", "");
        if (password.Length == 0)
            throw new OperationException("Enter a password to protect the PDF with.");
        if (PdfFiles.RequiresPassword(request.Input.Path))
            throw new OperationException("This PDF is already password-protected.");

        using var document = PdfFiles.OpenForEdit(request.Input.Path);
        var allowPrinting = request.Options.GetBool("allowPrinting", true);
        var allowCopying = request.Options.GetBool("allowCopying", true);
        var restricted = !allowPrinting || !allowCopying;

        var settings = document.SecuritySettings;
        settings.UserPassword = password;
        // With restrictions the owner password must differ from the open password, or opening would grant full rights.
        settings.OwnerPassword = restricted ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : password;
        settings.PermitPrint = allowPrinting;
        settings.PermitFullQualityPrint = allowPrinting;
        settings.PermitExtractContent = allowCopying;
        settings.PermitModifyDocument = !restricted;
        settings.PermitAnnotations = !restricted;
        settings.PermitFormsFill = true;
        settings.PermitAssembleDocument = !restricted;
        document.SecurityHandler.SetEncryptionToV5();
        cancellationToken.ThrowIfCancellationRequested();
        return OperationResult.FromOutputs(Outputs.File(request, ".pdf", "protected", path => PdfFiles.Save(document, path)));
    }, cancellationToken);

    static Task<OperationResult> UnlockAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var path = request.Input.Path;
        var password = request.Options.GetString("password", "");
        var needsPassword = PdfFiles.RequiresPassword(path);
        if (!needsPassword && !IsEncrypted(path))
            throw new OperationException("This PDF isn't password-protected.");
        if (needsPassword && password.Length == 0)
            throw new OperationException("Enter the PDF's current password.");
        var openWith = needsPassword ? password : null;

        // Opening for import validates the password (friendly error when it's wrong).
        using var source = PdfFiles.OpenForImport(path, openWith);
        PdfDocument document;
        try
        {
            document = PdfFiles.OpenForEdit(path, openWith ?? (password.Length > 0 ? password : null));
            document.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        }
        catch (OperationException)
        {
            // The open password doesn't allow editing (there's a separate owner password): rebuild from the pages.
            document = new PdfDocument();
            PdfFiles.CopyInfo(source, document);
            foreach (var page in source.Pages)
                document.AddPage(page);
        }

        using (document)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return OperationResult.FromOutputs(Outputs.File(request, ".pdf", "unlocked", working => PdfFiles.Save(document, working)));
        }
    }, cancellationToken);

    /// <summary>Encrypted without an open password (permissions only).</summary>
    static bool IsEncrypted(string path)
    {
        try
        {
            using var document = PdfText.Open(path);
            return document.IsEncrypted;
        }
        catch (OperationException)
        {
            return true;
        }
    }
}
