import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
import numpy as np

source = Path(__file__).resolve().parents[2] / "src/WaveLab/Transcription/lyrics_recovery.py"
spec = importlib.util.spec_from_file_location("lyrics_recovery", source)
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)


def line(start, end, text, probability=.9, logprob=-.1):
    tokens = text.split()
    words = [SimpleNamespace(start=start + (end-start)*i/len(tokens),
                             end=start + (end-start)*(i+1)/len(tokens), word=" " + word,
                             probability=probability) for i, word in enumerate(tokens)]
    return recovery.line_from_segment(SimpleNamespace(start=start, end=end, text=text, words=words,
        avg_logprob=logprob, no_speech_prob=.01, compression_ratio=1.1), 200)


class RecoveryTests(unittest.TestCase):
    def test_a_whole_missing_phrase_is_inserted_and_marked_for_review(self):
        primary = [line(0, 2, "first phrase"), line(8, 10, "last phrase")]
        result = recovery.merge_passes(primary, [line(4, 6, "previously missed words")], "the original mix")
        self.assertEqual([item["start"] for item in result], [0, 4, 8])
        self.assertTrue(result[1]["recovered"])
        self.assertTrue(result[1]["needs_review"])
        self.assertIn("original mix", result[1]["recovery_note"])
        self.assertEqual(len(primary), 2)

    def test_added_words_keep_the_previous_reading_as_an_alternative(self):
        result = recovery.merge_passes([line(2, 5, "you catch me")], [line(2, 5, "you cannot catch me")], "the original mix")
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["text"], "you cannot catch me")
        self.assertEqual(result[0]["alternative_text"], "you catch me")
        self.assertTrue(result[0]["recovered"])

    def test_a_conflicting_reading_does_not_overwrite_the_primary_text(self):
        result = recovery.merge_passes([line(2, 5, "you catch me")], [line(2, 5, "you cut me")], "the original mix")
        self.assertEqual(result[0]["text"], "you catch me")
        self.assertEqual(result[0]["alternative_text"], "you cut me")
        self.assertTrue(result[0]["needs_review"])

    def test_later_repeated_choruses_are_retained_but_same_time_duplicates_are_not(self):
        result = recovery.merge_passes([line(2, 5, "sing it again")],
            [line(2, 5, "sing it again"), line(20, 23, "sing it again")], "the original mix")
        self.assertEqual(len(result), 2)
        self.assertEqual(result[1]["start"], 20)

    def test_unsupported_retry_text_is_not_used_to_fill_a_gap(self):
        result = recovery.merge_passes([], [line(5, 8, "unreliable candidate", .2, -2)], "a focused replay")
        self.assertEqual(result, [])

    def test_one_confident_function_word_cannot_validate_an_uncertain_phrase(self):
        candidate = line(4, 5, "thank you")
        candidate["words"][0]["probability"] = .05
        candidate["words"][1]["probability"] = .99
        self.assertEqual(recovery.merge_passes([], [candidate], "a focused replay"), [])

    def test_a_weak_extra_negation_is_only_offered_as_an_alternative(self):
        candidate = line(2, 5, "you cannot catch me")
        candidate["words"][1]["probability"] = .1
        result = recovery.merge_passes([line(2, 5, "you catch me")], [candidate], "the original mix")
        self.assertEqual(result[0]["text"], "you catch me")
        self.assertEqual(result[0]["alternative_text"], candidate["text"])

    def test_word_timing_drift_does_not_duplicate_the_end_of_a_line(self):
        result = recovery.merge_passes([line(0, 3, "sing it again")], [line(2.9, 3.5, "it again")], "the original mix")
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["text"], "sing it again")

    def test_a_missing_suffix_is_kept_when_the_candidate_also_contains_the_line_end(self):
        result = recovery.merge_passes([line(0, 3, "but I am easy")], [line(2.9, 4.5, "easy to hear")], "the original mix")
        self.assertEqual(len(result), 2)
        self.assertEqual(result[1]["text"], "to hear")

    def test_retry_windows_cover_an_empty_transcript_without_cutting_boundaries(self):
        windows = recovery.recovery_windows([], 45)
        self.assertEqual(windows[0][0], 0)
        self.assertEqual(windows[-1][1], 45)
        self.assertTrue(all(end-start <= 14 for start, end in windows))
        self.assertTrue(all(left[1] - right[0] >= 2 for left, right in zip(windows, windows[1:])))

    def test_sung_words_are_not_discarded_by_speech_skip_rules(self):
        class Model:
            def transcribe(self, audio, **options):
                # Mirrors the upstream no-speech skip that discarded this whole window.
                skipped = options["no_speech_threshold"] is not None
                segment = SimpleNamespace(start=1, end=5, text="a held syllable",
                    words=[SimpleNamespace(start=1, end=5, word=" a held syllable", probability=.6)],
                    avg_logprob=-1.2, no_speech_prob=.8, compression_ratio=1.1)
                return iter([] if skipped else [segment]), SimpleNamespace(language="en")
        lines, _ = recovery.transcribe(Model(), np.full(6*16000, .02, dtype=np.float32),
            np.full(6*16000, .02, dtype=np.float32), "en", False, False, False, "", lambda *_: None)
        self.assertEqual(lines[0]["text"], "a held syllable")
        self.assertTrue(lines[0]["needs_review"])
        self.assertIsNone(recovery.music_options()["hallucination_silence_threshold"])
        self.assertEqual(recovery.music_options(speech=True)["hallucination_silence_threshold"], 2)

    def test_the_original_mix_is_checked_even_when_the_first_pass_returns_no_lines(self):
        class Model:
            calls = []
            def transcribe(self, audio, **options):
                self.calls.append(len(audio))
                candidate = line(1, 3, "previously missing phrase")
                segment = SimpleNamespace(start=1, end=3, text=candidate["text"],
                    words=[SimpleNamespace(**word) for word in candidate["words"]],
                    avg_logprob=-.1, no_speech_prob=.01, compression_ratio=1.1)
                return iter([segment] if len(self.calls) == 2 else []), SimpleNamespace(language="en")
        model = Model()
        samples = np.full(4*16000, .02, dtype=np.float32)
        lines, _ = recovery.transcribe(model, samples, samples, "en", False, True, True, "", lambda *_: None)
        self.assertEqual(model.calls[:2], [len(samples), len(samples)])
        self.assertEqual(lines[0]["text"], "previously missing phrase")
        self.assertTrue(lines[0]["recovered"])

    def test_literal_sung_music_is_not_confused_with_a_stage_annotation(self):
        self.assertIsNotNone(line(0, 2, "music"))
        self.assertIsNone(line(0, 2, "[Music]"))

    def test_quiet_recognition_copy_is_boosted_without_changing_source(self):
        source = np.array([.001, -.002, 0], dtype=np.float32)
        result = recovery.prepare_audio(source)
        self.assertGreater(float(abs(result).max()), float(abs(source).max()))
        np.testing.assert_array_equal(source, np.array([.001, -.002, 0], dtype=np.float32))
        np.testing.assert_array_equal(recovery.prepare_audio(np.zeros(20, dtype=np.float32)), 0)


if __name__ == "__main__":
    unittest.main()
