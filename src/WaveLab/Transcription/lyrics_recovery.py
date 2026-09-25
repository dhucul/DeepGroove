"""Music transcription with independent coverage checks; no lyric completion or text invention."""
import copy
import math
import re

RATE = 16000


def normalized_words(text):
    return re.findall(r"\w+", text.casefold(), flags=re.UNICODE)


def music_options(speech=False, hints=""):
    return dict(beam_size=5, best_of=5, temperature=[0.0, 0.2, 0.4],
                condition_on_previous_text=False, word_timestamps=True,
                vad_filter=speech,
                # Speech heuristics can discard entire windows of singing, including
                # held syllables and deliberately unclear pronunciation. Retain those
                # readings for review rather than silently treating them as silence.
                no_speech_threshold=0.6 if speech else None,
                log_prob_threshold=-1.0, compression_ratio_threshold=2.4,
                hallucination_silence_threshold=2.0 if speech else None,
                hotwords=hints or None)


def prepare_audio(audio):
    """Bounded gain on the recognition copy, including quiet retry excerpts."""
    import numpy as np
    samples = np.asarray(audio, dtype=np.float32)
    if not len(samples):
        return samples
    peak = float(np.max(np.abs(samples)))
    if peak <= 1e-8:
        return samples
    gain = min(20.0, .8 / peak) if peak < .15 else min(1.0, .98 / peak)
    return samples * gain if gain != 1 else samples


def has_audio(audio):
    import numpy as np
    return bool(len(audio) and np.max(np.abs(audio)) > 1e-8)


def needs_review(segment, words):
    return (not words or segment.avg_logprob < -0.8 or segment.no_speech_prob > 0.5
            or segment.compression_ratio > 2.4
            or any(w.probability < 0.5 for w in words))


def line_from_segment(segment, duration, offset=0):
    start = max(0, min(duration, segment.start + offset))
    end = max(start, min(duration, segment.end + offset))
    text = segment.text.strip()
    tokens = normalized_words(text)
    annotation = (text.startswith(("[", "(", "♪", "♫"))
                  and tokens in (["music"], ["instrumental"], ["silence"], ["applause"]))
    if not tokens or end <= start or not math.isfinite(start + end) or annotation:
        return None
    words = list(segment.words or [])
    return {"start": start, "end": end, "text": text, "model_text": text,
            "needs_review": needs_review(segment, words), "alternative_text": "",
            "recovered": False, "recovery_note": "",
            "avg_logprob": segment.avg_logprob, "no_speech_prob": segment.no_speech_prob,
            "compression_ratio": segment.compression_ratio,
            "words": [{"start": max(start, min(end, w.start + offset)),
                       "end": max(start, min(end, w.end + offset)),
                       "word": w.word, "probability": max(0, min(1, w.probability))}
                      for w in words]}


def decode_float_channels(path):
    """Resample without the 16-bit quantization that can erase very quiet syllables."""
    import io
    import av
    import numpy as np
    with av.open(str(path), mode="r", metadata_errors="ignore") as container:
        count = 1 if len(container.streams.audio[0].codec_context.layout.channels) == 1 else 2
        resampler = av.AudioResampler(format="fltp", layout="mono" if count == 1 else "stereo", rate=RATE)
        buffers = [io.BytesIO() for _ in range(count)]

        def append(frame):
            for converted in resampler.resample(frame):
                for buffer, samples in zip(buffers, converted.to_ndarray()):
                    buffer.write(samples.tobytes())

        for frame in container.decode(audio=0):
            append(frame)
        append(None)
    return tuple(np.frombuffer(buffer.getvalue(), dtype=np.float32) for buffer in buffers)


def word_support(words):
    words = [word for word in words if normalized_words(word["word"])]
    return (bool(words) and sum(word["probability"] for word in words) / len(words) >= .5
            and sum(word["probability"] >= .5 for word in words) / len(words) >= .6)


def credible_candidate(line):
    # A retry must have acoustic support. More generated text is not automatically
    # more accurate; uncertain candidates with little evidence must not fill gaps.
    words = line["words"]
    return (bool(words) and line.get("avg_logprob", -10) >= -1.0
            and line.get("no_speech_prob", 1) <= .7
            and line.get("compression_ratio", 10) <= 2.4
            and word_support(words))


def contains_in_order(shorter, longer):
    position = 0
    for token in longer:
        if position < len(shorter) and shorter[position] == token:
            position += 1
    return position == len(shorter)


def added_words_supported(old_tokens, candidate):
    position = 0
    for word in candidate["words"]:
        for token in normalized_words(word["word"]):
            if position < len(old_tokens) and token == old_tokens[position]:
                position += 1
            elif word["probability"] < .5:
                return False
    return position == len(old_tokens)


def trim_boundary_duplicates(words, lines):
    """Alignment can put the same ending word just beyond an existing line's end."""
    words = list(words)
    for line in lines:
        if not words:
            break
        old = normalized_words(line["text"])
        new = [token for word in words for token in normalized_words(word["word"])]
        before = abs(words[0]["start"] - line["end"]) < .75
        after = abs(words[-1]["end"] - line["start"]) < .75
        for size in range(min(len(old), len(new)), 0, -1):
            if before and old[-size:] == new[:size]:
                count, remove = 0, 0
                for word in words:
                    count += len(normalized_words(word["word"]))
                    remove += 1
                    if count >= size:
                        break
                if count == size:
                    words = words[remove:]
                break
            if after and old[:size] == new[-size:]:
                count, remove = 0, 0
                for word in reversed(words):
                    count += len(normalized_words(word["word"]))
                    remove += 1
                    if count >= size:
                        break
                if count == size:
                    words = words[:-remove]
                break
    return words


def recovered_copy(line, note, alternative=""):
    line = copy.deepcopy(line)
    line.update(recovered=True, needs_review=True, recovery_note=note, alternative_text=alternative)
    return line


def merge_passes(primary, candidates, source):
    """Add uncovered words; retain both readings when a pass adds words to an existing line.

    All matching is local in time. Identical text in a later chorus is never deduplicated.
    """
    result = copy.deepcopy(primary)
    for candidate in sorted(candidates, key=lambda line: line["start"]):
        if not credible_candidate(candidate):
            continue
        overlapping = [line for line in result
                       if min(line["end"], candidate["end"]) - max(line["start"], candidate["start"]) > .08]
        if not overlapping:
            result.append(recovered_copy(candidate, f"Found by {source}. Replay this added line to verify it."))
            continue
        overlapping.sort(key=lambda line: line["start"])
        previous = " ".join(line["text"] for line in overlapping)
        old_tokens, new_tokens = normalized_words(previous), normalized_words(candidate["text"])
        if old_tokens == new_tokens:
            continue
        covers = (candidate["start"] <= overlapping[0]["start"] + .35
                  and candidate["end"] >= max(line["end"] for line in overlapping) - .35)
        if (covers and len(old_tokens) >= 2 and len(new_tokens) > len(old_tokens)
                and contains_in_order(old_tokens, new_tokens) and added_words_supported(old_tokens, candidate)):
            result = [line for line in result if not any(line is old for old in overlapping)]
            result.append(recovered_copy(candidate, f"{source.capitalize()} added words. The earlier reading is kept as an alternative.", previous))
            continue
        best = max(overlapping, key=lambda line: min(line["end"], candidate["end"]) - max(line["start"], candidate["start"]))
        # Keep an existing alternate instead of overwriting it with every retry.
        if not best.get("alternative_text"):
            best["alternative_text"] = candidate["text"]
            best["needs_review"] = True
        # Recover a prefix/suffix (or words in a timing gap) without inserting a second
        # copy of words already represented by a primary line.
        groups, group = [], []
        for word in candidate["words"]:
            midpoint = (word["start"] + word["end"]) / 2
            covered = any(line["start"] - .12 <= midpoint <= line["end"] + .12 for line in result)
            if covered or word["end"] <= word["start"]:
                if group:
                    groups.append(group)
                    group = []
            else:
                group.append(word)
        if group:
            groups.append(group)
        for words in groups:
            words = trim_boundary_duplicates(words, overlapping)
            if not word_support(words):
                continue
            text = "".join(w["word"] for w in words).strip()
            if not normalized_words(text):
                continue
            extra = copy.deepcopy(candidate)
            extra.update(start=words[0]["start"], end=words[-1]["end"], text=text, model_text=text, words=copy.deepcopy(words))
            result.append(recovered_copy(extra, f"Found by {source}. Replay these added words to verify them."))
    return sorted(result, key=lambda line: (line["start"], line["end"]))


def recovery_windows(lines, duration):
    """Short, overlapping retries give omitted phrases a fresh decoder context."""
    windows, cursor = [], 0.0
    for line in sorted(lines, key=lambda item: item["start"]):
        if line["start"] - cursor >= .75:
            windows.append((max(0, cursor - .6), min(duration, line["start"] + .6)))
        cursor = max(cursor, line["end"])
    if duration - cursor >= .75:
        windows.append((max(0, cursor - .6), duration))
    for line in lines:
        if line["needs_review"] and not line.get("recovered"):
            windows.append((max(0, line["start"] - .6), min(duration, line["end"] + .6)))
    merged = []
    for start, end in sorted(windows):
        if merged and start <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(end, merged[-1][1]))
        else:
            merged.append((start, end))
    result = []
    for start, end in merged:
        while end - start > 14:
            result.append((start, start + 14))
            start += 12  # Two seconds of shared context avoid cutting boundary words.
        if end - start >= .75:
            result.append((start, end))
    if lines:
        first = min(lines, key=lambda line: line["start"])
        if first["start"] > .75:
            # A leading-gap retry ended just after the first recognized word, often
            # cutting the phrase before the decoder had enough context. Shift past
            # part of a long instrumental introduction and retain the next phrase(s).
            start = max(0, first["start"] - 6)
            end = min(duration, start + 28, max(first["end"] + 8, start + 20))
            if end - start >= .75 and (start, end) not in result:
                result.insert(0, (start, end))
    return result


def transcribe(model, audio, original, language, speech, compare, isolated, hints, report):
    """Return a transcript and detected language, with music-specific coverage checks."""
    duration = len(audio) / RATE
    options = music_options(speech, hints)
    prepared = prepare_audio(audio)
    segments, info = model.transcribe(prepared, language=language, task="transcribe", **options)
    lines = []
    for segment in segments:
        line = line_from_segment(segment, duration)
        if line and has_audio(prepared[int(line["start"] * RATE):int(line["end"] * RATE)]):
            lines.append(line)
        report("Transcribing words and timing…", .50 + .23 * min(1, segment.end / duration))
    if not compare or speech:
        return lines, info.language
    if isolated:
        report("Checking the whole original recording for missed words…", .74)
        other, _ = model.transcribe(prepare_audio(original), language=info.language, task="transcribe", **options)
        candidates = []
        for segment in other:
            line = line_from_segment(segment, duration)
            if line and has_audio(original[int(line["start"] * RATE):int(line["end"] * RATE)]):
                candidates.append(line)
            report("Checking the whole original recording for missed words…", .74 + .11 * min(1, segment.end / duration))
        lines = merge_passes(lines, candidates, "the original mix")
    windows = recovery_windows(lines, duration)
    for index, (start, end) in enumerate(windows):
        excerpt = audio[int(start * RATE):int(end * RATE)]
        if not has_audio(excerpt):
            continue
        report("Rechecking an uncovered or unclear passage…", .86 + .13 * index / max(1, len(windows)))
        retry, _ = model.transcribe(prepare_audio(excerpt), language=info.language, task="transcribe", **{**options, "beam_size": 8})
        candidates = []
        for segment in retry:
            line = line_from_segment(segment, duration, offset=start)
            if line and line["end"] <= end + .05:
                candidates.append(line)
        lines = merge_passes(lines, candidates, "a focused replay")
    return lines, info.language
