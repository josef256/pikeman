#!/usr/bin/env python3
"""
Rebuilds the animated water frames the user LOVED.
Source: /Volumes/LaCie/pikeman/water/animation/waterN.png  (20 frames, 256x121, from user's inpaint animation)
Output: Assets/Resources/Ships/../Water/w00..w19.png        (512x512 board-covering frames, drawn ONCE in-engine)

Technique (see memory/battleship.md):
- Compose each small frame into a 512x512 board-covering image (256 cap only applies to GENERATING, not our textures).
- IRREGULAR vertical placement of full-width rows, wrap-accumulated with a Hann window -> no periodic band, no hard seam.
- DENSER spacing (34-52px) -> even overlap -> uniformly CALM across the whole board (the look the user loved).
- FIXED layout (seed) across all 20 frames -> only the waves move, layout stays put (smooth animation).
- Subtle 2D low-frequency luminance for depth.
In-engine: DrawBoardBase draws the current frame ONCE (texcoords 0,0,1,1), index = floor(Time.time*12) % 20.
Loop-restart pop is still unsolved (ping-pong was rejected).
"""
import numpy as np, glob, os, re
from PIL import Image

SRC = "/Volumes/LaCie/pikeman/water/animationv2"   # 27 frames incl. user's transition frames for a seamless loop
OUT = "/Volumes/LaCie/pikeman/pikeman/Assets/Resources/Water"
S = 512

files = sorted(glob.glob(SRC + "/water*.png"),
               key=lambda p: int(re.search(r'(\d+)', os.path.basename(p)).group(1)))
frames = [np.array(Image.open(f).convert("RGBA")).astype(float) for f in files]
H, W = frames[0].shape[:2]

rng = np.random.RandomState(9)
positions = []; y = 0
while y < S + H:
    positions.append(dict(y=y % S, hflip=rng.rand() < 0.5, vflip=rng.rand() < 0.5, xroll=rng.randint(0, W)))
    y += rng.randint(34, 52)            # DENSER = even overlap = uniform calm
hann = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(H) / (H - 1))
yy, xx = np.mgrid[0:S, 0:S]
lf = (np.sin(2*np.pi*xx/S+0.4) + np.sin(2*np.pi*yy/S+1.1)
      + 0.6*np.sin(2*np.pi*2*yy/S+0.3) + 0.5*np.sin(2*np.pi*(xx+yy)/S))
lf = (lf / np.abs(lf).max())[..., None]

def wide_row(fr, p):
    b = fr.copy()
    if p['hflip']: b = b[:, ::-1]
    if p['vflip']: b = b[::-1, :]
    cols = int(np.ceil(S / W)) + 1
    wide = np.tile(b, (1, cols, 1)); wide = np.roll(wide, p['xroll'], axis=1)[:, :S]
    return wide

def build(fr):
    acc = np.zeros((S, S, 4)); wsum = np.zeros((S, 1))
    for p in positions:
        row = wide_row(fr, p)
        for k in range(H):
            yy2 = (p['y'] + k) % S
            acc[yy2] += row[k] * hann[k]; wsum[yy2, 0] += hann[k]
    out = acc / np.maximum(wsum[..., None], 1e-3)
    out[..., :3] = np.clip(out[..., :3] + lf * 11.0, 0, 255); out[..., 3] = 255
    return np.clip(out, 0, 255).astype(np.uint8)

os.makedirs(OUT, exist_ok=True)
for i, fr in enumerate(frames):
    Image.fromarray(build(fr), "RGBA").save(f"{OUT}/w{i:02d}.png")
print(f"wrote {len(frames)} frames to {OUT}")
