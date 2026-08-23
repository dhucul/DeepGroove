using System.Windows;

namespace WaveLab.Util;

/// <summary>
/// Carries an element's <see cref="FrameworkElement.DataContext"/> into places the element tree
/// does not reach.
/// </summary>
/// <remarks>
/// A <see cref="System.Windows.Data.CollectionContainer"/> inside a
/// <see cref="System.Windows.Data.CompositeCollection"/> sits in neither the visual nor the logical
/// tree, so a plain <c>{Binding}</c> written there resolves against nothing and the collection
/// silently comes back empty. A <see cref="Freezable"/> held in a resource dictionary *does* take
/// the inheritance context of the element that owns the dictionary, which is the whole trick: bind
/// <see cref="Data"/> once on the window and everything downstream binds through the proxy.
/// </remarks>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
