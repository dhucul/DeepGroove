# Vocal separator comparison

Status: completed on 2026-09-26. Candidate retained for research; the existing
Release remains unchanged and no additional application copy was created.

## Result and decision

Mel-Band RoFormer improved the raw full-song score on four of five public songs,
but increased omissions overall and lost opening coverage in the local regression
song. It is promising, especially for speed and fewer instrumental false positives,
but these results do not support replacing the current default without further
work on omissions and a broader evaluation with complete references.

During the reference audit, one supposedly complete reference was found to contain
an explicit `(Not transcribed)` placeholder from 16.75 to 35.23 seconds in
The Sunshine Garcia Band track. It remains in the recorded five-song comparison,
but the four songs without explicit gaps are also reported separately. This
sensitivity check was added after scoring; no inference settings or outputs changed.
The large overall improvement is concentrated in that one track, although its
reference gap does not by itself establish that the remaining gains are spurious.

| Full-song lyric error rate | HTDemucs-ft | Mel-Band RoFormer |
| --- | ---: | ---: |
| Zeno - Signs | 26.81% | 17.39% |
| Raft Monk - Tiring | 12.07% | 9.77% |
| Cristina Vane - So Easy | 11.72% | 10.34% |
| The Sunshine Garcia Band - For I Am The Moon (reference gap) | 58.76% | 25.18% |
| Bobby Nobody - Stitch Up | 11.45% | 15.08% |
| All five, raw published references (1,234 words) | 23.82% | 15.72% |
| Four references without explicit gaps (960 words) | 13.85% | 13.02% |

On the four references without explicit gaps, errors fell only from 133 to 125:
substitutions 56 to 41, omissions **51 to 55**, and extra words **26 to 29**.
On all five raw references, errors fell from 294 to 194, but omissions still rose
from 69 to 71. The last song regressed from 41 to 54 errors, including an increase
from 14 to 28 omissions. Do not summarize this study as a reliable recall gain.

The complementary older, trusted annotation intervals tell a different story:
118 intervals covering 713 reference words improved from 24.68% to 23.28% error,
with omissions falling from 64 to 50 but additions rising from 55 to 69. Joining
those intervals per song improves 16.46% to 13.78%; this subset is not a substitute
for complete-song scoring. In its 25 instrumental intervals, false positives fell
from four passages/seven words to two passages/three words.

Actual first-line intervals from the four complete MUSDB-ALT references produced
seven versus ten errors across only 26 words (four omissions in both variants).
Timing can contribute to these errors. The first *trusted old annotation* is often
later in a song; those diagnostic rows are retained separately and must not be
described as the opening line.

For the local regression song, the current pipeline keeps a line covering the
uncertain opening at 9.3 seconds; the candidate skips that text and begins with the
following lyric. Both retain the user-confirmed "easy to fool" wording, with four
and five occurrences respectively, and neither contains "meet you in a pool".
The repetition count and complete local transcript have no verified reference;
neither a higher line count nor a new opening interpretation is proof of accuracy.

Total measured pipeline time for the five public songs fell from 624.35 seconds
to 390.28 seconds (10.4 to 6.5 minutes). Candidate separation peaked at 2,207.9 MiB
of allocated CUDA tensors, excluding desktop/driver overhead. These timings include
model loading and process startup on the local RTX 5060 and are not general hardware
benchmarks.

All 38 runtime checks and 23 research checks passed. A separate output audit verified
all 12 stereo stems retained duration and finite samples, all 12 transcripts retained
valid times, the frozen model/data/helper hashes matched, and the installed Release
helpers remained unchanged. The 20-second compatibility smoke test also passed.

## Fixed experiment

Compare the installed HTDemucs-ft pipeline (two shifts, 50% overlap, seven-second
segments) with Kimberley Jensen's published Mel-Band RoFormer vocal checkpoint.
Both feed the same bundled Whisper large-v3 model, English language, seed zero,
original-mixture comparison, and deployed pause-aware retry logic. No lyric hints,
fine-tuned transcriber, second-opinion model, or reference-based edits are used.

The candidate uses the publisher's eight-second chunks and four overlaps, selected
before looking at lyric scores. The checkpoint and source revisions are pinned and
hashed. The checkpoint is loaded with `weights_only=True`; research dependencies
and downloaded assets stay outside the application bundle. Each GPU stage runs in
a separate process. Input duration and finite output samples are checked.

Selection seed 20260928 chooses five artists absent from our fine-tuning data and
all three earlier five-song evaluation selections, requiring complete MUSDB-ALT
references. The selection is saved before model evaluation:

- Zeno - Signs
- Raft Monk - Tiring
- Cristina Vane - So Easy
- The Sunshine Garcia Band - For I Am The Moon
- Bobby Nobody - Stitch Up

These songs are fresh to our experiments. The separator publisher does not give
an exhaustive training-song list, so independence from its pretraining cannot be
established. Five songs are a screening comparison, not a general accuracy claim.

## Decision criteria

Judge complete-song lyric errors using MUSDB-ALT and `alt-eval==1.2.0`, plus trusted
timed intervals from the older annotations. Report substitutions, omissions,
incorrect additions, instrumental false positives, first trusted lyric intervals,
and results per song. Whole-song and timed scoring must be kept separate because
timing drift can increase interval errors without changing the words.

A useful candidate should show a consistent reduction in lyric errors and omitted
words, avoid increasing invented instrumental words, and retain the confirmed
"easy to fool" chorus in the local regression song. Its opening remains uncertain;
an altered reading cannot be labelled correct without a reliable reference.
Cleaner-sounding audio alone is not a reason to change the app. Review processing
time and memory cost alongside accuracy before deciding on further integration.

## Reproduction

Use the existing isolated research Python environment with
`requirements-separation.txt`. Run `prepare_separator.py` with an artifact directory
to fetch the pinned public files. Run `prepare_acceptance.py music` with seed
20260928, the original training manifest, the combined excluded selections, and
the pinned MUSDB-ALT full references. `evaluate_separators.py` takes `--assets`,
`--bundle`, `--data`, `--user-song`, and `--output`. It verifies source helpers match
the installed bundle and refuses resume after a change to its recorded settings.

Score using `score_acceptance.py --variants demucs mel --skip-speech` and
`score_full_lyrics.py --variants demucs mel`, then `score_separator_details.py`
with `--runs`, `--base-model`, `--data`, and `--references`. The last tool audits
reference gaps and uses the actual first complete-reference line for opening scores.
Assets and evidence are under ignored `artifacts/lyrics-training/`:

- `separator-melband/`: pinned source, checkpoint, checksums.
- `acceptance-data/separator-fresh/`: deterministic selection and public test audio.
- `evaluations/separator-smoke/`: 20-second GPU compatibility and length check.
- `evaluations/separator-fresh/`: paired audio, transcripts, timings and scores.

Sources: [publisher's implementation](https://github.com/KimberleyJensen/Mel-Band-Roformer-Vocal-Model),
[checkpoint](https://huggingface.co/KimberleyJSN/melbandroformer),
[MUSDB18-HQ](https://zenodo.org/records/3338373),
[MUSDB-ALT](https://huggingface.co/datasets/jazasyed/musdb-alt).
Public evaluation audio and reference lyrics remain local research data.
