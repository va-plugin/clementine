namespace Clementine.Engines.Images.Imaging;

/// <summary>Quick header-only facts about an image (used for dialog defaults).</summary>
public static class ImageProbe
{
    /// <summary>Displayed size (EXIF rotation taken into account), or null if the header can't be read.</summary>
    public static Task<(uint Width, uint Height)?> SizeAsync(InputFile input, CancellationToken cancellationToken) =>
        Task.Run(() => Size(input), cancellationToken);

    public static (uint Width, uint Height)? Size(InputFile input)
    {
        MagickRuntime.EnsureInitialized();
        try
        {
            using var image = new MagickImage();
            image.Ping(input.Path, ImageLoader.ReadSettings(input, LoadOptions.Still, firstFrameOnly: true));
            var rotated = image.Orientation is OrientationType.LeftTop or OrientationType.RightTop or OrientationType.RightBottom or OrientationType.LeftBottom;
            return rotated ? (image.Height, image.Width) : (image.Width, image.Height);
        }
        catch (MagickException)
        {
            return null;
        }
    }
}
