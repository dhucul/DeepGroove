"""Fetch pinned public separator assets for local research, never the app bundle."""
import hashlib
import json
from pathlib import Path
import requests
import sys

CODE = "25f44ffb55ee3c301281bba21b2d6d311cb69ae2"
MODEL = "ac9b0614ab3cd7f77219e18ba494dfd93956c348"
CHECKSUM = "87201f4d31afb5bc79993230fc49446918425574db48c01c405e44f365c7559e"
FILES = ["README.md", "requirements.txt", "utils.py", "inference.py",
         "configs/config_vocals_mel_band_roformer.yaml",
         "models/mel_band_roformer/__init__.py", "models/mel_band_roformer/attend.py",
         "models/mel_band_roformer/mel_band_roformer.py"]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    root = Path(sys.argv[1])
    root.mkdir(parents=True, exist_ok=True)
    hashes = {}
    for name in FILES:
        url = f"https://raw.githubusercontent.com/KimberleyJensen/Mel-Band-Roformer-Vocal-Model/{CODE}/{name}"
        response = requests.get(url, timeout=60)
        response.raise_for_status()
        path = root / "source" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(response.content)
        hashes[name] = digest(path)
    url = f"https://huggingface.co/KimberleyJSN/melbandroformer/resolve/{MODEL}/MelBandRoformer.ckpt"
    path = root / "MelBandRoformer.ckpt"
    if not path.exists() or digest(path) != CHECKSUM:
        partial = path.with_suffix(".part")
        with requests.get(url, stream=True, timeout=(30, 180)) as response:
            response.raise_for_status()
            with partial.open("wb") as output:
                count = 0
                for chunk in response.iter_content(8 * 1024 * 1024):
                    output.write(chunk)
                    count += len(chunk)
                    print(f"Downloaded {count / 1024**2:.0f} MiB", flush=True)
        if digest(partial) != CHECKSUM:
            raise ValueError("Published model checksum mismatch")
        partial.replace(path)
    (root / "provenance.json").write_text(json.dumps({"code_commit": CODE, "model_commit": MODEL,
        "checkpoint_sha256": CHECKSUM, "source_sha256": hashes, "checkpoint_url": url,
        "usage": "Local research only; training overlap with test corpus is not disclosed by publisher."}, indent=2), encoding="utf-8")
    print("Pinned separator assets verified.", flush=True)


if __name__ == "__main__":
    main()
