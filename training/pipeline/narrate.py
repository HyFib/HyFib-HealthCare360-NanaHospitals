"""Narration: what the narrator says for each line of a storyboard, as audio with the time of every sentence.

    python -I narrate.py <segments.json> <out-dir>          (segments: [{"id": "s01.say", "text": "..."}])
    python -I narrate.py --say "text" <out.wav>             (one line, to listen to a pronunciation)

The text is what the captions show. The voice hears a spoken form: the pronunciation lexicon (lexicon.json) spells
initialisms the way Indian hospitals say them, numbers become words the way an Indian speaker reads them (rupees in
lakhs and crores, times as "eleven thirty", codes such as GWF-01 letter by letter), and each sentence is synthesised
on its own, so the captions know exactly when each sentence starts and ends.

Environment: HC360_TTS_MODELS (the folder setup-models.sh fills), HC360_VOICE (default hf_alpha), HC360_SPEED (0.82,
about 135-145 words a minute), HC360_TTS_CACHE (sentence audio kept between runs).
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import sys
import wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
MODELS = Path(os.environ.get("HC360_TTS_MODELS", HERE.parent / ".models"))
VOICES = {"hf_alpha": 31, "hf_beta": 32, "hm_omega": 33, "hm_psi": 34, "bf_emma": 21}
VOICE = os.environ.get("HC360_VOICE", "hf_alpha")
SPEED = float(os.environ.get("HC360_SPEED", "0.82"))
CACHE = Path(os.environ.get("HC360_TTS_CACHE", HERE.parent / ".cache" / "tts"))
RATE = 48000  # the video's audio rate
SENTENCE_PAUSE = 0.40  # seconds between two sentences of one line

LEXICON: dict[str, str] = json.loads((HERE / "lexicon.json").read_text(encoding="utf-8"))["terms"]
_LEX_KEYS = sorted(LEXICON, key=len, reverse=True)
_LEX_RE = re.compile("|".join(
    (r"(?<![\w-])" + re.escape(k) + r"(?![\w-])") if re.match(r"\w", k) else re.escape(k) for k in _LEX_KEYS))

ONES = ("zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen "
        "seventeen eighteen nineteen").split()
TENS = "_ _ twenty thirty forty fifty sixty seventy eighty ninety".split()
ORDINAL = {"one": "first", "two": "second", "three": "third", "five": "fifth", "eight": "eighth", "nine": "ninth",
           "twelve": "twelfth"}


def below_thousand(n: int) -> str:
    if n < 20:
        return ONES[n]
    if n < 100:
        return TENS[n // 10] + ("" if n % 10 == 0 else "-" + ONES[n % 10])
    rest = n % 100
    return ONES[n // 100] + " hundred" + ("" if rest == 0 else " and " + below_thousand(rest))


def indian(n: int) -> str:
    """Whole numbers the Indian way: 1,50,000 is one lakh fifty thousand."""
    if n == 0:
        return "zero"
    parts = []
    for size, name in ((10_000_000, "crore"), (100_000, "lakh"), (1000, "thousand")):
        if n >= size:
            parts.append(indian(n // size) + " " + name)
            n %= size
    if n:
        parts.append(below_thousand(n) if not parts or n >= 100 else "and " + below_thousand(n))
    return " ".join(parts)


def ordinal(n: int) -> str:
    words = indian(n)
    last = re.split(r"[ -]", words)[-1]
    if last in ORDINAL:
        new = ORDINAL[last]
    elif last.endswith("y"):
        new = last[:-1] + "ieth"
    else:
        new = last + "th"
    return words[: len(words) - len(last)] + new


def digits(s: str) -> str:
    return " ".join(ONES[int(c)] for c in s if c.isdigit())


def year(n: int) -> str:
    if 2000 <= n <= 2009:
        return indian(n)
    return below_thousand(n // 100) + " " + (below_thousand(n % 100) if n % 100 >= 10 else "oh " + ONES[n % 100])


def clock(h: int, m: int, ampm: str | None) -> str:
    """10:47 am is "ten forty-seven in the morning": clearer to a listener than letters, and never "uh em"."""
    if not ampm and h >= 13:  # 21:00 is "nine in the evening", never "twenty-one o'clock"
        ampm, h = "pm", h - 12
    said = ONES[h] if h < 20 else indian(h)
    if m:
        said += " " + (("oh " + ONES[m]) if m < 10 else below_thousand(m))
    if ampm:
        pm = ampm.lower().startswith("p")
        hour24 = h % 12 + (12 if pm else 0)
        said += " in the " + ("morning" if not pm else "afternoon" if hour24 < 17 else "evening")
    elif not m:
        said += " o'clock"
    return said


def spoken(text: str) -> str:
    """What the voice is asked to say for a caption's text."""
    s = _LEX_RE.sub(lambda m: LEXICON[m.group(0)], text.replace("’", "'").replace("‘", "'"))
    # Codes: GWF-01, GM-001, OPD-01, NH-000123 (letters, then each digit).
    s = re.sub(r"\b([A-Z]{1,6})-(\d{1,8})\b", lambda m: " ".join(m.group(1)) + " " + digits(m.group(2)), s)
    # Money: ₹1,930 / ₹ 1,50,000.50 (the lexicon already turned ₹ into "rupees ").
    s = re.sub(r"rupees\s*(\d{1,3}(?:,\d{2,3})*|\d+)(?:\.(\d\d))?",
               lambda m: indian(int(m.group(1).replace(",", ""))) + " rupees"
               + (" and " + below_thousand(int(m.group(2))) + " paise" if m.group(2) and int(m.group(2)) else ""), s)
    s = re.sub(r"\b(\d{1,2}):(\d{2})(?:\s*(a\.m\.|p\.m\.|am|pm)(?![a-z]))?",
               lambda m: clock(int(m.group(1)), int(m.group(2)), m.group(3))
               + ("." if (m.group(3) or "").endswith(".") and m.end() >= len(m.string.rstrip()) else ""), s, flags=re.I)
    s = re.sub(r"\b(\d+)/(\d+)\b", lambda m: m.group(1) + " over " + m.group(2), s)  # BP 152/94
    s = re.sub(r"\b(\d+)\.(\d+)\s*%", lambda m: indian(int(m.group(1))) + " point " + digits(m.group(2)) + " percent", s)
    s = re.sub(r"\b(\d+)\s*%", lambda m: indian(int(m.group(1))) + " percent", s)
    s = re.sub(r"\b(\d+)\.(\d+)\b", lambda m: indian(int(m.group(1))) + " point " + digits(m.group(2)), s)
    s = re.sub(r"\b(\d+)(st|nd|rd|th)\b", lambda m: ordinal(int(m.group(1))), s)
    s = re.sub(r"\b(19\d\d|20\d\d)\b", lambda m: year(int(m.group(1))), s)
    s = re.sub(r"\b\d{5,}\b", lambda m: digits(m.group(0)), s)  # phone numbers, long ids: digit by digit
    s = re.sub(r"\b\d{1,3}(?:,\d{2,3})+\b|\b\d+\b", lambda m: indian(int(m.group(0).replace(",", ""))), s)
    s = re.sub(r"(?<=[A-Za-z])/(?=[A-Za-z])", " or ", s)  # in-patient/out-patient
    return re.sub(r"\s+", " ", s).strip()


# Abbreviations that end in a full stop but do not end a sentence.
_KEEP = ["Dr.", "Mr.", "Mrs.", "Ms.", "No.", "e.g.", "i.e.", "vs.", "St."]


def sentences(text: str) -> list[str]:
    t = text.strip()
    for i, a in enumerate(_KEEP):
        t = t.replace(a, a.replace(".", f"\u0000{i}\u0000"))
    parts = re.split(r"(?<=[.!?])\s+(?=[\"'“(A-Z0-9₹])", t)
    out = []
    for p in parts:
        for i, a in enumerate(_KEEP):
            p = p.replace(a.replace(".", f"\u0000{i}\u0000"), a)
        if p.strip():
            out.append(p.strip())
    return out


_tts = None


def engine():
    global _tts
    if _tts is None:
        import sherpa_onnx

        k = MODELS / "kokoro-multi-lang-v1_0"
        _tts = sherpa_onnx.OfflineTts(sherpa_onnx.OfflineTtsConfig(
            model=sherpa_onnx.OfflineTtsModelConfig(
                kokoro=sherpa_onnx.OfflineTtsKokoroModelConfig(
                    model=str(k / "model.onnx"), voices=str(k / "voices.bin"), tokens=str(k / "tokens.txt"),
                    data_dir=str(k / "espeak-ng-data"), dict_dir=str(k / "dict"), lexicon=str(k / "lexicon-gb-en.txt")),
                num_threads=4),
            max_num_sentences=1))
    return _tts


def trim(x: np.ndarray, rate: int) -> np.ndarray:
    """Cuts the voice's leading and trailing silence, keeping 90 ms either side: a quiet first consonant (the p of
    "part") cut short is heard as another word."""
    level = np.abs(x)
    loud = np.where(level > max(0.003, level.max() * 0.005))[0]
    if len(loud) == 0:
        return x
    pad = int(rate * 0.09)
    return x[max(0, loud[0] - pad): loud[-1] + pad]


def resample(x: np.ndarray, src: int, dst: int) -> np.ndarray:
    if src == dst:
        return x
    n = int(round(len(x) * dst / src))
    return np.interp(np.linspace(0, len(x) - 1, n), np.arange(len(x)), x).astype(np.float32)


def say(sentence_spoken: str) -> np.ndarray:
    """One sentence as audio at RATE, cached by voice, speed and spoken text."""
    key = hashlib.sha1(f"kokoro-v1.0|{VOICE}|{SPEED}|{sentence_spoken}".encode()).hexdigest()
    path = CACHE / f"{key}.npy"
    if path.exists():
        return np.load(path)
    audio = engine().generate(sentence_spoken, sid=VOICES[VOICE], speed=SPEED)
    x = resample(trim(np.asarray(audio.samples, dtype=np.float32), audio.sample_rate), audio.sample_rate, RATE)
    CACHE.mkdir(parents=True, exist_ok=True)
    np.save(path, x)
    return x


def write_wav(path: Path, x: np.ndarray) -> None:
    pcm = np.clip(x * 32767, -32768, 32767).astype(np.int16)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())


def narrate(segments: list[dict], out: Path) -> list[dict]:
    out.mkdir(parents=True, exist_ok=True)
    result = []
    for seg in segments:
        pieces, timing, t = [], [], 0.0
        for i, sentence in enumerate(sentences(seg["text"])):
            if i:
                pieces.append(np.zeros(int(RATE * SENTENCE_PAUSE), np.float32))
                t += SENTENCE_PAUSE
            x = say(spoken(sentence))
            pieces.append(x)
            timing.append({"text": sentence, "spoken": spoken(sentence), "start": round(t, 3),
                           "end": round(t + len(x) / RATE, 3)})
            t += len(x) / RATE
        audio = np.concatenate(pieces) if pieces else np.zeros(1, np.float32)
        write_wav(out / f"{seg['id']}.wav", audio)
        result.append({"id": seg["id"], "text": seg["text"], "duration": round(len(audio) / RATE, 3),
                       "sentences": timing})
    (out / "narration.json").write_text(json.dumps({"voice": VOICE, "speed": SPEED, "rate": RATE,
                                                    "segments": result}, indent=1), encoding="utf-8")
    return result


if __name__ == "__main__":
    if sys.argv[1] == "--say":
        x = np.concatenate([np.concatenate([say(spoken(s)), np.zeros(int(RATE * SENTENCE_PAUSE), np.float32)])
                            for s in sentences(sys.argv[2])])
        print(spoken(sys.argv[2]))
        write_wav(Path(sys.argv[3]), x)
    else:
        segs = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
        res = narrate(segs, Path(sys.argv[2]))
        print(f"{len(res)} lines, {sum(r['duration'] for r in res):.1f} s of narration")
