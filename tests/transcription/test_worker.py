"""Protocol tests; no downloaded models or third-party dependencies required."""
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import json
import tempfile
import unittest
from unittest.mock import patch

worker_path = Path(__file__).resolve().parents[2] / "src/WaveLab/Transcription/lyrics_worker.py"
spec = importlib.util.spec_from_file_location("lyrics_worker", worker_path)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


def segment(**changes):
    values = dict(start=1, end=3, text=" Sing together ", avg_logprob=-0.1,
                  no_speech_prob=0.01, compression_ratio=1.2,
                  words=[SimpleNamespace(start=1, end=3, word="Sing together", probability=.9)])
    return SimpleNamespace(**(values | changes))


class WorkerTests(unittest.TestCase):
    def test_vocals_only_always_enables_separation_without_requiring_transcription_options(self):
        with patch.object(worker.sys, "argv", ["lyrics_worker", "--vocals-only", "--input", "song.wav", "--output", "result.json"]):
            with patch.object(worker, "run") as run:
                worker.main()
        args = run.call_args.args[0]
        self.assertTrue(args.vocals_only)
        self.assertTrue(args.isolate)
        self.assertFalse(args.speech)

    def test_uncertain_word_is_flagged_without_rewriting_it(self):
        raw = segment(words=[SimpleNamespace(start=1, end=3, word="together", probability=.2)])
        line = worker.line_from_segment(raw, 5)
        self.assertTrue(line["needs_review"])
        self.assertEqual(line["text"], "Sing together")

    def test_good_repeated_chorus_is_not_deduplicated(self):
        lines = [worker.line_from_segment(segment(start=i, end=i+2), 10) for i in (1, 5)]
        self.assertEqual(len(lines), 2)
        self.assertFalse(lines[0]["needs_review"])

    def test_high_no_speech_is_flagged(self):
        self.assertTrue(worker.line_from_segment(segment(no_speech_prob=.8), 10)["needs_review"])

    def test_end_and_words_are_clamped_to_input_duration(self):
        line = worker.line_from_segment(segment(end=7), 2)
        self.assertEqual(line["end"], 2)
        self.assertEqual(line["words"][0]["end"], 2)

    def test_empty_or_out_of_range_segments_are_omitted(self):
        self.assertIsNone(worker.line_from_segment(segment(text="  "), 10))
        self.assertIsNone(worker.line_from_segment(segment(start=20, end=22), 10))

    def test_result_is_utf8_atomic_and_nan_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "result.json"
            worker.save_result(target, {"text": "Écouter 歌"})
            self.assertEqual(json.loads(target.read_text(encoding="utf-8"))["text"], "Écouter 歌")
            self.assertFalse(target.with_suffix(".partial").exists())
            with self.assertRaises(ValueError):
                worker.save_result(target, {"start": float("nan")})
            self.assertEqual(json.loads(target.read_text(encoding="utf-8"))["text"], "Écouter 歌")


if __name__ == "__main__":
    unittest.main()
