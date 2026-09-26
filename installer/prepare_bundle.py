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


def read_manifest(path):
    if path is None:
        return {}
    try:
        result = json.loads(path.read_text(encoding="utf-8"))
        return result if isinstance(result, dict) else {}
    except (OSError, ValueError):
        return {}


def cached_model_files(bundle, state, repository, revision):
    """Reuse only a recorded pinned snapshot whose complete assets still match."""
    if (state.get("schema_version") != 1 or not isinstance(state.get("models"), dict)
            or state["models"].get(repository) != revision
            or not isinstance(state.get("required_files"), dict)):
        return None
    prefix = f"models/hub/models--{repository.replace('/', '--')}/snapshots/{revision}/"
    files = {name: size for name, size in state["required_files"].items()
             if isinstance(name, str) and name.startswith(prefix)}
    names = {name[len(prefix):] for name in files}
    if repository.startswith("adefossez/"):
        if "htdemucs_ft.yaml" not in names or sum(name.endswith(".safetensors") for name in names) != 4:
            return None
    elif not {"config.json", "model.bin", "tokenizer.json"}.issubset(names) or not any(name.startswith("vocabulary.") for name in names):
        return None
    for name, size in files.items():
        path = bundle / name
        if (type(size) is not int or size <= 0 or not path.resolve().is_relative_to(bundle.resolve())
                or path.is_symlink() or not path.is_file() or path.stat().st_size != size):
            return None
    return files


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--requirements", type=Path, required=True)
    parser.add_argument("--build-fingerprint", required=True)
    parser.add_argument("--reuse-manifest", type=Path)
    args = parser.parse_args()
    receipts = [read_manifest(args.bundle / "bundle.json"), read_manifest(args.reuse_manifest)]

    required = {}
    for repository, revision in MODELS.items():
        cached = next((files for state in receipts
                       if (files := cached_model_files(args.bundle, state, repository, revision))), None)
        destination = args.bundle / "models/hub" / ("models--" + repository.replace("/", "--"))
        if cached:
            print(f"Reusing verified local model: {repository}", flush=True)
            required.update(cached)
        else:
            print(f"Downloading public model {repository} at {revision} (no account required)...", flush=True)
            from huggingface_hub import snapshot_download
            patterns = (["*.safetensors", "htdemucs_ft.yaml", "README.md", "LICENSE*"]
                        if repository.startswith("adefossez/") else
                        ["config.json", "preprocessor_config.json", "model.bin", "tokenizer.json", "vocabulary.*", "README.md", "LICENSE*"])
            source = Path(snapshot_download(repository, revision=revision, allow_patterns=patterns))
            snapshot = destination / "snapshots" / revision
            snapshot.mkdir(parents=True, exist_ok=True)
            for file in source.iterdir():
                if not file.is_file():
                    continue
                output = snapshot / file.name
                # The shipped program must not depend on developer-cache links.
                if output.is_symlink():
                    output.unlink()
                if not output.exists() or output.stat().st_size != file.stat().st_size:
                    shutil.copy2(file, output, follow_symlinks=True)
                required[output.relative_to(args.bundle).as_posix()] = output.stat().st_size
        if cached_model_files(args.bundle, {"schema_version": 1, "models": MODELS, "required_files": required}, repository, revision) is None:
            raise RuntimeError(f"The prepared model is incomplete: {repository}")
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
    temporary = args.bundle / "bundle.json.partial"
    temporary.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    temporary.replace(args.bundle / "bundle.json")
    print("Bundled transcription runtime and all models are ready.", flush=True)


if __name__ == "__main__":
    main()
