# Saturn cores: the frames they send

Researched 2026-10-22 for issue #9. Used in the `systems/saturn` preset (Console timing 4, Frames over 300
lines 1), README (Systems) and RECOMMENDED-CORES.md. Sources are the cores' code at the commits named;
Beetle Saturn changes often, so check again before relying on the details.

## Beetle Saturn (libretro/beetle-saturn-libretro, master d0d8417, 2026-10-21)

- No internal resolution or upscaling option (`libretro_core_options.h`).
- Height: `(last_scanline + 1 - initial_scanline) << interlaced` (`libretro.c` ~1617). With the defaults,
  224 rows in NTSC (scanlines 8-231) and 256 in PAL (16-271), whether the game uses 224 or 240 lines.
  The height doubles (448, 512) only while the VDP2 is in an interlace mode (unless the deinterlacer is
  set to bob). Non-interlaced pictures are never sent as 448 rows.
- Single-density interlace (VDP2 LSMD = 10) goes out as 448 rows with the two fields woven: on a TV that
  is an interlaced raster carrying the same picture in both fields, so the gaps between the lines fill.
  With Frames over 300 lines 1 the shader draws it that way.
- Width (`vdp2_render.c` ~4313, CorrectAspect on in `ss.c` ~2033): 330 in the 320 modes and 352 in the 352
  modes, 660 and 704 in hi-res, less the horizontal overscan mask; Horizontal blend doubles the low-res
  widths. Typical NTSC frames: 330x224, 352x224, 660x448, 704x448.
- Video options and defaults: initial_scanline 8, last_scanline 231, initial_scanline_pal 16,
  last_scanline_pal 271, horizontal_overscan 0, horizontal_blend off, deinterlacer weave (weave, bob,
  bob_offset, fastmad, progressive, off), mesh_transparency off.

## Kronos (libretro/yabause, branch kronos, 3791ffb)

- `kronos_resolution_mode`: original, 2X, 4X, 8X; default original (`libretro_core_options.h` ~132).
- At original the frame is the VDP2's size, 320/352/640/704 x 224/240/256 (`commongl.c` ~2020-2069); the
  height doubles only for double-density interlace (`vidcs.c`, Vdp2SetResolution).
- Line count: a fixed 263 / 313 per field, no half line (`yabause.c` ~1074).

## YabaSanshiro (libretro/yabause, branch yabasanshiro)

- `yabasanshiro_resolution_mode`: original, 2x, 4x, 720p, 1080p, 4k; default original
  (`yabause/src/libretro/libretro.c` ~110). Native sizes; the height doubles only for double-density
  interlace (`vidogl.c` ~7940).

## What follows for the shader

- Console timing 0 (auto) reads 330x224 and 352x224 as a Mega Drive in H40 mode (wider than 300 pixels,
  fewer than 232 lines), with the wrong subcarrier rhythm. The Saturn's line is 227.5 cycles, as the
  PlayStation's (see 480i-field-timing.md), so Console timing 4.
- With Console timing 4 and Frames over 300 lines 0, a 448-line frame would be read as a 224-line game at 2x
  and its two fields averaged into one line. None of the three cores sends an upscaled frame by default, so
  `systems/saturn` sets Frames over 300 lines 1. Raised resolution in Kronos or YabaSanshiro: set it to 0.
