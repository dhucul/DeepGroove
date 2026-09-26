import copy
import importlib.util
from pathlib import Path
from types import SimpleNamespace
import unittest
import numpy as np

ROOT = Path(__file__).resolve().parents[2]


def module(name):
    folder = "training/lyrics" if name == "lyrics_opinions" else "src/WaveLab/Transcription"
    spec = importlib.util.spec_from_file_location(name, ROOT / folder / (name + ".py"))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


opinions = module("lyrics_opinions")
recovery = module("lyrics_recovery")


def make_line(start=2, end=5, text="a different phrase"):
    words = text.split()
    return dict(start=start, end=end, text=text, model_text=text, needs_review=True, recovered=False,
        alternative_text="a previous phrase", recovery_note="", words=[dict(start=start+(end-start)*i/len(words),
        end=start+(end-start)*(i+1)/len(words), word=" "+word, probability=.4) for i, word in enumerate(words)])


class OpinionTests(unittest.TestCase):
    def test_suggestion_does_not_rewrite_primary_or_existing_alternative(self):
        class Model:
            def transcribe(self, audio, **options):
                words = [SimpleNamespace(start=2+i, end=3+i, word=" "+word, probability=.95)
                         for i, word in enumerate(("a", "better", "phrase"))]
                return iter([SimpleNamespace(start=2, end=5, text="a better phrase", words=words,
                    avg_logprob=-.1, no_speech_prob=.01, compression_ratio=1)]), SimpleNamespace(language="en")
        lines = [make_line()]
        before = copy.deepcopy(lines)
        audio = np.zeros(12*16000, dtype=np.float32)
        audio[2*16000:5*16000] = .1
        proposals = opinions.suggest(Model(), audio, lines, "en", recovery)
        self.assertEqual(len(proposals), 1)
        result = opinions.attach_suggestions(lines, proposals)
        self.assertEqual(lines, before)
        self.assertEqual(result[0]["text"], before[0]["text"])
        self.assertEqual(result[0]["alternative_text"], "a previous phrase")
        self.assertEqual(result[0]["second_opinion_text"], "a better phrase")
        self.assertEqual(result[0]["words"], before[0]["words"])

    def test_missing_phrase_remains_blank_until_a_user_accepts_it(self):
        lines = [make_line()]
        words = [dict(start=7, end=8, word=" missing", probability=.95),
                 dict(start=8, end=9, word=" words", probability=.95)]
        result = opinions.attach_suggestions(lines, [dict(kind="gap", start=7, end=9, text="missing words", words=words)])
        self.assertEqual(result[1]["text"], "")
        self.assertTrue(result[1]["possible_missing"])
        self.assertEqual(result[1]["second_opinion_text"], "missing words")
        self.assertEqual(" ".join(r["text"] for r in result if r["text"]), lines[0]["text"])

    def test_targets_are_bounded_and_do_not_assume_timed_words_match_edited_text(self):
        line = make_line(5, 65, " ".join("word"+str(i) for i in range(30)))
        targets = opinions.plan_targets([line], 100)
        self.assertTrue(all(0 <= r["view_start"] < r["view_end"] <= 100 for r in targets))
        self.assertTrue(all(r["view_end"]-r["view_start"] <= 20 for r in targets))
        self.assertEqual(len([r for r in targets if r["kind"] == "line"]), 1)
        line["text"] = "manually corrected content"
        self.assertFalse(any(r["kind"] == "line" for r in opinions.plan_targets([line], 100)))

    def test_partial_opinion_retains_the_unchecked_prefix_and_suffix(self):
        line = make_line(0, 8, "keep these doubtful words and this ending")
        target = dict(first_word=2, last_word=4)
        text = opinions.proposed_text(line, target, [dict(word=" clearer"), dict(word=" words")])
        self.assertEqual(text, "keep these clearer words and this ending")

    def test_language_mismatch_and_silence_do_not_invoke_the_singing_model(self):
        class Model:
            def transcribe(self, *args, **kwargs):
                raise AssertionError("Model must not run")
        self.assertEqual(opinions.suggest(Model(), np.ones(16000), [make_line()], "fr", recovery), [])
        self.assertEqual(opinions.suggest(Model(), np.zeros(160000), [make_line()], "en", recovery), [])

    def test_cosmetic_duplicate_and_unrelated_readings_are_rejected(self):
        self.assertFalse(opinions.plausible_difference("Easy to fool.", "easy to fool"))
        self.assertFalse(opinions.plausible_difference("easy to fool", "subscribe to the channel"))
        self.assertTrue(opinions.plausible_difference("but I'm easy to fool", "but I'm easy for you"))
        self.assertFalse(opinions.plausible_difference("I'm alright", "i'm all right"))
        self.assertFalse(opinions.plausible_difference("The punchlines", "the punch lines"))
        self.assertFalse(opinions.plausible_difference("How long will this take", "long will this take"))

    def test_gap_cannot_be_a_contraction_or_a_duplicate_ending(self):
        words = [dict(start=5.1, end=5.2, word=" i'm", probability=.99)]
        self.assertFalse(opinions.useful_gap("i'm", words, []))
        words = [dict(start=5.1, end=5.2, word=" all", probability=.99), dict(start=5.2, end=5.4, word=" right", probability=.99)]
        self.assertFalse(opinions.useful_gap("all right", words, [make_line(2, 5, "we feel alright")]))
        words[0]["start"] = 7
        words[0]["end"] = 7.2
        words[1]["start"] = 7.2
        words[1]["end"] = 7.4
        self.assertTrue(opinions.useful_gap("all right", words, [make_line(2, 5, "we feel alright")]))

    def test_cross_check_requires_the_same_reading_from_the_original_mix(self):
        class Model:
            def __init__(self, middle):
                self.middle = middle
            def transcribe(self, audio, **options):
                words = [SimpleNamespace(start=2+i, end=3+i, word=" "+word, probability=.95)
                         for i, word in enumerate(("a", self.middle, "phrase"))]
                return iter([SimpleNamespace(start=2, end=5, text="a "+self.middle+" phrase", words=words,
                    avg_logprob=-.1, no_speech_prob=.01, compression_ratio=1)]), SimpleNamespace(language="en")
        proposal = dict(kind="line", line_index=0, start=2, end=5, text="a better phrase", words=[], mean_probability=.9)
        original = [make_line()]
        audio = np.full(12 * 16000, .1, dtype=np.float32)
        accepted = opinions.cross_check(Model("better"), audio, original, [proposal], "en", recovery)
        self.assertTrue(accepted[0]["cross_checked"])
        self.assertEqual(opinions.cross_check(Model("different"), audio, original, [proposal], "en", recovery), [])
        self.assertEqual(original, [make_line()])


if __name__ == "__main__":
    unittest.main()
