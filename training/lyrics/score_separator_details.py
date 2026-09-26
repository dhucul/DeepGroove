"""Summarize opening intervals, local regression, and separator runtime costs."""
import argparse
import json
import hashlib
from pathlib import Path
import re
from score_acceptance import score, words_in_interval
from train_adapter import atomic_json


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--base-model", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--references", type=Path, required=True)
    args = parser.parse_args()
    from transformers import WhisperProcessor
    from alt_eval import compute_metrics
    normalize = WhisperProcessor.from_pretrained(args.base_model, local_files_only=True).tokenizer.normalize
    selected = json.loads(args.data.read_text(encoding="utf-8"))["songs"]
    references = {row["name"]: row for row in map(json.loads, args.references.read_text(encoding="utf-8").splitlines())}
    incomplete = [row["song"] for row in selected if any("not transcribed" in line["text"].casefold()
                    for line in references[row["song"]]["lines"])]
    report = {"scope": "Opening = actual first annotated MUSDB-ALT line; songs with explicit annotation gaps are excluded.",
              "reference_sha256": hashlib.sha256(args.references.read_bytes()).hexdigest(),
              "excluded_incomplete_references": incomplete,
              "complete_reference_songs": [row["song"] for row in selected if row["song"] not in incomplete],
              "results": {}}
    for name in ("demucs", "mel"):
        rows = json.loads((args.runs / f"{name}-scored-intervals.json").read_text(encoding="utf-8"))["rows"]
        songs = sorted({row["song"] for row in rows})
        first_trusted = [min((row for row in rows if row["song"] == song and row["kind"] != "d" and row["text"].strip()),
                        key=lambda row: row["start"]) for song in songs]
        openings, complete_truth, complete_predictions = [], [], []
        for index, song in enumerate(selected, 1):
            if song["song"] in incomplete:
                continue
            reference = references[song["song"]]
            lines = json.loads((args.runs / f"song-{index:02d}" / f"{name}.json").read_text(encoding="utf-8"))["lines"]
            first = min((line for line in reference["lines"] if line["text"].strip()), key=lambda line: line["start"])
            openings.append({**first, "song": song["song"],
                             "prediction": words_in_interval(lines, first["start"], first["end"])})
            complete_truth.append(reference["text"])
            complete_predictions.append(" ".join(line["text"] for line in lines))
        local_path = args.runs / "user-song" / f"{name}.json"
        local = json.loads(local_path.read_text(encoding="utf-8"))["lines"] if local_path.exists() else []
        full = normalize(" ".join(line["text"] for line in local))
        local_report = {"line_count": len(local), "easy_to_fool": len(re.findall(r"easy to fool", full)),
                        "meet_you_in_a_pool": len(re.findall(r"meet you in a pool", full)),
                        "first_line": local[0] if local else None}
        times = {}
        for case in sorted(args.runs.glob("song-*")):
            stage = json.loads((case / f"{name}-stage.json").read_text(encoding="utf-8"))
            seconds = stage["seconds"]
            if name == "mel":
                separation = json.loads((case / "separate-mel-stage.json").read_text(encoding="utf-8"))
                seconds += separation["seconds"]
            times[case.name] = round(seconds, 2)
        report["results"][name] = {"openings": {"overall": compute_metrics([r["text"] for r in openings],
                [r["prediction"] for r in openings], include_other=False, languages="en"), "rows": openings},
            "first_trusted_intervals": {"overall": score(first_trusted, normalize), "rows": first_trusted},
            "whole_song_complete_references_only": compute_metrics(complete_truth, complete_predictions, include_other=False, languages="en"),
            "local_regression": local_report, "total_pipeline_seconds_by_case": times,
            "total_pipeline_seconds": round(sum(times.values()), 2)}
    atomic_json(args.runs / "separator-details.json", report)
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
