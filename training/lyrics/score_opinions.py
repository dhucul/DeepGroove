"""Measure suggestion usefulness without presenting perfect user choices as accuracy."""
import argparse
import copy
import json
from pathlib import Path


def apply_proposals(lines, proposals):
    result = copy.deepcopy(lines)
    for proposal in proposals:
        if proposal["kind"] == "line":
            result[proposal["line_index"]]["text"] = proposal["text"]
        else:
            result.append({"start": proposal["words"][0]["start"], "end": proposal["words"][-1]["end"], "text": proposal["text"]})
    return "\n".join(r["text"] for r in sorted(result, key=lambda r: (r["start"], r["end"])))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--references", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--runs", type=Path, required=True)
    args = parser.parse_args()
    from alt_eval import compute_metrics
    references = {r["name"]: r for r in [json.loads(line) for line in args.references.read_text(encoding="utf-8").splitlines() if line.strip()]}
    songs = json.loads(args.data.read_text(encoding="utf-8"))["songs"]
    def measure(reference, text):
        value = compute_metrics([reference], [text], languages="en", include_other=False)
        value["errors"] = value["substitutions"] + value["deletions"] + value["insertions"]
        return value
    totals = dict(helpful=0, harmful=0, neutral=0, suggestions=0, baseline_errors=0, all_accepted_errors=0)
    details, missing = {}, []
    for index, row in enumerate(songs, 1):
        song = row["song"]
        if song not in references:
            missing.append(song)
            continue
        directory = args.runs / f"song-{index:02d}"
        baseline = json.loads((directory / "base.json").read_text(encoding="utf-8"))
        opinions = json.loads((directory / "opinions.json").read_text(encoding="utf-8"))
        if opinions["main_unchanged"] is not True:
            raise ValueError("Main transcript was changed")
        truth = references[song]["text"]
        initial = measure(truth, apply_proposals(baseline["lines"], []))
        comparisons = []
        for proposal in opinions["suggestions"]:
            changed = measure(truth, apply_proposals(baseline["lines"], [proposal]))
            delta = changed["errors"] - initial["errors"]
            outcome = "helpful" if delta < 0 else "harmful" if delta > 0 else "neutral"
            totals[outcome] += 1
            totals["suggestions"] += 1
            comparisons.append({"kind": proposal["kind"], "line_index": proposal.get("line_index"),
                "start": proposal["start"], "end": proposal["end"], "probability": proposal["mean_probability"],
                "outcome": outcome, "error_delta": delta, "proposed_text": proposal["text"]})
        all_accepted = measure(truth, apply_proposals(baseline["lines"], opinions["suggestions"]))
        totals["baseline_errors"] += initial["errors"]
        totals["all_accepted_errors"] += all_accepted["errors"]
        details[song] = {"baseline": initial, "all_accepted": all_accepted, "suggestions": comparisons}
    report = {"scope": "Each suggestion is scored individually against complete references. Main transcript remains unchanged; users must review alternatives.",
              "totals": totals, "missing_references": missing, "songs": details}
    output = args.runs / "suggestion-scores.json"
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    print(json.dumps({"totals": totals, "missing_references": missing}, indent=2))


if __name__ == "__main__":
    main()
