using System.Windows.Controls;

namespace WaveLab.Views.Controls;

/// <summary>
/// Modal cover for a document-mutating operation. Expects a
/// <see cref="ViewModels.ProgressHost"/> as its DataContext.
/// </summary>
public partial class ProgressOverlay : UserControl
{
    public ProgressOverlay() => InitializeComponent();
}
