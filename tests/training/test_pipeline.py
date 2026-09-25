import importlib.util
import json
from pathlib import Path
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


class PipelineTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
