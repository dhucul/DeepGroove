import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("prepare_bundle", Path(__file__).resolve().parents[2] / "installer/prepare_bundle.py")
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class BundleCacheTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.bundle = Path(self.directory.name)
        self.repository = "Systran/faster-whisper-large-v3"
        self.revision = builder.MODELS[self.repository]
        self.prefix = f"models/hub/models--Systran--faster-whisper-large-v3/snapshots/{self.revision}/"
        self.files = {}
        for name in ("config.json", "model.bin", "tokenizer.json", "vocabulary.json"):
            path = self.bundle / (self.prefix + name)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"fixture")
            self.files[self.prefix + name] = 7
        self.state = {"schema_version": 1, "models": {self.repository: self.revision}, "required_files": self.files}

    def cached(self):
        return builder.cached_model_files(self.bundle, self.state, self.repository, self.revision)

    def test_complete_snapshot_is_reusable_after_download_cache_is_removed(self):
        self.assertEqual(self.files, self.cached())

    def test_missing_or_truncated_asset_cannot_be_reused(self):
        path = self.bundle / (self.prefix + "model.bin")
        path.write_bytes(b"short")
        self.assertIsNone(self.cached())
        path.unlink()
        self.assertIsNone(self.cached())

    def test_changed_revision_requires_model_preparation(self):
        self.state["models"][self.repository] = "different"
        self.assertIsNone(self.cached())

    def test_incomplete_receipt_does_not_make_partial_download_ready(self):
        del self.files[self.prefix + "model.bin"]
        self.assertIsNone(self.cached())

    def test_receipt_cannot_include_files_outside_bundle(self):
        self.files[self.prefix + "../../../../../../outside"] = 7
        self.assertIsNone(self.cached())

    def test_interrupted_manifest_is_a_cache_miss(self):
        manifest = self.bundle / "bundle.json"
        manifest.write_text('{"schema_version":', encoding="utf-8")
        self.assertEqual({}, builder.read_manifest(manifest))
        manifest.write_text(json.dumps(self.state), encoding="utf-8")
        self.assertEqual(self.state, builder.read_manifest(manifest))

    def test_model_builder_reuses_receipt_without_importing_download_client(self):
        # Simulate a removed build manifest/cache with a valid installed-bundle receipt.
        receipt = self.bundle / "installed-receipt.json"
        receipt.write_text(json.dumps(self.state), encoding="utf-8")
        requirements = self.bundle / "requirements.txt"
        requirements.write_text("fixture", encoding="utf-8")
        for relative in ("python/python.exe", "python/python311.dll", "python/Lib/os.py",
                         "python/Lib/site-packages/torch/__init__.py", "python/Lib/site-packages/torch/lib/torch_cpu.dll",
                         "python/Lib/site-packages/faster_whisper/__init__.py", "python/Lib/site-packages/demucs/api.py"):
            path = self.bundle / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"fixture")
        with patch.dict(builder.MODELS, {self.repository: self.revision}, clear=True), \
             patch.dict("sys.modules", {"huggingface_hub": None}), \
             patch("sys.argv", ["prepare_bundle", "--bundle", str(self.bundle), "--requirements", str(requirements),
                                "--build-fingerprint", "changed-script", "--reuse-manifest", str(receipt)]):
            builder.main()
        manifest = builder.read_manifest(self.bundle / "bundle.json")
        self.assertEqual("changed-script", manifest["build_fingerprint"])
        self.assertEqual(7, manifest["required_files"][self.prefix + "model.bin"])
        self.assertFalse((self.bundle / "bundle.json.partial").exists())


if __name__ == "__main__":
    unittest.main()
