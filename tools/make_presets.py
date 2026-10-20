#!/usr/bin/env python3
"""Generate the Lola Retro .slangp presets.

All presets share the same pass chain and differ only in parameter values,
so they are generated from one table to keep them consistent.
Every preset is written twice: curved/ keeps each tube's curvature,
flat/ is the same preset with a flat screen and square corners.
hdr/curved/ and hdr/flat/ are the same presets for HDR displays: their last two
passes write HDR10 (crt-hdr.slang, present-hdr.slang).

Usage: python3 tools/make_presets.py
"""
import re
from pathlib import Path

PACK = Path(__file__).resolve().parent.parent / "shaders" / "lola-retro"

PASSES = """shaders = 7

shader0 = ../shaders/encode.slang
alias0 = LR_Signal
filter_linear0 = false
wrap_mode0 = clamp_to_border
scale_type_x0 = source
scale_x0 = 4.0
scale_type_y0 = source
scale_y0 = 1.0
float_framebuffer0 = true

shader1 = ../shaders/decode.slang
alias1 = LR_Decoded
filter_linear1 = true
wrap_mode1 = clamp_to_border
scale_type1 = source
scale1 = 1.0
float_framebuffer1 = true

shader2 = ../shaders/glow-h.slang
filter_linear2 = true
wrap_mode2 = clamp_to_edge
scale_type_x2 = source
scale_x2 = 0.125
scale_type_y2 = source
scale_y2 = 0.5
float_framebuffer2 = true

shader3 = ../shaders/glow-v.slang
alias3 = LR_Glow
filter_linear3 = true
wrap_mode3 = clamp_to_edge
scale_type3 = source
scale3 = 1.0
float_framebuffer3 = true

shader4 = ../shaders/beam.slang
alias4 = LR_Beam
filter_linear4 = true
wrap_mode4 = clamp_to_edge
scale_type_x4 = absolute
scale_x4 = 8192
scale_type_y4 = source
scale_y4 = 2.05
float_framebuffer4 = true

shader5 = ../shaders/crt.slang
alias5 = LR_CRT
filter_linear5 = true
wrap_mode5 = clamp_to_edge
scale_type5 = viewport
scale5 = 1.0
float_framebuffer5 = true

shader6 = ../shaders/present.slang
filter_linear6 = false
wrap_mode6 = clamp_to_edge
scale_type6 = viewport
scale6 = 1.0
"""

# The beam pass (shader4) is 8192 samples wide for any source, and at least one row per
# source line: 2.05 x the half-height glow output covers odd line counts too.

# Values for every parameter a preset sets. Anything not listed keeps the default from common.inc.
BASE = dict(
    LR_CONSOLE=0, LR_NORM=0, LR_CABLE=1, LR_COMB=0, LR_YTRAP=0.75, LR_SHARP=0.35, LR_SAT=1.25, LR_TINT=0, LR_DEMOD=0,
    LR_BRIGHT=0, LR_CONTRAST=1.05, LR_NOISE=0, LR_STEADY=1,
    LR_SIG_LO=0.2, LR_SIG_HI=0.44, LR_HFOCUS=0.5, LR_GAMMA=2.4, LR_PERSIST=0.08, LR_CONV=0.4,
    LR_MASK_TYPE=2, LR_MASK_STR=0.5, LR_MASK_TRIADS=360, LR_WHITE=8500, LR_PHOS=0,
    LR_CURV_X=0.045, LR_CURV_Y=0.06, LR_BEND=0.3, LR_CORNER=0.045, LR_GLOW=0.06, LR_GLOW_SIZE=0.5, LR_VIGNETTE=0.3, LR_OVERSCAN=0.05, LR_AMBIENT=0.002,
    # Monitor in mm: PC monitors (31 kHz) always use it; a typical mid-90s 15-inch, 0.28 mm dot mask.
    # LR_MON_MM turns it on for 15 kHz modes (the Amiga 1084S preset).
    LR_MON_MM=0, LR_MON_SIZE=15, LR_MON_PITCH=0.28, LR_MON_SPOT_LO=0.30, LR_MON_SPOT_HI=0.45, LR_MON_CORNER=1.4,
)

PRESETS = {
    # Ordinary living-room TV on the composite cable that came in the box
    "consumer-tv-composite": dict(),
    # The same TV fed RGB through SCART (common in Europe)
    "consumer-tv-scart-rgb": dict(LR_CABLE=3),
    # The same TV showing a game on a TV programme: the console in a studio, the picture
    # interlaced by the studio, on Betacam SP tape, through the transmitter and a roof antenna
    "tv-show-broadcast": dict(LR_CABLE=4, LR_NORM=2, LR_NOISE=0.06, LR_TAPE=2, LR_GHOST=0.03),
    # Sony Trinitron on S-Video: aperture grille, flat vertically, cool white, Japanese P22 phosphors
    "trinitron-s-video": dict(
        LR_CABLE=2, LR_SHARP=0.3, LR_SAT=1.15, LR_SIG_LO=0.18, LR_SIG_HI=0.4, LR_HFOCUS=0.4,
        LR_PERSIST=0.06, LR_CONV=0.18, LR_MASK_TYPE=1, LR_MASK_TRIADS=380, LR_WHITE=9300, LR_PHOS=4,
        LR_CURV_X=0.04, LR_CURV_Y=0.0, LR_GLOW=0.05, LR_VIGNETTE=0.22, LR_AMBIENT=0.001),
    # Broadcast/studio monitor on RGB: sharp, fine grille, calibrated D65, EBU phosphors, standard decoder
    "studio-monitor-rgb": dict(
        LR_CABLE=3, LR_SHARP=0.0, LR_SAT=1.0, LR_CONTRAST=1.0, LR_SIG_LO=0.14, LR_SIG_HI=0.32,
        LR_HFOCUS=0.3, LR_PERSIST=0.04, LR_CONV=0.05, LR_MASK_TYPE=1, LR_MASK_STR=0.6,
        LR_MASK_TRIADS=450, LR_WHITE=6500, LR_PHOS=3, LR_DEMOD=1, LR_CURV_X=0.02, LR_CURV_Y=0.02, LR_GLOW=0.03,
        LR_VIGNETTE=0.15, LR_OVERSCAN=0.03, LR_AMBIENT=0.0),
    # Sony PVM-14L2 on composite (issue #6): a 13-inch Trinitron with a 0.25 mm aperture grille
    # (198 mm high: about 790 stripe triads per height), flat vertically like every Trinitron, with
    # Sony's Trinitron phosphors (Japanese P22, as in trinitron-s-video), its digital comb filter
    # (it only works where the subcarrier flips every line; on the Mega Drive and SNES it falls back
    # to the notch filter, as an adaptive comb does), standard decoder axes, and white at D93
    # (9300 K), its other preset besides D65. Spec sheet: Sony PVM-L2 series. The beam was set
    # against photos of a real PVM-14L2 (Sonic 1, MiSTer on composite): at 600 TV lines its spot is
    # wide enough that the lines merge on mid-tones, unlike a sharper broadcast monitor.
    "studio-monitor-composite": dict(
        LR_CABLE=1, LR_COMB=1, LR_SHARP=0.0, LR_SAT=1.0, LR_CONTRAST=1.0, LR_SIG_LO=0.3, LR_SIG_HI=0.38,
        LR_HFOCUS=0.35, LR_PERSIST=0.04, LR_CONV=0.05, LR_MASK_TYPE=1, LR_MASK_STR=0.6,
        LR_MASK_TRIADS=790, LR_WHITE=9300, LR_PHOS=4, LR_DEMOD=1, LR_CURV_X=0.02, LR_CURV_Y=0.0, LR_GLOW=0.03,
        LR_VIGNETTE=0.15, LR_OVERSCAN=0.03, LR_AMBIENT=0.0),
    # Old TV on the antenna input: RF snow, ghosting, soft focus, round tube
    "old-tv-rf": dict(
        LR_CABLE=0, LR_YTRAP=0.6, LR_SHARP=0.5, LR_NOISE=0.09, LR_SAT=1.15, LR_SIG_LO=0.24, LR_SIG_HI=0.5,
        LR_HFOCUS=0.7, LR_PERSIST=0.12, LR_CONV=0.6, LR_MASK_TYPE=3, LR_MASK_STR=0.45,
        LR_MASK_TRIADS=320, LR_WHITE=7500, LR_CURV_X=0.07, LR_CURV_Y=0.09, LR_GLOW=0.1,
        LR_VIGNETTE=0.4, LR_OVERSCAN=0.06, LR_AMBIENT=0.005),
    # Arcade CRT projector (three tubes on a flat screen), e.g. Daytona USA deluxe
    "crt-projector": dict(
        LR_CABLE=3, LR_SHARP=0.0, LR_SAT=1.18, LR_CONTRAST=1.0, LR_SIG_LO=0.24, LR_SIG_HI=0.46,
        LR_HFOCUS=0.6, LR_PERSIST=0.05, LR_CONV=0.85, LR_MASK_TYPE=4, LR_MASK_STR=0.4,
        LR_MASK_TRIADS=400, LR_WHITE=7500, LR_CURV_X=0.0, LR_CURV_Y=0.0, LR_GLOW=0.08,
        LR_VIGNETTE=0.45, LR_OVERSCAN=0.02, LR_AMBIENT=0.006),
    # PC VGA monitor: 31 kHz line doubling, fine dot mask. The picture set up to fill the glass up
    # to a thin black border, as owners set it with the monitor's controls: the raster is straight
    # and the glass bows out a little, so the border is about 4 mm at the middle of each edge and
    # the corners of the picture just stay clear of the rounded corners of the glass. A nearly
    # flat glass (flat square tube), a little lighter than a TV's.
    "pc-monitor-vga": dict(
        LR_CONSOLE=7, LR_CABLE=3, LR_SHARP=0.0, LR_SAT=1.08, LR_SIG_LO=0.3, LR_SIG_HI=0.5,
        LR_HFOCUS=0.3, LR_PERSIST=0.03, LR_CONV=0.12, LR_MASK_TYPE=3, LR_MASK_STR=0.45,
        LR_MASK_TRIADS=600, LR_WHITE=9300, LR_CURV_X=0.025, LR_CURV_Y=0.03, LR_GLOW=0.04,
        LR_VIGNETTE=0.15, LR_OVERSCAN=-0.035, LR_AMBIENT=0.003),
    # Commodore 1084S on RGB, described from its service manual (1084S-P1): 14-inch Philips
    # M34EAQ10X tube, 0.42 mm slot mask, 6.5 MHz video amplifier, white set to D (6500 K).
    # EBU phosphors. Picture size as owners set it up: width and height turned up until a
    # 320 x 256 screen nearly fills the glass (about 5 mm of black around it). The factory setting
    # (overscan -0.015: the whole active picture, 260 x 186 mm for the test grid, on the
    # 264 x 198 mm face) left wide black borders around Amiga lores (45 of 52 us).
    # Curvature from photos of real Amiga monitors: the picture edges bow about 1.5% of the
    # picture height near the corners (top and bottom edge averaged, which cancels the camera angle).
    "amiga-1084s": dict(
        LR_CABLE=3, LR_SHARP=0.1, LR_SAT=1.12, LR_SIG_LO=0.2, LR_SIG_HI=0.42, LR_HFOCUS=0.45,
        LR_PERSIST=0.05, LR_CONV=0.25, LR_MASK_TYPE=2, LR_MASK_STR=0.5, LR_MASK_TRIADS=470,
        LR_WHITE=6500, LR_PHOS=3, LR_CURV_X=0.035, LR_CURV_Y=0.045, LR_GLOW=0.06, LR_VIGNETTE=0.25,
        LR_OVERSCAN=0.1, LR_AMBIENT=0.002,
        LR_MON_MM=1, LR_MON_SIZE=14, LR_MON_PITCH=0.42, LR_MON_SPOT_LO=0.28, LR_MON_SPOT_HI=0.65,
        LR_MON_CORNER=1.5),
}

# Better 15-inch PC monitors: the same tube size with a finer mask and a smaller, better focused beam.
# "extreme" is sharper than any real 15-inch monitor (the gaps between lines show at 1024x768),
# for long work on the desktop.
PC_TUBES = {
    "pc-monitor-vga-good": dict(LR_MON_PITCH=0.25, LR_MON_SPOT_LO=0.25, LR_MON_SPOT_HI=0.35),
    "pc-monitor-vga-premium": dict(LR_MON_PITCH=0.25, LR_MON_SPOT_LO=0.20, LR_MON_SPOT_HI=0.28, LR_MON_CORNER=1.3),
    "pc-monitor-vga-premium-plus": dict(LR_MON_PITCH=0.24, LR_MON_SPOT_LO=0.17, LR_MON_SPOT_HI=0.24,
                                        LR_MON_CORNER=1.25, LR_CONV=0.08),
    "pc-monitor-vga-extreme": dict(LR_MON_PITCH=0.22, LR_MON_SPOT_LO=0.13, LR_MON_SPOT_HI=0.18,
                                   LR_MON_CORNER=1.15, LR_CONV=0.05, LR_GLOW=0.02),
}
for _name, _over in PC_TUBES.items():
    PRESETS[_name] = {**PRESETS["pc-monitor-vga"], **_over}


# systems/: one ready preset per system, so nobody has to find Console timing. Each is the
# device most people had for that system, with the system's timing set by hand (the automatic
# guess cannot tell systems with the same frame size apart).
SYSTEMS = {
    "nes": ("consumer-tv-composite", dict(LR_CONSOLE=1)),
    "snes": ("consumer-tv-composite", dict(LR_CONSOLE=1)),
    "master-system": ("consumer-tv-composite", dict(LR_CONSOLE=3)),
    "mega-drive-genesis": ("consumer-tv-composite", dict(LR_CONSOLE=2)),
    # 227.5 cycles per line like the PlayStation; NTSC only, so the standard is set too
    "pc-engine-turbografx": ("consumer-tv-composite", dict(LR_CONSOLE=4, LR_NORM=1)),
    # PlayStation, Saturn and N64 keep auto: set by hand, a 480-line frame would always be read as
    # a 2x upscaled 240-line one, and their native 480i games (Tekken 3, Virtua Fighter 2) would lose it
    "playstation": ("consumer-tv-composite", dict()),
    "saturn": ("consumer-tv-composite", dict()),
    "nintendo-64": ("consumer-tv-composite", dict()),
    "playstation-2": ("trinitron-s-video", dict(LR_CONSOLE=5)),
    "gamecube": ("trinitron-s-video", dict(LR_CONSOLE=5)),
    "dreamcast": ("consumer-tv-composite", dict(LR_CONSOLE=5)),
    "arcade-15khz": ("amiga-1084s", dict(LR_CONSOLE=8)),
    "amiga": ("amiga-1084s", dict(LR_CONSOLE=6)),
    "dos-pc": ("pc-monitor-vga", dict()),
    # The PC-98's 640x400 on its computer monitor: the same 400 sharp lines as VGA's 640x400 on a
    # 15-inch PC monitor (24 or 31 kHz makes no difference to the picture, the line spacing comes
    # from the picture height), so the PC monitor model, which also gives it an HDR version
    "pc-98": ("pc-monitor-vga", dict()),
}


def fmt(v):
    return f"{float(v):.4f}".rstrip("0").rstrip(".") if "." in f"{float(v):.4f}" else str(v)


# What the flat/ folder changes on top of every preset
FLAT = dict(LR_CURV_X=0.0, LR_CURV_Y=0.0, LR_CORNER=0.0)

# HDR presets: the shader writes HDR10 itself in the last pass. That pass must render
# straight to the screen (no scale_type), otherwise RetroArch adds a copy pass and
# converts the already-HDR picture a second time.
HDR = dict(LR_NITS_WHITE=200, LR_NITS_PEAK=400)


def hdr_passes(passes):
    last = int(re.search(r"^shaders = (\d+)", passes, flags=re.M).group(1)) - 1
    p = passes.replace("../shaders/", "../../shaders/")
    p = p.replace("/crt.slang", "/crt-hdr.slang").replace("/present.slang", "/present-hdr.slang")
    p = p.replace(f"scale_type{last} = viewport\nscale{last} = 1.0\n", "")
    assert "crt-hdr.slang" in p and "present-hdr.slang" in p and f"scale_type{last}" not in p
    return p


# The TV show preset has one more pass, after the beam pass: show-lines.slang makes one beam row
# per line of the programme (577 rows high, see SHOW_LINES_ROWS in common.inc), so the CRT pass
# reads one row per line instead of interpolating at every screen pixel. It takes over the
# LR_Beam alias; the CRT and present passes move one place down.
SHOW_LINES_PASS = """shader5 = ../shaders/show-lines.slang
alias5 = LR_Beam
filter_linear5 = false
wrap_mode5 = clamp_to_edge
scale_type_x5 = absolute
scale_x5 = 8192
scale_type_y5 = absolute
scale_y5 = 577
float_framebuffer5 = true

"""


def with_show_lines(passes):
    p = passes.replace("shaders = 7", "shaders = 8").replace("alias4 = LR_Beam\n", "")
    for old, new in ((6, 7), (5, 6)):
        p = re.sub(rf"^([a-z_]+){old} = ", rf"\g<1>{new} = ", p, flags=re.M)
    p = p.replace("shader6 = ../shaders/crt.slang", SHOW_LINES_PASS + "shader6 = ../shaders/crt.slang")
    assert "shaders = 8" in p and "alias5 = LR_Beam" in p and "shader7 = ../shaders/present.slang" in p
    return p


# The TV presets run the encoder and decoder at a fixed 1280 samples per line (ENC_SAMPLES in
# common.inc: 4 per pixel of a 320-wide console), so a core running at a higher internal
# resolution costs no more than a native one. The PC and Amiga presets keep 4 samples per frame
# pixel (their beam pass reads every pixel exactly); they never get upscaled frames.
def with_fixed_samples(passes):
    old = "scale_type_x0 = source\nscale_x0 = 4.0\n"
    assert old in passes
    return passes.replace(old, "scale_type_x0 = absolute\nscale_x0 = 1280\n")


# Presets with their own pass chain
PRESET_PASSES = {"tv-show-broadcast": with_fixed_samples(with_show_lines(PASSES))}
for _name in ("consumer-tv-composite", "consumer-tv-scart-rgb", "trinitron-s-video", "studio-monitor-rgb",
              "studio-monitor-composite",
              "old-tv-rf", "crt-projector"):
    PRESET_PASSES[_name] = with_fixed_samples(PASSES)


# Every RetroArch preset starts with the native pass (native.slang): for a core running at a
# higher internal resolution it averages the rows of each console line once, so the encoder
# reads one row instead of k at every tap (its cost grew with the square of the resolution).
# PASSES itself stays without it: the Amiberry and 86Box versions (make_glsl.py) never get
# upscaled frames.
NATIVE_PASS = """shader0 = ../shaders/native.slang
filter_linear0 = false
wrap_mode0 = clamp_to_edge
scale_type0 = source
scale0 = 1.0
float_framebuffer0 = true

"""


def with_native_pass(passes):
    n = int(re.search(r"^shaders = (\d+)", passes, flags=re.M).group(1))
    p = re.sub(r"^([a-z_]+)(\d+) = ", lambda m: f"{m.group(1)}{int(m.group(2)) + 1} = ", passes, flags=re.M)
    p = p.replace(f"shaders = {n}", f"shaders = {n + 1}").replace("shader1 = ../shaders/encode.slang",
                                                                    NATIVE_PASS + "shader1 = ../shaders/encode.slang")
    assert f"shaders = {n + 1}" in p and "shader0 = ../shaders/native.slang" in p and f"shader{n} = " in p
    return p

VARIANTS = {
    "curved": ({}, None),
    "flat": (FLAT, None),
    "hdr/curved": (HDR, hdr_passes),
    "hdr/flat": ({**FLAT, **HDR}, hdr_passes),
}


def write(folder, name, passes, values):
    lines = [f"# Lola Retro - {name} ({folder})", "# Generated by tools/make_presets.py", "",
             passes.rstrip(), ""]
    lines.append('parameters = "' + ";".join(values) + '"')
    lines += [f"{k} = {fmt(v)}" for k, v in values.items()]
    out = PACK / folder / f"{name}.slangp"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(lines) + "\n")
    print("wrote", out.relative_to(PACK.parent.parent))


def main():
    for folder, variant in (("systems/curved", {}), ("systems/flat", FLAT)):
        for name, (device, over) in SYSTEMS.items():
            dev = PRESETS[device]
            passes = with_native_pass(PRESET_PASSES.get(device, PASSES)).replace("../shaders/", "../../shaders/")
            write(folder, name, passes, {**BASE, **dev, **over, **variant})
    for folder, (variant, change) in VARIANTS.items():
        (PACK / folder).mkdir(parents=True, exist_ok=True)
        for name, over in PRESETS.items():
            passes = with_native_pass(PRESET_PASSES.get(name, PASSES))
            if change:
                passes = change(passes)
            write(folder, name, passes, {**BASE, **over, **variant})


if __name__ == "__main__":
    main()
