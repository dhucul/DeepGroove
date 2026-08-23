using System.Globalization;
using System.Windows.Data;

namespace WaveLab.Util;

/// <summary>
/// Doubles the underscores in a string so that a menu shows the text as it was written.
/// </summary>
/// <remarks>
/// A menu item presents its header through a <c>ContentPresenter</c> with
/// <c>RecognizesAccessKey="True"</c> — the mechanism that turns <c>_File</c> into Alt+F. Applied to
/// text nobody wrote as a mnemonic it quietly eats a character and claims a shortcut: a recent file
/// at <c>C:\takes\take_1.wav</c> lists as <c>take1.wav</c>, and typing 1 with the menu open invokes
/// it. Doubling is the escape the presenter itself defines, and it is a display concern only —
/// whatever is passed to a command must stay the path that was stored.
/// </remarks>
public sealed class AccessKeyEscapeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text.Contains('_') ? text.Replace("_", "__") : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A menu header is display-only.");
}
