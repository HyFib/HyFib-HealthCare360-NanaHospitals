"""The checks a training video must pass before anyone watches it, made on the finished file, by tools other than the
ones that made it.

    python -I qa.py <video-dir>        writes qa.json and qa.md; exits 1 when a gate fails

Gates:
  run          the capture finished every step, and the server answered no request with an error (5xx)
  speech       an independent speech recogniser (Whisper small.en) transcribes each narrated line of the finished video;
               its word error rate against the script is at most 5% (homophones such as queue/cue count as heard)
  pace         narration between 120 and 150 words a minute (clear for listeners whose first language is Tamil)
  captions     every cue at most 2 lines of 42 characters, on screen 0.8 to 7 s, at most 17 characters a second,
               none overlapping, and together they say every sentence of the script
  loudness     integrated loudness -16 LUFS (+-1), true peak at most -1 dBTP (EBU R128, measured by ffmpeg)
  format       H.264 1920x1080 at 30 fps, yuv420p, AAC 48 kHz, chapters present
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from narrate import MODELS, spoken  # noqa: E402

# Words a listener tells apart only by context, which the captions on screen give: both sides map to one spelling.
HOMOPHONES = {"cue": "queue", "cues": "queues", "que": "queue", "qs": "queues", "too": "to", "two": "to",
              "four": "for", "write": "right", "know": "no", "weight": "wait", "there": "their", "it's": "its",
              "ok": "okay", "alright": "all right", "xray": "x ray"}


# Spellings a recogniser may choose for the same spoken words.
SAME = [(r"\buser name", "username"), (r"\bhome page", "homepage"), (r"\bctrl\b", "control"), (r"\besc\b", "escape"),
        (r"\bpractice\b", "practise"), (r"\blicense\b", "licence"), (r"\bcenter\b", "centre"), (r"\bcheck up", "checkup"),
        (r"\bcheck-up", "checkup"), (r"\be-mail", "email"), (r"\bset up\b", "setup"), (r"(\w{3,})iz(e|es|ed|ing|ation)\b", r"\1is\2"),
        (r"\bokay\b", "ok"), (r"\bwi-?fi\b", "wifi"), (r"\bx-ray", "x ray")]


def words(text: str) -> list[str]:
    text = re.sub(r"(?i)\b(hc|health\s*care)\s*360\b", lambda m: "HC360" if m.group(1).lower() == "hc" else "HealthCare360", text)
    t = spoken(text).lower()
    for pattern, repl in SAME:
        t = re.sub(pattern, repl, t)
    t = t.replace("-", " ").replace("’", "'")
    t = re.sub(r"[^a-z' ]", " ", t)
    out, run = [], []
    for w in t.split():
        w = HOMOPHONES.get(w.strip("'"), w.strip("'"))
        if len(w) == 1 and w not in ("a",) or w in ("ay",):
            run.append(w)
            continue
        if run:
            out.append("".join(run))
            run = []
        if w in ("and",):  # "one thousand and thirty" vs "one thousand thirty": numbers are read either way
            continue
        out.append(w)
    if run:
        out.append("".join(run))
    return out


def wer(ref: list[str], hyp: list[str]) -> tuple[int, list]:
    """Edit distance on words, with the edits."""
    n, m = len(ref), len(hyp)
    d = np.zeros((n + 1, m + 1), dtype=np.int32)
    d[:, 0] = np.arange(n + 1)
    d[0, :] = np.arange(m + 1)
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            d[i, j] = min(d[i - 1, j] + 1, d[i, j - 1] + 1, d[i - 1, j - 1] + (ref[i - 1] != hyp[j - 1]))
    edits, i, j = [], n, m
    while i > 0 or j > 0:
        if i > 0 and j > 0 and d[i, j] == d[i - 1, j - 1] + (ref[i - 1] != hyp[j - 1]):
            if ref[i - 1] != hyp[j - 1]:
                edits.append((ref[i - 1], hyp[j - 1]))
            i, j = i - 1, j - 1
        elif i > 0 and d[i, j] == d[i - 1, j] + 1:
            edits.append((ref[i - 1], ""))
            i -= 1
        else:
            edits.append(("", hyp[j - 1]))
            j -= 1
    return int(d[n, m]), edits[::-1]


def recogniser():
    import sherpa_onnx

    d = MODELS / "sherpa-onnx-whisper-small.en"
    return sherpa_onnx.OfflineRecognizer.from_whisper(
        encoder=str(d / "small.en-encoder.int8.onnx"), decoder=str(d / "small.en-decoder.int8.onnx"),
        tokens=str(d / "small.en-tokens.txt"), num_threads=4, language="en", task="transcribe")


def audio16k(mp4: Path) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(mp4), "-ac", "1", "-ar", "16000",
                          "-f", "s16le", "-"], capture_output=True, check=True).stdout
    return np.frombuffer(raw, np.int16).astype(np.float32) / 32768


def transcribe(rec, x: np.ndarray) -> str:
    s = rec.create_stream()
    s.accept_waveform(16000, x)
    rec.decode_stream(s)
    return s.result.text.strip()


def probe(mp4: Path) -> dict:
    out = subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-show_chapters", "-show_format", "-of", "json",
                          str(mp4)], capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def loudness(mp4: Path) -> dict:
    err = subprocess.run(["ffmpeg", "-hide_banner", "-nostats", "-i", str(mp4), "-map", "0:a", "-af", "ebur128=peak=true",
                          "-f", "null", "-"], capture_output=True, text=True).stderr
    summary = err[err.rfind("Summary:"):]
    i = float(re.search(r"I:\s+(-?[\d.]+) LUFS", summary).group(1))
    tp = float(re.search(r"Peak:\s+(-?[\d.]+) dBFS", summary).group(1))
    return {"integratedLufs": i, "truePeakDbtp": tp}


def srt(path: Path) -> list[dict]:
    cues = []
    for block in path.read_text(encoding="utf-8").strip().split("\n\n"):
        lines = block.split("\n")
        a, b = lines[1].split(" --> ")
        sec = lambda s: int(s[:2]) * 3600 + int(s[3:5]) * 60 + int(s[6:8]) + int(s[9:12]) / 1000  # noqa: E731
        cues.append({"start": sec(a), "end": sec(b), "lines": lines[2:]})
    return cues


def main():
    root = Path(sys.argv[1])
    cap = json.loads((root / "capture.json").read_text(encoding="utf-8"))
    comp = json.loads((root / "compose.json").read_text(encoding="utf-8"))
    nar = {s["id"]: s for s in json.loads((root / "audio" / "narration.json").read_text(encoding="utf-8"))["segments"]}
    vid = cap["id"]
    mp4 = root / f"{vid}.mp4"
    gates, report = {}, {"id": vid, "title": cap["title"]}

    # run
    gates["run"] = not (root / "failure.json").exists() and not cap.get("serverErrors")
    report["run"] = {"steps": sum(1 for b in cap["beats"] if b["type"] == "step"), "serverErrors": cap.get("serverErrors", [])}

    # speech and pace
    rec = recogniser()
    x = audio16k(mp4)
    total_ref, total_err, worst, n_words, speech_s = 0, 0, [], 0, 0.0
    for sid, start, dur in comp["placements"]:
        a, b = int(max(0, start - 0.15) * 16000), int((start + dur + 0.25) * 16000)
        pieces, refs = [], []
        if dur <= 28:
            pieces.append(transcribe(rec, x[a:b]))
        else:  # long lines: one window per sentence
            for s in nar[sid]["sentences"]:
                sa, sb = int((start + s["start"] - 0.1) * 16000), int((start + s["end"] + 0.2) * 16000)
                pieces.append(transcribe(rec, x[sa:sb]))
        heard = " ".join(pieces)
        ref, hyp = words(nar[sid]["text"]), words(heard)
        errors, edits = wer(ref, hyp)
        total_ref += len(ref)
        total_err += errors
        n_words += len(nar[sid]["text"].split())
        speech_s += dur
        if errors:
            worst.append({"line": sid, "said": nar[sid]["text"], "heard": heard, "errors": errors,
                          "edits": [f"{r or '∅'}→{h or '∅'}" for r, h in edits]})
    speech_wer = total_err / max(1, total_ref)
    wpm = n_words / max(1e-6, speech_s) * 60
    gates["speech"] = speech_wer <= 0.05
    gates["pace"] = 120 <= wpm <= 150
    worst.sort(key=lambda w: -w["errors"])
    report["speech"] = {"wer": round(speech_wer * 100, 2), "words": total_ref, "errors": total_err,
                        "linesWithErrors": worst[:12], "recogniser": "Whisper small.en (int8, sherpa-onnx)"}
    report["pace"] = {"wordsPerMinute": round(wpm, 1), "speechSeconds": round(speech_s, 1)}

    # captions
    cues = srt(root / f"{vid}.srt")
    problems = []
    for i, c in enumerate(cues):
        d = c["end"] - c["start"]
        chars = sum(len(l) for l in c["lines"])
        if len(c["lines"]) > 2:
            problems.append(f"cue {i + 1}: {len(c['lines'])} lines")
        for l in c["lines"]:
            if len(l) > 42:
                problems.append(f"cue {i + 1}: line of {len(l)} characters")
        if d < 0.8 or d > 7.0:
            problems.append(f"cue {i + 1}: {d:.2f} s on screen")
        if chars / max(d, 1e-6) > 17:
            problems.append(f"cue {i + 1}: {chars / d:.1f} characters a second")
        if i and c["start"] < cues[i - 1]["end"] - 1e-3:
            problems.append(f"cue {i + 1}: overlaps the one before")
    said = re.sub(r"\s+", " ", " ".join(" ".join(c["lines"]) for c in cues))
    script = re.sub(r"\s+", " ", " ".join(nar[sid]["text"] for sid, _, _ in comp["placements"]))
    if said != script:
        problems.append("the captions do not say exactly what the narration says")
    gates["captions"] = not problems
    cps = [sum(len(l) for l in c["lines"]) / max(1e-6, c["end"] - c["start"]) for c in cues]
    report["captions"] = {"cues": len(cues), "maxCharsPerSecond": round(max(cps), 1),
                          "meanCharsPerSecond": round(sum(cps) / len(cps), 1), "problems": problems[:20]}

    # loudness and format
    loud = loudness(mp4)
    gates["loudness"] = abs(loud["integratedLufs"] + 16) <= 1.0 and loud["truePeakDbtp"] <= -1.0
    report["loudness"] = loud
    p = probe(mp4)
    v = next(s for s in p["streams"] if s["codec_type"] == "video")
    a = next(s for s in p["streams"] if s["codec_type"] == "audio")
    fmt = {"video": f"{v['codec_name']} {v['width']}x{v['height']} {v['r_frame_rate']} {v['pix_fmt']}",
           "audio": f"{a['codec_name']} {a['sample_rate']} Hz", "chapters": len(p.get("chapters", [])),
           "seconds": round(float(p["format"]["duration"]), 1), "megabytes": round(int(p["format"]["size"]) / 1e6, 1)}
    gates["format"] = (v["codec_name"] == "h264" and v["width"] == 1920 and v["height"] == 1080
                       and v["r_frame_rate"] == "30/1" and v["pix_fmt"] == "yuv420p" and a["codec_name"] == "aac"
                       and a["sample_rate"] == "48000" and fmt["chapters"] >= 3)
    report["format"] = fmt
    report["gates"] = gates
    report["passed"] = all(gates.values())
    (root / "qa.json").write_text(json.dumps(report, indent=1), encoding="utf-8")

    md = [f"# QA: {cap['title']}", "", "| Gate | Result | Measured |", "|---|---|---|"]
    md.append(f"| Run | {'pass' if gates['run'] else 'FAIL'} | {report['run']['steps']} steps, {len(report['run']['serverErrors'])} server errors |")
    md.append(f"| Speech | {'pass' if gates['speech'] else 'FAIL'} | WER {report['speech']['wer']}% over {total_ref} words |")
    md.append(f"| Pace | {'pass' if gates['pace'] else 'FAIL'} | {report['pace']['wordsPerMinute']} words a minute |")
    md.append(f"| Captions | {'pass' if gates['captions'] else 'FAIL'} | {len(cues)} cues, at most {report['captions']['maxCharsPerSecond']} characters a second |")
    md.append(f"| Loudness | {'pass' if gates['loudness'] else 'FAIL'} | {loud['integratedLufs']} LUFS, peak {loud['truePeakDbtp']} dBTP |")
    md.append(f"| Format | {'pass' if gates['format'] else 'FAIL'} | {fmt['video']}, {fmt['audio']}, {fmt['chapters']} chapters, {fmt['seconds']} s, {fmt['megabytes']} MB |")
    if worst:
        md += ["", "Lines the recogniser heard differently:", ""]
        md += [f"- `{w['line']}`: {', '.join(w['edits'])}" for w in worst[:12]]
    if problems:
        md += ["", "Caption problems:", ""] + [f"- {p}" for p in problems[:20]]
    (root / "qa.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print("\n".join(md[2:10]))
    sys.exit(0 if report["passed"] else 1)


if __name__ == "__main__":
    main()
