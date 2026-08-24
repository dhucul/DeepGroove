# Deep Groove — status and what is deliberately not here

This was the v1 expansion plan. **Everything it classified as Phase A through D is built**, so it
is no longer a plan; it is kept as a status map and as the record of what was deliberately declined
and why. `CLAUDE.md` is the engineering record — how each piece works, what was measured, and which
approaches were tried and rejected. When the two disagree, `CLAUDE.md` is the one maintained
alongside the code.

Verified against the source, not from memory, on 2026-08-19.

## 1. Shipped

**File & formats** — WAV 16/24-bit PCM and 32-bit float, RF64/BW64 above 2 GB, Wave64; AIFF/AIFF-C
import and AIFF 16/24/32-bit output; MP3/FLAC/M4A/WMA import via Media Foundation; MP3/AAC/WMA/FLAC
export; ID3v2.4 tagging; chunk-preserving round trips; multi-tab documents; drag-and-drop and
command-line open.

**Editing & processing** — sample-accurate selection, cut/copy/paste/trim, cross-tab clipboard,
unlimited undo/redo with a memory budget, gain, normalize, fades, reverse, DC removal, insert
silence, crossfade smoothing, silence detection and trimming, stereo tools, channel balance.

**Effects rack** — ordered real-time chain with add/remove/reorder/bypass, auto-generated parameter
UI, per-effect reset, factory and user presets, destructive apply with undo. Studio EQ, linear-phase
EQ, dynamic EQ, limiter, compressor, multiband compressor, gate, de-esser, transient shaper, reverb,
convolution reverb, delay, chorus, saturation, filter, stereo width, mono-to-stereo, channel
balance, level normalizer, noise reduction, hum removal, trim — more than the plan asked for.

**Time and pitch** — time stretch without pitch change, pitch shift without length change, tuner and
tempo estimation, polyphase sample-rate conversion.

**Restoration** — declip (arch reconstruction and A-SPADE, chosen per channel), click and crackle
repair, spectral noise reduction with a learned profile, hum removal, wow and flutter, needle-drop
and run-out detection, and a spectral editor with time-frequency reassignment and region repair.
Validated against six external corpora; see `docs/validation-corpora.md`.

**Analysis & metering** — peak pyramid waveform, spectrum analyser, spectrogram, constant-Q,
amplitude and frequency rulers, vertical amplitude zoom, EBU R128 loudness with history, true peak,
phase and correlation, statistics dialog, loudness compliance targets.

**Delivery** — CD transfer and PQ sheet editor, DDP 2.00 image sets, CD import, batch converter,
export dialog with format, depth, rate conversion and selection-only.

**Transport, recording & UI** — WASAPI playback and recording with device selection and buffer
control, input monitoring, metronome and click track, punch-in, markers and regions with a panel and
sidecar persistence, audio montage (single lane), settings persistence, autosave **with crash
recovery**, recent files, window placement, command palette, help catalogue, VST3 hosting with an
embedded editor window.

**VST3 shipped despite being deferred here as "a candidate for v3".** So did noise-shaped dither,
listed as a nice-to-have. Both are done.

## 2. Still deliberately not here

- **Multitrack timeline, mixer, buses, sends, automation** — a different product architecture; a
  rewrite rather than an extension. The **audio montage is a single-lane clip timeline**, not a
  step towards this: one lane is what makes an overlap unambiguously a crossfade, which is what
  makes the join measurable. No mixer, no bus, no send, no automation.
- **MIDI** — little value in a stereo wave editor with no instrument path.
- **Vocal isolation / ML denoise** — needs bundled models; out of scope for a lean native app.
  **Still declined, but the ceiling is now measured rather than assumed**: an oracle Wiener mask,
  which is what a perfect estimator of this kind produces, beats the shipped spectral gate by
  **+9.63 dB over 108 cells and wins 108 of 108** — **+7.15 dB** against the better of the gate and
  doing nothing. So there is real room. The cheap half shipped as the adaptive depth rule; **the
  rest is now measured to be model-shaped**: plain Wiener and decision-directed Wiener gains driven
  by the learned profile were built and measured over 120 cells, and the best of them collects
  **+0.10 dB** under the shipping configuration while moving one cell below do-nothing. Three
  estimator families (MMSE-STSA, subtraction-scaled Wiener, DD-Wiener) have now failed to close the
  gap, because the profile is stationary and the oracle's advantage is the per-frame signal/noise
  split. A model that estimates that split is the only untried thing left. See the CLAUDE.md
  section "The noise-mask headroom is not reachable from the profile".
- **OGG Vorbis / Opus** — no Windows system codec, so it would add a native dependency.
- **AU / LV2** — not applicable on Windows.
- **Customizable shortcut editor, workspace layouts** — never started, and nothing has asked for
  them.

## 3. Actually open

- **A-SPADE overshoots on lightly clipped material. Closed, not solved — do not start a sixth
  attempt without new evidence.** Five have been made and all five declined: a hard
  `EstimatedTruePeak` bound, headroom scaled by plateau length, per-event gating, recentring the
  estimator, and the shoulder-claim cap, which shipped and was withdrawn the same day when a fifth
  corpus scored it −38.6 dB held out. The axis is now known — the cap gains on dense material and
  loses on sparse — but with five corpora splitting two dense and two sparse, any gate that
  separates them is as likely to be fitting corpus identity as signal, and leave-one-corpus-out
  would only partly catch it. This entry used to say **what would justify trying again is more
  corpora, not another idea**. A sixth arrived — Creative Commons netlabel music, the first
  measurably dense population here at a 12.9 dB median crest factor — and it **sharpened the
  problem without softening it**: four of its 68 cells now score below leaving the damage alone,
  the first cells to do so in 532, all four at the mildest severity with under 1.1% of samples
  clipped, and the arch wins three of them outright. So the axis is confirmed and the corner is
  now visible on real material rather than inferred. It is still not a licence to fit a gate: the
  two rules that would catch exactly these cells, a damage floor and a short-plateau exception,
  were each shipped and each destroyed by a later corpus, at 19.8 and 668.7 dB. The ceiling is
  about 0.4 dB a cell on record transfers, and every attempt so far has cost more elsewhere than
  it gained there.

- **Four cells of 532 now lose to leaving the damage alone, and the gate was weakened rather than
  the defect fixed.** They are named in `docs/validation-corpora.md` with their clipped fractions
  and plateau lengths. `DeclipCorpusTests.TheChainBeatsLeavingTheDamageAlone` no longer asserts
  every cell; it asserts that nothing loses once there is real damage (every cell at 0.50 and
  below, all six corpora), that every population gains by a wide margin, and that losses stay rare
  and stay at the mildest severity — so it still fails if the corner spreads.

- **The click detector now finds 82% of clicks 6 dB above the local level, against 15% this
  morning**, after four mechanism fixes, a prediction-residual nomination pass and a confidence
  floor that only became meaningful once the recovery gate stopped conflating quiet with
  unrecovered. What it costs is false detections on speech, 2.75 to 4.51 a second on undamaged
  audio, where classical is unchanged at 0.06. Measured against real click shapes rather than
  invented ones, expect eleven points less at 12 dB and nineteen at 6. **A sixth corpus makes that
  trade look worse than it did**: dense percussive music is digital-born and click-shaped, and the
  same recovery gate takes it from 0.35 to 1.45 events a second median and 3.1 to 11.8 at worst —
  the highest false-positive rate of any digital corpus here, above Windows Media's 10.7. It also
  takes the record transfers from 1.21 to 2.56. The cost is on music as well as speech.
- **Whether record transfers reading 2.56 events a second is right cannot be settled here, and two
  attempts to settle it physically both failed.** Differencing two transfers of the same performance
  fails because different pressings do not correlate at sample level (0.05 to 0.08). The vertical
  groove component is not music-free enough, and does not apply to stereo LPs anyway. It needs a
  recording with its clicks marked by ear.
- **Recall below 12 dB is worse than the synthetic figures say.** Measured against real click shapes
  lifted off shellac, and now over six corpora, recall is 83% at 12 dB above the local level and 63%
  at 6 dB, against 94% and 82% for invented damage. Read the synthetic numbers as eleven points
  optimistic at 12 dB and nineteen at 6.

- **Spectral heal at the local level is close to clean.** Weighting cells by how far they stand
  above their own surroundings — strictly where the continuation refused, loosely where it
  reconstructed — took cells that came out worse from 14 of 55 to 2, and the worst from −4.60 dB
  to −1.33, improving every other severity too. Two cells still lose, both tonal orchestral.
- **Wow and flutter removes drift instead of adding it, and now reports close to the truth — but
  the floor is the remaining limit.** The estimator measured velocity and integrated it, which
  injected 220 to 290 samples of drift whatever was planted; it now measures position and leaves 230
  down to 80. The reported figure is compensated for the reference window's own filter and reads 70
  to 93% of the truth from 2.4% down to 0.6%, against 36 to 52% before. Below that it over-reads —
  0.3% planted reads 0.283% — because it is measuring its own noise and the compensation amplifies
  that too. **Below about 1% wow the correction is worse than leaving the recording alone**, and
  over six corpora that is now measured rather than suspected: residual timing error goes 96 → 98
  samples at 0.6% planted and 55 → 78 at 0.3%, against 269 → 225 at 2.4%.
