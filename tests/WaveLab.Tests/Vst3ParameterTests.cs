using WaveLab.Audio.Vst3;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// The VST3 parameter path, driven against a plugin that actually publishes parameters.
/// </summary>
/// <remarks>
/// <para>
/// Every one of the 22 plugins installed on this machine reports <b>zero host-visible parameters</b>
/// — chased down to the plugins rather than the host, with five hypotheses eliminated. The
/// consequence was that this whole path had never executed: <c>ReadParameters</c>,
/// <c>SetParameter</c>, <c>ApplyParameter</c>, <c>Vst3ParameterChanges</c> and the rack's filtering
/// were all correct-looking code with nothing to run against, and "written but unverifiable" was a
/// standing roadmap entry.
/// </para>
/// <para>
/// <see cref="Vst3SyntheticPlugin"/> closes that. It is a real VST3 plugin as far as the host can
/// tell — a factory, a component, a separate controller, correct vtable slots, stdcall — built out
/// of the same machinery the host already uses to hand <i>itself</i> to plugins. The host code is
/// exercised completely unmodified.
/// </para>
/// </remarks>
public sealed class Vst3ParameterTests(ITestOutputHelper output)
{
    private const int Rate = 44_100, Channels = 2, Block = 256;

    private static Vst3ClassInfo Info() => new(
        Vst3SyntheticPlugin.ComponentClassId, "Synthetic Gain", "Audio Module Class",
        SubCategories: "Fx", Vendor: "WaveLab tests", Version: "1.0", SdkVersion: "VST 3.7");

    private static Vst3Plugin Open(Vst3SyntheticPlugin synthetic)
    {
        Vst3Plugin? plugin = Vst3Plugin.Create(synthetic.CreateModule(), Info(), out string error);
        Assert.True(plugin != null, $"the host would not open the synthetic plugin: {error}");
        return plugin!;
    }

    /// <summary>
    /// The host reads a published parameter list, with each parameter's identity and flags intact.
    /// </summary>
    [Fact]
    public void TheHostReadsParametersAPluginPublishes()
    {
        using var synthetic = new Vst3SyntheticPlugin();
        using Vst3Plugin plugin = Open(synthetic);

        Assert.Equal(4, plugin.DeclaredParameterCount);
        Assert.Equal(4, plugin.Parameters.Count);

        Vst3Parameter gain = plugin.Parameters.Single(p => p.Id == Vst3SyntheticPlugin.GainId);
        Assert.Equal("Gain", gain.Title);
        Assert.Equal("dB", gain.Units);
        Assert.Equal(0.5, gain.DefaultNormalized, 6);
        Assert.True(gain.CanAutomate);
        Assert.False(gain.IsBypass || gain.IsHidden || gain.IsReadOnly);

        // The flag bits are what the rack filters on, so each one is checked on the parameter that
        // carries it rather than assumed to have survived the round trip.
        Assert.True(plugin.Parameters.Single(p => p.Id == Vst3SyntheticPlugin.BypassId).IsBypass);
        Assert.True(plugin.Parameters.Single(p => p.Id == Vst3SyntheticPlugin.HiddenId).IsHidden);
        Assert.True(plugin.Parameters.Single(p => p.Id == Vst3SyntheticPlugin.ReadOnlyId).IsReadOnly);

        // Identifiers are the plugin's own and need not be indices; reading them as indices is a
        // mistake that would pass every other assertion here.
        Assert.Equal([100u, 101u, 102u, 103u], plugin.Parameters.Select(p => p.Id));
    }

    /// <summary>Reading a parameter back gets what was written, through the controller.</summary>
    [Fact]
    public void SettingAParameterRoundTripsThroughTheController()
    {
        using var synthetic = new Vst3SyntheticPlugin();
        using Vst3Plugin plugin = Open(synthetic);

        Assert.Equal(0.5, plugin.GetParameter(Vst3SyntheticPlugin.GainId), 6);
        Assert.True(plugin.SetParameter(Vst3SyntheticPlugin.GainId, 0.25));
        Assert.Equal(0.25, plugin.GetParameter(Vst3SyntheticPlugin.GainId), 6);
        Assert.Equal(0.25, synthetic.ValueOf(Vst3SyntheticPlugin.GainId), 6);

        // Out of range is clamped by the host rather than passed on, so a plugin never sees a
        // normalised value that is not normalised.
        plugin.SetParameter(Vst3SyntheticPlugin.GainId, 9.0);
        Assert.Equal(1.0, synthetic.ValueOf(Vst3SyntheticPlugin.GainId), 6);
    }

    /// <summary>
    /// <b>The one that matters.</b> A parameter set through the host must reach <c>process</c>, not
    /// merely the plugin's own display.
    /// </summary>
    /// <remarks>
    /// This is the documented failure that looks like success: <c>setParamNormalized</c> moves the
    /// controller and nothing else, so a rack slider moves, the plugin's editor agrees with it, and
    /// the audio does not change. The processor hears a parameter only through an
    /// <c>IParameterChanges</c> carried into <c>process</c>. Until now nothing could prove that
    /// route worked — no installed plugin publishes a parameter to send down it.
    /// </remarks>
    [Fact]
    public void AParameterSetOnTheHostReachesTheProcessorAndChangesTheAudio()
    {
        using var synthetic = new Vst3SyntheticPlugin();
        using Vst3Plugin plugin = Open(synthetic);
        Assert.True(plugin.Configure(Rate, Channels, Block), "the host would not configure the plugin");

        var block = new float[Block * Channels];
        for (int i = 0; i < block.Length; i++) block[i] = 0.5f;

        // Unity: the synthetic plugin maps a normalised 0.5 to a linear gain of 1.
        plugin.ApplyParameter(Vst3SyntheticPlugin.GainId, 0.5);
        var atUnity = (float[])block.Clone();
        Assert.True(plugin.ProcessInterleaved(atUnity, 0, atUnity.Length));
        Assert.Equal(0.5f, atUnity[0], 4);

        // Half: the same block must come back at half the level, which can only happen if the value
        // travelled through IParameterChanges into process.
        plugin.ApplyParameter(Vst3SyntheticPlugin.GainId, 0.25);
        var atHalf = (float[])block.Clone();
        Assert.True(plugin.ProcessInterleaved(atHalf, 0, atHalf.Length));

        output.WriteLine($"gain 0.5 -> {atUnity[0]:F4}, gain 0.25 -> {atHalf[0]:F4}");
        Assert.Equal(0.25f, atHalf[0], 4);

        // And the processor is what saw it, not just the controller.
        Assert.Contains(synthetic.ProcessorSawParameters, p => p.Id == Vst3SyntheticPlugin.GainId && p.Value == 0.25);

        // Every sample of every channel, so a change that reached only the first frame would fail.
        Assert.All(atHalf, sample => Assert.Equal(0.25f, sample, 4));
    }

    /// <summary>
    /// Setting only the controller leaves the audio alone, which is why the change list exists.
    /// </summary>
    /// <remarks>
    /// The negative of the test above, and it is worth having explicitly: it fails if someone
    /// "simplifies" <c>ApplyParameter</c> down to <c>SetParameter</c>, which would look correct
    /// everywhere except in the samples.
    /// </remarks>
    [Fact]
    public void SettingOnlyTheControllerDoesNotChangeTheAudio()
    {
        using var synthetic = new Vst3SyntheticPlugin();
        using Vst3Plugin plugin = Open(synthetic);
        Assert.True(plugin.Configure(Rate, Channels, Block), "the host would not configure the plugin");

        var block = new float[Block * Channels];
        Array.Fill(block, 0.5f);

        plugin.SetParameter(Vst3SyntheticPlugin.GainId, 0.25);      // controller only
        Assert.True(plugin.ProcessInterleaved(block, 0, block.Length));

        // The processor never heard it, so it is still at the default gain of 1. The controller,
        // meanwhile, did move - which is exactly how this failure hides in a real host.
        Assert.Equal(0.5f, block[0], 4);
        Assert.Equal(0.25, synthetic.ValueOf(Vst3SyntheticPlugin.GainId), 6);
        Assert.Equal(0.5, synthetic.ProcessorValueOf(Vst3SyntheticPlugin.GainId), 6);
        Assert.DoesNotContain(synthetic.ProcessorSawParameters, p => p.Id == Vst3SyntheticPlugin.GainId);
    }

    /// <summary>A plugin's editor moving a knob reaches the host through the component handler.</summary>
    [Fact]
    public void AnEditFromThePluginsOwnEditorReachesTheHost()
    {
        using var synthetic = new Vst3SyntheticPlugin();
        using Vst3Plugin plugin = Open(synthetic);

        Assert.True(plugin.HandlerAccepted, "the controller refused the host's component handler");
        Assert.True(synthetic.TookComponentHandler);

        var seen = new List<Vst3ParameterEdit>();
        plugin.ParameterEdited += edit => seen.Add(edit);

        // What a plugin does when a user moves something in its own editor: beginEdit, performEdit,
        // endEdit. Only performEdit carries a value, and it is the host's only way to hear about it.
        synthetic.PerformEditFromEditor(Vst3SyntheticPlugin.GainId, 0.75);

        Assert.Single(seen);
        Assert.Equal(Vst3SyntheticPlugin.GainId, seen[0].Id);
        Assert.Equal(0.75, seen[0].Normalized, 6);
    }

    /// <summary>State carries what the parameter list cannot, and the controller is told about it.</summary>
    /// <remarks>
    /// <para>
    /// The component's <c>getState</c>/<c>setState</c> restores the processor; a controller that is
    /// not separately told stays showing whatever it had, so the plugin sounds restored and looks
    /// unrestored. The synthetic plugin records whether <c>setComponentState</c> arrived.
    /// </para>
    /// <para>
    /// <b>The block in the middle is not padding.</b> <c>ApplyParameter</c> writes the controller and
    /// <i>queues</i> for the processor, and the processor reads that queue only inside
    /// <c>process</c> - so a parameter moved while nothing is playing has not reached the component
    /// when <c>getState</c> is called, and the state saved is the value from before the move. Found
    /// by writing this test without the block and watching 0.8 come back as 0.5.
    /// </para>
    /// <para>
    /// It is a property rather than a defect, and only because two things are true at once: the rack
    /// stores each parameter value in a preset <i>as well as</i> the opaque state, and restores state
    /// first so the values land on top; and a plugin publishing no parameters - which is all 22 here
    /// - has a component state that never depended on the queue. Break either and stale state ships.
    /// </para>
    /// </remarks>
    [Fact]
    public void StateRoundTripsAndTheControllerIsToldAboutIt()
    {
        using var synthetic = new Vst3SyntheticPlugin();
        using Vst3Plugin plugin = Open(synthetic);
        Assert.True(plugin.Configure(Rate, Channels, Block), "the host would not configure the plugin");

        // Handed over once at open, before anything else was asked of the controller.
        Assert.True(synthetic.TookComponentState);

        plugin.ApplyParameter(Vst3SyntheticPlugin.GainId, 0.8);
        plugin.ProcessInterleaved(new float[Block * Channels], 0, Block * Channels);
        Assert.Equal(0.8, synthetic.ProcessorValueOf(Vst3SyntheticPlugin.GainId), 6);

        byte[] state = plugin.SaveState();
        Assert.Equal(sizeof(double), state.Length);

        plugin.ApplyParameter(Vst3SyntheticPlugin.GainId, 0.1);
        Assert.Equal(0.1, plugin.GetParameter(Vst3SyntheticPlugin.GainId), 6);

        // Restores the processor, then tells the controller, which is the two-step the host owes a
        // split plugin. Both halves must land or one of them is lying about what is loaded.
        Assert.True(plugin.RestoreState(state));
        Assert.Equal(0.8, plugin.GetParameter(Vst3SyntheticPlugin.GainId), 6);
        Assert.Equal(0.8, synthetic.ProcessorValueOf(Vst3SyntheticPlugin.GainId), 6);
    }

    /// <summary>
    /// The rack shows the parameters a user can move, and hides the three kinds that it must not.
    /// </summary>
    /// <remarks>
    /// A bypass parameter belongs to the rack's own power LED rather than to a slider, a hidden one
    /// is hidden because the plugin says so, and a read-only one is a meter. Drawing any of the
    /// three as a slider is a control that either does nothing or fights the rack.
    /// </remarks>
    [Fact]
    public void TheRackDrawsOnlyTheParametersAUserCanMove()
    {
        using var synthetic = new Vst3SyntheticPlugin();
        using Vst3Plugin plugin = Open(synthetic);
        using var effect = new WaveLab.Audio.Effects.Vst3Effect(
            new Vst3PluginRef(plugin, "synthetic.vst3", null));

        output.WriteLine($"published {plugin.Parameters.Count}, rack shows " +
            $"{effect.Params.Count}: {string.Join(", ", effect.Params.Select(p => p.Label))}");

        Assert.Single(effect.Params);
        Assert.Equal("GAIN", effect.Params[0].Label);      // the rack upper-cases what it draws
    }
}
