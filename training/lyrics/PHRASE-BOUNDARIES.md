# Pause-aware lyrics recovery

This experiment uses the existing bundled Whisper model. No additional training,
download, service, or model is needed by the application.

**Outcome:** the pause-aware retry strategy passed the independent check with a
modest error reduction and was enabled in the existing Release app. The main-pass
replacement was rejected. The original model and both main passes remain in use.

## Approach

The isolated-vocal waveform is measured in 20-ms blocks with a 60-ms RMS envelope.
Pauses lasting at least 600 ms guide cut positions. The threshold is 6% of the
maximum envelope level. It is used only to choose cuts: the windows cover the
entire recording, including quiet openings and low-level vocals.

Each core has at most 26 seconds before contextual padding; a final core can reach
28 seconds with padding on one side. Two seconds of context are retained at each
available edge, with input windows capped at Whisper's 30-second limit. The last
pair is balanced to avoid leaving a very short tail. Short windows gain surrounding
context where available. Recordings of 30 seconds or less retain their existing path.

The design is informed by [Syed et al., 2025](https://arxiv.org/abs/2506.15514), but
is an independent implementation. It keeps complete audio coverage rather than
treating an amplitude threshold as proof that there are no words.

## Development comparison

Two strategies were fixed before comparison:

- `primary`: transcribe the main vocal pass in the new windows.
- `retry`: preserve the main and original-mix passes, using the new windows for
  uncovered/uncertain passages. Keep the existing opening-context retry.

The original model, inputs, hints (none), and decoder seeds were held constant.
Previously generated isolated vocals and baseline transcripts were reused only
after checking the cached input identity. Five previously evaluated songs were
used as development data, plus the user's local regression song.

| Development measure | Existing | Main-pass replacement | Pause-aware retries |
|---|---:|---:|---:|
| Joined trusted-interval WER | 18.12% | 20.34% | 17.52% |
| Timed-interval WER | 20.54% | 22.95% | 19.93% |
| Revised whole-song reference WER | 20.70% | 25.90% | 20.28% |

The main-pass replacement also lost the opening phrase in the local regression
song and was rejected. The retry strategy retained the opening phrase and all four
confirmed chorus endings. The opening wording itself remains uncertain.

Only `retry` proceeds to the independent check. Its parameters are frozen before
reading fresh results. The measured results below support enabling that strategy
only for isolated music with **Check for missed words** enabled.

## Independent check

Five different artists were selected with seed 20260926, excluding all artists
in training/development and the prior five-song selection:

- Side Effects Project — Sing With Me
- James Elder & Mark M Thompson — The English Actor
- PR — Oh No
- Nerve 9 — Pray For The Rain
- AM Contra — Heart Peripheral

Each full recording is isolated and transcribed with both the unchanged baseline
and the frozen retry strategy. The existing interval/omission/instrumental metrics
are retained. An additional full-song check uses the authors' revised
[MUSDB-ALT references](https://huggingface.co/datasets/jazasyed/musdb-alt), pinned to
revision `42d82cb939c20435a4118da64efef96dc6ea8c08`, and `alt-eval==1.2.0`.
The latter scorer retains backing vocals and non-lexical sung sounds. No reference
text is supplied to the recognizer. PR — Oh No is excluded by the reference authors
because of processed vocal samples; it remains in the five-song interval test.

Both scoring methods are recorded. The new check does not replace or selectively
discard an unfavorable existing score.

| Independent result | Existing | Pause-aware retries |
|---|---:|---:|
| Timed-interval WER, all five songs | 25.99% | 24.17% |
| Joined trusted-interval WER, all five songs | 22.14% | 20.64% |
| Revised complete-song WER, four available references | 18.86% | 18.34% |
| Instrumental words invented in the 19 scored instrumental intervals | 7 | 7 |
| Deletions within timed intervals | 55 | 53 |
| Deletions in joined trusted intervals | 28 | 30 |
| Deletions in revised complete-song references | 110 | 113 |

There were 92 trusted intervals (939 normalized words), and 935 words after joining
and normalizing per song. The revised full-song references contained 1,543 words.
The gain is modest and primarily comes from fewer extra incorrect words. Omission
counts do not improve consistently, and this is not a claim that every missed
lyric is recovered. The uncertain opening wording in the local regression song
remains uncertain.

The five-song timed comparison improves by 17 word errors; the revised four-song
comparison improves by eight errors. The heavily processed PR track remains in
the interval comparison even though the newer reference authors exclude it.

## Release verification

Only `lyrics_recovery.py` and `lyrics_worker.py` were replaced, atomically, in the
existing `src/WaveLab/bin/Release/net10.0-windows/Transcription` folder. The helper
was installed before the caller. The application was not closed or duplicated;
its model files and existing transcripts were not modified.

The installed worker passed its readiness check and transcribed the local regression
song. Its complete line/word/timing output exactly matched the tested retry output:
13 lines, the opening phrase beginning at 9.3 seconds, four confirmed chorus endings,
and no recurrence of the earlier incorrect chorus wording. Speech, original-mix
mode, disabled missed-word checking, and short selections retain their previous path.

## Tests and reproduction

Thirty-eight transcription tests passed, including coverage of quiet openings,
sample-bounded windows, pauses versus syllable gaps, gain invariance, repeated
words, global time offsets, and preservation of speech/unseparated behavior.
The regular training/evaluation test suite also passed (15 checks).

Research scripts are in `training/lyrics/`: `evaluate_phrases.py` runs the model,
`score_acceptance.py --variants base retry --skip-speech` scores timed references,
and `score_full_lyrics.py` scores whole-song references. Install the additional
scorer only in the research environment using `requirements-evaluation.txt`.
The inference runtime and model bundle are unchanged; only the two application
transcription scripts were updated.

Local evidence under ignored `artifacts/lyrics-training/`:

- `evaluations/phrase-development/`: both prototypes and the original baseline.
- `evaluations/phrase-fresh/`: frozen retry strategy versus baseline on new songs.
- `acceptance-data/phrase-fresh/`: selection, public audio, and interval references.
- `acceptance-data/musdb-alt/`: revised references and download provenance.
- `evaluations/phrase-release-smoke/`: installed-worker result and exact-output verification.

Audio and reference lyrics remain local. The research datasets' noncommercial
terms still apply; neither dataset nor trained weights are redistributed here.
