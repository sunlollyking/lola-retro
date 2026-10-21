#!/usr/bin/env python3
"""Run the Unity version of Lola Retro in WebGL 2 in a browser, and compare it with the RetroArch passes.

Unity's WebGL builds run on WebGL 2 (OpenGL ES 3.0): GLSL ES 3.00, float targets only with
EXT_color_buffer_float, no samplers in the shader (each texture's own filter), and the GPU's
texture size limit. This runs the Unity passes there, as Unity's C# side runs them
(tools/unity-preview.py makes the plan: the same textures, draws and uniforms), with the passes
compiled the way Unity's WebGL builds get them: HLSL to SPIR-V (glslang) to GLSL ES 3.00
(SPIRV-Cross). The page compares the picture with the slang passes' (rendered here with moderngl)
and times the chain.

It writes a folder with the page and its data. Serve it and open it in a browser:
    tools/webgl-preview.py consumer-tv-composite test-images/platformer.png OUT_DIR
    python3 -m http.server -d OUT_DIR 8123
then http://localhost:8123/. The result is on the page and in window.lolaResult.

Needs: glslangValidator and spirv-cross on PATH; pip install moderngl numpy pillow.

Usage: tools/webgl-preview.py PRESET FRAME.png OUT_DIR [--size 1440x1080] [--frames 3]
       [--input-scale K] [--max-texture N] [--set NAME=VALUE ...]
"""
import argparse
import base64
import json
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import importlib.util                                                          # noqa: E402
_spec = importlib.util.spec_from_file_location("unity_preview", Path(__file__).resolve().parent / "unity-preview.py")
up = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(up)


def es_program(path, define):
    """GLSL ES 3.00 of a Unity pass (both stages), its uniform blocks and samplers."""
    stages = {}
    for short, entry in (("vert", "LRVert"), ("frag", "LRFrag")):
        spv = up.TMP / f"{path.stem}{define or ''}.es.{short}.spv"
        up.run("glslangValidator", "-D", "-V", "-S", short, "-e", entry, "-o", spv,
               *([f"-D{define}"] if define else []), path)
        refl = json.loads(up.run("spirv-cross", spv, "--reflect", "--entry", entry))
        loc = "outputs" if short == "vert" else "inputs"
        names = [a for v in refl.get(loc, []) for a in
                 ("--rename-interface-variable", "out" if short == "vert" else "in", v["location"], f"lr_var{v['location']}")]
        glsl = up.run("spirv-cross", spv, "--es", "--version", "300", "--entry", entry, *names)
        # Unity's float is highp on WebGL
        glsl = glsl.replace("precision mediump float;", "precision highp float;")
        stages[short] = (glsl, refl)
    blocks = {}
    for _, refl in stages.values():
        for ubo in refl.get("ubos", []):
            t = refl["types"][ubo["type"]]
            blocks[t["name"].replace("$", "_")] = (ubo["block_size"], {m["name"]: (m["offset"], m["type"]) for m in t["members"]})
    samplers = sorted(set(re.findall(r"uniform\s+\w*\s*sampler2D(?:Array)?\s+(\w+)\s*;", stages["frag"][0])))
    return dict(vs=stages["vert"][0], fs=stages["frag"][0], blocks=blocks, samplers=samplers)


def pack(blocks, values):
    """Each uniform block's data (std140, from the reflection's offsets), base64."""
    out = {}
    for name, (size, members) in blocks.items():
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
            else:
                raise SystemExit(f"uniform type {typ} not handled")
        out[name] = base64.b64encode(bytes(data)).decode()
    return out


PAGE = r"""<!doctype html>
<html><head><meta charset="utf-8"><title>Lola Retro WebGL 2 test</title>
<style>body{background:#111;color:#ddd;font:14px system-ui;margin:16px}canvas{max-width:100%;border:1px solid #333}pre{white-space:pre-wrap}</style>
</head><body><h3>Lola Retro, Unity passes in WebGL 2</h3><pre id="log">running…</pre><canvas id="view"></canvas>
<script>
const log = s => { document.getElementById('log').textContent += '\n' + s; };
async function main() {
  const plan = await (await fetch('plan.json')).json();
  const input = new Float32Array(await (await fetch('input.bin')).arrayBuffer());
  const ref = new Uint8Array(await (await fetch('reference.bin')).arrayBuffer());
  const canvas = document.createElement('canvas');
  const gl = canvas.getContext('webgl2', {antialias: false});
  if (!gl) throw new Error('no WebGL 2');
  const result = {renderer: gl.getParameter(gl.RENDERER), version: gl.getParameter(gl.VERSION),
                  maxTextureSize: gl.getParameter(gl.MAX_TEXTURE_SIZE),
                  colorBufferFloat: !!gl.getExtension('EXT_color_buffer_float')};
  const dbg = gl.getExtension('WEBGL_debug_renderer_info');
  if (dbg) result.unmaskedRenderer = gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL);
  log(JSON.stringify(result));

  // programs
  const t0 = performance.now();
  const progs = {};
  for (const [key, p] of Object.entries(plan.programs)) {
    const sh = (type, src) => { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(key + ': ' + gl.getShaderInfoLog(s)); return s; };
    const pr = gl.createProgram();
    gl.attachShader(pr, sh(gl.VERTEX_SHADER, p.vs)); gl.attachShader(pr, sh(gl.FRAGMENT_SHADER, p.fs));
    gl.linkProgram(pr);
    if (!gl.getProgramParameter(pr, gl.LINK_STATUS)) throw new Error(key + ': ' + gl.getProgramInfoLog(pr));
    progs[key] = pr;
  }
  result.compileMs = Math.round(performance.now() - t0);
  log('compiled ' + Object.keys(progs).length + ' programs in ' + result.compileMs + ' ms');

  // textures and targets
  const fmt = {f1: [gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE], f2: [gl.RGBA16F, gl.RGBA, gl.HALF_FLOAT], f4: [gl.RGBA32F, gl.RGBA, gl.FLOAT]};
  const tex = {};
  for (const [name, t] of Object.entries(plan.textures)) {
    const [w, h] = t.size, f = fmt[t.kind], g = gl.createTexture();
    const target = t.slices ? gl.TEXTURE_2D_ARRAY : gl.TEXTURE_2D;
    gl.bindTexture(target, g);
    if (t.slices) gl.texImage3D(target, 0, f[0], w, h, t.slices, 0, f[1], f[2], input);
    else gl.texImage2D(target, 0, f[0], w, h, 0, f[1], f[2], name === 'input' ? input : null);
    const filt = t.linear ? gl.LINEAR : gl.NEAREST;
    gl.texParameteri(target, gl.TEXTURE_MIN_FILTER, filt); gl.texParameteri(target, gl.TEXTURE_MAG_FILTER, filt);
    gl.texParameteri(target, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.texParameteri(target, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    let fbo = null;
    if (name !== 'input') {
      fbo = gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, g, 0);
      const st = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
      if (st !== gl.FRAMEBUFFER_COMPLETE) throw new Error(name + ' (' + t.kind + ' ' + w + 'x' + h + '): framebuffer incomplete ' + st);
      gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
    }
    tex[name] = {g, fbo, target, size: [w, h]};
  }
  const vao = gl.createVertexArray();
  const ubos = [];
  const draws = plan.draws.map(d => {
    const pr = progs[d.program];
    const blocks = Object.entries(d.blocks).map(([name, b64], k) => {
      const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
      const buf = gl.createBuffer(); gl.bindBuffer(gl.UNIFORM_BUFFER, buf);
      gl.bufferData(gl.UNIFORM_BUFFER, bytes, gl.STATIC_DRAW); ubos.push(buf);
      return [gl.getUniformBlockIndex(pr, name), k, buf];
    });
    return {pr, blocks, d};
  });
  function run() {
    for (const {pr, blocks, d} of draws) {
      gl.useProgram(pr);
      for (const [idx, k, buf] of blocks) { if (idx === gl.INVALID_INDEX) continue; gl.uniformBlockBinding(pr, idx, k); gl.bindBufferBase(gl.UNIFORM_BUFFER, k, buf); }
      d.samplers.forEach(([u, name], unit) => {
        gl.activeTexture(gl.TEXTURE0 + unit); gl.bindTexture(tex[name].target, tex[name].g);
        gl.uniform1i(gl.getUniformLocation(pr, u), unit);
      });
      const t = tex[d.target];
      gl.bindFramebuffer(gl.FRAMEBUFFER, t.fbo); gl.viewport(0, 0, t.size[0], t.size[1]);
      gl.bindVertexArray(vao); gl.drawArrays(gl.TRIANGLES, 0, 3);
    }
  }
  run();
  const err = gl.getError();
  if (err) throw new Error('GL error ' + err);

  // the output: linear light, rows from the bottom -> 8-bit sRGB codes, compared with RetroArch's
  const out = tex[plan.output], [W, H] = out.size;
  const px = new Float32Array(W * H * 4);
  gl.bindFramebuffer(gl.FRAMEBUFFER, out.fbo); gl.readPixels(0, 0, W, H, gl.RGBA, gl.FLOAT, px);
  const code = v => { v = Math.min(Math.max(v, 0), 1); return Math.floor((v <= 0.0031308 ? v * 12.92 : 1.055 * Math.pow(v, 1 / 2.4) - 0.055) * 255 + 0.5); };
  const img = new ImageData(W, H);
  let max = 0, sum = 0, over = 0;
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const i = ((H - 1 - y) * W + x) * 4, j = (y * W + x) * 3, o = (y * W + x) * 4;
    let m = 0;
    for (let c = 0; c < 3; c++) { const v = code(px[i + c]); img.data[o + c] = v; const dd = Math.abs(v - ref[j + c]); m = Math.max(m, dd); sum += dd; }
    img.data[o + 3] = 255; max = Math.max(max, m); if (m > 1) over++;
  }
  Object.assign(result, {width: W, height: H, maxDiff: max, meanDiff: sum / (W * H * 3), percentOffByMoreThan1: 100 * over / (W * H)});
  const view = document.getElementById('view'); view.width = W; view.height = H; view.getContext('2d').putImageData(img, 0, 0);

  // time the chain (one frame: every pass once)
  const perFrame = plan.draws.length / plan.frames;
  const oneFrame = draws.slice(draws.length - perFrame);
  const saved = draws.splice(0, draws.length, ...oneFrame);
  run(); gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.FLOAT, new Float32Array(4));
  const n = 20, t1 = performance.now();
  for (let k = 0; k < n; k++) run();
  gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.FLOAT, new Float32Array(4));
  result.msPerFrame = +((performance.now() - t1) / n).toFixed(2);
  draws.splice(0, draws.length, ...saved);
  log(JSON.stringify(result, null, 1));
  window.lolaResult = result;
}
main().catch(e => { log('FAILED: ' + e.message); window.lolaResult = {error: e.message}; });
</script></body></html>
"""


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("preset")
    ap.add_argument("frame")
    ap.add_argument("out_dir")
    ap.add_argument("--size", default="1440x1080")
    ap.add_argument("--frames", type=int, default=3)
    ap.add_argument("--input-scale", type=int, default=1, metavar="K")
    ap.add_argument("--max-texture", type=int, default=16384, metavar="N",
                    help="the GPU's largest texture: the beam passes are no wider (4096 on many phones)")
    ap.add_argument("--set", action="append", default=[], metavar="NAME=VALUE")
    a = ap.parse_args()
    out = Path(a.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    size = tuple(int(v) for v in a.size.split("x"))
    values = up.preset_values(a.preset, {k: float(v) for k, v in (s.split("=") for s in a.set)})
    frame = np.asarray(Image.open(a.frame).convert("RGB"), dtype=np.float64) / 255.0

    plan = up.unity_plan(frame, values, size, a.frames, a.input_scale, max_texture=a.max_texture)
    programs, draws = {}, []
    for d in plan["draws"]:
        key = d["file"] + (f"#{d['define']}" if d["define"] else "")
        if key not in programs:
            programs[key] = es_program(up.UNITY / "Shaders" / d["file"], d["define"])
        p = programs[key]
        samplers = []
        for u in p["samplers"]:
            n = up.texture_name(u, d["textures"])
            assert n, f"{key}: no texture for {u}"
            samplers.append([u, d["textures"][n]])
        # WebGL is OpenGL: Unity's targets start at the bottom
        draws.append(dict(program=key, target=d["target"], samplers=samplers,
                          blocks=pack(p["blocks"], dict(d["values"], LR_UVTop=0.0))))
    textures = {n: dict(size=list(t["size"]), kind=t["kind"], linear=t["linear"], slices=t["slices"])
                for n, t in plan["textures"].items()}
    (out / "plan.json").write_text(json.dumps(dict(
        programs={k: dict(vs=p["vs"], fs=p["fs"]) for k, p in programs.items()},
        textures=textures, draws=draws, output=plan["output"], frames=a.frames)))
    (out / "input.bin").write_bytes(plan["textures"]["input"]["data"].astype("f4").tobytes())

    # RetroArch's picture, 8-bit codes, top row first
    ctx = up.moderngl.create_standalone_context(require=410)
    ra = up.render_retroarch(ctx, frame, a.preset, values, size, a.frames)
    (out / "reference.bin").write_bytes(np.floor(ra * 255.0 + 0.5).astype(np.uint8).tobytes())
    Image.fromarray(np.floor(ra * 255.0 + 0.5).astype(np.uint8)).save(out / "reference.png")
    (out / "index.html").write_text(PAGE)
    print("wrote", out, f"({len(programs)} programs, {len(draws)} draws)")


if __name__ == "__main__":
    main()
