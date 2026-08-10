using WaveLab.Help;
using Xunit;

namespace WaveLab.Tests;

public sealed class HelpCatalogTests
{
    [Fact]
    public void CatalogHasUniqueCompleteTopics()
    {
        Assert.True(HelpCatalog.Topics.Count >= 15);
        Assert.Equal(HelpCatalog.Topics.Count,
            HelpCatalog.Topics.Select(topic => topic.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (HelpTopic topic in HelpCatalog.Topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Id));
            Assert.False(string.IsNullOrWhiteSpace(topic.Category));
            Assert.False(string.IsNullOrWhiteSpace(topic.Title));
            Assert.False(string.IsNullOrWhiteSpace(topic.Summary));
            Assert.NotEmpty(topic.Sections);
            Assert.All(topic.Sections, section =>
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Title));
                Assert.False(string.IsNullOrWhiteSpace(section.Body));
            });
        }
    }

    [Theory]
    [InlineData(HelpCatalog.StartTopicId)]
    [InlineData(HelpCatalog.RecordingTopicId)]
    [InlineData(HelpCatalog.ShortcutsTopicId)]
    public void MenuTopicsResolve(string id)
    {
        Assert.Equal(id, HelpCatalog.GetTopic(id).Id);
    }

    [Fact]
    public void UnknownTopicFallsBackToGettingStarted()
    {
        Assert.Equal(HelpCatalog.StartTopicId, HelpCatalog.GetTopic("not-a-topic").Id);
        Assert.Equal(HelpCatalog.StartTopicId, HelpCatalog.GetTopic(null).Id);
    }

    [Theory]
    [InlineData("device mix format", HelpCatalog.RecordingTopicId)]
    [InlineData("TRUE PEAK", HelpCatalog.RecordingTopicId)]
    [InlineData("autosave recovery", "settings")]
    [InlineData("dither 16-bit", "formats")]
    [InlineData("batch normalize", "tools")]
    public void SearchFindsTermsAcrossAllHelpText(string query, string expectedTopicId)
    {
        Assert.Contains(HelpCatalog.Search(query), topic => topic.Id == expectedTopicId);
    }

    [Fact]
    public void SearchUsesAllTermsAndHandlesBoundaryQueries()
    {
        Assert.Same(HelpCatalog.Topics, HelpCatalog.Search("  "));
        Assert.Empty(HelpCatalog.Search("term-that-does-not-exist"));
        Assert.All(HelpCatalog.Search("record clipping"), topic =>
        {
            Assert.Contains("record", topic.SearchText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("clipping", topic.SearchText, StringComparison.OrdinalIgnoreCase);
        });
    }
}
