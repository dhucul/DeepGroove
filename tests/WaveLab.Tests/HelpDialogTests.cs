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
        // The application and the theme come from the shared UI thread rather than being stood up
        // here: an Application is per-process, so a second one thrown up by this test would fail
        // whenever it ran beside another test that needs one.
        string? selectedTopicId = Wpf.Run(() =>
        {
            var dialog = new HelpDialog(HelpCatalog.RecordingTopicId);
            var content = Assert.IsAssignableFrom<FrameworkElement>(dialog.FindName("topicContent"));
            string? topic = Assert.IsType<HelpTopic>(content.DataContext).Id;
            dialog.Close();
            return topic;
        });

        Assert.Equal(HelpCatalog.RecordingTopicId, selectedTopicId);
    }

    [Fact]
    public void TheSubmenuTemplateHostsItsChildPopup()
    {
        Wpf.Run(() =>
        {
            var menuStyle = Assert.IsType<Style>(Application.Current.TryFindResource(typeof(MenuItem)));
            var submenu = new MenuItem { Header = "Parent", Style = menuStyle };
            submenu.Items.Add(new MenuItem { Header = "Child" });

            // Inside a Menu, because that is where one lives: the themed item binds its content
            // alignment to its ItemsControl ancestor, and an item with no menu around it reports a
            // binding failure that has nothing to do with the template being asked about.
            var menu = new Menu();
            menu.Items.Add(submenu);

            Assert.True(submenu.ApplyTemplate());
            Assert.IsType<Popup>(submenu.Template.FindName("PART_Popup", submenu));
        });
    }
}
