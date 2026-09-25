# Lyrics & Speech (experimental)

Open a song, optionally select a difficult passage, and choose **Tools > Lyrics & Speech…**.
The command is also in the command palette. This feature reads the current audio, including
unsaved edits; it never changes the audio, applies the master rack, or uploads a recording.

## First use

1. Click **Set up local engine**. No API key or paid account is required.
2. Allow approximately 20 GB of free disk space, including installation caches and temporary audio. Setup downloads a private Python 3.11
   environment and inference dependencies; the first transcription downloads model weights.
   An Internet connection is required for setup and uncached models. Afterwards cached models
   can run offline. Setup does not alter the system Python installation.
3. Start with **Music · isolate vocals**, **Accuracy · large-v3**, and the song's language if
   known. Automatic language detection is available, but can struggle with long instrumental
   introductions. Select a verse for a quicker first test.
4. Click **Transcribe**. Review the result while listening. Use **Cancel** to stop a job;
   closing the window also cancels and waits for the worker to exit.

The current engine installer supports x64 Windows. Automatic processor selection installs
PyTorch's CUDA 12.8 build when the NVIDIA driver utility is present; otherwise it installs the
CPU build. A compatible NVIDIA driver and enough available GPU memory are required for GPU
inference. The 8 GB RTX 5060 development system ran the large-v3 model successfully. Detected
GPU failures are retried in a fresh CPU process. You can select **CPU only** explicitly;
use **Repair / change engine** after changing this setting to replace the installed torch
build. CPU inference, especially fine-tuned vocal separation, can be slow.

Software and model caches live under `%LOCALAPPDATA%\WaveLab\Lyrics`. Software installation
uses a pinned, SHA-256-verified uv archive from Astral's official release, pinned direct
dependencies from PyPI, and torch from the official PyTorch index. Models come from their
upstream Hugging Face repositories (Demucs can fall back to its upstream Meta download host).
Audio is passed only to a local child process. Working audio and isolated vocals are removed
after success, failure, or normal cancellation; a system crash can leave files in `Lyrics\jobs`.

## Reading and correcting

- Double-click a line's text to correct it; select it and click **Replay line** to hear the
  original mix. **Loop** repeats that line with a little context at each edge.
- **Stop** also stops song playback that was already running when you opened this window.
- **Review** is a recognition heuristic, not a calibrated accuracy percentage. It flags weak
  word probabilities, weak segment likelihood, possible non-voice content, and repetitive
  decoding. High-confidence output can still be wrong.
- With vocal isolation enabled, **Check difficult lines against original mix** transcribes
  uncertain passages a second time from the unseparated audio. When it disagrees, an alternate
  reading appears below the list. **Use alternate reading** swaps the two texts; nothing is
  silently rewritten by a language model.
- Corrections remain with the open audio tab when this window closes. They are not included
  in audio saves or autosave recovery. **Export before closing the audio tab or app**.
- If the audio is edited afterwards, the old transcript is retained for export but replay is
  disabled until it is regenerated, because its timings may no longer match.

## Exports

**Copy text** copies corrected lines. **Export** supports UTF-8 plain text, timed LRC lyrics,
SRT subtitles, and detailed JSON. Times always refer to the start of the whole source audio,
including when only a selection was transcribed. JSON retains `model_text` and the original
model's word timings/probabilities separately from edited `text`; word timings do not realign
when you correct a line. Timing is approximate, especially for sustained sung syllables.

## Quality and limits

The default pipeline uses fine-tuned **HTDemucs (`htdemucs_ft`)** with two shift passes and
50% overlap, followed by **Whisper large-v3 through faster-whisper / CTranslate2**, beam
search, temperature fallback, and word timestamps. Vocal isolation can help with dense mixes,
but separation artifacts sometimes hurt recognition; **Music · original mix** is useful for
comparison. **Speed · turbo** trades some model capacity for speed. **Speech** enables voice
activity detection; music modes deliberately disable it so held vowels and soft singing are
not discarded as non-speech. Previous-text conditioning is disabled to reduce repetition loops.
Actual repeated choruses are retained. Spelling hints supply names and unusual words only.

If the stereo channels strongly cancel in mono, the analysis copy uses the stronger channel
for both recognition and vocal separation. Silence is checked across the source channels;
ordinary stereo and the original recording are preserved.

Work is limited to **30 minutes per request** to bound memory and temporary disk usage. Use
selections for album sides and long recordings. Mono/stereo is preserved in the analysis copy;
files with more than two channels are downmixed to mono. This is an experimental transcription
assistant, not a verified lyrics database. Choirs, harsh vocals, overlapping voices, reverb,
unfamiliar languages, and instrumental music can still cause omissions or invented words.
We do not claim best-in-class accuracy across songs without a representative benchmark.

Implementation references:

- [Demucs upstream and published model results](https://github.com/adefossez/demucs)
- [faster-whisper options and hardware requirements](https://github.com/SYSTRAN/faster-whisper)
- [Research on source separation for lyrics transcription](https://arxiv.org/abs/2506.15514)

## Development checks

```powershell
dotnet test WaveLab.sln -c Release --filter FullyQualifiedName~LyricsTests
python -m unittest discover -s tests/transcription -p test_worker.py -v
# Audio decoder checks use the installed local engine's Python and need no model downloads:
& "$env:LOCALAPPDATA\WaveLab\Lyrics\environment-v1\Scripts\python.exe" -m unittest discover -s tests/transcription -v
```

These checks require no downloaded models. They exercise selection offsets, invalid worker
data, cancellation, audio resampling/channel preservation, stereo phase cancellation, corrected
text export, stale audio protection, transport control, and the real WPF dialog. Inference smoke tests should also exercise a local voice
sample, a mix containing that voice, silence, and the CPU path. Those smoke tests establish
functionality, not singing-recognition accuracy.
