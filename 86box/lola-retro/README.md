# Lola Retro for 86Box

The PC monitor (VGA) presets of [Lola Retro](https://github.com/zoran123456/lola-retro) for the PC emulator
[86Box](https://86box.net). It is the RetroArch shader converted to GLSL, and it gives the same picture: a
mid-90s 15-inch monitor described the way its spec sheet would (14-inch picture, dot mask pitch, beam spot
size), so every video mode looks as it did on that one tube. The basic preset is an average monitor: 320×200
games show chunky pixels with fine lines, 640×480 is sharp, 1024×768 a little soft. Four more presets are
better monitors, up to one sharper than any real tube.

![Windows 98 in 86Box with the premium PC monitor preset](https://raw.githubusercontent.com/zoran123456/lola-retro/main/docs/86box-win98.jpg)

<sub>Windows 98 at 1024×768 from 86Box through `pc-monitor-vga-premium`.</sub>

## Install

1. 86Box 6.0 or newer.
2. Put this `lola-retro` folder anywhere, for example next to 86Box.
3. **View → Renderer → OpenGL (3.0 Core)** (newer builds call it just OpenGL).
4. **View → Renderer options…** opens the Shader Manager. **Add**, choose one of the presets below, then **OK**.
   Keep it the only shader in the list: it needs the emulator's frame as its input.

| Preset | Monitor |
|---|---|
| `pc-monitor-vga.glslp` | An average 15-inch monitor (0.28 mm dot mask, beam spot 0.30–0.45 mm). Right for DOS games. |
| `pc-monitor-vga-good.glslp` | A good 15-inch monitor (0.25 mm, 0.25–0.35 mm). |
| `pc-monitor-vga-premium.glslp` | A premium 15-inch monitor (0.25 mm, 0.20–0.28 mm): 1024×768 is sharp. |
| `pc-monitor-vga-premium-plus.glslp` | The best 15-inch monitors (0.24 mm, 0.17–0.24 mm). |
| `pc-monitor-vga-extreme.glslp` | Sharper than any real tube (0.22 mm, 0.13–0.18 mm), for long work on the Windows desktop. |

All five use the same shader files and differ only in their parameter values. The same part of the
screen through each of them, at 1024×768 on a 3840×2560 screen:

![The same dialog through the five PC monitor presets](https://raw.githubusercontent.com/zoran123456/lola-retro/main/docs/86box-pc-presets.jpg)

## 86Box settings

| Setting | Use | Why |
|---|---|---|
| View → Window scale factor | **2x** or more, or full screen | The tube needs screen pixels: at 1x the lines and the dot mask are finer than the window's pixels. Full screen on a 1080p or 4K display is best. |
| View → Force 4:3 display ratio | on | The window is then 4:3, like the tube. Without it the tube still stays 4:3, with black bars at the sides. |
| View → OpenGL input stretch mode | **Full screen stretch** (default) | The shader needs the frame exactly as the video card drew it. The other modes resize it, and the shader would take it for a different video mode. (OpenGL input scale then makes no difference.) |
| View → Filter method | either | The shader reads the frame pixel by pixel, so this makes no difference. |
| Settings → Display → Monitor → VGA screen type | **RGB Color** (default) | The preset is a colour monitor. Amber, green and white monitors had no dot mask. |
| Settings → Display → Monitor → Overscan | off (default) | The shader draws the monitor's black border itself. |

86Box hands over the frame as the VGA card drew it: 320×200 games arrive as 640×400 (the card already draws
every line twice), text mode as 720×400. The shader draws the 400 lines the monitor showed, the same picture
as RetroArch's 320×200.

## Parameters

**View → Renderer options… → Configure** lists 29 of them, for each preset separately. They mean the same as in RetroArch (see the
[shader pack's README](https://github.com/zoran123456/lola-retro/blob/main/shaders/lola-retro/README.md));
**Reset** returns to the preset. The rest (cable, console timing, the TV controls) is fixed at the preset's values.
To fill more of the screen, raise the shader's **Overscan** (like turning up the monitor's width and height).
To see the monitor at its real size on a large display instead, enter your screen's height under
**Real size** (in cm, or in inches with unit 1), in full screen. **Around the tube** then puts the
monitor around it, as large as the real one: in a room (1), or in front of one of ten moving scenes
(2–11: space, synthwave, tiles, bouncing lines, plasma, copper bars, tunnel, hills, night city, arcade carpet;
**Scene speed** slows them down or stops them). With
**View → Fullscreen stretch mode → Full screen stretch** it fills the whole screen (the tube stays 4:3).
**Room light** sets how bright the room is: 1 a dim room, 3 a lit room, 20 daylight, and **Screen light on
the casing** how strongly the screen lights the walls around the glass (1 as on the real monitor).

## Differences from the RetroArch version

- No phosphor afterglow: 86Box has no feedback textures. A still picture is the same; moving objects
  leave no faint trail.
- The tube stays 4:3 whatever shape the window has.

Checked against 86Box's source with a test renderer that runs the preset the way 86Box does
(`tools/86box-preview.py`): in text mode, 640×350, 640×400, 640×480, 800×600 and 1024×768 the picture matches
the RetroArch version within 2/255 per pixel. In use in 86Box on macOS; reports from other systems are welcome.

## For developers

The files in this folder are generated; don't edit them. In the [repository](https://github.com/zoran123456/lola-retro),
change the slang passes in `shaders/lola-retro/shaders`, then run `python3 tools/make_glsl.py`. How the
conversion works is described at the top of that script.

## License

MIT, see the LICENSE file in this folder.
