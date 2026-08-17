using WaveLab.Util;

namespace WaveLab.ViewModels;

/// <summary>
/// The little the tab strip needs to know about whatever is open in a tab.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of thing live in tabs now: an audio document and a montage. They have almost nothing in
/// common — a montage has no samples of its own, no peak pyramid and no selection in samples — so
/// this base is deliberately three members wide. Everything else stays on the concrete types, and
/// the commands that need audio reach it through <c>MainViewModel.ActiveDocument</c>, which is null
/// whenever the active tab is not a document. That is what keeps forty-odd audio commands from
/// having to ask what sort of tab they are looking at: they simply become unavailable.
/// </para>
/// </remarks>
public abstract class TabViewModel : ObservableObject
{
    /// <summary>Identifies this tab for the lifetime of the session.</summary>
    public Guid TabId { get; } = Guid.NewGuid();

    /// <summary>What the tab shows, including whatever mark it uses for unsaved work.</summary>
    public abstract string Title { get; }

    public abstract bool IsDirty { get; }

    /// <summary>A short word for the tab's badge: what kind of thing this is.</summary>
    public abstract string Kind { get; }
}
