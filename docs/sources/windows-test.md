# Windows: the presets on D3D11, D3D12, Vulkan and glcore (Intel laptop)

Measured 2026-10-22 by the author on his own laptop, with a test script that ran RetroArch with its own
config (the built-in image viewer, PNG test frames, no core). Shaders v1.16. Up to then the presets had only
been run on macOS (Metal, glcore, Vulkan through MoltenVK); D3D11 and D3D12, where RetroArch turns the slang
passes into HLSL for Microsoft's compiler, had not been tried since issue #1.

## The machine

Intel Core Ultra 5 225U (15 W), Intel Graphics (4 Xe cores, 64 EUs, up to 2 GHz; Notebookcheck,
https://www.notebookcheck.net/Intel-Core-Ultra-5-225U-Processor-Benchmarks-and-Specs.943067.0.html), driver
32.0.101.8508, 16 GB RAM, Windows 11 Home 26200, a 1920x1200 screen at 60 Hz, on AC power, RetroArch 1.22.2.
RetroArch ran full screen with a 4:3 picture, so the CRT pass drew 1600x1200.

## The pictures

21 preset and frame pairs on each driver (every device preset, lite and super lite composite at 1x and 3x,
and no shader), screenshot after 60 frames. Every run started and wrote a screenshot; no errors in the logs.

- D3D11, Vulkan and glcore give the same picture: 65-90 dB PSNR between them. Exceptions, all looked at:
  - old-tv-rf (D3D11 against Vulkan 31.5 dB) and tv-show-broadcast (46 dB): the snow and the line jitter
    come from a hash, and Microsoft's compiler rounds it differently, so the noise falls elsewhere. Same
    mean level, same look.
  - super lite composite on D3D11 (46.5 dB): differences up to 44 levels on 0.1% of the pixels, on the
    edges of fine text; side by side no difference shows.
- D3D12: RetroArch's D3D12 driver cannot take a screenshot of the shader's output and saved the raw frame
  instead, so its picture was not checked; it ran without errors.

## Loading times

Seconds from start to exit for a 60-frame run (mostly loading the preset and compiling its passes):

| Driver | Full presets | Lite | Super lite | No shader |
|---|---|---|---|---|
| D3D11 | 25-31 (46 the first time) | 5.6 | 4.9-5.9 | 2.8 |
| D3D12 | 24-27 (44 the first time) | 3.9 | 4.2-4.3 | 2.0 |
| glcore | 8-13 | 3.6-3.9 | 3.6-4.6 | 2.2 |
| Vulkan | 2.7-7 (10 the first time) | 3.4-3.6 | 3.4-4.4 | 2.1 |

On D3D11 and D3D12 a full preset takes over 20 seconds longer to load than on Vulkan, every time a game
starts with it. Lite and super lite do not: their CRT pass leaves out Around the tube and the afterglow, so
the long compile is very likely the full CRT pass (crt.inc with Around the tube and its ten scenes,
which crt-lite.slang leaves out with LR_NO_BACKDROP) in Microsoft's HLSL compiler.
Not measured per pass. The laptop's CPU is a 15 W one; a desktop CPU should compile faster.

## Speed

VSync and audio sync off, so RetroArch drew frames as fast as it could (RetroArch alone: 0.5-0.7 ms a
frame, so not held at 60 Hz). Each preset ran 100 and 1100 frames; the difference is the time of 1000
frames. Median of three, 320x240 test frame, 1600x1200 picture. The GPU was 89-97% busy during these runs
(Windows' GPU engine counters), so the time is the GPU's.

Milliseconds per frame, the preset's own cost (RetroArch alone taken off), full / lite / super lite:

| Preset | Vulkan | D3D11 |
|---|---|---|
| consumer-tv-composite | 6.58 / 3.42 / 2.48 | 7.75 / 4.17 / 2.58 |
| consumer-tv-scart-rgb | 5.42 / 2.19 / 1.68 | 4.55 / 2.44 / 1.95 |
| trinitron-s-video | 6.03 / 2.93 / 2.11 | 5.48 / 3.17 / 1.85 |

- Every full preset fits in the 16.7 ms of a 60 Hz frame with room for the emulator: this laptop does not
  need lite.
- Lite is 1.9-2.5 times as fast as full, super lite 2.6-3.2 times (Vulkan), close to the M4 Pro's 1.7-2.1
  and 2.6-3.0 (lite-presets.md).
- Against the M4 Pro (whole frame, 1440x1080: composite 1.81 ms), the laptop takes about 4 times as long for
  1.23 times the pixels: roughly 3.3 times slower, as their FP32 figures say (about 2 against 6.7 TFLOPS).
- Vulkan's times were steady (the three runs within 0.1 ms for most presets, 0.4 at most); D3D11's full
  presets varied by up to 2 ms,
  probably because the long compile before each run heated the shared 15 W chip.
- The GPU busy share of D3D11's full presets came out 0: the sampler skips the first third of a run, and
  there the compile took most of it. Not a property of the shader.

Not measured: the second run in power-saving mode (GPU clock against time), and a real core.
