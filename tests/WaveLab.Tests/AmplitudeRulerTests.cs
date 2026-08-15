using WaveLab.Views.Controls;
using Xunit;

namespace WaveLab.Tests;

public sealed class AmplitudeRulerTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(-6.020599913, 50)]
    [InlineData(-20, 10)]
    public void DbMarkersUseTheWaveformsLinearAmplitudeScale(double levelDb, double expectedOffset)
    {
        Assert.Equal(expectedOffset, AmplitudeRuler.MarkerOffset(levelDb, 100), 6);
    }

    [Fact]
    public void RulerIncludesCommonPeakReferenceLevels()
    {
        Assert.Equal([0, -3, -6, -12, -24], AmplitudeRuler.MarkerLevelsDb);
    }
}
