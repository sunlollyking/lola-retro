#!/usr/bin/env python3
"""Render a frame through the Unity version of Lola Retro offscreen, the way its C# side runs it
(Runtime/LolaRetroChain.cs), and compare it with the RetroArch passes.

Unity is not needed: both run on OpenGL here. The Unity passes (HLSL) are compiled with glslang's
HLSL front end, the slang passes with glslang, and SPIRV-Cross turns both into GLSL 4.10. So the
comparison tests the translation (tools/glsl_to_hlsl.py, tools/make_unity.py): the same picture,
up to rounding, means the HLSL computes what the slang computes.

Unity's side as LolaRetroChain.cs runs it:
- the frame is a texture in linear light (as a camera renders it), rows from the bottom;
- Unity's source pass makes the console's frame from it, then the passes run with the sizes and
  filters of LolaRetroPasses.cs (every target clamps to the edge; each texture's own filter is
  used, as on OpenGL ES), the show-lines pass only on a TV show;
- the last pass writes linear light; it is coded to 8-bit sRGB as Unity's backbuffer would.
--uvtop runs the passes as on Direct3D, Metal and Vulkan (LR_UVTop: clip space turned upside down
against the rows), where Unity's targets start at the top.

RetroArch's side follows the preset's .slangp chain (tools/make_presets.py): the frame with the
top row first, each texture read with RetroArch's filter and edge rule (clamp_to_border with a
real black border), float_framebuffer targets in RGBA16F, the last pass into 8 bits.

Needs: glslangValidator and spirv-cross on PATH; pip install moderngl numpy pillow.

Usage: tools/unity-preview.py PRESET FRAME.png OUT.png [--size 1440x1080] [--frames 3] [--uvtop]
       [--input-scale K] [--compare RETROARCH_OUT.png] [--set NAME=VALUE ...]
PRESET is a preset of tools/make_presets.py (consumer-tv-composite, amiga-1084s, ...).
"""
import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

import moderngl
import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import make_presets as mp                              # noqa: E402
from make_glsl import parameter_table                  # noqa: E402
from make_unity import parse_chain, CHAIN, OUT as UNITY  # noqa: E402
from check_shaders import expand, split_stages          # noqa: E402

SLANG = Path(__file__).resolve().parent.parent / "shaders" / "lola-retro" / "shaders"
TMP = Path(tempfile.mkdtemp(prefix="lola-unity-"))


def run(*args):
    r = subprocess.run([str(a) for a in args], capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit(f"{args[0]} failed:\n{r.stdout}{r.stderr}")
    return r.stdout


def cross(spv, entry=None, flip=False):
    """SPIR-V -> GLSL 4.10 and its reflection."""
    extra = ["--entry", entry] if entry else []
    refl = json.loads(run("spirv-cross", spv, "--reflect", *extra))
    # the stages are linked by name: name the varyings by their location
    stage = refl["entryPoints"][0]["mode"]
    names = []
    if stage == "vert":
        names = [a for o in refl.get("outputs", []) for a in ("--rename-interface-variable", "out", o["location"], f"lr_var{o['location']}")]
    elif stage == "frag":
        names = [a for i in refl.get("inputs", []) for a in ("--rename-interface-variable", "in", i["location"], f"lr_var{i['location']}")]
    glsl = run("spirv-cross", spv, "--version", "410", "--no-es", *extra, *names, *(["--flip-vert-y"] if flip else []))
    # macOS has OpenGL 4.1 without binding layouts; units and blocks are set by name here
    glsl = re.sub(r"layout\(binding = \d+, ", "layout(", glsl)
    glsl = re.sub(r"layout\(binding = \d+\)\s*", "", glsl)
    return glsl, refl


_cache = {}


def unity_program(ctx, path, flip):
    """The Unity pass in path (HLSL, LRVert and LRFrag) as a GL program."""
    key = (path, flip)
    if key not in _cache:
        stages = {}
        for short, entry in (("vert", "LRVert"), ("frag", "LRFrag")):
            spv = TMP / f"{path.stem}.{short}.spv"
            run("glslangValidator", "-D", "-V", "-S", short, "-e", entry, "-o", spv, path)
            stages[short] = cross(spv, entry, flip and short == "vert")
        _cache[key] = program(ctx, stages)
    return _cache[key]


def slang_program(ctx, name):
    if name not in _cache:
        stages = {}
        for stage, src in split_stages(expand(SLANG / f"{name}.slang")).items():
            short = "vert" if stage == "vertex" else "frag"
            f = TMP / f"{name}.{short}"
            f.write_text("\n".join(src) + "\n")
            spv = TMP / f"{name}.{short}.spv"
            run("glslangValidator", "-V", "-o", spv, f)
            stages[short] = cross(spv)
        _cache[name] = program(ctx, stages)
    return _cache[name]


class Program:
    """A GL program with its uniform blocks (name -> (size, {member: (offset, type)}))."""
    def __init__(self, prog, blocks):
        self.prog, self.blocks = prog, blocks

    def set_block(self, ctx, values):
        """Fill every uniform block from values (missing members stay 0)."""
        self.buffers = []
        for k, (name, (size, members)) in enumerate(self.blocks.items()):
            data = bytearray(size)
            for m, (off, typ) in members.items():
                v = values.get(m)
                if v is None:
                    continue
                if typ == "float":
                    data[off:off + 4] = np.float32(v).tobytes()
                elif typ in ("int", "uint"):
                    data[off:off + 4] = np.array(v, dtype=np.int32 if typ == "int" else np.uint32).tobytes()
                elif typ == "vec4":
                    data[off:off + 16] = np.array(v, dtype=np.float32).tobytes()
                elif typ == "mat4":
                    data[off:off + 64] = np.array(v, dtype=np.float32).T.tobytes()
                else:
                    raise SystemExit(f"uniform type {typ} not handled")
            buf = ctx.buffer(bytes(data))
            self.prog[name].binding = k
            buf.bind_to_uniform_block(k)
            self.buffers.append(buf)

    def samplers(self):
        """GLSL sampler uniforms: name in the shader -> the texture's name in the pass."""
        return {n: n for n in self.prog if isinstance(self.prog[n], moderngl.Uniform) and self.prog[n].fmt == "1i"}


def program(ctx, stages):
    vs, vr = stages["vert"]
    fs, fr = stages["frag"]
    prog = ctx.program(vertex_shader=vs, fragment_shader=fs)
    blocks = {}
    for refl in (vr, fr):
        for ubo in refl.get("ubos", []):
            t = refl["types"][ubo["type"]]
            # the block's name in the GLSL is the type's name
            blocks[t["name"].replace("$", "_")] = (ubo["block_size"], {m["name"]: (m["offset"], m["type"]) for m in t["members"]})
    return Program(prog, {n: b for n, b in blocks.items() if n in prog})


def texture_name(uniform, names):
    """SPIRV-Cross names a texture read through a sampler SPIRV_Cross_Combined<texture><sampler>."""
    u = uniform.replace("SPIRV_Cross_Combined", "")
    for n in sorted(names, key=len, reverse=True):
        if u.startswith(n):
            return n
    return None


def size_of(scale, source, viewport):
    kind, k = scale
    v = k if kind == "absolute" else (source if kind == "source" else viewport) * k
    return max(1, int(np.floor(v + 0.5)))


def target(ctx, w, h, is_float):
    tex = ctx.texture((w, h), 4, dtype="f2" if is_float else "f1")
    return tex, ctx.framebuffer(color_attachments=[tex])


def preset_values(name, overrides):
    table = parameter_table()
    values = {n: d[1] for n, d in table.items()}
    values.update(mp.BASE)
    values.update(mp.PRESETS[name])
    values.update(overrides)
    return values


# ---------------------------------------------------------------- Unity

def render_unity(ctx, frame_srgb, values, out_size, frames, uvtop, input_scale=1):
    H, W = frame_srgb.shape[:2]
    console = (W, H)
    names = ["source"] + [p["src"] for p in CHAIN]
    files = [UNITY / "Shaders" / "lola-retro-source.hlsl"] + [UNITY / "Shaders" / "Passes" / f"{n}.hlsl" for n in names[1:]]
    show = values["LR_CABLE"] > 3.5
    monitor = round(values["LR_CONSOLE"]) == 7 or values["LR_MON_MM"] > 0.5
    run_idx = [i for i in range(len(names)) if not (names[i] == "show-lines" and not show)]
    linear_of = [False] + [p["filter"] for p in CHAIN]
    float_of = [False] + [p["float"] for p in CHAIN]

    size = {0: console}
    prev = 0
    for i in run_idx[1:]:
        p = CHAIN[i - 1]
        sx = ("source", 4.0) if p["src"] == "encode" and monitor else p["sx"]
        size[i] = (size_of(sx, size[prev][0], out_size[0]), size_of(p["sy"], size[prev][1], out_size[1]))
        prev = i

    # the input: linear light, rows from the bottom (Unity's texture layout); input_scale: the
    # camera's image at that many screen pixels per console pixel
    lin = np.where(frame_srgb <= 0.04045, frame_srgb / 12.92, ((frame_srgb + 0.055) / 1.055) ** 2.4)
    lin = lin.repeat(input_scale, axis=0).repeat(input_scale, axis=1)
    IW, IH = W * input_scale, H * input_scale
    inp = ctx.texture((IW, IH), 4, np.ascontiguousarray(np.dstack([lin, np.ones((IH, IW))])[::-1]).astype("f4").tobytes(), dtype="f4")

    targets = {}
    for k, i in enumerate(run_idx):
        if names[i] in ("crt", "present"):
            continue
        tex, fbo = target(ctx, *size[i], float_of[i])
        nxt = run_idx[k + 1]
        tex.filter = (moderngl.LINEAR, moderngl.LINEAR) if linear_of[nxt] else (moderngl.NEAREST, moderngl.NEAREST)
        tex.repeat_x = tex.repeat_y = False
        targets[i] = (tex, fbo)
    crt_i = names.index("crt")
    glow = [target(ctx, *size[crt_i], True) for _ in range(2)]
    for tex, fbo in glow:
        tex.filter = (moderngl.NEAREST, moderngl.NEAREST)
        tex.repeat_x = tex.repeat_y = False
        fbo.clear(0, 0, 0, 0)
    out_tex = ctx.texture(out_size, 4, dtype="f4")
    out_fbo = ctx.framebuffer(color_attachments=[out_tex])

    alias = {"Original": 0, "LR_Decoded": names.index("decode"), "LR_Glow": names.index("glow-v"),
             "LR_Beam": names.index("show-lines") if show else names.index("beam")}
    for f in range(frames):
        cur, last = glow[f & 1], glow[(f + 1) & 1]
        textures = {"LR_Input": inp, "LR_CRTFeedback": last[0]}
        source_size = console
        for k, i in enumerate(run_idx):
            prog = unity_program(ctx, files[i], uvtop)
            if names[i] == "crt":
                fbo, osz = cur[1], size[i]
            elif names[i] == "present":
                fbo, osz = out_fbo, out_size
            else:
                fbo, osz = targets[i][1], size[i]
            vals = dict(values)
            vals.update(LR_SourceSize=[source_size[0], source_size[1], 1 / source_size[0], 1 / source_size[1]],
                        LR_OriginalSize=[W, H, 1 / W, 1 / H],
                        LR_OutputSize=[osz[0], osz[1], 1 / osz[0], 1 / osz[1]],
                        LR_FrameCountI=f, LR_UVTop=1.0 if uvtop else 0.0, LR_OutputLinear=1.0,
                        LR_InputLinear=1.0, LR_InputRect=[0, 0, IW, IH])
            prog.set_block(ctx, vals)
            tex_of = dict(textures)
            if k > 0:
                prev_i = run_idx[k - 1]
                tex_of["Source"] = cur[0] if names[prev_i] == "crt" else targets[prev_i][0]
            for n, j in alias.items():
                if j in targets:
                    tex_of[n] = targets[j][0]
            unit = 0
            for u in prog.samplers():
                n = texture_name(u, tex_of)
                assert n, f"{names[i]}: no texture for {u}"
                tex_of[n].use(location=unit)
                prog.prog[u].value = unit
                unit += 1
            fbo.use()
            ctx.vertex_array(prog.prog, []).render(moderngl.TRIANGLES, vertices=3)
            source_size = osz
    lin_out = np.frombuffer(out_tex.read(), dtype="f4").reshape(out_size[1], out_size[0], 4)[::-1, :, :3]
    lin_out = np.clip(lin_out, 0.0, 1.0)
    srgb = np.where(lin_out <= 0.0031308, lin_out * 12.92, 1.055 * lin_out ** (1 / 2.4) - 0.055)
    return np.floor(srgb * 255.0 + 0.5).astype(np.uint8)


# ---------------------------------------------------------------- RetroArch

def render_retroarch(ctx, frame_srgb, name, values, out_size, frames):
    H, W = frame_srgb.shape[:2]
    passes = parse_chain(mp.with_native_pass(mp.PRESET_PASSES.get(name, mp.PASSES)))
    frame = ctx.texture((W, H), 4, (np.dstack([frame_srgb, np.ones((H, W))]) * 255 + 0.5).astype("u1").tobytes())
    sizes, prev = [], (W, H)
    for p in passes:
        prev = (size_of(p["sx"], prev[0], out_size[0]) if p["sx"] else out_size[0],
                size_of(p["sy"], prev[1], out_size[1]) if p["sy"] else out_size[1])
        sizes.append(prev)
    outs = []
    for i, p in enumerate(passes):
        if i == len(passes) - 1:
            outs.append(target(ctx, *sizes[i], False))
        elif p["alias"] == "LR_CRT":
            outs.append([target(ctx, *sizes[i], True) for _ in range(2)])
        else:
            outs.append(target(ctx, *sizes[i], p["float"]))
    crt = next(i for i, p in enumerate(passes) if p["alias"] == "LR_CRT")
    for _, fbo in outs[crt]:
        fbo.clear(0, 0, 0, 0)
    quad = ctx.buffer(np.array([-1, -1, 0, 0, 1, -1, 1, 0, -1, 1, 0, 1, 1, 1, 1, 1], dtype="f4").tobytes())

    def sampler(tex, linear, wrap):
        s = ctx.sampler(texture=tex, filter=(moderngl.LINEAR, moderngl.LINEAR) if linear else (moderngl.NEAREST, moderngl.NEAREST),
                        repeat_x=False, repeat_y=False)
        if wrap == "clamp_to_border":
            s.border_color = (0.0, 0.0, 0.0, 0.0)
        return s

    for f in range(frames):
        cur = lambda j: outs[j][f & 1] if j == crt else outs[j]          # noqa: E731
        source, source_size = frame, (W, H)
        for i, p in enumerate(passes):
            prog = slang_program(ctx, p["src"])
            osz = sizes[i]
            vals = dict(values)
            vals.update(MVP=np.eye(4))
            prog.set_block(ctx, vals)
            pr = prog.prog
            for k, v in (("SourceSize", source_size), ("OriginalSize", (W, H)), ("OutputSize", osz)):
                if f"params.{k}" in pr:
                    pr[f"params.{k}"].value = (v[0], v[1], 1 / v[0], 1 / v[1])
            if "params.FrameCount" in pr:
                pr["params.FrameCount"].value = f
            reads = {"Source": (source, p["filter"], p["wrap"]), "Original": (frame, passes[0]["filter"], passes[0]["wrap"])}
            for j, q in enumerate(passes[:i]):
                if q["alias"]:
                    reads[q["alias"]] = (cur(j)[0], passes[j + 1]["filter"], passes[j + 1]["wrap"])
            reads["LR_CRTFeedback"] = (outs[crt][(f + 1) & 1][0], passes[crt + 1]["filter"], passes[crt + 1]["wrap"])
            keep, unit = [], 0
            for u in prog.samplers():
                n = texture_name(u, reads)
                assert n, f"{p['src']}: no texture for {u}"
                s = sampler(*reads[n])
                s.use(location=unit)
                pr[u].value = unit
                keep.append(s)
                unit += 1
            cur(i)[1].use()
            ctx.vertex_array(pr, [(quad, "2f 2f", "Position", "TexCoord")] if "TexCoord" in pr else
                             [(quad, "2f 8x", "Position")]).render(moderngl.TRIANGLE_STRIP)
            source, source_size = cur(i)[0], osz
    data = np.frombuffer(outs[-1][0].read(), dtype="u1").reshape(sizes[-1][1], sizes[-1][0], 4)
    return data[:, :, :3].copy()


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("preset")
    ap.add_argument("frame")
    ap.add_argument("out")
    ap.add_argument("--size", default="1440x1080")
    ap.add_argument("--frames", type=int, default=3)
    ap.add_argument("--uvtop", action="store_true")
    ap.add_argument("--input-scale", type=int, default=1, metavar="K",
                    help="Unity's input is the frame enlarged K times (a camera image at the screen's resolution)")
    ap.add_argument("--compare", metavar="RETROARCH_OUT")
    ap.add_argument("--set", action="append", default=[], metavar="NAME=VALUE")
    a = ap.parse_args()
    out_size = tuple(int(v) for v in a.size.split("x"))
    overrides = {k: float(v) for k, v in (s.split("=") for s in a.set)}
    values = preset_values(a.preset, overrides)
    frame = np.asarray(Image.open(a.frame).convert("RGB"), dtype=np.float64) / 255.0
    ctx = moderngl.create_standalone_context(require=410)
    unity = render_unity(ctx, frame, values, out_size, a.frames, a.uvtop, a.input_scale)
    Image.fromarray(unity).save(a.out)
    print("wrote", a.out)
    if a.compare:
        ra = render_retroarch(ctx, frame, a.preset, values, out_size, a.frames)
        Image.fromarray(ra).save(a.compare)
        print("wrote", a.compare)
        d = np.abs(unity.astype(int) - ra.astype(int))
        print(f"difference: max {d.max()}, mean {d.mean():.4f}, pixels off by more than 1: "
              f"{(d.max(axis=2) > 1).mean() * 100:.3f}%")


if __name__ == "__main__":
    main()
