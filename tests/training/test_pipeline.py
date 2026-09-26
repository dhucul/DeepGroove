import importlib.util
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2] / "training/lyrics"


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / (name + ".py"))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


prepare = module("prepare_musdb")
train = module("train_adapter")
sys.modules["train_adapter"] = train
sys.modules["prepare_musdb"] = prepare
evaluate = module("evaluate_adapter")
acceptance = module("prepare_acceptance")
scoring = module("score_acceptance")


class PipelineTests(unittest.TestCase):
    def test_fresh_song_scoring_drops_overlapping_reference_intervals(self):
        rows = [dict(start=0, end=4, annotation_line=1), dict(start=3, end=7, annotation_line=2),
                dict(start=8, end=12, annotation_line=3)]
        self.assertEqual(acceptance.trusted_nonoverlapping(rows, [(1, 0, 4), (2, 3, 7), (3, 8, 12)]), [rows[2]])

    def test_full_song_word_scoring_uses_half_open_intervals_and_deduplicates(self):
        line = {"text": "alpha beta", "words": [dict(start=.4, end=.6, word=" alpha"),
                                                  dict(start=.9, end=1.1, word=" beta")]}
        self.assertEqual(scoring.words_in_interval([line, line], 0, 1), "alpha")
        self.assertEqual(scoring.words_in_interval([line, line], 1, 2), "beta")
        with self.assertRaisesRegex(ValueError, "no word timing"):
            scoring.words_in_interval([dict(text="untimed words", words=[])], 0, 1)

    def test_joined_song_score_separates_boundary_timing_from_word_accuracy(self):
        rows = [dict(song="Song", start=0, text="alpha", prediction="alpha beta"),
                dict(song="Song", start=1, text="beta", prediction="")]
        self.assertGreater(scoring.score(rows, str.strip)["wer"], 0)
        self.assertEqual(scoring.score(scoring.joined_song_rows(rows), str.strip)["wer"], 0)

    def test_cached_baseline_rejects_different_references_or_model(self):
        row = dict(song="Song", source="mixture", start=0, end=4, text="reference")
        prediction = {**row, "prediction": "words", "truncated": False}
        prior = dict(manifest_sha256="hash", base_model="model", base_revision="revision", generation_cap=128)
        evaluate.validate_reused_base([row], [prediction], prior, "hash", prior, 128)
        with self.assertRaisesRegex(ValueError, "reference labels"):
            evaluate.validate_reused_base([{**row, "text": "changed"}], [prediction], prior, "hash", prior, 128)
        with self.assertRaisesRegex(ValueError, "same data"):
            evaluate.validate_reused_base([row], [prediction], prior, "hash", {**prior, "base_revision": "changed"}, 128)

    def test_uncertain_and_incompatible_annotations_cannot_enter_training(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "annotations.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("Artist - Song.txt", "\n".join([
                    "00:00 00:04 a clear first phrase", "00:04 00:08 b same words together",
                    "* 00:08 00:12 c uncertain overlapping words", "00:12 00:16 c different simultaneous words",
                    "00:16 00:20 a unknown?", "00:20 00:24 d", "00:24 01:10 a too long",
                ]))
            songs, counts = prepare.parse_annotations(path)
            self.assertEqual([row["kind"] for row in songs["Artist - Song"]], ["a", "b", "d"])
            self.assertEqual(songs["Artist - Song"][-1]["text"], "")
            self.assertEqual(counts["uncertain_excluded"], 2)

    def test_artist_split_keeps_all_recordings_together(self):
        songs = {f"Artist {i} - Song {j}": [] for i in range(20) for j in range(2)}
        splits = prepare.split_artists(songs)
        self.assertIn("validation", splits.values())
        for i in range(20):
            self.assertEqual(splits[f"Artist {i} - Song 0"], splits[f"Artist {i} - Song 1"])

    def test_audio_download_cannot_use_test_tracks_or_escape_its_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            for name in ("test/Song/vocals.wav", "train/../vocals.wav", "train/Song/drums.wav", "../../outside.wav"):
                with self.subTest(name=name), self.assertRaises(ValueError):
                    prepare.safe_destination(Path(directory), name)

    def test_manifest_rejects_artist_leakage_and_uncertain_labels(self):
        base = dict(song="Song one", artist="Singer", split="train", start=0, end=4, kind="a", text="clear words")
        other = {**base, "song": "Song two", "artist": "Singer feat. Guest", "split": "validation"}
        with self.assertRaisesRegex(ValueError, "artist leakage"):
            train.validate_manifest([base, other])
        other["artist"] = "Another singer"
        train.validate_manifest([base, other])
        other["text"] = "uncertain?"
        with self.assertRaisesRegex(ValueError, "Uncertain"):
            train.validate_manifest([base, other])

    def test_status_writes_are_valid_and_leave_no_temporary_file(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "status.json"
            train.atomic_json(target, {"phase": "training", "step": 10})
            self.assertEqual(json.loads(target.read_text())["step"], 10)
            self.assertFalse(target.with_suffix(".tmp").exists())

    def test_rounded_end_timestamp_is_clamped_but_bad_intervals_are_rejected(self):
        self.assertEqual(train.audio_interval(20, 26, 1000, 25949), (20000, 25949))
        self.assertEqual(train.audio_interval(0, 4, 1000, 5000), (0, 4000))
        for start, end in ((20, 26.2), (-1, 4), (27, 28), (20, 19)):
            with self.subTest(start=start, end=end), self.assertRaises(ValueError):
                train.audio_interval(start, end, 1000, 25949)

    def test_evaluation_uses_only_reserved_rows_and_marks_paired_selection_clips(self):
        training = dict(song="Training song", artist="Training singer", split="train",
                        start=0, end=4, kind="a", text="words", source="mixture")
        reserved = {**training, "song": "Reserved song", "artist": "Reserved singer", "split": "validation"}
        rows = [training, reserved, {**reserved, "source": "vocals"}, {**reserved, "start": 4, "end": 8}]
        selected = evaluate.select_validation(rows, [reserved])
        self.assertEqual(len(selected), 3)
        self.assertEqual(sum(r["used_for_selection"] for r in selected), 2)
        self.assertEqual(sum(r["first_annotated_phrase"] for r in selected), 2)
        self.assertTrue(all(r["split"] == "validation" for r in selected))

    def test_evaluation_counts_missing_words_and_instrumental_hallucinations(self):
        rows = [dict(song="One", text="one two", prediction="one", truncated=False),
                dict(song="Two", text="", prediction="invented words", truncated=False)]
        result = evaluate.metrics(rows, str.strip)
        self.assertEqual(result["reference_words"], 2)
        self.assertEqual(result["deletions"], 1)
        self.assertEqual(result["insertions"], 2)
        self.assertEqual(result["negative_false_positives"], 1)
        self.assertEqual(result["words_on_negatives"], 2)
        self.assertEqual(result["wer"], 1.5)
        self.assertIsNone(evaluate.metrics(rows[1:], str.strip)["wer"])

    def test_cap_recheck_includes_both_models_failures_without_other_excerpts(self):
        row = dict(song="Song", source="mixture", start=0, end=4, truncated=True)
        vocal = {**row, "source": "vocals"}
        unrelated = {**row, "start": 8, "end": 12, "truncated": False}
        ids = evaluate.capped_excerpt_ids({"base": [row, unrelated], "adapted": [vocal, unrelated]})
        self.assertEqual(ids, {evaluate.excerpt_id(row), evaluate.excerpt_id(vocal)})

    def test_instrumentals_train_no_speech_at_the_start_without_language_prefix(self):
        class Tokenizer:
            unk_token_id = eos_token_id = 50257
            def convert_tokens_to_ids(self, token):
                return {"<|nospeech|>": 50363, "<|startoftranscript|>": 50258}[token]
            def __call__(self, text):
                return SimpleNamespace(input_ids=[50258, 50259, 50360, 50364] + ([100] if text else []) + [50257])
        tokenizer = Tokenizer()
        labels, decoder = train.training_targets(tokenizer, {"kind": "d", "text": ""})
        self.assertEqual(labels, [50363, -100, -100, 50257])
        self.assertEqual(decoder, [50258, 50259, 50360, 50364])
        self.assertEqual(train.training_targets(tokenizer, {"kind": "a", "text": "word"}),
                         ([50259, 50360, 50364, 100, 50257], [50258, 50259, 50360, 50364, 100]))

    def test_instrumental_annotation_cannot_silently_discard_real_text(self):
        training = dict(song="Training", artist="Singer", split="train", start=0, end=4, kind="a", text="words")
        invalid = {**training, "song": "Reserved", "artist": "Another singer", "split": "validation", "kind": "d"}
        with self.assertRaisesRegex(ValueError, "instrumental labels"):
            train.validate_manifest([training, invalid])


if __name__ == "__main__":
    unittest.main()
