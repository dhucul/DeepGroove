using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WaveLab.Help;
using WaveLab.Views;
using Xunit;

namespace WaveLab.Tests;

public sealed class HelpDialogTests
{
    [Fact]
    public void DialogLoadsWithThemeAndSelectsRequestedTopic()
    {
        Exception? failure = null;
        string? selectedTopicId = null;
        bool submenuPopupPresent = false;
        var thread = new Thread(() =>
        {
            Application? application = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/WaveLab;component/Themes/Theme.xaml",
                        UriKind.Absolute),
                });

                var dialog = new HelpDialog(HelpCatalog.RecordingTopicId);
                var content = Assert.IsAssignableFrom<FrameworkElement>(dialog.FindName("topicContent"));
                selectedTopicId = Assert.IsType<HelpTopic>(content.DataContext).Id;
                dialog.Close();

                var menuStyle = Assert.IsType<Style>(application.TryFindResource(typeof(MenuItem)));
                var submenu = new MenuItem { Header = "Parent", Style = menuStyle };
                submenu.Items.Add(new MenuItem { Header = "Child" });
                Assert.True(submenu.ApplyTemplate());
                Assert.IsType<Popup>(
                    submenu.Template.FindName("PART_Popup", submenu));
                submenuPopupPresent = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                application?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Help dialog smoke test timed out.");
        Assert.Null(failure);
        Assert.Equal(HelpCatalog.RecordingTopicId, selectedTopicId);
        Assert.True(submenuPopupPresent, "The submenu template does not host its child popup.");
    }
}
