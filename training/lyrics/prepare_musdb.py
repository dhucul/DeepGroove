"""Prepare the public MUSDB18 English training partition; never read the test partition."""
import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
import json
from pathlib import Path, PurePosixPath
import random
import shutil
import time
import zipfile

AUDIO_URL = "https://zenodo.org/records/3338373/files/musdb18hq.zip?download=1"
ANNOTATION_URL = "https://zenodo.org/records/3989267/files/train_lyrics.zip?download=1"
ANNOTATION_MD5 = "dc89e2175edb94eca26dd504a2eef1c7"
LICENSE_URL = "https://zenodo.org/records/3989267/files/LICENSE.txt?download=1"


def seconds(value):
    result = 0.0
    for part in value.split(":"):
        result = result * 60 + float(part)
    return result


def parse_annotations(path):
    songs, counts = {}, Counter()
    with zipfile.ZipFile(path) as archive:
        for name in archive.namelist():
            if not name.endswith(".txt") or "__MACOSX" in name:
                continue
            song = PurePosixPath(name).stem
            rows = []
            for number, row in enumerate(archive.read(name).decode("utf-8-sig").splitlines(), 1):
                row = row.strip()
                if not row:
                    continue
                if "*" in row or "?" in row:
                    counts["uncertain_excluded"] += 1
                    continue
                fields = row.split(maxsplit=3)
                try:
                    start, end = seconds(fields[0]), seconds(fields[1])
                    kind = fields[2].lower()
                    text = fields[3].strip() if len(fields) > 3 else ""
                except (ValueError, IndexError):
                    counts["malformed_excluded"] += 1
                    continue
                if not 1 <= end-start <= 30 or start < 0:
                    counts["duration_excluded"] += 1
                    continue
                if kind not in ("a", "b", "d"):
                    counts["overlapping_different_words_excluded"] += 1
                    continue
                if kind != "d" and not text:
                    counts["empty_positive_excluded"] += 1
                    continue
                rows.append(dict(start=start, end=end, text="" if kind == "d" else text,
                                 kind=kind, annotation_line=number))
                counts["negative" if kind == "d" else "positive"] += 1
            if any(row["text"] for row in rows):
                songs[song] = rows
    return songs, dict(counts)


def split_artists(songs):
    # Keep both stems, every excerpt, and every recording by an artist in one split.
    artists = sorted({song.split(" - ", 1)[0] for song in songs})
    random.Random(42).shuffle(artists)
    validation = set(artists[:max(1, round(len(artists)*.15))])
    return {song: "validation" if song.split(" - ", 1)[0] in validation else "train" for song in songs}


def safe_destination(root, name):
    parts = PurePosixPath(name).parts
    if (len(parts) != 3 or parts[0] != "train" or any(part in (".", "..") for part in parts)
            or parts[-1] not in ("vocals.wav", "mixture.wav")):
        raise ValueError(f"Unexpected archive member: {name}")
    destination = (root / Path(*parts)).resolve()
    if not destination.is_relative_to(root.resolve()):
        raise ValueError("Archive path escapes dataset directory")
    return destination


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--annotations", type=Path)
    args = parser.parse_args()
    import requests
    from remotezip import RemoteZip

    args.output.mkdir(parents=True, exist_ok=True)
    archive_path = args.output / "train_lyrics.zip"
    if args.annotations:
        shutil.copyfile(args.annotations, archive_path)
    elif not archive_path.exists():
        response = requests.get(ANNOTATION_URL, timeout=60)
        response.raise_for_status()
        archive_path.write_bytes(response.content)
    if hashlib.md5(archive_path.read_bytes()).hexdigest() != ANNOTATION_MD5:
        raise ValueError("Annotation archive checksum does not match the published dataset")
    license_path = args.output / "ANNOTATION-LICENSE.txt"
    if not license_path.exists():
        response = requests.get(LICENSE_URL, timeout=60)
        response.raise_for_status()
        license_path.write_bytes(response.content)
    songs, filters = parse_annotations(archive_path)
    assignments = split_artists(songs)
    raw = args.output / "audio"
    rows = []
    # Two reusable sources per song are sufficient; do not download drums, bass,
    # accompaniment, or any test-set recording. Zip CRC checks verify each member.
    with RemoteZip(AUDIO_URL, timeout=180) as remote:
        entries = {entry.filename: entry for entry in remote.infolist()}
        expected_bytes = sum(entries[f"train/{song}/{kind}.wav"].compress_size
                             for song in songs for kind in ("mixture", "vocals"))
        print(json.dumps({"stage": "download", "songs": len(songs), "compressed_gib": round(expected_bytes/2**30, 2),
                          "filters": filters}), flush=True)
    def download_song(song):
        song_rows = []
        with RemoteZip(AUDIO_URL, timeout=180) as remote:
            for source in ("mixture", "vocals"):
                name = f"train/{song}/{source}.wav"
                entry = entries[name]
                path = safe_destination(raw, name)
                path.parent.mkdir(parents=True, exist_ok=True)
                if not path.exists() or path.stat().st_size != entry.file_size:
                    for attempt in range(4):
                        temporary = path.with_suffix(".part")
                        try:
                            with remote.open(name) as incoming, temporary.open("wb") as output:
                                shutil.copyfileobj(incoming, output, length=1024*1024)
                            if temporary.stat().st_size != entry.file_size:
                                raise IOError("Downloaded member has incorrect length")
                            temporary.replace(path)
                            break
                        except Exception:
                            temporary.unlink(missing_ok=True)
                            if attempt == 3:
                                raise
                            time.sleep(2 ** (attempt+1))
                for annotation in songs[song]:
                    if not annotation["text"] and source == "vocals":
                        continue
                    song_rows.append(dict(song=song, artist=song.split(" - ", 1)[0], split=assignments[song],
                                          audio=str(path), source=source, **annotation))
        return song_rows
    with ThreadPoolExecutor(max_workers=4) as executor:
        futures = [executor.submit(download_song, song) for song in sorted(songs)]
        for index, future in enumerate(as_completed(futures), 1):
            rows.extend(future.result())
            print(json.dumps({"stage": "download", "completed_songs": index, "total_songs": len(songs)}), flush=True)
    rows.sort(key=lambda row: (row["song"], row["source"], row["start"]))
    selected = []
    for split in ("train", "validation"):
        positives = [row for row in rows if row["split"] == split and row["text"]]
        negatives = [row for row in rows if row["split"] == split and not row["text"]]
        random.Random(43).shuffle(negatives)
        selected.extend(positives + negatives[:max(1, len(positives)//9)])
    with (args.output / "manifest.jsonl").open("w", encoding="utf-8") as output:
        for row in selected:
            output.write(json.dumps(row, ensure_ascii=False) + "\n")
    summary = {"dataset": "MUSDB18-HQ + MUSDB18 lyrics extension (training partition only)",
               "audio_url": AUDIO_URL, "annotation_url": ANNOTATION_URL, "annotation_md5": ANNOTATION_MD5,
               "usage": "Educational/noncommercial research pilot; no production deployment or model publication.",
               "label_quality": "Manual author transcripts; uncertain/overlapping-different-word rows excluded, not independently human-verified.",
               "song_assignments": assignments, "filters": filters,
               "examples": dict(Counter(row["split"] for row in selected)),
               "minutes_by_split": {split: round(sum(row["end"]-row["start"] for row in selected if row["split"] == split)/60, 2)
                                    for split in ("train", "validation")}}
    (args.output / "dataset.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({"stage": "ready", "examples": summary["examples"], "minutes": summary["minutes_by_split"]}), flush=True)


if __name__ == "__main__":
    main()
