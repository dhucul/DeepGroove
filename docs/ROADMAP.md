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
Validated against four external corpora; see `docs/validation-corpora.md`.

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
- **OGG Vorbis / Opus** — no Windows system codec, so it would add a native dependency.
- **AU / LV2** — not applicable on Windows.
- **Customizable shortcut editor, workspace layouts** — never started, and nothing has asked for
  them.

## 3. Actually open

- **A-SPADE overshoots on lightly clipped material and nothing currently stops it.** A cap keyed on
  the overshoot the shoulders claim shipped and was withdrawn: it gains on dense material and loses
  on sparse, because the shoulders claim little overshoot exactly when the signal is sparse. Over
  five corpora it is worth 0.017 dB a cell and −38.6 dB held out. The problem is real; the fix has
  to **separate dense material from sparse**, and has to clear five corpora. Five attempts so far,
  five declined.

- **The click detector still misses about half the quietest clicks.** Recall is now 86% at 12 dB
  above the local level and 51% at 6 dB, up from 68% and 15%, after three mechanism fixes and a
  prediction-residual nomination pass. What limits it now is the acceptance tests rather than
  detection: raising the nomination threshold from 8 to 14 sigma costs one point of recall and no
  gain at all. False detections on digital-born material fell from 45.9 events a second at worst to
  9.9, but 9.9 on an undamaged alarm is still 9.9.
- **Whether record transfers reading 1.21 events a second is right cannot be settled here, and two
  attempts to settle it physically both failed.** Differencing two transfers of the same performance
  fails because different pressings do not correlate at sample level (0.05 to 0.08). The vertical
  groove component is not music-free enough, and does not apply to stereo LPs anyway. It needs a
  recording with its clicks marked by ear.
- **Recall below 12 dB is worse than the synthetic figures say.** Measured against real click shapes
  lifted off shellac, recall is 74% at 12 dB above the local level and 40% at 6 dB, against 85% and
  51% for invented damage. Read the synthetic numbers as about eleven points optimistic.

- **Spectral heal at the local level is much better but not clean.** Weighting refused cells by how
  far they stand above their own surroundings took cells that came out worse from 13 of 61 to 2, and
  the worst from −4.5 dB to −2.2, while improving every other severity as well. Two cells still lose.
- **Wow and flutter now removes drift instead of adding it, but still under-reports.** The
  estimator measured velocity and integrated it, which injected 220 to 290 samples of drift
  whatever was planted; it now measures position and leaves 227 down to 75. What it *reports* is
  still about 2.5x low, and the reference width that would fix the number makes the correction
  worse, so the two cannot currently be had together. Below about 1% wow the correction still does
  not improve on leaving the recording alone.
