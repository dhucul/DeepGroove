import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
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
    def test_phrase_windows_cover_quiet_openings_and_the_complete_recording(self):
        samples = np.full(round(132.5 * 16000), .1, dtype=np.float32)
        samples[:9 * 16000] = .00001
        samples[22 * 16000:24 * 16000] = 0
        windows = recovery.phrase_windows(samples)
        self.assertEqual(windows[0]["core_start"], 0)
        self.assertEqual(windows[-1]["core_end"], 132.5)
        self.assertTrue(22 < windows[0]["core_end"] < 24)
        for left, right in zip(windows, windows[1:]):
            self.assertAlmostEqual(left["core_end"], right["core_start"])
        for item in windows:
            self.assertTrue(0 <= item["start"] <= item["core_start"] < item["core_end"] <= item["end"] <= 132.5)
            self.assertLessEqual(round(item["end"] * 16000) - round(item["start"] * 16000), 30 * 16000)
            self.assertGreaterEqual(item["end"] - item["start"], 20 - 1e-8)

    def test_phrase_splits_use_pauses_rather_than_short_syllable_gaps(self):
        samples = np.full(80 * 16000, .1, dtype=np.float32)
        samples[20 * 16000:round(20.2 * 16000)] = 0
        samples[23 * 16000:24 * 16000] = 0
        boundary = recovery.phrase_windows(samples)[0]["core_end"]
        self.assertTrue(23 < boundary < 24)

    def test_phrase_boundaries_do_not_change_when_the_whole_song_is_quieter(self):
        samples = np.full(70 * 16000, .1, dtype=np.float32)
        samples[24 * 16000:25 * 16000] = 0
        self.assertEqual(recovery.phrase_windows(samples), recovery.phrase_windows(samples * .001))

    def test_phrase_windows_handle_silence_continuous_voice_and_short_inputs(self):
        for duration in (0, .02, 8, 29, 30, 30.02, 35, 60, 97):
            for value in (0, .05):
                with self.subTest(duration=duration, value=value):
                    windows = recovery.phrase_windows(np.full(round(duration * 16000), value, dtype=np.float32))
                    if not duration:
                        self.assertEqual(windows, [])
                    else:
                        self.assertEqual(windows[0]["core_start"], 0)
                        self.assertAlmostEqual(windows[-1]["core_end"], duration)
                        self.assertTrue(all(w["end"] - w["start"] <= 30 + 1e-8 for w in windows))
                        self.assertTrue(all(a["core_end"] == b["core_start"] for a, b in zip(windows, windows[1:])))
        with self.assertRaises(ValueError):
            recovery.phrase_windows(np.array([np.nan], dtype=np.float32))

    def test_phrase_overlap_owns_words_once_and_preserves_true_repetition(self):
        window = dict(start=24, end=52, core_start=26, core_end=50)
        previous = [line(25.6, 26.2, "again")]
        duplicate = recovery.phrase_line(line(25.8, 27.8, "again now"), window, previous)
        self.assertEqual(duplicate["text"], "now")
        repeated = recovery.phrase_line(line(26.3, 26.9, "again"), window, previous)
        self.assertEqual(repeated["text"], "again")
        centered = recovery.phrase_line(line(22, 28, "left middle right"),
            dict(start=22, end=28, core_start=24, core_end=26), [])
        self.assertEqual(centered["text"], "middle")
        self.assertEqual((centered["start"], centered["end"]), (24, 26))

    def test_phrase_transcription_offsets_are_global(self):
        class Model:
            def transcribe(self, audio, **options):
                segment = SimpleNamespace(start=2, end=3, text="word",
                    words=[SimpleNamespace(start=2, end=3, word=" word", probability=.9)],
                    avg_logprob=-.1, no_speech_prob=.01, compression_ratio=1)
                return iter([segment]), SimpleNamespace(language="en")
        windows = [dict(start=0, end=22, core_start=0, core_end=20),
                   dict(start=18, end=40, core_start=20, core_end=40)]
        result = recovery.transcribe_phrases(Model(), np.full(40 * 16000, .1, dtype=np.float32),
            windows, "en", recovery.music_options(), lambda *_: None, .5, .73)
        self.assertEqual([(r["start"], r["end"]) for r in result], [(2, 3), (20, 21)])

    def test_phrase_strategy_does_not_change_speech_or_unseparated_music(self):
        class Model:
            def transcribe(self, audio, **options):
                return iter([]), SimpleNamespace(language="en")
        audio = np.full(40 * 16000, .1, dtype=np.float32)
        with patch.object(recovery, "phrase_windows", side_effect=AssertionError("not applicable")):
            for speech, isolated in ((True, True), (True, False), (False, False)):
                recovery.transcribe(Model(), audio, audio, "en", speech, False, isolated, "", lambda *_: None,
                                    phrase_mode="primary")

    def test_pause_retries_keep_both_main_passes_and_use_longer_context(self):
        class Model:
            def __init__(self):
                self.lengths = []
            def transcribe(self, audio, **options):
                self.lengths.append(len(audio))
                main = SimpleNamespace(start=10, end=12, text="known words",
                    words=[SimpleNamespace(start=10, end=11, word=" known", probability=.9),
                           SimpleNamespace(start=11, end=12, word=" words", probability=.9)],
                    avg_logprob=-.1, no_speech_prob=.01, compression_ratio=1)
                return iter([main] if len(self.lengths) <= 2 else []), SimpleNamespace(language="en")
        model = Model()
        samples = np.full(70 * 16000, .02, dtype=np.float32)
        result, language = recovery.transcribe(model, samples, samples, "en", False, True, True, "", lambda *_: None,
                                               phrase_mode="retry")
        self.assertEqual(model.lengths[:2], [len(samples), len(samples)])
        self.assertEqual([r["text"] for r in result], ["known words"])
        self.assertEqual(language, "en")
        self.assertTrue(all(length <= 30 * 16000 for length in model.lengths[2:]))
        self.assertTrue(any(length > 14 * 16000 for length in model.lengths[2:]))

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

    def test_a_confident_but_conflicting_pool_reading_cannot_replace_the_correct_chorus(self):
        correct = "You can't catch me, but I'm easy to fool"
        incorrect = "You can't catch me, but I'll meet you in a pool"
        primary = [line(16, 22, correct)]
        retry = line(16, 22, incorrect, probability=.99, logprob=-.01)
        result = recovery.merge_passes(primary, [retry], "a focused replay")
        self.assertEqual(result[0]["text"], correct)
        self.assertEqual(result[0]["alternative_text"], incorrect)
        self.assertFalse(result[0]["recovered"])

    def test_an_opening_retry_includes_the_missing_line_and_following_context(self):
        # If the opening at 9.3 seconds was missed, the first recognized phrase
        # begins at 12.94. The retry must include both the opening and the chorus.
        windows = recovery.recovery_windows([line(12.94, 15.72, "the next phrase")], 132.5)
        self.assertTrue(any(start < 9.3 and end > 22 for start, end in windows))
        self.assertTrue(all(0 <= start < end <= 132.5 and end-start <= 28 for start, end in windows))

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
