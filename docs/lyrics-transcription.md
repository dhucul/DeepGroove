# Lyrics & Speech (experimental)

Open a song, optionally select a difficult passage, and choose **Tools > Lyrics & Speech…**.
Click **Transcribe**. The engine, vocal separator, and both speech models are included in
the Release application and installer. There is no separate setup, account, API key, or
first-use download. Transcription works offline; audio never leaves your computer.

The command also appears in the command palette. It reads the current audio, including
unsaved edits, without changing the recording or applying the master rack.

## Reading and correcting

- Start with **Music · isolate vocals** and **Accuracy · large-v3**. Choose the language if
  known, and optionally supply names or unusual words as spelling hints. Select a verse for
  a quicker first test. **Speed · turbo** uses the included faster speech model.
- Double-click a line to correct it. Select a line and click **Replay line** to hear the
  original mix. **Loop** repeats it with a little context at either edge. **Stop** also
  stops song playback that was running before you opened this window.
- **Review** flags uncertain recognition, not a calibrated accuracy percentage. Even
  high-confidence text can be wrong. Listen while reviewing the result.
- **Check for missed words (slower)** compares the whole original recording with the
  separated-vocal transcript, then retries uncovered or unclear passages in short overlapping
  windows. Added phrases are marked **Recovered** and need a listening check. If a pass adds
  words to an existing line, its earlier reading is kept as an alternative. Conflicting readings
  also remain available with **Use alternate reading**; there is no lyric-completion language model.
- **Cancel** stops the worker and keeps the previous transcript. Closing the window also
  cancels, waits for the worker, and removes temporary audio.
- Corrections remain with the open audio tab, but are not included in audio saves or
  autosave recovery. **Export before closing the audio tab or app**. Audio edits retain the
  old text for export but disable replay until its timings are regenerated.

## Vocals-only audio

Click **Vocals to new tab** in the Lyrics & Speech window to extract the singing as audio.
Use **Selected audio only** for a passage, or turn it off for the whole song. This runs the
included vocal separator directly and skips speech recognition. Your existing transcript and
the original audio are preserved. On completion the dialog closes and a new, unsaved
**song - vocals.wav** tab opens. Use **File > Save As** or **Export** to choose the file location
and format. The result is stereo, 44.1 kHz, 32-bit float working audio; a selected passage starts
at zero in its new file. Cancellation opens no new tab and removes temporary output.

Separation can leave backing music or introduce artifacts; listen before saving a final copy.

## Exports

Copy corrected text or export UTF-8 TXT, timed LRC lyrics, SRT subtitles, or detailed JSON.
Times refer to the beginning of the whole source file even when a selection was transcribed.
JSON preserves original `model_text` and word timing/probability evidence separately from
edited `text`; editing a line does not realign its word timings. Timing is approximate,
especially for sustained singing.

## Quality and performance

The default pipeline uses fine-tuned **HTDemucs (`htdemucs_ft`)**, two shift passes and
50% overlap, then **Whisper large-v3 through faster-whisper / CTranslate2** with beam search,
temperature fallback, and word timestamps. Separation can help dense mixes but can also
introduce artifacts; **Music · original mix** is available for comparison. **Speech** enables
voice activity detection; music modes disable it and the speech-specific no-speech/word-duration
  deletion heuristics so long or uncertain sung words are retained for review. Recognition copies
  retain floating-point precision and use bounded gain to help quieter syllables; source audio
  and vocals-only exports are unchanged.
Previous-text conditioning is disabled to reduce repetition loops; actual repeated choruses
are retained. When stereo channels strongly cancel, the analysis uses the stronger channel
for both recognition and separation. The source recording is unchanged.

The x64 Windows program uses NVIDIA CUDA when supported, with a CPU fallback. Both paths
are included; **CPU only** needs no reinstall. CPU processing, especially vocal isolation,
may take longer than the recording. Requests are limited to **30 minutes** to bound working
memory and temporary disk use. Use selections for long recordings and album sides.

Models and runtime files are part of the application's `Transcription\Engine` directory.
Keep that directory with the executable when moving the program. Only temporary jobs and
runtime caches go under `%LOCALAPPDATA%\WaveLab\Lyrics`; normal cancellation and completion
remove working audio. A system crash can leave files in its `jobs` directory. A missing or
damaged payload reports an incomplete installation rather than offering a runtime download.

Choirs, overlapping voices, harsh vocals, reverb and instrumental music can still produce
omissions or invented words. This is a transcription assistant, not a verified lyrics
database; we do not claim best-in-class song accuracy without a representative benchmark.

## Building and testing

The ordinary **Release** build prepares the complete bundle and copies it into the normal
`src\WaveLab\bin\Release\net10.0-windows` application folder. Publishing and the Windows
installer include that same payload. The first developer build needs Internet access and
space for the runtime, models and download cache; subsequent unchanged builds reuse the
verified bundle under `artifacts\lyrics-bundle`. Nothing is downloaded by the running app.

`installer\Prepare-LyricsBundle.ps1` uses a checksum-verified uv release to prepare a
standalone Python 3.11 distribution (not a virtual environment with absolute machine paths),
the pinned CPU/CUDA dependencies, and pinned model revisions. Model-cache symlinks are
materialized into ordinary files. Static compilation libraries are omitted; third-party
licenses and model cards are retained. The installer uses data slices because the bundled
models exceed a single installer executable's size limit.

```powershell
dotnet build src/WaveLab/WaveLab.csproj -c Release
dotnet test WaveLab.sln -c Release
& '.\src\WaveLab\bin\Release\net10.0-windows\Transcription\Engine\python\python.exe' -m unittest discover -s tests/transcription -v
```

For code-only CI checks, `-p:SkipLyricsBundle=true` avoids downloading/copying the large
payload; such a build is not distributable with working transcription. Debug builds can
explicitly use a prepared bundle when testing the engine. Model-free checks cover timing,
exports, malformed output, phase cancellation, transport, payload validation/relocation,
and immediate transcription availability in the real WPF dialog. Inference smoke tests
should exercise both models and vocal isolation with networking disabled and empty user
caches; these establish functionality, not singing-recognition accuracy.

References: [Demucs](https://github.com/adefossez/demucs),
[faster-whisper](https://github.com/SYSTRAN/faster-whisper),
[source-separation research](https://arxiv.org/abs/2506.15514).
