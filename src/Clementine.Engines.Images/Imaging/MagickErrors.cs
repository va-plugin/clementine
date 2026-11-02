namespace Clementine.Engines.Images.Imaging;

/// <summary>Turns ImageMagick exceptions into friendly <see cref="OperationException"/>s.</summary>
public static class MagickErrors
{
    public static OperationException Reading(MagickException exception, InputFile input) => exception switch
    {
        _ when IsOverLimit(exception) => new OperationException(
            "This image is too large to process on this computer.", exception.Message, exception),
        MagickMissingDelegateErrorException => new OperationException(
            $"Clementine can't read this kind of image ({input.Extension}).", exception.Message, exception),
        MagickCorruptImageErrorException or MagickCoderErrorException => new OperationException(
            "This image looks damaged or incomplete, so it couldn't be read.", exception.Message, exception),
        MagickBlobErrorException or MagickFileOpenErrorException => new OperationException(
            "Couldn't open this file. It may be in use or no longer there.", exception.Message, exception),
        _ => new OperationException("Couldn't read this image.", exception.Message, exception),
    };

    public static OperationException Writing(MagickException exception, string formatName) => exception switch
    {
        _ when IsOverLimit(exception) => new OperationException(
            "This image is too large to save on this computer.", exception.Message, exception),
        MagickMissingDelegateErrorException => new OperationException(
            $"Saving as {formatName} isn't supported on this computer.", exception.Message, exception),
        _ => new OperationException($"Couldn't save the image as {formatName}.", exception.Message, exception),
    };

    /// <summary>
    /// ImageMagick refusing an image over the limits <see cref="MagickRuntime"/> sets: a resource-limit error, the
    /// pixel cache's "width or height exceeds limit" / "cache resources exhausted", or libpng's "exceeds user limit"
    /// (a warning attached to its generic "Invalid IHDR data" error).
    /// </summary>
    public static bool IsOverLimit(MagickException exception) =>
        exception is MagickResourceLimitErrorException
        || exception.Message.Contains("exceeds limit", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("exceeds user limit", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("cache resources exhausted", StringComparison.OrdinalIgnoreCase)
        || exception.RelatedExceptions.Any(IsOverLimit);
}
