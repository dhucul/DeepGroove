"""Build-time only: materialize pinned models and a relocatable, offline runtime manifest."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path

MODELS = {
    "Systran/faster-whisper-large-v3": "edaa852ec7e145841d8ffdb056a99866b5f0a478",
    "mobiuslabsgmbh/faster-whisper-large-v3-turbo": "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf",
    "adefossez/HTDemucs-ft": "d74ac89c3a1e874fc78f152555cf4d8533f06cd4",
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--requirements", type=Path, required=True)
    parser.add_argument("--build-fingerprint", required=True)
    args = parser.parse_args()
    from huggingface_hub import snapshot_download

    required = {}
    for repository, revision in MODELS.items():
        print(f"Bundling {repository} at {revision}…", flush=True)
        patterns = (["*.safetensors", "htdemucs_ft.yaml", "README.md", "LICENSE*"]
                    if repository.startswith("adefossez/") else
                    ["config.json", "preprocessor_config.json", "model.bin", "tokenizer.json", "vocabulary.*", "README.md", "LICENSE*"])
        source = Path(snapshot_download(repository, revision=revision, allow_patterns=patterns))
        destination = args.bundle / "models/hub" / ("models--" + repository.replace("/", "--"))
        snapshot = destination / "snapshots" / revision
        snapshot.mkdir(parents=True, exist_ok=True)
        for file in source.iterdir():
            if not file.is_file():
                continue
            output = snapshot / file.name
            # Dereference the download cache's links: the shipped program must not depend
            # on a developer cache, a junction, or an absolute path on this machine.
            if not output.exists() or output.stat().st_size != file.stat().st_size:
                shutil.copy2(file, output, follow_symlinks=True)
            required[output.relative_to(args.bundle).as_posix()] = output.stat().st_size
        (destination / "refs").mkdir(exist_ok=True)
        reference = destination / "refs/main"
        reference.write_text(revision, encoding="utf-8")
        required[reference.relative_to(args.bundle).as_posix()] = reference.stat().st_size

    for relative in ["python/python.exe", "python/python311.dll", "python/Lib/os.py",
                     "python/Lib/site-packages/torch/__init__.py",
                     "python/Lib/site-packages/torch/lib/torch_cpu.dll",
                     "python/Lib/site-packages/faster_whisper/__init__.py",
                     "python/Lib/site-packages/demucs/api.py"]:
        required[relative] = (args.bundle / relative).stat().st_size
    # Preserve distribution notices and make every installed package/license discoverable.
    notices = ["Deep Groove bundled transcription components", "",
               "The Python distribution's license is in python/LICENSE.txt.",
               "Package licenses and notices are preserved in python/Lib/site-packages,",
               "including each package's .dist-info directory. Model cards/licenses are",
               "preserved alongside the model files. Upstream model repositories:", ""]
    notices.extend(f"https://huggingface.co/{name}/tree/{revision}" for name, revision in MODELS.items())
    (args.bundle / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(notices) + "\n", encoding="utf-8")
    manifest = {"schema_version": 1,
                "build_fingerprint": args.build_fingerprint,
                "requirements_sha256": hashlib.sha256(args.requirements.read_bytes()).hexdigest().upper(),
                "models": MODELS, "required_files": required}
    (args.bundle / "bundle.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print("Bundled transcription runtime and all models are ready.", flush=True)


if __name__ == "__main__":
    main()
