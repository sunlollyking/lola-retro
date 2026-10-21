# 480i/576i fields on PlayStation, Saturn and N64 timing (Console timing 4)

Researched 2026-10-22 for issue #9. Used in `common.inc`, `getTiming()`: the field step for an interlaced
frame on Console timing 4.

## Question

The composite simulation moves the colour subcarrier phase from one field to the next by
`fract(lines per field x subcarrier cycles per line)`. Console timing 4 (PlayStation, also Saturn, N64,
PC Engine) has 263 lines per 240p frame (314 in PAL), which gives 0.5 cycle per frame. Do these consoles
keep 263 lines per field in 480i, or run the standard 262.5?

## Findings

| System | 240p / 288p | 480i / 576i | Source | Kind |
|---|---|---|---|---|
| PlayStation | 263 lines NTSC, 314 PAL | 262.5 lines per NTSC field, 312.5 per PAL field; 59.940 Hz interlaced vs 59.826 Hz progressive (50.000 vs 49.761 PAL) | psx-spx, GPU, "GPU Timings / Vertical Video Timings" (https://psx-spx.consoledev.net/graphicsprocessingunitgpu/) | hardware documentation |
| PlayStation | same | fields of 263 and 262 lines (`LinesPerField = 263 - field`; PAL `313 - field`) | Beetle PSX, `mednafen/psx/gpu.c` lines ~2340-2359 (commit 9bcc75d) | emulator source |
| PlayStation | same | always 263 / 314 (no half line) | DuckStation, `src/core/gpu.cpp` (commit 7a984ce) | emulator simplification |
| Saturn | 263 lines NTSC, 313 PAL | fields of 263 and 262 lines (313 and 312), in single and double density interlace | Beetle Saturn, `mednafen/ss/vdp2.c`, `VTimings` ~215-230, `IncVCounter` ~415-449 (commit d0d8417) | emulator source |
| Saturn | | the VDP2 manual (TVMD, LSMD bits) gives the modes but no line totals | https://www.infochunk.com/saturn/segahtml_en/hard/vdp2/hon/p02_40.htm | not found there |
| Nintendo 64 | VI_V_TOTAL 525 (526 half-lines = 263 lines) | VI_V_TOTAL 524 (525 half-lines = 262.5 lines); PAL 625 / 624 | n64brew, Video Interface (https://n64brew.dev/wiki/Video_Interface); libdragon `src/vi.h` | hardware documentation |

Subcarrier cycles per line:

- N64: n64brew states 227.5 chroma periods per NTSC line.
- PlayStation (inferred): psx-spx's NTSC GPU clock, 53.693175 MHz, is 15 x 3.579545 MHz, and Beetle PSX
  alternates lines of 3412 and 3413 clocks (3412.5 on average), which is 227.5 cycles. The refresh rates
  psx-spx gives match 3412.5 clocks exactly. PAL: the 53.20 MHz clock divided by 12 is the PAL colour clock
  (psx-spx, Pinouts), and 3405 / 12 = 283.75 cycles.
- Saturn (inferred): Mednafen's master clock (`ss.c` ~1814) is 488 x fsc; a line is 1708 clocks at master/65
  (320/640 modes) or 1820 at master/61 (352/704 modes), both 111020 master cycles = 227.5 cycles.

## Worked out

- 263 x 227.5 = 59832.5: 0.5 cycle per 263-line field. 262 x 227.5: 0. 262.5 x 227.5 = 59718.75: 0.75.
- PAL: 312.5 x 283.75 = 88671.875: 0.875 (the 25 Hz offset left out, as everywhere in the shader).
- A 525-line frame moves the phase by 0.5 cycle whether its fields are 262.5 + 262.5 or 263 + 262; the two
  only differ in where the half line sits. Two fields of 263 (0.5 each) would add up to 0 per frame, which
  matches DuckStation and Kronos, not the hardware.

## What the shader does

Interlaced frames on Console timing 4 use 0.75 (NTSC) and 0.875 (PAL) per field, the PlayStation 2 row's
values; progressive frames keep 0.5. With No flicker on (the default) the still picture barely changes: both
steps reach half a cycle after two fields, so the same pair of frames is averaged. With No flicker off the
dot crawl follows the 4-field sequence of the real console.

## Not found

- A Sega document with the Saturn's line totals.
- A direct statement of 227.5 cycles per line for the PlayStation or the Saturn (both worked out above).
