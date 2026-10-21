# Frames over 300 lines (LR_HIRES), issue #9

Worked out 2026-10-22. Used in `common.inc` (`upscaleOf`, `getTiming`, `maskType`, `phosphorSet`,
`decoderType`), `beam.slang` (the PC monitor's pixel reads) and the README (Parameters, Systems, PC monitor).
Related: [480i-field-timing.md](480i-field-timing.md), [saturn-cores.md](saturn-cores.md).

## The problem (issue #9, Tatsuya79)

- A frame's size cannot tell a console's own 480i from a 240-line game at 2x internal resolution: both are
  480 lines. Auto (Console timing 0) reads frames up to 576 lines as interlaced (with the PlayStation 2's
  timing) and taller ones as upscaled; a timing set by hand reads anything over 360 lines (1.5 x 240) as
  upscaled. Beetle Saturn never upscales, but with Console timing 4 (needed for its 320x224 games) its 448
  and 480-line frames were read as 224/240 lines at 2x, the two fields averaged into one line.
- px68k (X68000): its high-res screens were read as 3x upscaled 200-line frames even on auto. See
  [x68000-video.md](x68000-video.md) for what px68k really sends.

## The parameter

| Value | Frames with more than 300 native lines | Upscaled frames found |
|---|---|---|
| 0 (default) | the guess above, unchanged | 3x and up on auto; 2x and up by hand |
| 1 | the console's own 480i/576i on every TV timing (0-5, 8), with the timing as set | whole multiples of a 398-581-line mode (960: 480i at 2x, 1152: 576i at 2x) |
| 2 | drawn on the PC monitor model (a second screen), see below | the same; any other frame is native (1024x768) |

Amiga, VGA and 24 kHz timings (6, 7, 9) are never affected.

## Value 2: the computer monitor

As people did with a second screen (an Atari ST owner had a high-res monitor for the desktop and a TV for
games), every frame over 300 lines goes to the PC monitor model: VGA timing (scan mode 1), the tube in mm
from the Monitor values, RGB, sRGB phosphors, a dot mask at the Dot pitch (a mask turned off stays off).
`Timing` carries the switch (`monitor`) and the cable in use (`cable`); no pass reads Cable or Mask
directly. A compromise, off by default: one screen stands in for two and switches by itself, and the rest
of the preset stays (glass, curvature, overscan, misconvergence, white point, glow, the TV's knobs).

Found while testing:

- With the 480-line base, the fallback for a frame that is no whole multiple (`floor(h / 480 + 0.5)`) read
  1024x768 as 512x384 at 2x, averaging row pairs: text smeared. On the computer monitor such frames are now
  native.
- The TV presets decode 1280 samples per line (ENC_SAMPLES). The PC monitor's beam pass read each pixel at its
  centre; at 1024 wide (1.25 samples per pixel) that falls between two samples and mixed up to 3/8 of the
  next pixel in. Measured on 1-pixel vertical stripes (green channel, 8-bit, one screen row, Console timing 7
  with RGB in consumer-tv-composite, at 2048x1536): before, 137-158 with a beat every 4 pixels; after, an
  even 175/181, as pc-monitor-vga's 180/186. The beam pass now reads the sample that holds the pixel's centre
  whenever the decoder has no whole number of samples per pixel (PC presets, with 4 per pixel, are
  unchanged).
- consumer-tv-composite with value 2 and the glass values of pc-monitor-vga matches pc-monitor-vga at
  46.7 dB PSNR on a 1024x768 desktop; with the TV's own glass values, its overscan (0.05) cuts the desktop's
  edges and its misconvergence (0.4) colours small text. The README suggests Overscan -0.035 and
  Misconvergence 0.12 for a picture closer to pc-monitor-vga.

## How it was verified

The .slangp chains were run without RetroArch on macOS OpenGL 4.1 (a ctypes renderer, the slang passes
turned into GLSL 4.10 by text substitution), every preset from `git archive HEAD` against the working tree,
compared byte for byte: 86 presets x 13 test frames (the test images, 2x and 3x copies, 640x400, 640x480 and
1024x768 desktops), at Frames over 300 lines 0 and 1. A one-pass debug shader that includes `common.inc` and
writes the `Timing` fields checked `getTiming()` for 27 frame sizes x 10 timings x the three values.
glslangValidator was not available here.

Then in RetroArch itself (1.22.2, macOS, 2026-10-22), run from the command line with a temporary config
(`--config`, so the user's own settings stay untouched), the built-in image viewer showing the test images,
`--set-shader` for each preset (and `#reference` presets for Frames over 300 lines 1 and 2, Console timing
7 and 11), `--max-frames=90 --max-frames-ss-path` for a screenshot, and `--verbose --log-file`:

- Metal: every preset (92) and the extra cases compile (RetroArch's own glslang) and run; OpenGL core and
  Vulkan: a selection of them. The only errors in the logs are MIDI initialisation failures; the HDR presets
  warn on Vulkan and OpenGL that the last pass's format is replaced by the swapchain's (seen before too).
- Metal screenshots against the ctypes renderer, same preset, frame and size: 82-102 dB PSNR (TV, PC,
  1084S and X68000 presets; 448-line frames at Frames over 300 lines 1; 1024x768 at 2).
