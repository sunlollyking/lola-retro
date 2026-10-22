# Dither blend: how much composite blends

Worked out 2026-10-22 for a question on the libretro forum (Lola Retro thread): which Dither blend
(`LR_DBLEND`) on the RGB preset matches the composite preset. Used in the README (Optional: dither blend on
RGB) and in the Parameters table of `shaders/lola-retro/README.md`. No code changed.

## What the two do

- Composite (`encode.inc`, `decode.inc`): Dither blend is not used. A 1-pixel stripe is a luma signal at
  half the pixel clock. The console's luma trap (Luma trap in the console, 0.75 in the TV presets) and the
  TV's trap (0.92 deep) take out a band around the colour subcarrier, so stripes near the subcarrier go and
  stripes far from it stay. The colour of the stripes always goes: the chroma band is narrow (0.6 MHz NTSC,
  0.8 PAL).
- Dither blend (`common.inc`, DBW): takes out the given share of the part that alternates from one console
  pixel to the next. On RGB from R, G and B alike (brightness and colour), on S-Video from luma.

So the two match only in brightness. Composite takes out all of the stripes' colour; Dither blend the same
share of both.

## Worked out

The gain of each filter at the stripe's frequency (half the pixel clock), from the filter values in the
code. All are Gaussians with `exp(-2 pi^2 (0.1325 / fc)^2 f^2)` (-3 dB at fc):

- console trap: `1 - trap * (G(f - fsc, 0.5) + G(f + fsc, 0.5))`
- TV luma, composite: `(1 + s) G(f, fcY) - s G(f, fcY / 2.5)`, s = 1.4 x Sharpness (0.35), fcY 3.3 NTSC,
  4.2 PAL, minus the trap `0.92 * hf * (G(f - fsc, fcC) + G(f + fsc, fcC))`, hf the luma gain at fsc
- RGB: `G(f, 6.5)`; S-Video luma: as composite luma with fcY 4.6 NTSC, 5.0 PAL, no trap

Dither blend matching composite is 1 - composite / RGB (or / S-Video):

| Mode (pixel clock) | NTSC, vs RGB | NTSC, vs S-Video | PAL, vs RGB | PAL, vs S-Video |
|---|---|---|---|---|
| 256 wide (5.37 MHz: NES, SNES, Master System, Mega Drive H32, PS1 256) | 0.47 | 0.54 | 0.03 | 0.15 |
| 320 wide (6.71 MHz: PS1 320, Mega Drive H40) | 0.95 | 0.96 | 0.42 | 0.51 |
| 368 wide (7.67 MHz: PS1 368) | 0.97 | | 0.79 | |

Rounded for the README: NTSC 1 and 0.5, PAL about 0.45 and 0.1. PAL blends less because its subcarrier,
4.43 MHz, is further from stripes at 2.7 and 3.3 MHz than NTSC's 3.58 MHz.

The same for other settings (NTSC, vs RGB): Luma trap in the console 0 (an early Mega Drive): 0.30 and
0.83; 1: 0.53 and 0.99. Comb filter on: 0.13 and 0.65 (the comb keeps vertical stripes in luma, they do
not change from line to line).

This is what the shader's composite does, not a measurement of a real TV. The beam and the mask act on
both pictures alike, so they are left out.
