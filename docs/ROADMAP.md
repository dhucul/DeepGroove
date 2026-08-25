# Deep Groove — status, declined work, and open defects

Deep Groove is a Windows audio editor: one project, `src/WaveLab`, C# / WPF / .NET 10, shipping as
**2.0.31**. This file began as the v1 expansion plan. Everything it classified as Phase A through D
is built, so it is no longer a plan; it is three things — what ships, what was deliberately declined
and why, and which defects are characterised but not fixed.

Where to look for what:

| file | holds |
| --- | --- |
| `CLAUDE.md` | the engineering record — how each piece works, what was measured, what was tried and rejected |
| `docs/validation-corpora.md` | the six corpora, and every declip / click / crackle / heal figure quoted below |
| `listening/NOTES.md` | A/B renders and what they sounded like |
| this file | status, declines, open defects |

When this file and `CLAUDE.md` disagree, `CLAUDE.md` is the one maintained alongside the code.

**Verified against the source on 2026-08-25** — menus, effect registry, DSP tree, test suite and
installer script read directly, not recalled.

## 1. Shipped

**File & formats** — WAV 16/24-bit PCM and 32-bit float, RF64/BW64 above 2 GB, Wave64; AIFF/AIFF-C
import and AIFF 16/24/32-bit output; MP3/FLAC/M4A/WMA import via Media Foundation; MP3/AAC/WMA/FLAC
export; ID3v2.4 tagging; chunk-preserving round trips; open-as-bit-depth; multi-tab documents;
drag-and-drop and command-line open; recent files; Close All Files.

**Editing & processing** — sample-accurate selection, resizable by dragging either edge or with
Shift+click; cut/copy/paste/trim; cross-tab clipboard; undo/redo with a memory budget and a
browsable **Edit History** panel; gain, peak normalize, **loudness normalize** — which offers a rack
limiter when the true-peak ceiling stops it reaching the target, rather than only reporting the
shortfall — **Match Loudness across tabs**, fades, reverse, DC removal, insert silence, crossfade
smoothing, silence detection / trimming / splitting, channel tools (swap, phase invert per channel,
balance, mono mixdown, mono-to-stereo, channel extraction).

**Effects rack** — ordered real-time chain with add/remove/reorder/bypass, auto-generated parameter
UI, per-effect reset, factory and user presets, destructive apply with undo, and an output-mix
ceiling readout. Twenty-two effects ship: studio EQ, linear-phase EQ, dynamic EQ, limiter,
compressor, multiband compressor, gate, de-esser, transient shaper, reverb, convolution reverb,
delay, chorus, saturation, filter, stereo width, mono-to-stereo, channel balance, level normalizer,
noise reduction, hum removal, trim — more than the plan asked for.

**Time and pitch** — time stretch without pitch change, pitch shift without length change, tuner and
tempo estimation, polyphase sample-rate conversion.

**Restoration** — declip (arch reconstruction and A-SPADE, chosen per channel), click and pop
repair, surface crackle repair, vertical surface-noise collapse ahead of de-crackling, spectral
noise reduction with a learned profile and depth scaled to how much noise there is to remove, fixed
and drifting hum removal, wow and flutter, disc equalisation curves, stylus azimuth correction,
needle-drop and run-out detection, a spectral editor with time-frequency reassignment and region
repair, the **Analyze & Tune** workbench for vinyl cleanup and clean transfers, and **residual
capture** — what a pass removed, kept as a file you can play. Validated against six external
corpora; see `docs/validation-corpora.md`.

**Analysis & metering** — peak pyramid waveform, spectrum analyser, spectrogram, constant-Q,
amplitude and frequency rulers, vertical amplitude zoom, EBU R128 loudness with history, true peak,
phase and correlation, statistics dialog, loudness compliance targets.

**Delivery** — CD transfer and PQ sheet editor, DDP 2.00 image sets, CD import, Prepare Tracks for
Audio CD, batch converter, export dialog with format, depth, rate conversion and selection-only.

**Transport, recording & UI** — WASAPI playback and recording with device selection and buffer
control, input monitoring, metronome and click track, punch-in, markers and regions with a panel and
sidecar persistence, audio montage (single lane), settings persistence, autosave with crash
recovery, window placement, command palette, help catalogue, VST3 hosting with an embedded editor
window.

**Packaging** — Inno Setup installer (`installer/WaveLab.iss`) producing
`DeepGroove-Setup-<version>.exe` from a self-contained win-x64 publish. Both the payload and the
intermediate build land under `artifacts/`; `InstallerVersionTests` pins the script's version to the
project's.

**Two things shipped despite being deferred here.** VST3, listed as "a candidate for v3", and
noise-shaped dither, listed as a nice-to-have. Both are done.

## 2. Deliberately not here

- **Multitrack timeline, mixer, buses, sends, automation** — a different product architecture; a
  rewrite rather than an extension. The **audio montage is a single-lane clip timeline**, not a step
  towards this: one lane is what makes an overlap unambiguously a crossfade, which is what makes the
  join measurable. No mixer, no bus, no send, no automation. `MontageDocument` says so in the code.
- **MIDI** — little value in a stereo wave editor with no instrument path.
- **OGG Vorbis / Opus** — no Windows system codec, so it would add a native dependency.
- **AU / LV2** — not applicable on Windows.
- **Customizable shortcut editor, workspace layouts** — never started, and nothing has asked for
  them.
- **Vocal isolation / ML denoise** — needs bundled models; out of scope for a lean native app.
  **Still declined, but the ceiling is now measured rather than assumed.** An oracle Wiener mask —
  what a perfect estimator of this kind produces — beats the shipped spectral gate by **+9.63 dB
  over 108 cells, winning 108 of 108**, and by **+7.15 dB** against the better of the gate and doing
  nothing. So there is real room. The cheap half of it shipped as the adaptive depth rule; **the
  rest is now measured to be model-shaped.** Plain Wiener and decision-directed Wiener gains driven
  by the learned profile were built and measured over 120 cells, and the best of them collects
  **+0.10 dB** under the shipping configuration while moving one cell below do-nothing. Three
  estimator families — MMSE-STSA, subtraction-scaled Wiener, DD-Wiener — have now failed to close
  the gap, because the profile is stationary and the oracle's advantage is the per-frame
  signal/noise split. A model that estimates that split is the only untried thing left. See
  `CLAUDE.md`, "The noise-mask headroom is not reachable from the profile".

## 3. Built, measured, and deleted

Five methods existed, were defect-fixed by the August audit, and were called by nothing. Each was
measured against the method that ships in its place, on the corpora above, and each lost:

| deleted | against | result |
| --- | --- | --- |
| `RepairClicksSpectral` | Janssen | **−15.29 dB**, wins 1 cell of 84, negative at 12 and 6 dB above the local level |
| `RepairClippingSpline` | arch / A-SPADE chain | **−2.09 dB**; loses 32 of 36 record-transfer cells — a structural ceiling, not a calibration |
| `DetectSilencesAdvanced` | shipped detector | same 100% recall; **9 ms edge error against 3**, and **2 spurious gaps against 0** |
| `RemoveHumAdvanced` | notch bank | removes **26.68 dB** of hum for 2.48 dB of music moved, against **48.67** for 0.45; wins 0 of 54 |
| MMSE noise reducer | spectral gate | unwired, and the audit's version hung `AnalyzeClicks` |

The hum result is the one worth keeping. The first measurement said the adaptive remover won 52 of
54, and it was measuring the metric rather than the filter: a whole-signal residual charges phase
rotation as error, and cascaded notches rotate phase far outside their own bandwidth — notching
music with **no hum in it at all** scores about 20 dB of "damage" that way.
`NoiseReductionCostTests.AWaveformResidualCannotScoreANotchBank` pins the trap. It is the third time
in this repo a waveform residual has given the wrong answer about a filter.

## 4. Open defects

Each of these is characterised and reproducible. None is a task to be picked up casually — where
attempts have already been made and declined, the count is given, because the cheap ideas are spent.

### Declip: A-SPADE overshoots on lightly clipped material

**Closed, not solved. Do not start a sixth attempt without new evidence.** Five have been made and
all five declined: a hard `EstimatedTruePeak` bound, headroom scaled by plateau length, per-event
gating, recentring the estimator, and the shoulder-claim cap — which shipped and was withdrawn the
same day when a fifth corpus scored it **−38.6 dB** held out.

The axis is known: the cap gains on dense material and loses on sparse. This entry used to say that
what would justify trying again is more corpora, not another idea. A sixth arrived — Creative
Commons netlabel music, the first measurably dense population here at a 12.9 dB median crest factor
— and it **sharpened the problem without softening it**. Four of its 68 cells score below leaving
the damage alone, the first cells to do so in 532, all four at the mildest severity with 0.01% to
1.04% of samples clipped and mean plateaus of 6.8 to 8.2 samples. The arch wins three of the four
outright, so it is a routing failure rather than a repair failure.

That is still not a licence to fit a gate. The two rules that would divert exactly these cells were
each shipped and each destroyed by a later corpus: a damage floor, at **19.8 dB**, and a
short-plateau exception, at **668.7 dB**. The ceiling is about 0.4 dB a cell on record transfers,
and every attempt so far has cost more elsewhere than it gained there.

**The test was weakened rather than the defect fixed.**
`DeclipCorpusTests.TheChainBeatsLeavingTheDamageAlone` no longer asserts every cell. It asserts that
nothing loses once there is real damage (every cell at 0.50 and below, all six corpora), that every
population gains by more than 3 dB, and that losses stay rare — under a fiftieth of the set — and
stay at the mildest severity. It still fails if the corner spreads. The four cells are named in
`docs/validation-corpora.md` with their clipped fractions and plateau lengths.

### Click detection: recall was bought with false positives, on music as well as speech

The detector finds **82% of clicks 6 dB above the local level**, against 15% before four mechanism
fixes, a prediction-residual nomination pass, and a confidence floor that only became meaningful
once the recovery gate stopped conflating quiet with unrecovered.

What it costs is false detections. On undamaged speech, 2.75 to 4.51 events a second, where
classical is unchanged at 0.06. The sixth corpus makes the trade look worse than it did: dense
percussive music is digital-born and click-shaped, and the same recovery gate takes it from **0.35
to 1.45 events a second median, and 3.1 to 11.8 at worst** — the highest false-positive rate of any
digital corpus here, above Windows Media's 10.7. It also takes the record transfers from **1.21 to
2.56**.

**Whether 2.56 a second on record transfers is right cannot be settled here, and two attempts to
settle it physically both failed.** Differencing two transfers of the same performance fails because
different pressings do not correlate at sample level (0.05 to 0.08). The vertical groove component
is not music-free enough, and does not apply to stereo LPs anyway. It needs a recording with its
clicks marked by ear.

**Recall below 12 dB is worse than the synthetic figures say.** Measured against real click shapes
lifted off shellac, over six corpora: **83% at 12 dB** above the local level and **63% at 6 dB**,
against 94% and 82% for invented damage. Read the synthetic numbers as eleven points optimistic at
12 dB and nineteen at 6.

### Wow and flutter: earns its keep on gross wow, a wash to a loss below about 1%

The estimator used to measure velocity and integrate it, which injected 220 to 290 samples of drift
whatever was planted; it now measures position and leaves 230 down to 80. What it reports is
compensated for the reference window's own filter and reads 70 to 93% of the truth from 2.4% down to
0.6%, against 36 to 52% before. Below that it over-reads — 0.3% planted reads 0.283% — because it is
measuring its own noise, and the compensation amplifies that too.

Over six corpora, residual timing error uncorrected against corrected: **269 → 225** samples at 2.4%
planted, **168 → 154** at 1.2%, **96 → 98** at 0.6%, **55 → 78** at 0.3%. So **below about 1% wow
the correction is worse than leaving the recording alone.**
`CorrectingPlantedWowRemovesTimingErrorRatherThanAddingIt` asserts the two upper severities and
reports the lower two, because nothing has been fitted to gate them.

### Spectral heal: close to clean, two cells still lose

Weighting cells by how far they stand above their own surroundings — strictly where the continuation
refused, loosely where it reconstructed — took cells that came out worse from **14 of 55 to 2**, and
the worst from **−4.60 dB to −1.33**, improving every other severity too. The two that still lose
are both tonal orchestral. Corpus 6 added no new losses: 30 cells, +12.50 dB mean, worst cell +1.19.

### Vertical surface noise: the recommendation is calibrated on five files from one collection

The analyzer's own 60th-percentile programme gate reads −11.5 and −9.2 on the two records in the
middle of the set, which is no gap at all — the clean gap a hand-written gate showed on the first
pass was an artefact of that gate. The ramp is what makes this survivable: it recommends on a
control the user can see and move, never applies silently, and the card says "some of what goes is
music" rather than claiming a pressing. But **five files from one collection is not a corpus**, and
the declip calibrations were fitted this confidently five times and held out four. This one needs a
real population before the recommendation is trusted further than the control it sets.
