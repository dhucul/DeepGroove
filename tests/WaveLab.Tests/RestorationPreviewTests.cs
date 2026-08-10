using WaveLab.Audio.Dsp;
using Xunit;

namespace WaveLab.Tests;

public sealed class RestorationPreviewTests
{
    [Fact]
    public void LeftAndRightAuditionRouteOnlyTheSelectedChannelToBothSpeakers()
    {
        float[][] stereo = [[0.1f, 0.2f, 0.3f], [-0.4f, -0.5f, -0.6f]];

        float[][] left = RestorationPreview.CreateAudition(stereo,
            RestorationAuditionMode.Left);
        float[][] right = RestorationPreview.CreateAudition(stereo,
            RestorationAuditionMode.Right);

        Assert.Equal(stereo[0], left[0]);
        Assert.Equal(stereo[0], left[1]);
        Assert.Equal(stereo[1], right[0]);
        Assert.Equal(stereo[1], right[1]);
        Assert.NotSame(stereo[0], left[0]);
        Assert.NotSame(left[0], left[1]);
        Assert.NotSame(stereo[1], right[0]);
        Assert.NotSame(right[0], right[1]);
    }

    [Theory]
    [InlineData(RestorationAuditionMode.Stereo)]
    [InlineData(RestorationAuditionMode.Left)]
    [InlineData(RestorationAuditionMode.Right)]
    public void MonoAuditionRemainsMono(RestorationAuditionMode mode)
    {
        float[][] mono = [[0.25f, -0.25f]];

        float[][] result = RestorationPreview.CreateAudition(mono, mode);

        Assert.Single(result);
        Assert.Equal(mono[0], result[0]);
        Assert.NotSame(mono[0], result[0]);
    }
}
