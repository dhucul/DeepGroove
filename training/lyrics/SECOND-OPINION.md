# Singing-model second-opinion experiment

This is a suggestion-only research prototype. It does not replace or rewrite the
current pause-aware transcript. **Decision: do not enable it in Release.** The
guards leave too few useful suggestions and did not make development suggestions
reliably helpful. No second-opinion model or interface was installed. The already
validated pause-aware retry improvement remains active in the single Release app.

## Method

The corrected singing adapter's fixed step-200 CTranslate2 export proposes readings
from isolated vocals. Only English is supported. Checks target uncertain/recovered
lines and gaps, with at most 48 windows and at most 20 seconds per input. Long lines
use their least-confident timed-word group; unchanged prefixes and suffixes are kept.
Text/word mismatches are skipped so edited or unmatched content cannot be discarded.

Proposals require acoustic confidence, plausible overlap with the current wording,
and valid timings. Cosmetic spelling/spacing changes, shortened copies, single-word
gap fragments, and duplicate line endings are filtered. Confidence is not treated
as a calibrated probability of correctness.

The final guard requires the original recognizer to produce the same reading from
the original mix, both with short context and with up to 28 seconds of surrounding
audio. Both recognizers share a pretrained foundation, so agreement is not proof.
The main transcript and its existing alternate readings remain unchanged.
Possible missing lines have blank primary text in the review artifact until a user
chooses a reading. They cannot silently appear in a plain-text export.

## Development findings

The previously evaluated five-song set was used only for development. Every proposal
was scored by replacing/inserting that one suggestion in the complete transcript
and comparing it with the revised MUSDB-ALT reference using `alt-eval==1.2.0`.
Each suggestion is classified as helpful, harmful, or neutral by its word-error
change. This avoids claiming an improvement based on a reviewer always choosing
only the correct alternatives.

| Development version | Helpful | Harmful | Neutral |
|---|---:|---:|---:|
| Initial model-confidence filtering | 12 | 27 | 1 |
| Cosmetic/fragment filters plus original-mix agreement | 2 | 3 | 0 |
| Also require longer-context agreement | 0 | 2 | 0 |

The initial version included clipped words, redundant endings, and spacing-only
differences. Cross-checking reduced the number of suggestions, but did not make the
remaining ones reliable on these songs. The local regression song produced no
supported new suggestion for its uncertain opening; its main transcript was intact.

## Frozen unused-song check

The final rule set is fixed before the following five songs are evaluated. Seed
20260927 selects previously unused artists, excluding the training/development
artists and both earlier five-song test selections. Full-song reference availability
is required before selection; no choice depends on model performance.

- Speak Softly — Broken Man
- Secretariat — Over The Top
- Georgia Wonder — Siren
- BKS — Bulldozer
- Punkdisco — Oral Hygiene

No reference text is supplied to either recognizer. Suggestions are scored individually,
and the hypothetical result of accepting every suggestion is recorded separately.
No main-transcript accuracy gain is claimed: the feature leaves it unchanged and
requires human review. A successful execution alone does not qualify it for release.

## Unused-song result and decision

All five complete-song runs finished with an unchanged main transcript. The final
guard allowed one suggestion on Speak Softly — Broken Man; it improved the reference
score by one word error. It allowed none on the other four songs. Thus the unused
test produced **one helpful, zero harmful, and zero neutral suggestions**. Accepting
that one suggestion would change the total error count from 260 to 259; it was not
automatically accepted or inserted into the main transcript.

The same final rule set produced zero helpful and two harmful suggestions on the
development set. Across the ten scored songs, that is only one helpful versus two
harmful suggestions. This is sparse, inconsistent evidence and does not justify
adding the trained model, extra processing, or a new review interface to the app.
The local regression song's uncertain opening received no supported new reading.

The helper and evaluation tools are retained under `training/lyrics/`, outside the
application's packaged Python files. This makes the result reproducible without
quietly enabling an unsuccessful experiment. No additional app copy was made.

## Tests and evidence

Eight new checks cover unchanged primary text/word timings/existing alternatives,
blank pending phrases, bounded contexts, safe partial-line replacement, language
and silence guards, cosmetic/clipped duplicates, and required recognizer agreement.
After separating this prototype from the application, the runtime transcription
suite passed 38 checks and the research suite passed 23 checks.

Local evidence, excluded from Git, is under `artifacts/lyrics-training/`:

- `evaluations/opinion-development/`: initial proposals and reference-based scoring.
- `evaluations/opinion-consensus-development/`: first cross-check.
- `evaluations/opinion-context-development/`: final development rule set.
- `evaluations/opinion-fresh/`: frozen unused-song evaluation.
- `acceptance-data/opinion-fresh/`: selection and public test audio.

The original and corrected trained-model evaluations remain documented in
[RESULTS.md](RESULTS.md), and the deployed pause-aware retry improvement in
[PHRASE-BOUNDARIES.md](PHRASE-BOUNDARIES.md).
Reference sources are [MUSDB18-HQ](https://zenodo.org/records/3338373) and
[MUSDB-ALT](https://huggingface.co/datasets/jazasyed/musdb-alt). Dataset terms remain
noncommercial research; no audio, reference lyrics, or trained weights are published.
