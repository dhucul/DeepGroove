"""Prepare deterministic fresh music/speech checks. Never supplies training data."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path, PurePosixPath
import random
import re
import shutil
import tarfile
import zipfile

from prepare_musdb import AUDIO_URL, parse_annotations, seconds
from train_adapter import atomic_json

SEED = 20260925


def download(url, path, checksum):
    import requests
    if path.exists() and hashlib.md5(path.read_bytes()).hexdigest() == checksum:
        return
    temporary = path.with_suffix(".part")
    digest = hashlib.md5()
    with requests.get(url, stream=True, timeout=(30, 180)) as response:
        response.raise_for_status()
        with temporary.open("wb") as output:
            for chunk in response.iter_content(1024 * 1024):
                output.write(chunk)
                digest.update(chunk)
    if digest.hexdigest() != checksum:
        raise ValueError(f"Published checksum mismatch for {path.name}")
    temporary.replace(path)


def artist(song):
    return re.split(r"\s+(?:feat\.?|ft\.?|featuring)\s+", song.split(" - ", 1)[0].casefold())[0]


def trusted_nonoverlapping(rows, bounds):
    # Manual annotations can overlap by a second. Exclude ambiguous intervals
    # rather than scoring the same recognized word against two references.
    return [row for row in rows if not any(
        number != row["annotation_line"] and min(row["end"], end) - max(row["start"], start) > .05
        for number, start, end in bounds)]


def music(args):
    from remotezip import RemoteZip
    archive = args.output / "test_lyrics.zip"
    download("https://zenodo.org/records/3989267/files/test_lyrics.zip?download=1", archive,
             "1fadfcc287a0cbd319f7ddf6e09b78bb")
    songs, filters = parse_annotations(archive)
    seen = {artist(json.loads(line)["song"]) for line in args.training_manifest.read_text(encoding="utf-8").splitlines()}
    if args.exclude_selection:
        excluded = json.loads(args.exclude_selection.read_text(encoding="utf-8"))["songs"]
        seen.update(artist(row if isinstance(row, str) else row["song"]) for row in excluded)
    names = sorted(name for name in songs if artist(name) not in seen)
    if args.full_references:
        available = {json.loads(line)["name"] for line in args.full_references.read_text(encoding="utf-8").splitlines() if line.strip()}
        names = [name for name in names if name in available]
    random.Random(args.seed).shuffle(names)
    selected, artists = [], set()
    for name in names:
        if artist(name) not in artists:
            selected.append(name)
            artists.add(artist(name))
        if len(selected) == 5:
            break
    if len(selected) != 5:
        raise ValueError("Five fresh artists are required")
    selection_path = args.output / "selection.json"
    if selection_path.exists():
        previous = json.loads(selection_path.read_text(encoding="utf-8"))
        if previous["songs"] != selected or previous["seed"] != args.seed:
            raise ValueError("Preserve the existing selection and use a new output directory")
    atomic_json(selection_path, {"seed": args.seed, "songs": selected,
        "selected_before_model_evaluation": True, "filters": filters,
        "source": "https://zenodo.org/records/3989267", "usage": "Noncommercial research evaluation only"})
    references = {}
    with zipfile.ZipFile(archive) as labels:
        for member in labels.namelist():
            song = PurePosixPath(member).stem
            if song not in selected:
                continue
            bounds = []
            for number, line in enumerate(labels.read(member).decode("utf-8-sig").splitlines(), 1):
                try:
                    fields = line.strip().lstrip("* ").split()
                    bounds.append((number, seconds(fields[0]), seconds(fields[1])))
                except (ValueError, IndexError):
                    continue
            references[song] = trusted_nonoverlapping(songs[song], bounds)
    def fetch(song):
        destination = args.output / "audio" / song / "mixture.wav"
        if not destination.resolve().is_relative_to(args.output.resolve()):
            raise ValueError("Dataset path escapes output")
        destination.parent.mkdir(parents=True, exist_ok=True)
        with RemoteZip(AUDIO_URL, timeout=180) as remote:
            member = f"test/{song}/mixture.wav"
            entry = remote.getinfo(member)
            if not destination.exists() or destination.stat().st_size != entry.file_size:
                temporary = destination.with_suffix(".part")
                with remote.open(member) as incoming, temporary.open("wb") as output:
                    shutil.copyfileobj(incoming, output, 1024 * 1024)
                if temporary.stat().st_size != entry.file_size:
                    raise ValueError("Wrong audio length")
                temporary.replace(destination)
        print(f"Fresh music ready: {song}", flush=True)
        return {"song": song, "audio": str(destination.resolve()), "intervals": references[song]}
    with ThreadPoolExecutor(max_workers=3) as executor:
        result = list(executor.map(fetch, selected))
    atomic_json(args.output / "music.json", {"seed": args.seed, "songs": result})


def speech(args):
    import soundfile as sf
    archive = args.output / "test-clean.tar.gz"
    download("https://openslr.trmal.net/resources/12/test-clean.tar.gz", archive,
             "32fa31d27d2e1cad72775fee3f4849a9")
    references, members, sex = {}, {}, {}
    with tarfile.open(archive, "r|gz") as source:
        for member in source:
            if not member.isfile():
                continue
            path = PurePosixPath(member.name)
            if path.name == "SPEAKERS.TXT":
                for line in source.extractfile(member).read().decode("utf-8").splitlines():
                    if not line.startswith(";") and "|" in line:
                        fields = [s.strip() for s in line.split("|")]
                        sex[fields[0]] = fields[1]
            elif "test-clean" in path.parts and path.name.endswith(".trans.txt"):
                for line in source.extractfile(member).read().decode("utf-8").splitlines():
                    identifier, text = line.split(" ", 1)
                    references[identifier] = text
            elif "test-clean" in path.parts and path.suffix == ".flac":
                members[path.stem] = member.name
    speakers = sorted({key.split("-")[0] for key in references if key in members})
    rng = random.Random(SEED)
    chosen = []
    for group in ("F", "M"):
        pool = [speaker for speaker in speakers if sex.get(speaker) == group]
        rng.shuffle(pool)
        chosen.extend(pool[:4])
    if len(chosen) != 8:
        rng.shuffle(speakers)
        chosen = speakers[:8]
    candidates = {}
    for speaker in chosen:
        pool = sorted(key for key in references if key.startswith(speaker + "-") and key in members)
        rng.shuffle(pool)
        candidates[speaker] = pool[:8]
    wanted = {members[key]: key for pool in candidates.values() for key in pool}
    audio = args.output / "audio"
    audio.mkdir(exist_ok=True)
    with tarfile.open(archive, "r|gz") as source:
        for member in source:
            if member.isfile() and member.name in wanted:
                identifier = wanted[member.name]
                if not re.fullmatch(r"\d+-\d+-\d+", identifier):
                    raise ValueError("Unexpected speech filename")
                with (audio / (identifier + ".flac")).open("wb") as output:
                    shutil.copyfileobj(source.extractfile(member), output)
    rows = []
    for speaker, pool in candidates.items():
        accepted = 0
        for identifier in pool:
            path = audio / (identifier + ".flac")
            duration = sf.info(path).duration
            if 2 <= duration <= 25:
                rows.append({"id": identifier, "speaker": speaker, "audio": str(path.resolve()),
                             "text": references[identifier], "duration": duration})
                accepted += 1
            if accepted == 3:
                break
        if accepted != 3:
            raise ValueError("Need three eligible excerpts for each selected speaker")
    atomic_json(args.output / "speech.json", {"seed": SEED, "source": "https://www.openslr.org/12",
        "license": "CC BY 4.0", "selected_before_model_evaluation": True, "examples": rows})
    print(f"Fresh speech ready: {len(rows)} clips from {len(chosen)} speakers", flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("kind", choices=("music", "speech"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--training-manifest", type=Path)
    parser.add_argument("--exclude-selection", type=Path)
    parser.add_argument("--seed", type=int, default=SEED)
    parser.add_argument("--full-references", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    if args.kind == "music" and not args.training_manifest:
        parser.error("Music preparation requires the original training manifest")
    (music if args.kind == "music" else speech)(args)


if __name__ == "__main__":
    main()
