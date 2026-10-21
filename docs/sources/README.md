# Sources and research notes

Why the shader uses the values it uses: the sources behind them, what was measured or worked out, what
could not be found, and what the code does with it. One file per topic. These notes are not part of the
release zips (those are made from `shaders/lola-retro`, `amiberry/lola-retro` and `86box/lola-retro`).

Sources are trusted in the order CLAUDE.md gives: spec sheets, service manuals and measurements (emulator
source code that follows the hardware counts as a measurement of it); then photos of a real screen (beam,
line gaps, geometry, not colour); last, how people remember it. Every note says which kind each fact is,
and marks what was worked out (inferred) rather than read.

| Note | Used in |
|---|---|
| [high-res-frames.md](high-res-frames.md) | Frames over 300 lines (`LR_HIRES`), issue #9 |
| [480i-field-timing.md](480i-field-timing.md) | subcarrier phase per field of 480i/576i on Console timing 4 (`getTiming`) |
| [saturn-cores.md](saturn-cores.md) | the `systems/saturn` preset |
| [x68000-video.md](x68000-video.md) | the X68000 monitor model: video modes, what px68k sends |
| [x68000-monitors.md](x68000-monitors.md) | the X68000 monitor model: the monitors |
