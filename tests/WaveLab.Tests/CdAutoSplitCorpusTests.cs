using WaveLab.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WaveLab.Tests;

/// <summary>
/// Find Tracks measured against real record transfers, which is what stops its two constants being
/// fitted to one file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in: nothing here runs without <c>WAVELAB_CORPUS=1</c>.</b> Sides are built by butting
/// real transfers from corpus 1 together, so the join between two tracks is real run-out groove
/// against real lead-in groove and the answer is known to the sample.
/// </para>
/// <para>
/// <b>The limitation, stated rather than discovered later:</b> a join made this way is not
/// identical to one continuous groove running between two songs on a pressing. It is the closest
/// thing available without a marked-up side, and this repo's record is emphatic that a threshold
/// fitted to synthetic material alone does not transfer — five declip calibrations went that way.
/// So this is the automated evidence and a real side remains the acceptance test.
/// </para>
/// </remarks>
public sealed class CdAutoSplitCorpusTests(ITestOutputHelper output)
{
    /// <summary>How far a found split may sit from the real join and still count as found.</summary>
    private const double ToleranceSeconds = 3.0;

    private sealed record BuiltSide(string Name, float[][] Channels, int SampleRate, int[] Joins);

    /// <summary>
    /// Butt <paramref name="count"/> transfers together and remember where the joins landed.
    /// </summary>
    private static BuiltSide? Build(IReadOnlyList<CorpusRecording> pool, int first, int count)
    {
        var parts = new List<AudioDocument>();
        for (int i = 0; i < count && first + i < pool.Count; i++)
        {
            try { parts.Add(AudioImporter.Load(pool[first + i].Path)); }
            catch (Exception) { return null; }
        }
        if (parts.Count < count) return null;

        int rate = parts[0].SampleRate;
        if (parts.Any(p => p.SampleRate != rate)) return null;

        int total = parts.Sum(p => p.Length);
        var data = new float[2][];
        for (int c = 0; c < 2; c++) data[c] = new float[total];

        var joins = new List<int>();
        int at = 0;
        foreach (AudioDocument part in parts)
        {
            if (at > 0) joins.Add(at);
            for (int c = 0; c < 2; c++)
                Array.Copy(part.Channels[Math.Min(c, part.Channels.Count - 1)], 0, data[c], at, part.Length);
            at += part.Length;
        }

        string name = string.Join(" + ", parts.Select(p => Path.GetFileNameWithoutExtension(p.Title)));
        return new BuiltSide(name, data, rate, [.. joins]);
    }

    /// <summary>
    /// The claim the feature rests on: the steadiest answer is the right one. Reported per side
    /// rather than asserted per side — what is asserted is the aggregate, because one awkward
    /// pressing should not be able to condemn a rule that is right about the rest.
    /// </summary>
    [Fact]
    public void TheSteadiestAnswerIsTheRightAnswer()
    {
        if (!DeclipCorpus.Enabled)
        {
            output.WriteLine("set WAVELAB_CORPUS=1 to run this.");
            return;
        }

        var pool = DeclipCorpus.Recordings().Where(r => r.Corpus == "1-record").ToList();
        Assert.True(pool.Count >= 2, $"corpus 1 holds {pool.Count} transfers; at least 2 are needed.");

        int sides = 0, rightCount = 0, everyJoinFound = 0;
        double worstMiss = 0;

        for (int tracks = 2; tracks <= 5; tracks++)
            for (int start = 0; start + tracks <= pool.Count; start += tracks)
            {
                BuiltSide? built = Build(pool, start, tracks);
                if (built == null) continue;
                sides++;

                CdSplitSweep sweep = CdTransfer.SweepTracks(built.Channels, built.SampleRate);
                CdSplitCandidate? best = sweep.Best;
                int found = best?.Tracks ?? 0;
                bool countRight = found == tracks;
                if (countRight) rightCount++;

                // The interior splits, against the joins that are actually there.
                double tolerance = ToleranceSeconds * built.SampleRate;
                var splits = best == null ? [] : best.Boundaries.Skip(1).Take(best.Boundaries.Count - 2).ToList();
                bool allFound = splits.Count == built.Joins.Length;
                double worstHere = 0;
                if (allFound)
                    foreach (int join in built.Joins)
                    {
                        double nearest = splits.Min(s => Math.Abs(s - (double)join));
                        worstHere = Math.Max(worstHere, nearest / built.SampleRate);
                        if (nearest > tolerance) allFound = false;
                    }
                if (allFound) { everyJoinFound++; worstMiss = Math.Max(worstMiss, worstHere); }

                output.WriteLine(
                    $"{tracks} wanted, {found} found{(countRight ? "" : "  <- WRONG COUNT")}" +
                    (allFound ? $", every join within {worstHere:0.0} s" : ", joins NOT all matched") +
                    $"  [{best?.LowestDb ?? 0:0}..{best?.HighestDb ?? 0:0} dB]  {built.Name}");
            }

        output.WriteLine("");
        output.WriteLine($"{sides} sides: {rightCount} with the right count, " +
            $"{everyJoinFound} with every join placed, worst placement {worstMiss:0.0} s");

        Assert.True(sides > 0, "no sides could be built from corpus 1.");
        // Stated as a majority rather than as every side, for the reason the declip corpus test
        // records: a bar that only today's material clears is a bar fitted to today's material.
        Assert.True(rightCount * 2 > sides,
            $"the steadiest answer was the right count on only {rightCount} of {sides} sides.");
    }

    /// <summary>
    /// The other half of the promise: when the user knows the count, aiming at it should hit. This
    /// is the weaker claim of the two and the more useful one, because a record label carries the
    /// number and the audio does not.
    /// </summary>
    [Fact]
    public void AimingAtACountTheSideActuallyHasHitsItMoreOftenThanGuessing()
    {
        if (!DeclipCorpus.Enabled)
        {
            output.WriteLine("set WAVELAB_CORPUS=1 to run this.");
            return;
        }

        var pool = DeclipCorpus.Recordings().Where(r => r.Corpus == "1-record").ToList();
        if (pool.Count < 2) return;

        int sides = 0, aimed = 0, unaimed = 0;
        for (int tracks = 2; tracks <= 5; tracks++)
            for (int start = 0; start + tracks <= pool.Count; start += tracks)
            {
                BuiltSide? built = Build(pool, start, tracks);
                if (built == null) continue;
                sides++;

                if (CdTransfer.SweepTracks(built.Channels, built.SampleRate) is { Best.Tracks: var free } &&
                    free == tracks) unaimed++;
                if (CdTransfer.SweepTracks(built.Channels, built.SampleRate, tracks) is { Best: not null })
                    aimed++;
            }

        output.WriteLine($"{sides} sides: aimed hit {aimed}, unaimed hit {unaimed}");
        Assert.True(sides > 0);
        Assert.True(aimed >= unaimed,
            $"aiming at the real count hit {aimed} of {sides} where taking the steadiest answer hit {unaimed}.");
    }
}
