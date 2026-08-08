# WaveLab

A sleek audio editor and mastering suite for Windows, built with WPF on .NET 10.

![WaveLab](docs/design/wavelab_main.png)

## Features

**Formats & files** — sample-accurate WAV 16/24-bit and 32-bit IEEE float (TPDF dither on 16-bit); MP3/FLAC/M4A/WMA import; export to WAV, MP3, AAC, WMA and FLAC (where Windows provides the encoder) with bitrate choice, sample-rate conversion and selection-only export; batch converter with LUFS/peak normalization and effect-chain processing; recent files, session restore, autosave with crash recovery.

**Editing** — multi-resolution waveform with zoom to sample level and vertical amplitude zoom (Ctrl+wheel); drag selection; cut/copy/paste/delete/trim; unlimited undo/redo with a configurable memory budget; markers and named regions with ruler flags, navigation shortcuts, a manager panel and sidecar persistence; edit-point smoothing (de-click joins); silence detection, trimming and split-to-regions.

**Master rack** — reorderable real-time effect chain: Studio EQ, Compressor, Noise Gate, Reverb, Stereo Delay, Chorus, Saturation, Low/High-Pass Filters, Precision Limiter (5 ms lookahead). Every effect has bypass, remove, move and **reset-to-default**; chains save/load as presets (factory presets included); the chain applies destructively to a selection or renders the whole file to a new tab, both latency-compensated.

**Processing** — gain, normalize, equal-power fades, reverse, DC removal, silence insertion; channel tools (swap, phase invert, balance, mono↔stereo, channel extraction); WSOLA time stretch and pitch shift; windowed-sinc sample-rate conversion.

**Restoration** — spectral noise reduction with a learned noise print, click/pop repair, mains-hum removal (50/60 Hz + harmonics).

**Analysis** — real-time spectrum analyzer, offline spectrogram, loudness history graph, goniometer with stereo correlation and balance; peak/RMS meters with hold; EBU R128 momentary/short-term/integrated LUFS, LRA and true peak; per-file audio statistics with clipping count; pitch detection (tuner) and tempo/BPM estimation.

**Recording** — WASAPI capture with device picker and live meters; metronome count-in at any BPM; click track while recording; punch-insert into the current selection with smoothed joins.

**Workflow** — settings dialog (devices, buffer/latency, autosave, export defaults) with restore-defaults; searchable command palette (Ctrl+Shift+P); window placement memory; CPU/RAM readouts; drag-and-drop and command-line file open.

## Building

Requires the .NET 10 SDK (ships with Visual Studio 2026).

```
dotnet build WaveLab.sln -c Release
dotnet run --project src/WaveLab
```

## Keyboard shortcuts

Space play/stop · Home go to start · Ctrl+O/S/Shift+S open/save/save-as · Ctrl+E export · Ctrl+Z/Y undo/redo · Ctrl+X/C/V/Del edit · Ctrl+A select all · Ctrl+= / Ctrl+- / Ctrl+0 zoom · Ctrl+M marker · Ctrl+Shift+M region · Alt+←/→ marker navigation · Ctrl+R record · Ctrl+Shift+P command palette · wheel zoom · Shift+wheel pan · Ctrl+wheel amplitude zoom

UI typefaces: [Inter](https://rsms.me/inter/) and [JetBrains Mono](https://www.jetbrains.com/lp/mono/), embedded. Design mockups and the feature roadmap live in `docs/`.
