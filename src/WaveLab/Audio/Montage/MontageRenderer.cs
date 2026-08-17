namespace WaveLab.Audio.Montage;

/// <summary>What a render produced, and what is worth knowing about it.</summary>
/// <param name="Channels">The rendered audio.</param>
/// <param name="SampleRate">The montage's rate.</param>
/// <param name="PeakAmplitude">
/// The largest absolute sample. Reported rather than clamped: overlapping clips can sum past full
/// scale, and silently limiting a montage would hide the one thing the user needs to fix.
/// </param>
/// <param name="Crossfades">How many joins were crossfaded rather than butted.</param>
/// <param name="MeanCorrelation">The average correlation measured across those joins.</param>
public sealed record MontageRenderResult(
    float[][] Channels, int SampleRate, double PeakAmplitude, int Crossfades, double MeanCorrelation)
{
    public int Length => Channels.Length > 0 ? Channels[0].Length : 0;
    public bool Clips => PeakAmplitude > 1.0;
    public double PeakDb => PeakAmplitude > 0 ? 20 * Math.Log10(PeakAmplitude) : double.NegativeInfinity;
}

/// <summary>
/// Renders a montage to one continuous piece of audio.
/// </summary>
/// <remarks>
/// <para>
/// The whole of the interesting work is deciding what happens where two clips overlap. Each clip
/// carries its own fades, but <b>an overlap is a crossfade and takes precedence over both</b>: the
/// clips' own fades describe what they do at a free edge, and a join is not a free edge. Where a
/// pair overlaps, the correlation between what the two clips actually contain is measured over that
/// exact span and handed to <see cref="Crossfade.Law"/>, which returns the pair of gains that holds
/// the level through it — equal-power for unrelated material, equal-gain for two takes of the same
/// thing, and the exact answer in between.
/// </para>
/// <para>
/// Envelopes are evaluated per sample from a handful of numbers rather than materialised. A
/// twenty-minute clip's envelope as a <c>double[]</c> is 423 MB, and a montage holds several.
/// </para>
/// </remarks>
public static class MontageRenderer
{
    /// <summary>How a segment's gain is derived at a point in it.</summary>
    private enum FadeKind
    {
        /// <summary>A free edge rising: the chosen shape.</summary>
        In,

        /// <summary>A free edge falling: the chosen shape, mirrored.</summary>
        Out,

        /// <summary>The incoming half of a crossfade: the chosen shape.</summary>
        CrossIn,

        /// <summary>The outgoing half: the partner the measured correlation asks for.</summary>
        CrossOut,
    }

    /// <summary>A gain ramp over a span of one clip, in that clip's own samples.</summary>
    private readonly record struct Segment(
        int Start, int Count, FadeKind Kind, FadeShape Shape, double Correlation)
    {
        public double GainAt(int offset)
        {
            if (Count <= 0) return 1;
            double t = Count == 1 ? 1 : offset / (double)(Count - 1);

            return Kind switch
            {
                FadeKind.In or FadeKind.CrossIn => Fades.In(Shape, t),
                FadeKind.Out => Fades.Out(Shape, t),
                FadeKind.CrossOut => Crossfade.Partner(Fades.In(Shape, t), Correlation),
                _ => 1,
            };
        }
    }

    private sealed class ClipPlan
    {
        public MontageClip Clip = null!;
        public MontageSource Source = null!;
        public Segment Head;
        public Segment Tail;

        public double EnvelopeAt(int offset)
        {
            if (Head.Count > 0 && offset < Head.Start + Head.Count && offset >= Head.Start)
                return Head.GainAt(offset - Head.Start);
            if (Tail.Count > 0 && offset >= Tail.Start && offset < Tail.Start + Tail.Count)
                return Tail.GainAt(offset - Tail.Start);
            return 1;
        }
    }

    /// <summary>Renders the montage. Throws if the lane has errors in it.</summary>
    public static MontageRenderResult Render(MontageDocument montage,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(montage);

        var errors = montage.Validate()
            .Where(i => i.Severity == MontageIssueSeverity.Error)
            .Select(i => i.Message)
            .ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        cancellationToken.ThrowIfCancellationRequested();
        List<ClipPlan> plans = Plan(montage, cancellationToken,
            out int crossfades, out double meanCorrelation);

        int length = montage.Length;
        int channels = montage.ChannelCount;
        var output = new float[channels][];
        for (int c = 0; c < channels; c++) output[c] = new float[length];

        double peak = 0;
        for (int p = 0; p < plans.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(plans.Count > 0 ? (double)p / plans.Count : 1);

            ClipPlan plan = plans[p];
            MontageClip clip = plan.Clip;
            double gain = clip.Gain;
            int sourceLength = plan.Source.Length;

            for (int i = 0; i < clip.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int timeline = clip.TimelineStart + i;
                if (timeline >= length) break;

                int at = clip.SourceStart + i;
                if (at < 0 || at >= sourceLength) continue;   // reading past the source is silence

                double envelope = plan.EnvelopeAt(i) * gain;
                for (int c = 0; c < channels; c++)
                {
                    float sample = plan.Source.Channels[c][at];
                    if (!float.IsFinite(sample)) continue;
                    output[c][timeline] += (float)(sample * envelope);
                }
            }
        }

        // The peak is taken after everything has summed, because it is the sum that can clip.
        for (int c = 0; c < channels; c++)
        {
            float[] channel = output[c];
            for (int i = 0; i < channel.Length; i++)
            {
                if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                double magnitude = Math.Abs(channel[i]);
                if (magnitude > peak) peak = magnitude;
            }
        }

        progress?.Report(1);
        return new MontageRenderResult(output, montage.SampleRate, peak, crossfades, meanCorrelation);
    }

    /// <summary>Renders into a document, so everything that works on audio works on the result.</summary>
    public static AudioDocument RenderToDocument(MontageDocument montage,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(montage);
        MontageRenderResult result = Render(montage, cancellationToken, progress);

        return new AudioDocument(result.Channels, result.SampleRate, sourceBitDepth: 32)
        {
            Title = string.IsNullOrWhiteSpace(montage.Title) ? "Montage" : montage.Title,
        };
    }

    /// <summary>
    /// Works out every clip's envelope: its own fades at free edges, and the measured law at joins.
    /// </summary>
    private static List<ClipPlan> Plan(MontageDocument montage, CancellationToken cancellationToken,
        out int crossfades, out double meanCorrelation)
    {
        montage.Sort();
        var plans = new List<ClipPlan>(montage.Clips.Count);
        foreach (MontageClip clip in montage.Clips)
        {
            plans.Add(new ClipPlan
            {
                Clip = clip,
                Source = montage.Sources[clip.SourceIndex],
            });
        }

        // Free-edge fades first, shortened together if the pair does not fit inside the clip.
        foreach (ClipPlan plan in plans)
        {
            MontageClip clip = plan.Clip;
            (int head, int tail) = FitFades(clip.FadeInSamples, clip.FadeOutSamples, clip.Length);

            plan.Head = new Segment(0, head, FadeKind.In, clip.FadeInShape, 0);
            plan.Tail = new Segment(clip.Length - tail, tail, FadeKind.Out, clip.FadeOutShape, 0);
        }

        crossfades = 0;
        double correlationTotal = 0;

        // Then the joins, which overwrite whatever the two clips wanted to do at that edge.
        for (int i = 0; i + 1 < plans.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClipPlan outgoing = plans[i];
            ClipPlan incoming = plans[i + 1];

            int overlap = MontageDocument.Overlap(outgoing.Clip, incoming.Clip);
            if (overlap <= 0) continue;

            int overlapStart = Math.Max(outgoing.Clip.TimelineStart, incoming.Clip.TimelineStart);
            int outgoingOffset = overlapStart - outgoing.Clip.TimelineStart;
            int incomingOffset = overlapStart - incoming.Clip.TimelineStart;

            // Measured on the audio that will actually be summed, not on the clips as a whole:
            // two sides of a record can be uncorrelated overall and share a held chord at the join.
            double correlation = Crossfade.MeasureCorrelation(
                outgoing.Source.Channels, outgoing.Clip.SourceStart + outgoingOffset,
                incoming.Source.Channels, incoming.Clip.SourceStart + incomingOffset,
                overlap);

            outgoing.Tail = new Segment(outgoingOffset, overlap, FadeKind.CrossOut,
                incoming.Clip.FadeInShape, correlation);
            incoming.Head = new Segment(incomingOffset, overlap, FadeKind.CrossIn,
                incoming.Clip.FadeInShape, correlation);

            crossfades++;
            correlationTotal += correlation;
        }

        meanCorrelation = crossfades > 0 ? correlationTotal / crossfades : 0;
        return plans;
    }

    /// <summary>
    /// Shortens a pair of fades that will not both fit, in proportion, so neither disappears and the
    /// clip keeps some unfaded middle.
    /// </summary>
    private static (int Head, int Tail) FitFades(int head, int tail, int length)
    {
        head = Math.Clamp(head, 0, Math.Max(0, length));
        tail = Math.Clamp(tail, 0, Math.Max(0, length));
        if (length <= 0) return (0, 0);
        if (head + tail <= length) return (head, tail);

        double scale = length / (double)(head + tail);
        return ((int)(head * scale), (int)(tail * scale));
    }
}
