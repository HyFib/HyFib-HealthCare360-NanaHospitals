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

# Names of the fictional people and places. A recogniser trained on Western English cannot spell Tamil and other Indian
# names it has never heard ("Kalaivani" comes back as "kalevani"); the captions show them, so a name is scored as
# heard whatever single word the recogniser wrote for it.
NAMES = set("""
kalaivani murugesan farah sheikh arjun mehta lakshmi narayanan rajendran pillai fatima begum begum's vimala raghavan
anand joseph meenakshi sundaram karthik subramanian ravi kumar senthil sunita devi priya menon mohammed irfan aarav
sharma joseph mathew deepa raman anitha krishnan suresh babu shalini rao vikram shetty arun prasad revathi selvam
karthikeyan murugan balaji ramasamy ramesh venkatesan selvaraj pandian malathi sekar gowri shankar jayanthi kannan
prakash arumugam divya natarajan sangeetha iyer nandhini muthu saravanan thangavel vignesh raja kalaiselvi murthy
sathish meera venkatesh balan pooja sundar theni madurai coimbatore kochi hyderabad nana kavya nair rahul verma sneha
""".split())

# Words a listener tells apart only by context, which the captions on screen give: both sides map to one spelling.
HOMOPHONES = {"cue": "queue", "cues": "queues", "que": "queue", "qs": "queues", "too": "to", "two": "to",
              "four": "for", "write": "right", "know": "no", "weight": "wait", "there": "their", "it's": "its",
              "ok": "okay", "alright": "all right", "xray": "x ray"}


# Spellings a recogniser may choose for the same spoken words.
SAME = [(r"\buser name", "username"), (r"\bhome page", "homepage"), (r"\bctrl\b", "control"), (r"\besc\b", "escape"),
        (r"\bpractice\b", "practise"), (r"\bpay slip", "payslip"), (r"\blicense\b", "licence"), (r"\bcheck up", "checkup"),
        (r"\bcheck-up", "checkup"), (r"\be-mail", "email"), (r"\bset up\b", "setup"), (r"(\w{3,})iz(e|es|ed|er|ers|ing|ation|ations)\b", r"\1is\2"),
        (r"\bokay\b", "ok"), (r"\bwi-?fi\b", "wifi"), (r"\bx-ray", "x ray"),
        # British and American spellings of the same spoken word.
        (r"anaesth", "anesth"), (r"\btheatres?\b", "theater"), (r"\bcatalogue", "catalog"), (r"\bhaem", "hem"),
        (r"\bpaed", "ped"), (r"orthopaed", "orthoped"), (r"gynaec", "gynec"), (r"\boesoph", "esoph"),
        (r"\bprogramme", "program"), (r"\benquir", "inquir"), (r"\bcolour", "color"), (r"\blitres?\b", "liter"), (r"\bmetres?\b", "meter"),
        (r"\bfo?etus", "fetus"), (r"\bcentres?\b", "center"), (r"\bcentre\b", "center")]


def words(text: str) -> list[str]:
    text = re.sub(r"(?i)\b(hc|health\s*care)\s*360\b", lambda m: "HC360" if m.group(1).lower() == "hc" else "HealthCare360", text)
    # A code heard in lower case is the same code: "nhob2600001" for NHOB26000001.
    text = re.sub(r"\b([a-z]{1,6})(\d{5,13})\b", lambda m: m.group(1).upper() + m.group(2), text)
    text = re.sub(r"\b([A-Z]{1,6}) ?-? ?(\d{1,8})\b", r"\1-\2", text)  # GM012, GM 012 and GM-012 are one token
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


def join_compounds(a: list[str], b: list[str]) -> list[str]:
    """Two words of `a` that the other side wrote as one ("out patient" and "outpatient", "store keeper") become one."""
    other = set(b)
    out, i = [], 0
    while i < len(a):
        if i + 1 < len(a) and a[i] + a[i + 1] in other:
            out.append(a[i] + a[i + 1])
            i += 2
        else:
            out.append(a[i])
            i += 1
    return out


def wer(ref: list[str], hyp: list[str]) -> tuple[int, list]:
    """Edit distance on words, with the edits."""
    n, m = len(ref), len(hyp)
    d = np.zeros((n + 1, m + 1), dtype=np.int32)
    d[:, 0] = np.arange(n + 1)
    d[0, :] = np.arange(m + 1)
    same = lambda a, b: a == b or a.rstrip("'s") in NAMES  # noqa: E731  a name matches whatever one word was written
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            d[i, j] = min(d[i - 1, j] + 1, d[i, j - 1] + 1, d[i - 1, j - 1] + (not same(ref[i - 1], hyp[j - 1])))
    edits, i, j = [], n, m
    while i > 0 or j > 0:
        if i > 0 and j > 0 and d[i, j] == d[i - 1, j - 1] + (not same(ref[i - 1], hyp[j - 1])):
            if not same(ref[i - 1], hyp[j - 1]):
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
    # What the narration says after a step must have been on the screen (a result scrolled out of view is not).
    off_screen = [b["id"] for b in cap["beats"] if b["type"] == "step" and b.get("after")
                  and (b.get("offScreen") or (b.get("result") and (b["result"][2] <= 0 or b["result"][3] <= 0)))]
    gates["run"] = not (root / "failure.json").exists() and not cap.get("serverErrors") and not off_screen
    report["run"] = {"steps": sum(1 for b in cap["beats"] if b["type"] == "step"), "serverErrors": cap.get("serverErrors", []),
                     "offScreen": off_screen}

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
        ref, hyp = join_compounds(ref, hyp), join_compounds(hyp, ref)
        errors, edits = wer(ref, hyp)
        # Whisper sometimes repeats a phrase that was said once, or stops after the first sentence of a window (both
        # known failures on short windows). A line heard with far more, or far fewer, words than were said is
        # transcribed again, sentence by sentence, and the better reading is kept: words missing from the audio are
        # missing from both readings, so this hides nothing that was not said.
        if (len(hyp) > len(ref) * 1.4 + 3 or len(hyp) < len(ref) * 0.7) and len(nar[sid]["sentences"]) > 1:
            again = []
            for snt in nar[sid]["sentences"]:
                sa, sb = int((start + snt["start"] - 0.1) * 16000), int((start + snt["end"] + 0.2) * 16000)
                again.append(transcribe(rec, x[max(0, sa):sb]))
            hyp2 = words(" ".join(again))
            ref2, hyp2 = join_compounds(words(nar[sid]["text"]), hyp2), join_compounds(hyp2, words(nar[sid]["text"]))
            errors2, edits2 = wer(ref2, hyp2)
            if errors2 < errors:
                heard, ref, hyp, errors, edits = " ".join(again), ref2, hyp2, errors2, edits2
        total_ref += len(ref)
        total_err += errors
        n_words += len(spoken(nar[sid]["text"]).split())  # what is said: "64.0 mg/L" is six spoken words
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
    md.append(f"| Run | {'pass' if gates['run'] else 'FAIL'} | {report['run']['steps']} steps, {len(report['run']['serverErrors'])} server errors{', results off the screen: ' + ', '.join(report['run']['offScreen']) if report['run']['offScreen'] else ''} |")
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
