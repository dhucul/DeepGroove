# WaveLab — feature inventory & expansion roadmap

## 1. Current feature inventory (v1)

**File & formats** — WAV 16/24-bit PCM and 32-bit IEEE float read/write (sample-accurate custom RIFF codec, WAVE_FORMAT_EXTENSIBLE read, TPDF dither on 16-bit export); MP3/FLAC/M4A/WMA import via Media Foundation; multi-tab documents; drag-and-drop and command-line open; dirty tracking with close/exit prompts.

**Editing** — sample-accurate selection; cut/copy/paste/delete/trim; cross-tab clipboard (channel-matched); unlimited undo/redo via region splicing; select all.

**Processing (destructive, undoable)** — gain ±3 dB, peak normalize to −0.3 dBFS, equal-power fade in/out, reverse, DC offset removal, insert silence.

**Views** — multi-resolution min/max/RMS waveform (peak pyramid, zoom to sample level), overview bar with draggable viewport, adaptive time ruler, wheel zoom at cursor / Shift-wheel pan, real-time 4096-pt spectrum analyzer with peak hold, offline spectrogram of the current view.

**Master section (real-time + offline render)** — 3-band Studio EQ with response curve, lookahead brickwall limiter with GR readout, render-through-chain to a new tab.

**Metering** — peak/RMS bars with peak hold, EBU R128 momentary/short-term/integrated LUFS, loudness range, 4× oversampled true peak.

**Transport & recording** — WASAPI shared playback with selection loop; WASAPI recording with device picker, live input meters, elapsed readout, capture to new tab.

**UI** — dark studio theme, custom window chrome, embedded Inter/JetBrains Mono, keyboard shortcuts, custom-templated menus/sliders/combos/tabs.

**Known limitations** — single-file editor (no multitrack timeline); whole-file in RAM incl. undo copies; no settings persistence; playback tied to default output device; no autosave; export limited to WAV.

## 2. Expansion plan (classified)

### CRITICAL — foundation (Phase A)
- Settings with persistence (AppData JSON): output device, buffer/latency, autosave, default export depth, restore-defaults button
- Output device selection + configurable WASAPI buffer + reported latency
- Autosave of dirty documents + crash recovery on launch
- Recent files menu; window placement persistence
- Undo history memory budget (evict oldest) — large-file robustness
- Audio statistics dialog (per-channel peak/true peak/RMS/DC/clipped samples, offline LUFS/LRA, copy to clipboard)
- Export dialog: WAV 16/24/32f, MP3, AAC/M4A, WMA, FLAC (when the Windows encoder is present); bitrate choice; optional sample-rate conversion; export selection only

### HIGH VALUE (Phases B & C)
- Effects rack: ordered chain of real-time effects replacing the fixed EQ→limiter pair — Studio EQ, Precision Limiter, Compressor, Noise Gate, Reverb, Stereo Delay, Chorus, Saturation, HP/LP Filter; add/remove/reorder/bypass; auto-generated parameter UI; **per-effect reset-to-default**; chain presets (factory + user JSON); destructive **apply chain to selection/file** with undo
- Markers & regions: add/rename/delete, ruler flags, prev/next navigation, region from selection, sidecar persistence next to the audio file, markers panel
- New analysis views: Loudness History graph (M/S over time), Phase view (goniometer + stereo correlation meter, correlation also live in master panel)
- Restoration suite: spectral noise reduction with learned noise profile, click/pop repair, 50/60 Hz hum removal (notch comb) — destructive with undo
- Silence tools: detect silences → markers, trim silence, split by silence
- Stereo tools: mono↔stereo convert, swap channels, extract channel, phase invert, per-channel balance
- Crossfade edit-point smoothing

### ADVANCED (Phase D)
- Time stretch (WSOLA) and pitch shift without length change
- High-quality sample-rate conversion (polyphase windowed sinc)
- Recording: count-in with metronome click at set BPM/meter, metronome during record, punch-insert into selection with edge crossfades
- Tuner (YIN pitch detect on selection → note/cents) and tempo/BPM estimation
- Batch converter (multi-file → format, optional normalize/chain preset, cancellable, background)
- Command palette (Ctrl+Shift+P, searchable commands)
- Vertical amplitude zoom; CPU/RAM readouts in status bar

### OPTIONAL / deferred (with reasons)
- **Multitrack timeline, mixer, buses, sends, automation** — a different product architecture (clip graph, per-track streams); would be a rewrite, not an extension. Deferred deliberately.
- **VST3 hosting** — real but large undertaking (COM interop, editor window embedding, threading); candidate for v3 as its own project phase.
- **MIDI** — little value in a stereo wave editor without an instrument path.
- **Vocal isolation / ML denoise** — needs bundled ML models; out of scope for a lean native app.
- **OGG Vorbis/Opus** — no Windows system codec; would add a native dependency.
- **AU/LV2** — not applicable on Windows.
- Customizable shortcut editor, workspace layouts, noise-shaped dither — nice-to-haves behind everything above.

## 3. Order of work
A (foundation) → B (rack) → C (markers/analysis/restoration) → D (tools) — building and smoke-testing on-device after each phase, keeping every existing feature working.
