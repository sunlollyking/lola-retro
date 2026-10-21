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
against the rows), where Unity's targets start at the top. --hdr runs the HDR passes (crt-hdr and
Unity's output-hdr, with the picture in nits in Rec.2020 around them, as URP's post-processing and
HDRP leave it) against RetroArch's hdr presets, both coded with PQ. --array reads the input from
slice 1 of a texture array, as for one eye in XR and in HDRP.

RetroArch's side follows the preset's .slangp chain (tools/make_presets.py): the frame with the
top row first, each texture read with RetroArch's filter and edge rule (clamp_to_border with a
real black border), float_framebuffer targets in RGBA16F, the last pass into 8 bits.

Needs: glslangValidator and spirv-cross on PATH; pip install moderngl numpy pillow.

Usage: tools/unity-preview.py PRESET FRAME.png OUT.png [--size 1440x1080] [--frames 3] [--uvtop]
       [--input-scale K] [--hdr] [--array] [--compare RETROARCH_OUT.png] [--set NAME=VALUE ...]
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


def unity_program(ctx, path, flip, define=None):
    """The Unity pass in path (HLSL, LRVert and LRFrag, with the #define of its ShaderLab pass)
    as a GL program."""
    key = (path, flip, define)
    if key not in _cache:
        stages = {}
        for short, entry in (("vert", "LRVert"), ("frag", "LRFrag")):
            spv = TMP / f"{path.stem}{define or ''}.{short}.spv"
            run("glslangValidator", "-D", "-V", "-S", short, "-e", entry, "-o", spv,
                *([f"-D{define}"] if define else []), path)
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


# HDR (--hdr): the picture Unity's post-processing hands on, in nits in Rec.2020 (URP with
# post-processing, HDRP), the display's levels as in RetroArch's hdr presets
REC709_TO_REC2020 = np.array([[0.627402, 0.329292, 0.043306],
                              [0.069095, 0.919544, 0.011360],
                              [0.016394, 0.088028, 0.895578]])


def pq(nits):
    """SMPTE ST 2084, as present-hdr.slang."""
    m1, m2, c1, c2, c3 = 0.1593017578125, 78.84375, 0.8359375, 18.8515625, 18.6875
    y = np.clip(nits / 10000.0, 0.0, 1.0) ** m1
    return ((c1 + c2 * y) / (1.0 + c3 * y)) ** m2


def preset_values(name, overrides):
    table = parameter_table()
    values = {n: d[1] for n, d in table.items()}
    values.update(mp.BASE)
    values.update(mp.PRESETS[name])
    values.update(mp.HDR)
    values.update(overrides)
    return values


# ---------------------------------------------------------------- Unity

def unity_plan(frame_srgb, values, out_size, frames, input_scale=1, hdr=False, array=False, max_texture=16384):
    """What LolaRetroChain.cs draws, as data: the textures (id -> size, format "f1" 8 bits, "f2"
    half or "f4" float, filter, slices, data), every draw of every frame (pass file and #define,
    target, uniform values, textures by name) and the output's id. tools/webgl-preview.py runs the
    same plan in a browser."""
    H, W = frame_srgb.shape[:2]
    console = (W, H)
    names = ["source"] + [p["src"] for p in CHAIN]
    files = [("lola-retro-source.hlsl", "LR_INPUT_ARRAY" if array else None)] + [
        ("Passes/crt-hdr.hlsl", None) if hdr and n == "crt" else
        ("lola-retro-output-hdr.hlsl", None) if hdr and n == "present" else
        (f"Passes/{n}.hlsl", None) for n in names[1:]]
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
        # as LolaRetroChain.cs: no wider than the GPU's largest texture (SystemInfo.maxTextureSize)
        size[i] = (min(size_of(sx, size[prev][0], out_size[0]), max_texture), size_of(p["sy"], size[prev][1], out_size[1]))
        prev = i

    # the input: linear light, rows from the bottom (Unity's texture layout); input_scale: the
    # camera's image at that many screen pixels per console pixel
    lin = np.where(frame_srgb <= 0.04045, frame_srgb / 12.92, ((frame_srgb + 0.055) / 1.055) ** 2.4)
    lin = lin.repeat(input_scale, axis=0).repeat(input_scale, axis=1)
    IW, IH = W * input_scale, H * input_scale
    to_pipeline = REC709_TO_REC2020 * values["LR_NITS_WHITE"] if hdr else np.eye(3)
    lin = lin @ to_pipeline.T
    rgba = np.ascontiguousarray(np.dstack([lin, np.ones((IH, IW))])[::-1]).astype("f4")
    if array:
        # the frame in slice 1 of two, noise in slice 0 (one eye of single-pass XR)
        noise = np.random.default_rng(1).random(rgba.shape).astype("f4") * 100.0
        rgba = np.stack([noise, rgba])
    textures = {"input": dict(size=(IW, IH), kind="f4", linear=False, slices=2 if array else 0, data=rgba)}
    rows = lambda m: {f"Row{r}": list(m[r]) + [0.0] for r in range(3)}          # noqa: E731
    matrices = {f"LR_Input{k}": v for k, v in rows(np.linalg.inv(to_pipeline)).items()}
    matrices.update({f"LR_Output{k}": v for k, v in rows(to_pipeline).items()})

    for k, i in enumerate(run_idx):
        if names[i] not in ("crt", "present"):
            # each target gets the filter of the pass that reads it
            textures[names[i]] = dict(size=size[i], kind="f2" if float_of[i] else "f1",
                                      linear=linear_of[run_idx[k + 1]], slices=0, data=None)
    crt_i = names.index("crt")
    for g in ("afterglow0", "afterglow1"):
        textures[g] = dict(size=size[crt_i], kind="f2", linear=False, slices=0, data=None)
    textures["output"] = dict(size=out_size, kind="f4", linear=False, slices=0, data=None)

    alias = {"Original": "source", "LR_Decoded": "decode", "LR_Glow": "glow-v",
             "LR_Beam": "show-lines" if show else "beam"}
    draws = []
    for f in range(frames):
        cur, last = f"afterglow{f & 1}", f"afterglow{(f + 1) & 1}"
        source_size = console
        for k, i in enumerate(run_idx):
            tgt = cur if names[i] == "crt" else "output" if names[i] == "present" else names[i]
            osz = textures[tgt]["size"]
            vals = dict(values)
            vals.update(LR_SourceSize=[source_size[0], source_size[1], 1 / source_size[0], 1 / source_size[1]],
                        LR_OriginalSize=[W, H, 1 / W, 1 / H],
                        LR_OutputSize=[osz[0], osz[1], 1 / osz[0], 1 / osz[1]],
                        LR_FrameCountI=f, LR_OutputLinear=1.0,
                        LR_InputLinear=1.0, LR_InputRect=[0, 0, IW, IH], LR_InputSlice=1.0, **matrices)
            tex_of = {"LR_InputArray" if array else "LR_Input": "input", "LR_CRTFeedback": last}
            if k > 0:
                prev_i = run_idx[k - 1]
                tex_of["Source"] = cur if names[prev_i] == "crt" else names[prev_i]
            tex_of.update({n: t for n, t in alias.items() if t in textures})
            draws.append(dict(file=files[i][0], define=files[i][1], target=tgt, values=vals, textures=tex_of))
            source_size = osz
    return dict(textures=textures, draws=draws, output="output")


def unity_signal(lin_out, hdr):
    """The output's linear light (rows from the bottom) as the display gets it: 8-bit sRGB codes
    / 255 (Unity's sRGB backbuffer), or the PQ signal for HDR10 (Unity's last pass, here exact)."""
    lin_out = lin_out[::-1].astype(np.float64)
    if hdr:
        return pq(lin_out)
    lin_out = np.clip(lin_out, 0.0, 1.0)
    srgb = np.where(lin_out <= 0.0031308, lin_out * 12.92, 1.055 * lin_out ** (1 / 2.4) - 0.055)
    return np.floor(srgb * 255.0 + 0.5) / 255.0


def render_unity(ctx, plan, uvtop, hdr):
    """Run a plan of unity_plan with moderngl."""
    tex = {}
    for name, t in plan["textures"].items():
        if t["slices"]:
            g = ctx.texture_array((*t["size"], t["slices"]), 4, t["data"].tobytes(), dtype=t["kind"])
        else:
            g = ctx.texture(t["size"], 4, None if t["data"] is None else t["data"].tobytes(), dtype=t["kind"])
        g.filter = (moderngl.LINEAR, moderngl.LINEAR) if t["linear"] else (moderngl.NEAREST, moderngl.NEAREST)
        g.repeat_x = g.repeat_y = False
        tex[name] = (g, None if t["data"] is not None else ctx.framebuffer(color_attachments=[g]))
        if tex[name][1] is not None:
            tex[name][1].clear(0, 0, 0, 0)
    for d in plan["draws"]:
        prog = unity_program(ctx, UNITY / "Shaders" / d["file"], uvtop, d["define"])
        prog.set_block(ctx, dict(d["values"], LR_UVTop=1.0 if uvtop else 0.0))
        for unit, u in enumerate(prog.samplers()):
            n = texture_name(u, d["textures"])
            assert n, f"{d['file']}: no texture for {u}"
            tex[d["textures"][n]][0].use(location=unit)
            prog.prog[u].value = unit
        tex[d["target"]][1].use()
        ctx.vertex_array(prog.prog, []).render(moderngl.TRIANGLES, vertices=3)
    out = tex[plan["output"]][0]
    w, h = out.size
    return unity_signal(np.frombuffer(out.read(), dtype="f4").reshape(h, w, 4)[:, :, :3], hdr)


# ---------------------------------------------------------------- RetroArch

def render_retroarch(ctx, frame_srgb, name, values, out_size, frames, hdr=False):
    """The picture as the display gets it: 8-bit codes / 255, or with hdr the HDR10 signal (here
    in float, not 10 bits)."""
    H, W = frame_srgb.shape[:2]
    passes = mp.with_native_pass(mp.PRESET_PASSES.get(name, mp.PASSES))
    passes = parse_chain(mp.hdr_passes(passes) if hdr else passes)
    frame = ctx.texture((W, H), 4, (np.dstack([frame_srgb, np.ones((H, W))]) * 255 + 0.5).astype("u1").tobytes())
    sizes, prev = [], (W, H)
    for p in passes:
        prev = (size_of(p["sx"], prev[0], out_size[0]) if p["sx"] else out_size[0],
                size_of(p["sy"], prev[1], out_size[1]) if p["sy"] else out_size[1])
        sizes.append(prev)
    outs = []
    for i, p in enumerate(passes):
        if i == len(passes) - 1:
            outs.append((lambda t: (t, ctx.framebuffer(color_attachments=[t])))(ctx.texture(sizes[i], 4, dtype="f4"))
                        if hdr else target(ctx, *sizes[i], False))
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
    if hdr:
        data = np.frombuffer(outs[-1][0].read(), dtype="f4").reshape(sizes[-1][1], sizes[-1][0], 4)
        return data[:, :, :3].astype(np.float64)
    data = np.frombuffer(outs[-1][0].read(), dtype="u1").reshape(sizes[-1][1], sizes[-1][0], 4)
    return data[:, :, :3] / 255.0


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
    ap.add_argument("--hdr", action="store_true", help="the HDR passes, against RetroArch's hdr presets")
    ap.add_argument("--array", action="store_true", help="the input is slice 1 of a texture array (XR, HDRP)")
    ap.add_argument("--compare", metavar="RETROARCH_OUT")
    ap.add_argument("--set", action="append", default=[], metavar="NAME=VALUE")
    a = ap.parse_args()
    out_size = tuple(int(v) for v in a.size.split("x"))
    overrides = {k: float(v) for k, v in (s.split("=") for s in a.set)}
    values = preset_values(a.preset, overrides)
    frame = np.asarray(Image.open(a.frame).convert("RGB"), dtype=np.float64) / 255.0
    ctx = moderngl.create_standalone_context(require=410)
    plan = unity_plan(frame, values, out_size, a.frames, a.input_scale, a.hdr, a.array)
    unity = render_unity(ctx, plan, a.uvtop, a.hdr)
    # HDR: the PQ signal shown as it is (a dim, flat picture on an SDR screen)
    save = lambda img, path: Image.fromarray(np.clip(img * 255.0 + 0.5, 0, 255).astype(np.uint8)).save(path)   # noqa: E731
    save(unity, a.out)
    print("wrote", a.out)
    if a.compare:
        ra = render_retroarch(ctx, frame, a.preset, values, out_size, a.frames, a.hdr)
        save(ra, a.compare)
        print("wrote", a.compare)
        # in steps of the output: 8 bits, or 10 bits for HDR10 (RetroArch's HDR pass dithers by half a step)
        steps = 1023.0 if a.hdr else 255.0
        d = np.abs(unity - ra).max(axis=2) * steps
        print(f"difference: max {d.max():.1f}, mean {d.mean():.4f}, pixels off by more than 1: "
              f"{(d > 1.0001).mean() * 100:.3f}% (steps of {'10' if a.hdr else '8'} bits)")


if __name__ == "__main__":
    main()
