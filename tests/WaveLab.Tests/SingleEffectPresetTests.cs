using System.Text.Json;
using WaveLab.Audio;
using WaveLab.Audio.Effects;
using WaveLab.ViewModels;
using Xunit;

namespace WaveLab.Tests;

public sealed class SingleEffectPresetTests
{
    public static TheoryData<string> BuiltIns() => EffectRenderAuditTests.BuiltIns();

    private static void WithFile(Action<string> run)
    {
        string path = Path.Combine(Path.GetTempPath(), $"WaveLab-effect-{Guid.NewGuid():N}.effect.json");
        try { run(path); }
        finally { File.Delete(path); }
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void EveryBuiltInRoundTripsOnlyTheSelectedCard(string typeId)
    {
        WithFile(path =>
        {
            using var master = new MasterSection();
            var first = new TrimEffect();
            var last = new DelayEffect();
            var effect = EffectFactory.Create(typeId);
            foreach (var p in effect.Params) effect.SetParam(p.Key, p.Min + (p.Max - p.Min) * 0.7);
            master.ReplaceChain([first, effect, last]);
            var rack = new MasterSectionViewModel(master);
            var selected = rack.Effects[1];
            var expected = effect.Params.ToDictionary(p => p.Key, p => effect.GetParam(p.Key));
            rack.SaveEffectPreset(selected, path);
            Assert.True(master.RackEnabled);
            var saved = JsonSerializer.Deserialize<EffectFactory.ChainPreset>(File.ReadAllText(path))!;
            Assert.Single(saved.Effects);
            Assert.Equal(typeId, saved.Effects[0].TypeId);
            foreach (var p in effect.Params) effect.SetParam(p.Key, p.Min);
            selected.Enabled = false; // loading settings must not change the user's bypass decision
            bool editorClosed = false;
            rack.EffectRemoving += vm =>
            {
                Assert.Same(selected, vm);
                Assert.Same(effect, master.ChainSnapshot[1]);
                editorClosed = true;
            };
            rack.LoadEffectPreset(selected, path);
            var chain = master.ChainSnapshot;
            Assert.True(editorClosed);
            Assert.Same(first, chain[0]);
            Assert.Same(last, chain[2]);
            Assert.NotSame(effect, chain[1]);
            Assert.False(chain[1].Enabled);
            foreach (var (key, value) in expected) Assert.Equal(value, chain[1].GetParam(key));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadingResumesPostRenderPreviewButHonoursIntentionalBypass(bool intentional)
    {
        WithFile(path =>
        {
            using var master = new MasterSection();
            master.ReplaceChain([new MonoToStereoEffect()]);
            var rack = new MasterSectionViewModel(master);
            var effect = rack.Effects[0];
            rack.SaveEffectPreset(effect, path);
            if (intentional) rack.RackEnabled = false;
            else rack.BypassAfterRender();
            rack.LoadEffectPreset(effect, path);
            Assert.Equal(!intentional, master.RackEnabled);
            Assert.Equal(!intentional, master.ExpandsMonoToStereo);
        });
    }

    [Theory]
    [InlineData("wrong-type")]
    [InlineData("chain")]
    [InlineData("broken")]
    [InlineData("null-parameters")]
    public void InvalidPresetsLeaveTheRackAndEditorAlone(string failure)
    {
        WithFile(path =>
        {
            using var master = new MasterSection();
            var effect = new MonoToStereoEffect();
            master.ReplaceChain([effect]);
            var rack = new MasterSectionViewModel(master);
            var preset = EffectFactory.Capture("test", [effect]);
            if (failure == "wrong-type") preset.Effects[0].TypeId = "eq";
            if (failure == "chain") preset.Effects.Add(preset.Effects[0]);
            if (failure == "null-parameters") preset.Effects[0].Params = null!;
            File.WriteAllText(path, failure == "broken" ? "{bad" : JsonSerializer.Serialize(preset));
            int closed = 0;
            rack.EffectRemoving += _ => closed++;
            Assert.ThrowsAny<Exception>(() => rack.LoadEffectPreset(rack.Effects[0], path));
            Assert.Same(effect, Assert.Single(master.ChainSnapshot));
            Assert.Equal(0, closed);
        });
    }

    [Fact]
    public void SavingOverwritesTheChosenFileWithoutLosingThePreviousPresetOnValidationFailure()
    {
        WithFile(path =>
        {
            var effect = new MonoToStereoEffect();
            EffectFactory.SaveEffectPreset(EffectFactory.Capture("test", [effect]), path);
            effect.SetParam("amount", 0.7525);
            EffectFactory.SaveEffectPreset(EffectFactory.Capture("test", [effect]), path);
            Assert.Equal(0.7525, EffectFactory.LoadEffectPreset(path, effect.TypeId).GetParam("amount"));
            string previous = File.ReadAllText(path);
            var invalid = EffectFactory.Capture("test", [effect]);
            invalid.Effects[0].Params["amount"] = double.NaN;
            Assert.Throws<InvalidDataException>(() => EffectFactory.SaveEffectPreset(invalid, path));
            Assert.Equal(previous, File.ReadAllText(path));
        });
    }

    [Fact]
    public void ConvolutionResponseChoiceSurvivesTheEffectPresetFile()
    {
        WithFile(path =>
        {
            var reverb = new ConvolutionReverbEffect();
            string response = Path.Combine(Path.GetTempPath(), $"missing-room-{Guid.NewGuid():N}.wav");
            ((IEffectState)reverb).RestoreStateText(response);
            reverb.SetParam("mix", 0.2375);
            EffectFactory.SaveEffectPreset(EffectFactory.Capture("Room", [reverb]), path);
            var restored = Assert.IsType<ConvolutionReverbEffect>(EffectFactory.LoadEffectPreset(path, "convolution"));
            Assert.Equal(response, restored.ResponsePath);
            Assert.True(restored.ResponseMissing);
            Assert.Equal(0.2375, restored.GetParam("mix"));
        });
    }

    [Fact]
    public void SavingNeverInstantiatesAnOpaquePluginState()
    {
        WithFile(path =>
        {
            // A plugin need not expose any knobs for its state to be useful. Saving should
            // serialize that state directly, without attempting to load this absent plugin.
            var preset = new EffectFactory.ChainPreset
            {
                Name = "Plugin settings",
                Effects = [new() { TypeId = "vst3:C:\\Absent\\Plugin.vst3", State = "AQIDBA==" }],
            };
            EffectFactory.SaveEffectPreset(preset, path);
            var restored = JsonSerializer.Deserialize<EffectFactory.ChainPreset>(File.ReadAllText(path))!;
            Assert.Equal("AQIDBA==", restored.Effects[0].State);
        });
    }
}
