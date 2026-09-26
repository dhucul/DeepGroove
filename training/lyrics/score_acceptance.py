"""Score trusted time intervals from complete-song application outputs and speech."""
import argparse
import json
from pathlib import Path

from train_adapter import atomic_json


def words_in_interval(lines, start, end):
    words, seen = [], set()
    for line in lines:
        if line["text"].strip() and not line.get("words"):
            raise ValueError("A nonempty transcript line has no word timing; cannot score intervals reliably")
        for word in line.get("words", []):
            middle = (word["start"] + word["end"]) / 2
            identity = (round(word["start"], 3), round(word["end"], 3), word["word"])
            if start <= middle < end and identity not in seen:
                seen.add(identity)
                words.append(word)
    return " ".join(word["word"].strip() for word in sorted(words, key=lambda w: (w["start"], w["end"])))


def score(rows, normalize):
    import jiwer
    refs = [normalize(row["text"]) for row in rows]
    preds = [normalize(row["prediction"]) for row in rows]
    stats = jiwer.process_words(refs, preds)
    count = stats.hits + stats.deletions + stats.substitutions
    negatives = [i for i, row in enumerate(rows) if row.get("kind") == "d"]
    return {"examples": len(rows), "reference_words": count, "wer": stats.wer if count else None,
        "deletions": stats.deletions, "insertions": stats.insertions, "substitutions": stats.substitutions,
        "instrumental_examples": len(negatives), "instrumental_false_positives": sum(bool(preds[i]) for i in negatives),
        "instrumental_invented_words": sum(len(preds[i].split()) for i in negatives)}


def joined_song_rows(rows):
    result = []
    for song in sorted({row["song"] for row in rows}):
        selected = sorted((row for row in rows if row["song"] == song), key=lambda r: r["start"])
        result.append({"song": song, "text": " ".join(r["text"] for r in selected),
                       "prediction": " ".join(r["prediction"] for r in selected)})
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-model", type=Path, required=True)
    parser.add_argument("--music-data", type=Path, required=True)
    parser.add_argument("--runs", type=Path, required=True)
    args = parser.parse_args()
    from transformers import WhisperProcessor
    processor = WhisperProcessor.from_pretrained(args.base_model, local_files_only=True)
    normalize = processor.tokenizer.normalize
    songs = json.loads(args.music_data.read_text(encoding="utf-8"))["songs"]
    report = {"scope": "Five complete fresh songs scored on trusted nonoverlapping annotation intervals; 24 read-speech clips.", "results": {}}
    for name in ("base", "adapted"):
        all_rows, per_song = [], {}
        for index, song in enumerate(songs, 1):
            path = args.runs / f"song-{index:02d}" / f"{name}.json"
            lines = json.loads(path.read_text(encoding="utf-8"))["lines"]
            rows = [{**row, "song": song["song"], "prediction": words_in_interval(lines, row["start"], row["end"])}
                    for row in song["intervals"]]
            all_rows.extend(rows)
            per_song[song["song"]] = score(rows, normalize)
        speech = json.loads((args.runs / "speech" / f"{name}.json").read_text(encoding="utf-8"))["predictions"]
        report["results"][name] = {"music": score(all_rows, normalize),
            "music_joined": score(joined_song_rows(all_rows), normalize),
            "music_positive": score([r for r in all_rows if r["kind"] != "d"], normalize),
            "per_song": per_song, "speech": score(speech, normalize)}
        atomic_json(args.runs / f"{name}-scored-intervals.json", {"rows": all_rows})
    atomic_json(args.runs / "acceptance.json", report)
    print(json.dumps(report, indent=2), flush=True)


if __name__ == "__main__":
    main()
