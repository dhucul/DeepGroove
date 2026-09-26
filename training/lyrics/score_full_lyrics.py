"""Additional whole-song scoring with the published MUSDB-ALT references."""
import argparse
import hashlib
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--references", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--runs", type=Path, required=True)
    parser.add_argument("--variants", nargs="+", required=True)
    args = parser.parse_args()
    from alt_eval import compute_metrics
    records = [json.loads(line) for line in args.references.read_text(encoding="utf-8").splitlines() if line.strip()]
    references = {row["name"]: row for row in records}
    if len(references) != len(records):
        raise ValueError("Duplicate reference song names")
    selected = json.loads(args.data.read_text(encoding="utf-8"))["songs"]
    examples = [(index, row["song"]) for index, row in enumerate(selected, 1) if row["song"] in references]
    if not examples:
        raise ValueError("No selected songs have whole-song references")
    result = {"reference_sha256": hashlib.sha256(args.references.read_bytes()).hexdigest(),
        "scope": "Complete transcripts, including backing vocals and vocables; alt-eval 1.2.0, case-insensitive WER.",
        "songs": [name for _, name in examples],
        "missing_reference": [row["song"] for row in selected if row["song"] not in references], "results": {}}
    for variant in args.variants:
        truth, predictions, details = [], [], {}
        for index, song in examples:
            path = args.runs / f"song-{index:02d}" / f"{variant}.json"
            lines = json.loads(path.read_text(encoding="utf-8"))["lines"]
            predicted = "\n".join(line["text"] for line in lines)
            reference = references[song]["text"]
            truth.append(reference)
            predictions.append(predicted)
            details[song] = compute_metrics([reference], [predicted], include_other=False, languages="en")
        result["results"][variant] = {"overall": compute_metrics(truth, predictions, include_other=False, languages="en"),
                                       "per_song": details}
    path = args.runs / "whole-song-scores.json"
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    temporary.replace(path)
    print(json.dumps({"songs": len(examples), "missing": result["missing_reference"],
                      "results": {name: value["overall"] for name, value in result["results"].items()}}, indent=2))


if __name__ == "__main__":
    main()
