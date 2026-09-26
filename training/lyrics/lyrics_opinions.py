"""Optional singing-model suggestions. Primary readings are never rewritten."""
import copy
import difflib
import math
import re

RATE = 16000


def tokens(text):
    return re.findall(r"\w+", text.replace("’", "'").casefold())


def reading_key(text):
    # Word spacing alone is not a useful alternative pronunciation.
    return "".join(tokens(re.sub(r"\balright\b", "all right", text.casefold())))


def plan_targets(lines, duration, limit=48):
    """Bound individual checks and prefer uncertain words over confident passages."""
    targets = []
    for index, line in enumerate(lines):
        if not (line["needs_review"] or line.get("recovered")) or len(tokens(line["text"])) < 2:
            continue
        words = line.get("words", [])
        first, last = 0, len(words)
        start, end = line["start"], line["end"]
        if end - start > 16:
            # A partial reading can only be spliced when the word evidence agrees
            # with the primary text; never discard unmatched or edited words.
            if not words or tokens("".join(w["word"] for w in words)) != tokens(line["text"]):
                continue
            weakest = min(range(len(words)), key=lambda i: words[i]["probability"])
            first, last = weakest, weakest + 1
            while first > 0 and words[last - 1]["end"] - words[first - 1]["start"] <= 12:
                first -= 1
            while last < len(words) and words[last]["end"] - words[first]["start"] <= 12:
                last += 1
            start, end = words[first]["start"], words[last - 1]["end"]
        if not 0 <= start < end <= duration or end - start > 16:
            continue
        confidence = sum(w["probability"] for w in words[first:last]) / max(1, last - first)
        targets.append(dict(kind="line", line_index=index, start=start, end=end,
            view_start=max(0, start - 2), view_end=min(duration, end + 2),
            first_word=first, last_word=last, priority=1 - confidence + (.25 if line.get("recovered") else 0)))
    cursor = 0.0
    for line in sorted(lines, key=lambda row: row["start"]):
        if line["start"] - cursor >= .9:
            start = cursor
            while line["start"] - start >= .9:
                end = min(line["start"], start + 12)
                targets.append(dict(kind="gap", start=start, end=end,
                    view_start=max(0, start - 2), view_end=min(duration, end + 2), priority=-1))
                start = end
        cursor = max(cursor, line["end"])
    while duration - cursor >= .9:
        end = min(duration, cursor + 12)
        targets.append(dict(kind="gap", start=cursor, end=end,
            view_start=max(0, cursor - 2), view_end=min(duration, end + 2), priority=-1))
        cursor = end
    # Reserve some checks for missing phrases, without unbounded instrumental scans.
    uncertain = sorted((t for t in targets if t["kind"] == "line"), key=lambda t: -t["priority"])
    gaps = [t for t in targets if t["kind"] == "gap"]
    selected = uncertain[:max(1, limit * 3 // 4)]
    selected.extend(gaps[:max(0, limit - len(selected))])
    return sorted(selected, key=lambda t: (t["start"], t["end"]))


def plausible_difference(original, candidate):
    old, new = tokens(original), tokens(candidate)
    if not new or old == new or len(new) > 2 * len(old) + 3:
        return False
    if reading_key(original) == reading_key(candidate):
        return False
    common = sum(block.size for block in difflib.SequenceMatcher(None, old, new, autojunk=False).get_matching_blocks())
    if len(new) < len(old) and common == len(new):
        return False  # Do not offer a clipped, shortened copy as a new reading.
    return common >= max(1, math.ceil(.45 * min(len(old), len(new))))


def proposed_text(line, target, words):
    text = "".join(word["word"] for word in words).strip()
    original_words = line.get("words", [])
    if target["first_word"] or target["last_word"] < len(original_words):
        prefix = "".join(w["word"] for w in original_words[:target["first_word"]]).strip()
        suffix = "".join(w["word"] for w in original_words[target["last_word"]:]).strip()
        text = " ".join(part for part in (prefix, text, suffix) if part)
    return text


def useful_gap(text, words, lines):
    lexical = re.findall(r"[^\W_]+(?:'[^\W_]+)*", text.replace("’", "'").casefold())
    meaningful = set(lexical) - {"ah", "oh", "ooh", "uh", "um", "mm", "mmm", "la", "da"}
    if len(words) < 2 or len(meaningful) < 2:
        return False
    key = reading_key(text)
    for line in lines:
        # A sustained final word can extend beyond the first pass's timestamps.
        if -.2 <= words[0]["start"] - line["end"] <= .5 and reading_key(line["text"]).endswith(key):
            return False
        if -.2 <= line["start"] - words[-1]["end"] <= .5 and reading_key(line["text"]).startswith(key):
            return False
    return True


def suggest(model, audio, lines, language, recovery, report=lambda *_: None, hints=""):
    import numpy as np
    if language != "en":
        return []
    samples = np.asarray(audio, dtype=np.float32)
    if samples.ndim != 1 or not np.isfinite(samples).all():
        raise ValueError("Second opinions require finite mono audio")
    duration = len(samples) / RATE
    targets = plan_targets(lines, duration)
    if not targets or not recovery.has_audio(samples):
        return []
    # Energy only gates new gap suggestions. It never removes primary words.
    hop = RATE // 10
    n = len(samples) // hop
    frames = samples[:n * hop].reshape(n, hop) if n else samples.reshape(1, -1)
    rms = np.sqrt(np.einsum("ij,ij->i", frames, frames, dtype=np.float64) / frames.shape[1])
    reference_level = max(float(np.percentile(rms, 95)), 1e-8)
    suggestions = []
    options = {**recovery.music_options(False, hints), "temperature": 0.0, "beam_size": 5,
               "no_speech_threshold": .5, "log_prob_threshold": -.8}
    for index, target in enumerate(targets):
        start, end = target["start"], target["end"]
        if target["kind"] == "gap":
            energy = rms[max(0, int(start * 10)):max(1, math.ceil(end * 10))]
            if np.count_nonzero(energy > .06 * reference_level) < 3:
                continue
        offset = target["view_start"]
        excerpt = samples[round(offset * RATE):round(target["view_end"] * RATE)]
        if not recovery.has_audio(excerpt):
            continue
        report("Checking another reading of an unclear passage…", .9 + .09 * index / max(1, len(targets)))
        segments, _ = model.transcribe(recovery.prepare_audio(excerpt), language="en", task="transcribe", **options)
        words, evidence = [], []
        for segment in segments:
            line = recovery.line_from_segment(segment, duration, offset=offset)
            if not line or line["end"] > target["view_end"] + .05 or not recovery.credible_candidate(line):
                continue
            evidence.append(line)
            words.extend(w for w in line["words"] if w["end"] > w["start"]
                         and start <= (w["start"] + w["end"]) / 2 < end)
        if not words or not evidence:
            continue
        mean_probability = sum(w["probability"] for w in words) / len(words)
        minimum = .8 if target["kind"] == "gap" else .65
        if mean_probability < minimum or sum(w["probability"] >= .5 for w in words) < .75 * len(words):
            continue
        if max(r["no_speech_prob"] for r in evidence) > .5:
            continue
        if target["kind"] == "line":
            original = lines[target["line_index"]]
            text = proposed_text(original, target, words)
            if not plausible_difference(original["text"], text):
                continue
            # A suggestion already present in the normal pass is not new evidence.
            if tokens(text) == tokens(original.get("alternative_text", "")):
                continue
        else:
            text = "".join(w["word"] for w in words).strip()
            if not useful_gap(text, words, lines) or len(tokens(text)) > 6 * (end - start) + 3:
                continue
        suggestions.append({"kind": target["kind"], "line_index": target.get("line_index"),
            "start": start, "end": end, "text": text, "mean_probability": mean_probability,
            "words": copy.deepcopy(words), "source": "Second opinion"})
    return suggestions


def cross_check(model, audio, lines, suggestions, language, recovery, report=lambda *_: None):
    """Keep only readings reproduced by the original recognizer in this context.

    This is agreement, not proof: both recognizers share a pretrained foundation.
    """
    if language != "en":
        return []
    duration = len(audio) / RATE
    targets = {(t["kind"], t.get("line_index"), t["start"], t["end"]): t for t in plan_targets(lines, duration)}
    accepted = []
    options = {**recovery.music_options(False, ""), "temperature": 0.0, "beam_size": 5}
    for index, proposal in enumerate(suggestions):
        if proposal["kind"] == "line":
            if not plausible_difference(lines[proposal["line_index"]]["text"], proposal["text"]):
                continue
        elif not useful_gap(proposal["text"], proposal["words"], lines):
            continue
        key = (proposal["kind"], proposal.get("line_index"), proposal["start"], proposal["end"])
        target = targets.get(key)
        if target is None:
            continue
        report("Comparing possible readings…", .97 + .025 * index / max(1, len(suggestions)))
        context = min(28, duration)
        center = (target["start"] + target["end"]) / 2
        long_start = max(0, min(duration - context, center - context / 2))
        views = list(dict.fromkeys([(target["view_start"], target["view_end"]), (long_start, long_start + context)]))
        confirmed = True
        for offset, view_end in views:
            excerpt = audio[round(offset * RATE):round(view_end * RATE)]
            segments, _ = model.transcribe(recovery.prepare_audio(excerpt), language="en", task="transcribe", **options)
            words = []
            for segment in segments:
                line = recovery.line_from_segment(segment, duration, offset=offset)
                if line and line["end"] <= view_end + .05 and recovery.credible_candidate(line):
                    words.extend(w for w in line["words"] if w["end"] > w["start"]
                        and target["start"] <= (w["start"] + w["end"]) / 2 < target["end"])
            if not words:
                confirmed = False
                break
            candidate = (proposed_text(lines[proposal["line_index"]], target, words) if proposal["kind"] == "line"
                         else "".join(w["word"] for w in words).strip())
            if reading_key(candidate) != reading_key(proposal["text"]):
                confirmed = False
                break
        if confirmed:
            accepted.append({**copy.deepcopy(proposal), "cross_checked": True, "confirmation_views": len(views)})
    return accepted


def attach_suggestions(lines, suggestions):
    """Return an editable review copy; suggested missing lines export as blank."""
    result = copy.deepcopy(lines)
    for suggestion in suggestions:
        if suggestion["kind"] == "line":
            line = result[suggestion["line_index"]]
            line["second_opinion_text"] = suggestion["text"]
            line["second_opinion_note"] = "Another possible reading. Replay the line before choosing."
            line["needs_review"] = True
        else:
            words = copy.deepcopy(suggestion["words"])
            result.append(dict(start=words[0]["start"], end=words[-1]["end"], text="", model_text=suggestion["text"],
                words=words, needs_review=True, recovered=False, alternative_text="", possible_missing=True,
                recovery_note="Possible missed words. Replay and choose a reading to add them.",
                second_opinion_text=suggestion["text"], second_opinion_note="Not added to your transcript until you choose it."))
    return sorted(result, key=lambda row: (row["start"], row["end"]))
