namespace Clementine.Wheel;

/// <summary>
/// Watchdog arithmetic: counts consecutive polls that found the primary button up. Popups that only make sense while
/// a drag is held (the catcher, the drag wheel) hide once it has been up for <see cref="Required"/> polls in a row, even
/// if every input notification was lost.
/// </summary>
internal sealed class ReleaseWatch(int required)
{
    int _upPolls;

    public int Required { get; } = Math.Max(1, required);

    /// <summary>Records one poll; true once the button has been up for <see cref="Required"/> polls in a row.</summary>
    public bool Poll(bool buttonDown)
    {
        _upPolls = buttonDown ? 0 : _upPolls + 1;
        return _upPolls >= Required;
    }

    public void Reset() => _upPolls = 0;
}
