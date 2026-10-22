# Upscaled frames: finding the console's lines

Worked out 2026-10-22 for issue #14 (SwanStation at 3x internal resolution: a black picture; 1x, 2x, 4x, 5x
and 6x showed one). Reported on an AMD Radeon 780M (Ryzen 7 8745H), SteamOS, RetroArch 1.22.2,
`flat/trinitron-s-video`, Castlevania: Symphony of the Night. Used in `common.inc` (`upscaleOf`,
`nativeLine`, `showLines`), `native.slang`, `decode.inc` and `beam.inc`.

## Sources

| Fact | Source | Kind |
|---|---|---|
| A 32-bit `x / y` (OpFDiv) may be off by 2.5 ULP; `mod` (OpFMod) is "inherited from x - y × floor(x/y)" | Vulkan specification, Vulkan Environment for SPIR-V, "Precision of Individual Operations", Table 3 (https://docs.vulkan.org/spec/latest/appendices/spirvenv.html) | spec |
| AMD's `v_rcp_f32` has a worst-case error of 1 ULP (from AMD's Sea Islands documentation) | LLVM AMDGPU commit 884acbb9 by Changpeng Fang, comment in the FDIV lowering (https://code.ornl.gov/llvm-doe/llvm-project/-/commit/884acbb9e167d5668e43581630239d688edec8ad) | compiler source quoting the ISA docs |

Not found: the RDNA 3 ISA guide's figure for `v_rcp_f32` itself, and whether RADV (the SteamOS driver) turns
a division by a non-constant into `x * rcp(y)`. Both are assumed, not read.

## Worked out: the black picture at 3x

The native pass keeps only the first row of each console line, `mod(y, k) > 0.5`. At 3x those rows are
`y = 3n`. If the GPU divides as `y * rcp(3)` and `rcp(3)` comes out one ULP low (0x3EAAAAAA,
0.33333331 instead of 0.33333334), then `3n * rcp(3) = n - 6e-8 n`, `floor` gives `n - 1` and `mod` gives
3 instead of 0, for every n from 1 up. Every row but the first is then dropped: a black picture. Both
values are within the 2.5 ULP the spec allows, so a conforming GPU can do this. 2x and 4x divide by a power
of two, which is exact. 5x worked on that GPU, so its `rcp(5)` is not low (the error of `rcp` depends on the
mantissa: 3 and 6 share one, 5 has another). 6x worked because it was read as 5x (below).
Not reproduced: the M4 Pro here shows nothing wrong at 3x. This is the explanation that fits the report.

The fix divides the row's centre: `mod(y + 0.5, k) > 1.0`, `floor((y + 0.5) / k)` in `nativeLine` (which
put the first row of every line in the line before, harmless as the encoder and decoder agreed). Every
caller passes whole rows. The decoder's step to the previous line in its own rows (`sr`) is now 1 or 2
outright instead of `st / k`, the beam pass stops at the last line with a margin of 0.01, and
`showLines` floors its offset with the same margin.

## Worked out: which factor

`upscaleOf` took the smallest k from 2 to 16 that gives a whole number of lines between 0.83 and 1.21
times the base (240, or 480 in the 480i class). Some heights fit two factors, and the smallest is not
always right. Heights 224, 240, 256 and 288 at 3x to 10x, read wrong before:

| Height | Read wrong at (as) |
|---|---|
| 224 | 5x (4x, 280 lines), 8x (7x, 256), 9x (7x, 288), 10x (8x, 280) |
| 240 | 6x (5x, 288 lines: PAL), 7x (6x, 280), 9x (8x, 270) |
| 256 | 9x (8x, 288) |
| 288 | none |

The #14 report's 6x was one of these: a 288-line PAL picture (the PAL decoder, 288 lines).

Now a k that gives a standard height wins: 240 or 224 lines for NTSC, 288 or 256 for PAL (doubled in the
480i class), NTSC first unless TV standard is 2 (PAL). Without one, the smallest k as before. Some heights
cannot be told apart from the frame alone (1440 is 240 x 6 and 288 x 5, 1792 is 224 x 8 and 256 x 7):

| TV standard | Read wrong (height at scale, up to 16x) |
|---|---|
| auto, NTSC | 288 at 5x, 7x, 10x; 256 at 7x, 9x, 14x, 15x; 224 at 15x |
| PAL | 240 at 6x, 12x, 16x; 224 at 8x, 9x, 15x, 16x; 256 at 9x |

Up to 4x no height is ambiguous. NTSC goes first because NTSC players with 224 or 240-line games lost more
before than PAL players lose now; the README tells PAL players at 5x and up to set TV standard 2.
Not checked: which heights each core sends at each internal resolution (SwanStation's frame in the report
is taken to be 240 lines, 720 at 3x).

## Checked in RetroArch

RetroArch 1.22.2 on the M4 Pro, image viewer, test frames upscaled by repeating pixels
(`test-images/test.png` 320x240, `platformer.png` 256x224), screenshots compared by PSNR:

- Old and new code give byte-identical pictures where the factor did not change: `flat/trinitron-s-video`
  at 1x, 3x, 4x, `curved/consumer-tv-composite` at 1x and 3x, `curved/tv-show-broadcast` at 3x.
- The division of a GPU whose reciprocal is one ULP low, simulated (`1/k` with one ULP taken off, in the
  native pass and `nativeLine`): the old code at 3x gives a black picture (mean level 10 against 97), the new
  code gives the same picture as without the simulation, at 1x and 3x.
- 224 lines at 5x against 4x: old 16.3 dB (read as 280 lines), new 61.1 dB. 240 lines at 6x against 4x:
  old 22.5 dB (288 lines, PAL), new 30.4 dB, with the same 240 lines. The rest at 6x is the native pass
  averaging two frame pixels per encoder sample for frames 1920 or more wide (`vBox`), unchanged here; 5x
  (1600 wide) against 4x is 56.2 dB.
- glcore and Vulkan against Metal, new code, 3x and 6x: 93-95 dB. Lite and super lite at 3x show the
  picture (56.5 and 34.4 dB against the full preset).
