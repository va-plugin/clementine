using Clementine.Core;

namespace Clementine.App.Options;

/// <summary>
/// Optional interface for operations whose options dialog should show a live preview of the result
/// (for example Rename shows the first few new names).
/// </summary>
public interface IOptionsPreview
{
    /// <summary>Preview text for the current values, or null to hide the preview.</summary>
    string? Preview(IReadOnlyList<InputFile> inputs, OptionValues values);
}
