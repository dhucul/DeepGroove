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

- **The sparse reconstruction cap loses 5.18 dB on spoken word** while gaining 15.9 on record
  transfers and 30.6 on Windows Media. `ShouldersBoundTheReconstruction` decides from the overshoot
  the shoulders claim, which cannot see that a signal is sparse with long silences. Whether that
  deserves a second variable is open — but four refinements to this cap have been measured and
  declined, three of which looked good until a held-out corpus, so the bar is a fifth corpus rather
  than another fit against the four in hand.
