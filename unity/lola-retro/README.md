# Lola Retro for Unity

[Lola Retro](https://github.com/zoran123456/lola-retro) as a Unity package: a CRT shader that simulates the
whole analog chain, not just scanlines.

`console video encoder → cable → TV decoder → electron beam → phosphor mask → glass`

Your game goes through the same passes as in the RetroArch version, with the same presets and every parameter:
composite blends dithering into new colours and makes 1-pixel stripes shimmer, RGB keeps them sharp, the beam
grows with brightness, the mask and the glass are drawn at your screen's resolution. Made for indie games with a
retro look: pixel art, PS1-style 3D, anything that should look like it ran on a TV or monitor of the 80s and 90s.

![The same frame on composite and RGB](https://raw.githubusercontent.com/zoran123456/lola-retro/main/docs/sonic-composite-vs-rgb.jpg)

<sub>The RetroArch version on composite and RGB. The Unity version draws the same picture: its passes are
translated from the RetroArch shaders and checked against them pixel for pixel (see [Tested](#tested)).</sub>

## Requirements

- Unity 6 (6000.0) or newer.
- URP (with Render Graph, the default in Unity 6) or the Built-in render pipeline. HDRP is not supported.
- A desktop GPU (Direct3D 11/12, Metal, Vulkan, OpenGL Core). It needs about what the RetroArch version needs,
  see [Performance](#performance).

## Install

**Window → Package Manager → + → Install package from git URL…** and enter

```
https://github.com/zoran123456/lola-retro.git?path=/unity/lola-retro
```

Add `#v1.12` (or another release tag) at the end to stay on that version.

## Set up

1. **URP only:** select your renderer asset (the Universal Renderer Data, or Renderer 2D Data in a 2D project),
   **Add Renderer Feature → Lola Retro**. Every camera that uses that renderer can then have Lola Retro.
2. Select the camera that shows your game and **Add Component → Rendering → Lola Retro**.
3. Pick a **Preset**. The default is the TV most people had: a consumer TV on composite. The presets under
   *Systems* are the device most people had for that console, with its timing (the NES and SNES dots, the Mega
   Drive's rainbow and so on).

That is all for a game that renders at the screen's resolution: Lola Retro makes a console picture of 240 lines
from it (**Lines**, with the width from the camera's shape) and shows it on the tube.

## The console's picture

A CRT shows a picture of a few hundred lines, and the passes need it at that resolution: the encoder works on the
console's pixels, the beam draws one line per console line. There are two ways to give it one.

**From the camera's image** (no Input). The camera renders at the screen's resolution as usual, and each console
pixel is the average of what it covers: like a 3D game rendered at 320×240, with clean edges. Set **Lines** (224
or 240 for most 8- and 16-bit looks, 480 for an interlaced picture) and leave **Width** at 0 for square pixels.
For pixel art, set Lines to your game's height: with the camera drawing its pixels at a whole multiple of it (the
Pixel Perfect Camera without *Upscale Render Texture*), every console pixel is exactly one of your pixels.

**From a render texture** (Input). For pixel-exact games, or PS1-style 3D at a low resolution: one camera
renders the game into a render texture at the console's resolution (for example 320×240, Filter Mode Point), and
the camera that shows the game has Lola Retro with that texture as **Input**. Every texel is then one console
pixel. That second camera renders nothing itself: Culling Mask *Nothing*, Background *Solid Color*, black. It
must render after the first one (a higher **Priority** in URP, **Depth** in the Built-in pipeline).

The **Tube shape** is 4:3 for console pictures up to 1.5:1 and the picture's own shape for wider ones (a 16:9
game gets a widescreen TV), in the middle of the screen with black around it. *Fill the screen* stretches the
tube to the screen. **Around the tube** (other than Black) always fills the screen and draws a 4:3 tube in it.

## Presets

| Preset | What it is |
|---|---|
| Consumer TV, composite | Start here. The TV most people had, on the composite cable that came in the box. |
| Consumer TV, SCART RGB | The same TV on RGB: a clean picture. |
| TV show (broadcast) | Games as they looked on a TV programme about games: through a studio, onto tape, through the antenna. |
| Sony Trinitron, S-Video | Aperture grille, flat vertically, cool white. PS2, GameCube, N64. |
| Studio monitor, RGB | The PVM look: sharp, a fine grille, calibrated colour. |
| Sony PVM-14L2, composite | A real PVM on composite, with its comb filter. |
| Old TV, RF | The antenna cable: snow, ghosting, soft focus, a round tube. |
| CRT projector | Three tubes on a flat screen, like a deluxe arcade cabinet. |
| PC monitor (VGA), good, premium, premium+, extreme | A mid-90s 15-inch PC monitor described in millimetres, and better ones. |
| Commodore 1084S (Amiga) | The Amiga monitor, from its service manual. |
| Systems | One preset per console or computer, as in the RetroArch version's `systems` folder. |

**Flat screen** makes any of them flat with square corners. A preset sets every value under **Settings**;
change them after that as you like (choosing a preset again resets them). They are the parameters of the
RetroArch version and mean the same, see its
[Parameters](https://github.com/zoran123456/lola-retro/blob/main/shaders/lola-retro/README.md#parameters).

## From a script

```csharp
using LolaRetro;

var crt = Camera.main.GetComponent<LolaRetroEffect>();
crt.ApplyPreset(LolaRetroPreset.OldTVRF);        // every value of the preset
crt.settings.cable = Cable.RGB;                  // then any parameter
crt.settings.roundedCorners = 0f;
crt.enabled = false;                             // off
```

An options menu can offer the cable (RF, composite, S-Video, RGB) and the presets; the picture changes in the
next frame.

## Good to know

- **UI.** A Screen Space - Overlay canvas is drawn after Lola Retro, sharp on top of the tube. To have the UI on
  the tube too, render it with the game: Screen Space - Camera on the camera whose image Lola Retro shows.
- **URP's own anti-aliasing and upscaling** run after Lola Retro and would blur the mask: on the camera, set
  Post-processing *Anti-aliasing* to None, and keep the URP asset's *Render Scale* at 1. URP's *Dithering* is not
  needed: the last pass dithers like the RetroArch version.
- **Colour.** Unity renders in linear light, a console sent gamma-coded RGB: the first pass codes the picture as an
  emulator's frame is coded, the last one turns the result back. Post-processing (tonemapping, colour grading)
  happens before Lola Retro, as if it were part of the game.
- **Afterglow** (Phosphor afterglow) works as in RetroArch: each camera keeps its last frame.
- **Not supported yet:** HDR output (the RetroArch version's `hdr` presets), XR, HDRP. Mobile and WebGL may work
  but have not been tried; the beam pass needs textures 8192 wide (narrower ones work, a little less exactly).

## Performance

The chain has nine passes (ten on a TV show) and the encoder and decoder do real signal filtering, as in the
RetroArch version: it needs a reasonably modern GPU; integrated graphics from the last few years should be fine
at 1080p. The cost hardly depends on the game: the encoder and decoder work at 1280 samples per line
whatever the console picture's width, the beam pass once per console line, the rest once per screen pixel.

## Tested

The passes are made from the RetroArch shaders by a converter (`tools/make_unity.py` in the repository). They
were checked by running both on the same GPU, every preset, cable and option, with the Unity passes run the way
the package runs them, and with the rows the right way up for both kinds of graphics API: the pictures agree to
within one step of 8-bit colour, apart from a few single pixels per frame where the mask changes colour
(`tools/unity-preview.py`). The C# side (the component and the URP renderer feature) has not been run in
Unity yet. Reports are welcome.

## License

MIT, see the LICENSE file in this folder.
