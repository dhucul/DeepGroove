using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class RackPreviewActivationTests
{
    [Fact]
    public void AdjustingAnEffectResumesPreviewAfterRender()
    {
        using var master = new MasterSection();
        var rack = new MasterSectionViewModel(master);
        rack.BypassAfterRender();
        EffectParamViewModel gain = Assert.Single(rack.Effects[0].Params,
            parameter => parameter.Label == "LOW GAIN");

        gain.Value = 3;

        Assert.True(master.RackEnabled);
        Assert.Equal("ACTIVE", rack.RackStateText);
        Assert.Contains("active in rack", rack.RackStatusText);
    }

    [Fact]
    public void EffectPowerSwitchResumesPreviewAfterRender()
    {
        using var master = new MasterSection();
        var enhancer = new MonoToStereoEffect();
        master.ReplaceChain([enhancer]);
        var rack = new MasterSectionViewModel(master);
        rack.BypassAfterRender();
        int topologyChanges = 0;
        rack.ProcessingTopologyChanged += () => topologyChanges++;

        rack.Effects[0].Enabled = false;
        Assert.True(master.RackEnabled);
        Assert.False(enhancer.Enabled);

        rack.BypassAfterRender();
        rack.Effects[0].Enabled = true;

        Assert.True(master.RackEnabled);
        Assert.True(enhancer.Enabled);
        Assert.Equal(1, topologyChanges);
    }

    [Fact]
    public void AddingAnyBuiltInEffectResumesPreviewAfterRender()
    {
        foreach ((string typeId, _) in EffectFactory.Available)
        {
            using var master = new MasterSection();
            var rack = new MasterSectionViewModel(master);
            rack.BypassAfterRender();

            rack.AddEffectCommand.Execute(typeId);

            Assert.True(master.RackEnabled);
            Assert.Contains(master.ChainSnapshot, effect => effect.TypeId == typeId);
        }
    }

    [Fact]
    public void AnIntentionalRackBypassIsNotCancelledByAParameterEdit()
    {
        using var master = new MasterSection();
        var rack = new MasterSectionViewModel(master) { RackEnabled = false };
        EffectParamViewModel gain = Assert.Single(rack.Effects[0].Params,
            parameter => parameter.Label == "LOW GAIN");

        gain.Value = 3;

        Assert.False(master.RackEnabled);
        Assert.Equal("BYPASSED", rack.RackStateText);
        Assert.Contains("remains bypassed", rack.RackStatusText);
    }
}
