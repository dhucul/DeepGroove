"""Audio decoding regressions: run with the local engine's Python; no models needed."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

import numpy as np
import soundfile as sf
from faster_whisper.audio import decode_audio

worker_path = Path(__file__).resolve().parents[2] / "src/WaveLab/Transcription/lyrics_worker.py"
spec = importlib.util.spec_from_file_location("lyrics_worker", worker_path)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class AudioTests(unittest.TestCase):
    def decode(self, samples):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "audio.wav"
            sf.write(path, samples, 44100, subtype="FLOAT")
            mono, peak, selected = worker.decode_for_transcription(path)
            if samples.ndim == 1:
                left = right = decode_audio(str(path), sampling_rate=16000)
            else:
                left, right = decode_audio(str(path), sampling_rate=16000, split_stereo=True)
            return mono, peak, selected, left, right

    def test_inverted_stereo_survives_instead_of_becoming_silence(self):
        voice = .4 * np.sin(2 * np.pi * 220 * np.arange(44100) / 44100)
        mono, peak, selected, left, right = self.decode(np.column_stack((voice, -voice)))
        self.assertEqual(selected, 0)
        self.assertGreater(peak, .39)
        np.testing.assert_array_equal(mono, left)
        np.testing.assert_allclose(left + right, 0, atol=1 / 32768)
        self.assertEqual(len(mono), 16000)  # No timing shift or duration change.

    def test_partial_cancellation_uses_the_stronger_right_channel(self):
        voice = .4 * np.sin(2 * np.pi * 220 * np.arange(44100) / 44100)
        mono, peak, selected, _, right = self.decode(np.column_stack((voice * .5, -voice)))
        self.assertEqual(selected, 1)
        np.testing.assert_array_equal(mono, right)
        self.assertGreater(peak, .39)

    def test_ordinary_stereo_retains_both_channels(self):
        time = np.arange(44100) / 44100
        samples = np.column_stack((.4 * np.sin(2 * np.pi * 220 * time), .4 * np.sin(2 * np.pi * 330 * time)))
        mono, _, selected, left, right = self.decode(samples)
        self.assertIsNone(selected)
        np.testing.assert_allclose(mono, (left + right) / 2)

    def test_hard_panned_voice_is_not_mistaken_for_cancellation(self):
        voice = .4 * np.sin(2 * np.pi * 220 * np.arange(44100) / 44100)
        mono, _, selected, left, _ = self.decode(np.column_stack((voice, np.zeros_like(voice))))
        self.assertIsNone(selected)
        np.testing.assert_allclose(mono, left / 2)

    def test_mono_and_true_silence_are_preserved(self):
        for level in (0, .25):
            with self.subTest(level=level):
                mono, peak, selected, left, _ = self.decode(np.full(44100, level, dtype=np.float32))
                self.assertIsNone(selected)
                np.testing.assert_array_equal(mono, left)
                self.assertAlmostEqual(peak, level, delta=1 / 32768)


if __name__ == "__main__":
    unittest.main()
