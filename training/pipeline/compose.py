"""Composes one training video from a captured run (capture.mjs) and its narration (narrate.py).

    python -I compose.py <video-dir>                     writes <id>.mp4, <id>.srt, <id>.vtt, <id>.transcript.txt
    python -I compose.py <video-dir> --preview 12.5 40   writes preview-12.5.png, preview-40.png (no video)

Layout (1920x1080): a title bar, the HC360 screen in a 1600x900 window, and a caption band of its own below it, so the
captions never cover the screen they explain. The screen is HC360 itself, captured at 2x, with a cursor that moves to
what is clicked, a ripple where it clicks, an amber outline on what the narration names, a spotlight on what it
explains, and a zoom where a form's detail matters. Captions follow the narration word for word (they are made from the
same text), at most two lines of 42 characters, and are burned in with libass; the same cues are written as SRT and
WebVTT for players and learning systems that show their own. Narration is normalised to -16 LUFS (EBU R128).
"""

from __future__ import annotations

import json
import math
import os
import re
import subprocess
import sys
import wave
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H, FPS = 1920, 1080, 30
APP_X, APP_Y, APP_W, APP_H = 160, 56, 1600, 900
BAND_Y = APP_Y + APP_H  # 956: the caption band
RATE = 48000

NAVY = (11, 42, 58)
NAVY_DEEP = (7, 28, 40)
TEAL = (10, 124, 167)
TEAL_LIGHT = (122, 184, 215)
AMBER = (245, 166, 35)
WHITE = (255, 255, 255)
DIM = (176, 204, 219)
GREEN = (46, 160, 67)

FONT_DIR = Path("/usr/share/fonts/opentype/inter")


def font(weight: str, size: int, display: bool = False) -> ImageFont.FreeTypeFont:
    name = ("InterDisplay-" if display else "Inter-") + weight + ".otf"
    return ImageFont.truetype(str(FONT_DIR / name), size)


def ease(x: float) -> float:
    x = min(1.0, max(0.0, x))
    return 4 * x * x * x if x < 0.5 else 1 - (-2 * x + 2) ** 3 / 2


# ------------------------------------------------------------------------------------------------ static artwork

def brand_mark(size: int) -> Image.Image:
    """The HC360 mark (src/web/apps/staff/src/shell/BrandMark.tsx), drawn at 4x and reduced."""
    s = size * 4
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    k = s / 64
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=14 * k, fill=TEAL + (255,))
    d.polygon([(27 * k, 14 * k), (37 * k, 14 * k), (37 * k, 27 * k), (50 * k, 27 * k), (50 * k, 37 * k), (37 * k, 37 * k),
               (37 * k, 50 * k), (27 * k, 50 * k), (27 * k, 37 * k), (14 * k, 37 * k), (14 * k, 27 * k), (27 * k, 27 * k)],
              fill=WHITE + (255,))
    d.ellipse([41 * k, 41 * k, 55 * k, 55 * k], fill=TEAL_LIGHT + (255,))
    return im.resize((size, size), Image.LANCZOS)


def cursor_image() -> Image.Image:
    k = 5  # drawn at 5x, reduced to 1.25x of a 22 px arrow
    pts = [(0, 0), (0, 21), (5, 16), (9, 25), (12, 24), (8, 15), (15, 15)]
    big = Image.new("RGBA", (24 * k, 30 * k), (0, 0, 0, 0))
    d = ImageDraw.Draw(big)
    poly = [((x + 2) * k, (y + 2) * k) for x, y in pts]
    shadow = Image.new("RGBA", big.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).polygon([(x + 2 * k, y + 2 * k) for x, y in poly], fill=(0, 0, 0, 110))
    shadow = shadow.filter(ImageFilter.GaussianBlur(2 * k))
    big = Image.alpha_composite(big, shadow)
    d = ImageDraw.Draw(big)
    d.polygon(poly, fill=(255, 255, 255, 255), outline=(20, 20, 20, 255), width=int(1.6 * k))
    return big.resize((int(24 * 1.25), int(30 * 1.25)), Image.LANCZOS)


CURSOR = None


def chrome(top_left: str, top_right: str) -> Image.Image:
    """The frame around the screen: title bar, side margins, caption band, and the window's shadow."""
    im = Image.new("RGB", (W, H), NAVY_DEEP)
    grad = np.linspace(0, 1, H, dtype=np.float32)[:, None, None]
    base = np.array(NAVY_DEEP, np.float32) * (1 - grad * 0.35) + np.array(NAVY, np.float32) * (grad * 0.35)
    im = Image.fromarray(np.broadcast_to(base, (H, W, 3)).astype(np.uint8))
    shadow = Image.new("L", (W, H), 0)
    ImageDraw.Draw(shadow).rounded_rectangle([APP_X - 2, APP_Y + 6, APP_X + APP_W + 2, APP_Y + APP_H + 10], 16, fill=170)
    shadow = shadow.filter(ImageFilter.GaussianBlur(14))
    im.paste((0, 0, 0), (0, 0), shadow)
    d = ImageDraw.Draw(im)
    d.rectangle([0, BAND_Y, W, H], fill=NAVY_DEEP)
    mark = brand_mark(30)
    im.paste(mark, (APP_X, 13), mark)
    d.text((APP_X + 42, 28), top_left, font=font("SemiBold", 21), fill=WHITE, anchor="lm")
    d.text((APP_X + APP_W, 28), top_right, font=font("Medium", 20), fill=DIM, anchor="rm")
    return im


def app_mask() -> Image.Image:
    m = Image.new("L", (APP_W * 2, APP_H * 2), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, APP_W * 2 - 1, APP_H * 2 - 1], 24, fill=255)
    return m.resize((APP_W, APP_H), Image.LANCZOS)


MASK = None


def wrap(text: str, f: ImageFont.FreeTypeFont, width: int) -> list[str]:
    lines, line = [], ""
    for word in text.split():
        test = (line + " " + word).strip()
        if f.getlength(test) <= width or not line:
            line = test
        else:
            lines.append(line)
            line = word
    if line:
        lines.append(line)
    return lines


def card(kind: str, video: dict, state: dict | None = None) -> Image.Image:
    """A full-screen card inside the frame: title, objectives, chapter, recap, check yourself, end."""
    state = state or {}
    im = chrome("HC360 training", video.get("roleLabel", ""))
    panel = Image.new("RGB", (APP_W, APP_H), NAVY)
    g = np.linspace(0, 1, APP_W, dtype=np.float32)[None, :, None]
    arr = np.array(NAVY, np.float32) * (1 - g * 0.5) + np.array((9, 70, 96), np.float32) * (g * 0.5)
    panel = Image.fromarray(np.broadcast_to(arr, (APP_H, APP_W, 3)).astype(np.uint8))
    d = ImageDraw.Draw(panel)
    x0, y = 110, 110
    if kind == "title":
        mark = brand_mark(84)
        panel.paste(mark, (x0, y), mark)
        d.text((x0 + 108, y + 18), "HC360 training", font=font("SemiBold", 30), fill=TEAL_LIGHT)
        d.text((x0 + 108, y + 56), video.get("hospital", "Nana Hospitals"), font=font("Regular", 24), fill=DIM)
        y += 170
        for line in wrap(video["title"], font("Bold", 64, True), APP_W - 2 * x0):
            d.text((x0, y), line, font=font("Bold", 64, True), fill=WHITE)
            y += 78
        y += 18
        chips = [video.get("roleLabel", "")] + ([f"Go-live wave {video['wave']}"] if video.get("wave") is not None else [])
        x = x0
        for chip in [c for c in chips if c]:
            f = font("SemiBold", 24)
            wdt = int(f.getlength(chip)) + 36
            d.rounded_rectangle([x, y, x + wdt, y + 46], 23, fill=TEAL)
            d.text((x + 18, y + 23), chip, font=f, fill=WHITE, anchor="lm")
            x += wdt + 14
        y += 80
        for line in wrap(video.get("audience", ""), font("Regular", 28), APP_W - 2 * x0):
            d.text((x0, y), line, font=font("Regular", 28), fill=DIM)
            y += 38
        foot = video.get("footer", "")
        d.text((x0, APP_H - 70), foot, font=font("Regular", 19), fill=(140, 170, 186))
    elif kind in ("objectives", "recap"):
        heading = "In this video you will learn to" if kind == "objectives" else "Remember"
        items = video["objectives"] if kind == "objectives" else video["recap"]
        d.text((x0, y), heading, font=font("Bold", 52, True), fill=WHITE)
        y += 110
        f = font("Medium", 34)
        for i, item in enumerate(items, 1):
            d.ellipse([x0, y - 2, x0 + 52, y + 50], fill=TEAL if kind == "objectives" else AMBER)
            d.text((x0 + 26, y + 24), str(i), font=font("Bold", 28), fill=WHITE, anchor="mm")
            for j, line in enumerate(wrap(item, f, APP_W - 2 * x0 - 80)):
                d.text((x0 + 80, y + 4 + j * 46), line, font=f, fill=WHITE)
            y += 46 * max(1, len(wrap(item, f, APP_W - 2 * x0 - 80))) + 34
    elif kind == "check":
        n, q, a = state["n"], state["q"], state["a"]
        d.text((x0, y), f"Check yourself  ·  {n} of {state['of']}", font=font("Bold", 48, True), fill=WHITE)
        y += 100
        for line in wrap(q, font("SemiBold", 40), APP_W - 2 * x0):
            d.text((x0, y), line, font=font("SemiBold", 40), fill=WHITE)
            y += 54
        y += 40
        if state.get("revealed"):
            lines = wrap(a, font("Medium", 34), APP_W - 2 * x0 - 60)
            d.rounded_rectangle([x0, y, APP_W - x0, y + 40 + 46 * len(lines) + 20], 16, fill=(14, 92, 52))
            d.text((x0 + 30, y + 20), "Answer", font=font("Bold", 24), fill=(160, 230, 180))
            for j, line in enumerate(lines):
                d.text((x0 + 30, y + 56 + j * 46), line, font=font("Medium", 34), fill=WHITE)
        else:
            d.text((x0, y), "Pause the video and answer.", font=font("Medium", 32), fill=AMBER)
    elif kind == "chapter":
        d.text((x0, 300), state["part"], font=font("SemiBold", 34), fill=TEAL_LIGHT)
        for j, line in enumerate(wrap(state["title"], font("Bold", 64, True), APP_W - 2 * x0)):
            d.text((x0, 350 + j * 78), line, font=font("Bold", 64, True), fill=WHITE)
    elif kind == "end":
        d.text((x0, y), "Well done", font=font("Bold", 56, True), fill=WHITE)
        y += 110
        for label, value in video.get("endLines", []):
            d.text((x0, y), label, font=font("SemiBold", 26), fill=TEAL_LIGHT)
            y += 40
            for line in wrap(value, font("Medium", 32), APP_W - 2 * x0):
                d.text((x0, y), line, font=font("Medium", 32), fill=WHITE)
                y += 44
            y += 30
        d.text((x0, APP_H - 70), video.get("footer", ""), font=font("Regular", 19), fill=(140, 170, 186))
    im.paste(panel, (APP_X, APP_Y), MASK)
    return im


# ------------------------------------------------------------------------------------------------ screens

class Shots:
    """Screenshots, and their views through the camera, cached."""

    def __init__(self, root: Path):
        self.root = root
        self.images: dict[str, Image.Image] = {}
        self.views: dict[tuple, np.ndarray] = {}

    def image(self, name: str) -> Image.Image:
        if name not in self.images:
            self.images[name] = Image.open(self.root / name).convert("RGB")
            if len(self.images) > 24:  # keep memory bounded on long videos
                self.images.pop(next(iter(self.images)))
        return self.images[name]

    def view(self, name: str, cam: tuple, quality: bool = True, spotlight: tuple | None = None) -> np.ndarray:
        key = (name, tuple(round(v, 1) for v in cam), quality, spotlight)
        if key in self.views:
            return self.views[key]
        im = self.image(name)
        x, y, w, h = cam
        out = im.resize((APP_W, APP_H), Image.LANCZOS if quality else Image.BILINEAR, box=(x, y, x + w, y + h))
        arr = np.asarray(out, dtype=np.uint8)
        if spotlight:
            sx0, sy0, sx1, sy1 = (int(v) for v in spotlight)
            dim = (arr.astype(np.float32) * 0.5).astype(np.uint8)
            dim[max(0, sy0):max(0, sy1), max(0, sx0):max(0, sx1)] = arr[max(0, sy0):max(0, sy1), max(0, sx0):max(0, sx1)]
            arr = dim
        if quality:
            self.views[key] = arr
            if len(self.views) > 60:
                self.views.pop(next(iter(self.views)))
        return arr


@dataclass
class Step:
    data: dict
    t0: float = 0.0
    dur: float = 0.0
    say_at: float = 0.0
    act_at: float = 0.0
    after_at: float = 0.0
    move: tuple = (0.0, 0.0)  # cursor move start, length
    cam: tuple = (0, 0, 0, 0)
    prev_cam: tuple = (0, 0, 0, 0)
    cursor_from: tuple | None = None
    cursor_to: tuple | None = None
    typing_len: float = 0.0


@dataclass
class Segment:
    kind: str  # card | step
    t0: float
    dur: float
    image: Image.Image | None = None
    image2: Image.Image | None = None  # a card's second state (an answer revealed)
    switch_at: float = 0.0
    step: Step | None = None
    chapter: str = ""
    fade_in: float = 0.0
    narration: list = field(default_factory=list)  # (segment id, absolute start)


class Video:
    def __init__(self, root: Path):
        global CURSOR, MASK
        CURSOR = CURSOR or cursor_image()
        MASK = MASK or app_mask()
        self.root = root
        self.cap = json.loads((root / "capture.json").read_text(encoding="utf-8"))
        nar = json.loads((root / "audio" / "narration.json").read_text(encoding="utf-8"))
        self.nar = {s["id"]: s for s in nar["segments"]}
        self.shots = Shots(root)
        vp = self.cap["viewport"]
        self.sw, self.sh = vp["width"] * vp["dpr"], vp["height"] * vp["dpr"]
        self.dpr = vp["dpr"]
        self.full = (0.0, 0.0, float(self.sw), float(self.sh))
        self.segments: list[Segment] = []
        self.chapters: list[tuple[float, str]] = []
        self.chrome_cache: dict[str, Image.Image] = {}
        self.build()

    # -------------------------------------------------------------------------------------------- timeline

    def dur(self, sid: str) -> float:
        return self.nar[sid]["duration"] if sid in self.nar else 0.0

    def box(self, b) -> tuple | None:
        if not b:
            return None
        x, y, w, h = b
        return (x * self.dpr, y * self.dpr, w * self.dpr, h * self.dpr)

    def camera_for(self, step: dict) -> tuple:
        zoom = step.get("zoom")
        if not zoom:
            return self.full
        z = float(zoom if not isinstance(zoom, bool) else 1.6)
        boxes = [self.box(step.get(k)) for k in ("target", "focus", "result")]
        boxes = [b for b in boxes if b]
        if not boxes:
            return self.full
        x0 = min(b[0] for b in boxes)
        y0 = min(b[1] for b in boxes)
        x1 = max(b[0] + b[2] for b in boxes)
        y1 = max(b[1] + b[3] for b in boxes)
        w = max(self.sw / z, (x1 - x0) * 1.35, (y1 - y0) * 1.35 * self.sw / self.sh)
        w = min(w, self.sw)
        h = w * self.sh / self.sw
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        x = min(max(0.0, cx - w / 2), self.sw - w)
        y = min(max(0.0, cy - h / 2), self.sh - h)
        return (x, y, w, h)

    def to_canvas(self, cam: tuple, p: tuple) -> tuple:
        x, y, w, h = cam
        return (APP_X + (p[0] - x) * APP_W / w, APP_Y + (p[1] - y) * APP_H / h)

    def add_card(self, kind: str, sid: str, state: dict | None = None, extra: float = 1.0, reveal: str | None = None):
        t0 = self.t
        img = card(kind, self.cap, state)
        seg = Segment("card", t0, 0, image=img, fade_in=0.4, chapter=self.chapter)
        start = 0.55
        seg.narration.append((sid, t0 + start))
        d = start + self.dur(sid) + extra
        if reveal:
            seg.image2 = card(kind, self.cap, {**(state or {}), "revealed": True})
            seg.switch_at = d
            seg.narration.append((reveal, t0 + d + 0.3))
            d += 0.3 + self.dur(reveal) + 1.6
        seg.dur = d
        self.segments.append(seg)
        self.t += d

    def build(self):
        self.t = 0.0
        self.chapter = ""
        c = self.cap
        self.chapters.append((0.0, "Introduction"))
        self.add_card("title", "title", extra=0.9)
        self.add_card("objectives", "objectives", extra=0.9)
        cursor = (APP_X + APP_W * 0.5, APP_Y + APP_H * 0.55)
        prev_cam = self.full
        part = 0
        for beat in c["beats"]:
            if beat["type"] == "chapter":
                part += 1
                self.chapter = beat["title"]
                self.chapters.append((self.t, beat["title"]))
                self.add_card("chapter", beat["id"], {"part": f"Part {part}", "title": beat["title"]}, extra=0.5)
                continue
            st = Step(beat)
            cam = self.camera_for(beat)
            st.cam, st.prev_cam = cam, prev_cam
            target = self.box(beat.get("target")) or self.box(beat.get("focus"))
            lead = 0.3 if cam == prev_cam else 0.75
            ds = self.dur(beat["id"] + ".say")
            move_len = 0.0
            if target:
                p = self.to_canvas(cam, aim(target, beat.get("action")))
                dist = math.dist(cursor, p)
                move_len = min(0.9, max(0.35, dist / 1500)) if dist > 4 else 0.0
                st.cursor_from, st.cursor_to = cursor, p
                st.move = (lead, move_len)
                cursor = p
            else:
                st.cursor_from = st.cursor_to = cursor
            st.say_at = lead
            say_end = lead + max(ds, move_len + 0.35)
            action = beat.get("action", "none")
            st.act_at = say_end + 0.15
            if action in ("click", "nav", "select", "press", "check"):
                act_len = 0.5
            elif action == "fill":
                st.typing_len = min(1.8, max(0.5, 0.055 * len(beat.get("text", ""))))
                act_len = st.typing_len + 0.15
            else:
                act_len = 0.0
                st.act_at = say_end
            st.after_at = st.act_at + act_len
            da = self.dur(beat["id"] + ".after")
            st.dur = st.after_at + (da + 0.45 if da else 0.4)
            seg = Segment("step", self.t, st.dur, step=st, chapter=self.chapter)
            if ds:
                seg.narration.append((beat["id"] + ".say", self.t + st.say_at))
            if da:
                seg.narration.append((beat["id"] + ".after", self.t + st.after_at + 0.1))
            st.t0 = self.t
            self.segments.append(seg)
            self.t += st.dur
            prev_cam = cam
        self.chapters.append((self.t, "Recap"))
        self.chapter = "Recap"
        self.add_card("recap", "recap", extra=1.0)
        for i, q in enumerate(c.get("check", []), 1):
            if i == 1:
                self.chapters.append((self.t, "Check yourself"))
            self.chapter = "Check yourself"
            self.add_card("check", f"check{i}.q", {"n": i, "of": len(c["check"]), "q": q["q"], "a": q["a"]},
                          extra=3.0, reveal=f"check{i}.a")
        self.chapter = ""
        self.add_card("end", "end", extra=1.5)
        self.total = self.t

    # -------------------------------------------------------------------------------------------- frames

    def frame_chrome(self, chapter: str) -> Image.Image:
        if chapter not in self.chrome_cache:
            self.chrome_cache[chapter] = chrome(f"HC360 training  ·  {self.cap.get('roleLabel', '')}", chapter)
        return self.chrome_cache[chapter]

    def step_frame(self, seg: Segment, t: float, quality: bool = True) -> tuple:
        """The frame of a step at local time t, and a key that is equal for frames that look the same."""
        st = seg.step
        b = st.data
        cam_k = ease((t - 0.0) / 0.6) if st.cam != st.prev_cam else 1.0
        cam = tuple(p + (q - p) * cam_k for p, q in zip(st.prev_cam, st.cam))
        moving_cam = cam_k < 1.0
        before, after = b.get("shotBefore"), b.get("shotAfter") or b.get("shotBefore")
        typing = b.get("typing") or []
        layer, blend, other = before, 0.0, None
        if t >= st.act_at:
            if b.get("action") == "fill" and typing:
                k = (t - st.act_at) / max(0.01, st.typing_len)
                layer = typing[min(len(typing) - 1, int(k * len(typing)))] if k < 1 else after
            elif b.get("action") in ("click", "nav", "select", "press", "check"):
                k = (t - st.act_at - 0.12) / 0.3
                if k >= 1:
                    layer = after
                elif k > 0 and after != before:
                    other, blend = after, ease(k)
            else:
                layer = after if t >= st.after_at else before
        spot = None
        if b.get("action") == "show" and b.get("focus") and not moving_cam:
            fb = self.box(b["focus"])
            p0 = self.to_canvas(cam, (fb[0] - 6 * self.dpr, fb[1] - 6 * self.dpr))
            p1 = self.to_canvas(cam, (fb[0] + fb[2] + 6 * self.dpr, fb[1] + fb[3] + 6 * self.dpr))
            spot = (p0[0] - APP_X, p0[1] - APP_Y, p1[0] - APP_X, p1[1] - APP_Y)
        q = quality and not moving_cam
        base = self.shots.view(layer, cam, q, spot)
        if other:
            base = (base.astype(np.float32) * (1 - blend) + self.shots.view(other, cam, q).astype(np.float32) * blend).astype(np.uint8)
        # Cursor
        mv0, mvl = st.move
        if st.cursor_to is None:
            cur = None
        elif mvl and t < mv0 + mvl:
            k = ease((t - mv0) / mvl) if t > mv0 else 0.0
            cur = tuple(a + (c - a) * k for a, c in zip(st.cursor_from, st.cursor_to))
        else:
            cur = st.cursor_to
        if cur and moving_cam and b.get("target"):
            tb = self.box(b.get("target"))
            cur = self.to_canvas(cam, aim(tb, b.get("action"))) if t >= mv0 + mvl else cur
        ripple = None
        if b.get("action") in ("click", "nav", "select", "check") and st.act_at <= t < st.act_at + 0.45:
            ripple = (t - st.act_at) / 0.45
        ring, ring_alpha = None, 0.0
        if b.get("action") == "show" and b.get("focus"):
            ring, ring_alpha = self.box(b["focus"]), min(1.0, max(0.0, (t - 0.2) / 0.3))
        elif b.get("target") and t < st.after_at:
            ring, ring_alpha = self.box(b["target"]), min(1.0, max(0.0, (t - mv0 - mvl + 0.1) / 0.25))
        elif b.get("result") and t >= st.after_at:
            ring, ring_alpha = self.box(b["result"]), min(1.0, max(0.0, (t - st.after_at - 0.1) / 0.3))
        key = None
        if not moving_cam and not other and ripple is None and (not mvl or t >= mv0 + mvl or t < mv0):
            key = ("s", id(seg), layer, cam, spot, cur, ring, round(ring_alpha, 2))
        return base, cam, cur, ripple, ring, ring_alpha, key

    def compose(self, seg: Segment, t: float, quality: bool = True) -> tuple:
        if seg.kind == "card":
            img = seg.image2 if (seg.image2 is not None and t >= seg.switch_at) else seg.image
            return np.asarray(img, dtype=np.uint8), ("c", id(img))
        base, cam, cur, ripple, ring, ring_alpha, key = self.step_frame(seg, t, quality)
        if key is not None and key == getattr(self, "_last_key", None):
            return self._last_frame, key
        im = self.frame_chrome(seg.chapter).copy()
        im.paste(Image.fromarray(base), (APP_X, APP_Y), MASK)
        if ring is not None and ring_alpha > 0:
            p0 = self.to_canvas(cam, (ring[0], ring[1]))
            p1 = self.to_canvas(cam, (ring[0] + ring[2], ring[1] + ring[3]))
            draw_ring(im, p0, p1, ring_alpha)
        if ripple is not None and cur:
            draw_ripple(im, cur, ripple)
        if cur:
            im.paste(CURSOR, (int(cur[0]) - 3, int(cur[1]) - 3), CURSOR)
        frame = np.asarray(im, dtype=np.uint8)
        if key is not None:
            self._last_key, self._last_frame = key, frame
        return frame, key

    def at(self, t: float, quality: bool = True) -> tuple:
        for i, seg in enumerate(self.segments):
            if seg.t0 <= t < seg.t0 + seg.dur or i == len(self.segments) - 1:
                local = t - seg.t0
                frame, key = self.compose(seg, local, quality)
                if seg.fade_in and local < seg.fade_in and i > 0:
                    prev = self.segments[i - 1]
                    pf, _ = self.compose(prev, prev.dur - 1 / FPS, quality)
                    k = ease(local / seg.fade_in)
                    frame = (pf.astype(np.float32) * (1 - k) + frame.astype(np.float32) * k).astype(np.uint8)
                    key = None
                if t < 0.5:
                    frame = (frame.astype(np.float32) * (t / 0.5)).astype(np.uint8)
                    key = None
                if t > self.total - 0.8:
                    frame = (frame.astype(np.float32) * max(0.0, (self.total - t) / 0.8)).astype(np.uint8)
                    key = None
                return frame, key
        raise ValueError(t)

    # -------------------------------------------------------------------------------------------- audio and captions

    def audio(self, path: Path) -> None:
        n = int((self.total + 0.5) * RATE)
        track = np.zeros(n, np.float32)
        for seg in self.segments:
            for sid, start in seg.narration:
                with wave.open(str(self.root / "audio" / f"{sid}.wav")) as w:
                    x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(np.float32) / 32768
                i = int(start * RATE)
                track[i:i + len(x)] += x[: max(0, n - i)]
        pcm = np.clip(track * 32767, -32768, 32767).astype(np.int16)
        with wave.open(str(path), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(RATE)
            w.writeframes(pcm.tobytes())

    def cues(self) -> list[tuple[float, float, str]]:
        cues = []
        for seg in self.segments:
            for sid, start in seg.narration:
                for s in self.nar[sid]["sentences"]:
                    for a, b, text in split_sentence(s):
                        cues.append([start + a, start + b, text])
        # Linger a little after each cue and keep at least 0.9 s on screen. A cue followed within 0.6 s by the next
        # stays until it (2 frames apart), so the band does not blink empty between two sentences.
        gap = 2 / FPS
        for i, c in enumerate(cues):
            nxt = cues[i + 1][0] if i + 1 < len(cues) else None
            end = max(c[1] + 0.25, c[0] + 0.9)
            if nxt is not None:
                if nxt - end < 0.6:
                    end = nxt - gap
                end = min(end, nxt - gap)
            c[1] = min(max(end, c[0] + 0.3), c[0] + 7.0)  # at most 7 s on screen
        return [(a, b, t) for a, b, t in cues]


def aim(box: tuple, action: str | None) -> tuple:
    """Where the cursor's tip goes on a box: to point at something, its lower-right edge, so the arrow does not cover
    it (a small icon would vanish under it); to click, a little below its middle, as a hand would."""
    x, y, w, h = box
    if action == "show":
        return (x + w * 0.88, y + h * 0.9)
    return (x + w * 0.5, y + h * 0.62)


def split_sentence(s: dict) -> list[tuple[float, float, str]]:
    """A sentence as caption cues of at most two lines of 42 characters, timed in proportion to what is spoken."""
    text, start, end = s["text"], s["start"], s["end"]
    cues = cue_texts(text)
    if len(cues) == 1 and end - start > 6.5:  # a long, slow sentence: two cues, so neither stays over 7 s
        halves = best_split(text)
        if halves:
            cues = cue_texts(halves[0]) + cue_texts(halves[1])
    weights = [max(1, len(c.replace("\n", " "))) for c in cues]
    total = sum(weights)
    out, t = [], start
    for c, wgt in zip(cues, weights):
        d = (end - start) * wgt / total
        out.append((t, t + d, c))
        t += d
    return out


CONJUNCTIONS = ("and", "but", "so", "then", "which", "when", "where", "or", "because", "until", "before", "after", "if",
                "while", "with", "for", "to")


def best_split(text: str, limit: int | None = None) -> tuple[str, str] | None:
    """The best place to cut a text in two: near the middle, after punctuation or before a conjunction."""
    words = text.split()
    best = None
    for i in range(1, len(words)):
        a, b = " ".join(words[:i]), " ".join(words[i:])
        if limit and (len(a) > limit or len(b) > limit):
            continue
        score = abs(len(a) - len(b))
        if a.endswith((",", ";", ":", "—")):
            score -= 25
        elif words[i].lower() in CONJUNCTIONS:
            score -= 12
        if best is None or score < best[0]:
            best = (score, a, b)
    return (best[1], best[2]) if best else None


def cue_texts(text: str) -> list[str]:
    """Cue texts for a sentence: each at most two lines of at most 42 characters (cut in two until it fits)."""
    if len(text) <= 42:
        return [text]
    lines = best_split(text, 42) if len(text) <= 85 else None
    if lines:
        return [f"{lines[0]}\n{lines[1]}"]
    parts = best_split(text)
    if parts is None:  # one word longer than a line: nothing to cut
        return [text]
    return cue_texts(parts[0]) + cue_texts(parts[1])


def draw_ring(im: Image.Image, p0: tuple, p1: tuple, alpha: float) -> None:
    pad = 7
    x0, y0, x1, y1 = p0[0] - pad, p0[1] - pad, p1[0] + pad, p1[1] + pad
    m = 14
    w, h = int(x1 - x0) + 2 * m, int(y1 - y0) + 2 * m
    if w <= 0 or h <= 0 or w > 4000 or h > 4000:
        return
    patch = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(patch)
    d.rounded_rectangle([m - 5, m - 5, w - m + 5, h - m + 5], 12, outline=AMBER + (int(70 * alpha),), width=9)
    d.rounded_rectangle([m, m, w - m, h - m], 9, outline=AMBER + (int(255 * alpha),), width=4)
    im.paste(patch, (int(x0) - m, int(y0) - m), patch)


def draw_ripple(im: Image.Image, c: tuple, k: float) -> None:
    r = 8 + 30 * ease(k)
    a = int(220 * (1 - k))
    s = 90
    patch = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(patch)
    d.ellipse([s / 2 - r, s / 2 - r, s / 2 + r, s / 2 + r], outline=AMBER + (a,), width=5)
    d.ellipse([s / 2 - 7, s / 2 - 7, s / 2 + 7, s / 2 + 7], fill=AMBER + (int(a * 0.8),))
    im.paste(patch, (int(c[0] - s / 2), int(c[1] - s / 2)), patch)


def ts(t: float, sep: str) -> str:
    ms = int(round(t * 1000))
    return f"{ms // 3600000:02d}:{ms // 60000 % 60:02d}:{ms // 1000 % 60:02d}{sep}{ms % 1000:03d}"


def write_captions(cues, base: Path) -> None:
    srt, vtt = [], ["WEBVTT", ""]
    for i, (a, b, text) in enumerate(cues, 1):
        srt += [str(i), f"{ts(a, ',')} --> {ts(b, ',')}", text, ""]
        vtt += [f"{ts(a, '.')} --> {ts(b, '.')}", text, ""]
    base.with_suffix(".srt").write_text("\n".join(srt), encoding="utf-8")
    base.with_suffix(".vtt").write_text("\n".join(vtt), encoding="utf-8")
    ass = ["[Script Info]", "ScriptType: v4.00+", f"PlayResX: {W}", f"PlayResY: {H}", "WrapStyle: 2", "",
           "[V4+ Styles]",
           "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, "
           "Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, "
           "MarginR, MarginV, Encoding",
           "Style: Caption,Inter Medium,44,&H00FFFFFF,&H00FFFFFF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,0,0,2,60,60,12,1",
           "", "[Events]", "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text"]
    for a, b, text in cues:
        t = text.replace("\n", "\\N")
        margin = 12 if "\n" in text else 38  # one line sits in the middle of the band, two fill it
        ass.append(f"Dialogue: 0,{ts(a, '.')[1:-1]},{ts(b, '.')[1:-1]},Caption,,0,0,{margin},,{t}")
    base.with_suffix(".ass").write_text("\n".join(ass) + "\n", encoding="utf-8")


def loudnorm(src: Path, dst: Path) -> dict:
    first = subprocess.run(["ffmpeg", "-hide_banner", "-nostats", "-i", str(src), "-af",
                            "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"],
                           capture_output=True, text=True, check=True).stderr
    m = json.loads(first[first.rindex("{"): first.rindex("}") + 1])
    flt = (f"loudnorm=I=-16:TP=-1.5:LRA=11:measured_I={m['input_i']}:measured_TP={m['input_tp']}:"
           f"measured_LRA={m['input_lra']}:measured_thresh={m['input_thresh']}:offset={m['target_offset']}:linear=true")
    subprocess.run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-i", str(src), "-af", flt, "-ar", str(RATE),
                    str(dst)], check=True)
    return m


def main():
    root = Path(sys.argv[1])
    v = Video(root)
    vid = v.cap["id"]
    if len(sys.argv) > 2 and sys.argv[2] == "--preview":
        for t in sys.argv[3:]:
            frame, _ = v.at(float(t))
            Image.fromarray(frame).save(root / f"preview-{t}.png")
        print(f"{v.total:.1f} s")
        return
    cues = v.cues()
    base = root / vid
    write_captions(cues, base)
    (root / f"{vid}.transcript.txt").write_text(
        "\n\n".join(s["text"] for seg in v.segments for sid, _ in seg.narration for s in [v.nar[sid]]), encoding="utf-8")
    v.audio(root / "narration-raw.wav")
    loud = loudnorm(root / "narration-raw.wav", root / "narration.wav")
    meta = [";FFMETADATA1", f"title={v.cap['title']}", f"artist=HyFib HealthCare360 training",
            f"comment={v.cap.get('footer', '')}"]
    for i, (t, title) in enumerate(v.chapters):
        end = v.chapters[i + 1][0] if i + 1 < len(v.chapters) else v.total
        if end - t < 0.5:
            continue
        meta += ["[CHAPTER]", "TIMEBASE=1/1000", f"START={int(t * 1000)}", f"END={int(end * 1000)}", f"title={title}"]
    (root / "chapters.txt").write_text("\n".join(meta) + "\n", encoding="utf-8")
    ass_path = str(base.with_suffix(".ass")).replace(":", "\\:").replace("'", "\\'")
    cmd = ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
           "-r", str(FPS), "-i", "-", "-i", str(root / "narration.wav"), "-i", str(root / "chapters.txt"),
           "-map", "0:v", "-map", "1:a", "-map_metadata", "2", "-map_chapters", "2",
           "-vf", f"ass='{ass_path}':fontsdir=/usr/share/fonts/opentype/inter",
           "-c:v", "libx264", "-preset", "medium", "-crf", os.environ.get("HC360_CRF", "22"), "-tune", "stillimage", "-pix_fmt", "yuv420p",
           "-g", "150", "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-movflags", "+faststart", "-shortest",
           str(base.with_suffix(".mp4"))]
    enc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    frames = int(math.ceil(v.total * FPS))
    last_key, last_bytes = None, None
    for i in range(frames):
        frame, key = v.at(i / FPS)
        if key is not None and key == last_key:
            enc.stdin.write(last_bytes)
            continue
        last_bytes = frame.tobytes()
        last_key = key
        enc.stdin.write(last_bytes)
    enc.stdin.close()
    if enc.wait() != 0:
        raise SystemExit("ffmpeg failed")
    placements = [[sid, round(start, 3), v.nar[sid]["duration"]] for seg in v.segments for sid, start in seg.narration]
    summary = {"id": vid, "seconds": round(v.total, 1), "cues": len(cues), "chapters": v.chapters,
               "placements": placements, "loudnessMeasuredBefore": {k: loud[k] for k in ("input_i", "input_tp")}}
    (root / "compose.json").write_text(json.dumps(summary, indent=1), encoding="utf-8")
    print(f"{vid}: {v.total:.1f} s, {len(cues)} captions, {frames} frames")


if __name__ == "__main__":
    main()
