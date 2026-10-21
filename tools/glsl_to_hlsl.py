#!/usr/bin/env python3
"""Translate the GLSL of the Lola Retro passes to HLSL, for the Unity version (tools/make_unity.py).

Only what the passes use is handled, and anything that would come out wrong stops the
conversion instead of slipping through:
- types: vec, ivec, uvec, bvec -> float, int, uint, bool vectors; matrices see below;
- a vector constructor with one argument (vec3(0.0), vec3(v4), vec3(greaterThan(...))) becomes a
  cast, which HLSL needs for both a scalar and a longer vector;
- array constructors float[5](...) become initialiser lists {...};
- mix, fract, inversesqrt, atan(y, x) -> lerp, frac, rsqrt, atan2; mod keeps GLSL's meaning
  (x - y floor(x / y), not fmod) through lr_mod; greaterThan, equal and the others become
  comparisons;
- matrices: GLSL fills them column by column, HLSL row by row, so mat2/mat3(...) become
  lr_mat2/lr_mat3(...), which build the same matrix, and M * v becomes mul(M, v);
  inverse(M) becomes lr_inverse(M);
- const outside functions becomes static const (a plain global is a uniform in HLSL);
- names that GLSL allows and HLSL reserves (line) get an lr_ prefix.
Comments are kept as they are.
"""
import re

# names GLSL code may use that are keywords in HLSL
HLSL_RESERVED = ["line", "point", "triangle", "lineadj", "triangleadj", "linear", "nointerpolation",
                 "snorm", "unorm", "matrix", "vector", "pass", "technique", "string", "register",
                 "packoffset", "groupshared", "compile", "dword", "row_major", "column_major",
                 "cbuffer", "tbuffer", "sampler1D", "sampler3D", "Texture2D", "SamplerState"]

TYPE_MAP = {f"{p}vec{n}": f"{t}{n}" for p, t in (("", "float"), ("i", "int"), ("u", "uint"), ("b", "bool"))
            for n in (2, 3, 4)}
TYPE_MAP.update({"mat2": "float2x2", "mat3": "float3x3", "mat4": "float4x4"})

COMPARISONS = {"greaterThan": ">", "greaterThanEqual": ">=", "lessThan": "<", "lessThanEqual": "<=",
               "equal": "==", "notEqual": "!="}
RENAMED = {"mix": "lerp", "fract": "frac", "inversesqrt": "rsqrt", "mod": "lr_mod", "inverse": "lr_inverse"}


def mask_comments(text):
    """The text with every comment replaced by a placeholder without brackets or names, and the
    comments, for unmask."""
    comments = []

    def keep(m):
        comments.append(m.group(0))
        return f"\x01{len(comments) - 1}\x02"
    return re.sub(r"//[^\n]*|/\*.*?\*/", keep, text, flags=re.S), comments


def unmask(text, comments):
    return re.sub(r"\x01(\d+)\x02", lambda m: comments[int(m.group(1))], text)


def close_paren(text, i):
    """Index just past the bracket that closes the one at text[i]."""
    pairs = {"(": ")", "[": "]", "{": "}"}
    stack = [pairs[text[i]]]
    j = i + 1
    while stack:
        c = text[j]
        if c in pairs:
            stack.append(pairs[c])
        elif c in ")]}":
            assert c == stack.pop(), f"unbalanced brackets near: {text[i:i + 80]!r}"
        j += 1
    return j


def split_args(inner):
    """The top-level arguments of a call, without the brackets."""
    if not inner.strip():
        return []
    args, depth, start = [], 0, 0
    for j, c in enumerate(inner):
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "," and depth == 0:
            args.append(inner[start:j])
            start = j + 1
    args.append(inner[start:])
    return [a.strip() for a in args]


def rewrite_calls(text, name_re, fn):
    """Replace every call name(...) whose name matches name_re with fn(name, args), innermost
    first, so that the arguments are already rewritten. fn returns None to leave a call."""
    pat = re.compile(r"(?<![\w.])(" + name_re + r")\s*\(")
    out, i = [], 0
    while True:
        m = pat.search(text, i)
        if not m:
            out.append(text[i:])
            return "".join(out)
        end = close_paren(text, m.end() - 1)
        inner = rewrite_calls(text[m.end():end - 1], name_re, fn)
        new = fn(m.group(1), split_args(inner))
        out.append(text[i:m.start()])
        out.append(new if new is not None else text[m.start():m.end()] + inner + ")")
        i = end


def operand_after(text, i):
    """(start, end) of the operand after text[i:] (spaces skipped): a name or a call, with any
    member, swizzle and index after it, a bracketed group, or a number."""
    j = i
    while text[j] in " \t\n":
        j += 1
    m = re.match(r"(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?", text[j:])
    if m:
        return j, j + m.end()
    start = j
    if text[j] == "(":
        j = close_paren(text, j)
    else:
        m = re.match(r"\w+", text[j:])
        assert m, f"no operand at: {text[j:j + 40]!r}"
        j += m.end()
        if text[j] == "(":
            j = close_paren(text, j)
    while True:
        m = re.match(r"\.\w+", text[j:])
        if m:
            j += m.end()
        elif text[j] == "[":
            j = close_paren(text, j)
        else:
            return start, j


def operand_before(text, i):
    """Start of the operand that ends just before text[i] (spaces skipped): a name or a call,
    with any member, swizzle and index, or a bracketed group."""
    j = i
    while text[j - 1] in " \t\n":
        j -= 1
    while True:
        if text[j - 1] in ")]":
            # back to the opening bracket
            depth = 0
            while True:
                j -= 1
                depth += {")": 1, "]": 1, "(": -1, "[": -1}.get(text[j], 0)
                if depth == 0:
                    break
        m = re.search(r"\w+$", text[:j])
        if m and (text[j - 1] not in ")]" or text[j] in "(["):
            j = m.start()
        if text[j - 1] == ".":
            j -= 1
            continue
        return j


def matrix_products(text, mat_names):
    """M * v -> mul(M, v) and v * M -> mul(v, M), where M is a matrix: a constructor, a call
    to a function that returns one, or a matrix variable. A product of matrices and a product
    whose other operand is itself a product are not used by the passes and stop the conversion."""
    names = "|".join(sorted(set(mat_names), key=len, reverse=True))
    pat = re.compile(r"(?<![\w.])(" + names + r")\b")
    pos = 0
    while True:
        m = pat.search(text, pos)
        if not m:
            return text
        start, end = m.start(), m.end()
        if text[end] == "(":
            end = close_paren(text, end)
        k = end
        while text[k] in " \t":
            k += 1
        before = text[:start].rstrip(" \t")
        if text[k] == "*" and text[k + 1] != "=":
            s, e = operand_after(text, k + 1)
            assert not pat.match(text, s), f"matrix * matrix is not handled: {text[start:e]!r}"
            assert text[e:].lstrip(" \t")[:1] not in ("*", "/"), f"M * v * x is not handled: {text[start:e + 10]!r}"
            text = text[:start] + f"mul({text[start:end]}, {text[s:e]})" + text[e:]
        elif before.endswith("*") and not before.endswith("/*"):
            s = operand_before(text, len(before) - 1)
            assert text[:s].rstrip(" \t")[-1:] not in ("*", "/"), f"x * v * M is not handled: {text[s - 10:end]!r}"
            text = text[:s] + f"mul({text[s:len(before) - 1].rstrip()}, {text[start:end]})" + text[end:]
            start = s
        pos = start + 4


def top_level_blocks(text):
    """(start, end) of every { } block outside any other: function bodies, struct bodies."""
    blocks, i = [], 0
    while True:
        j = text.find("{", i)
        if j < 0:
            return blocks
        k = close_paren(text, j)
        blocks.append((j, k))
        i = k


def global_consts(text):
    """const at the top level (outside any braces) -> static const."""
    out, depth, i = [], 0, 0
    for m in re.finditer(r"[{}]|(?<![\w.])const\b", text):
        out.append(text[i:m.start()])
        tok = m.group(0)
        if tok == "{":
            depth += 1
        elif tok == "}":
            depth -= 1
        elif depth == 0:
            tok = "static const"
        out.append(tok)
        i = m.end()
    out.append(text[i:])
    return "".join(out)


def convert(text):
    """GLSL code -> HLSL. The uniform blocks, the layout declarations and the texture reads must
    already be taken out (make_unity.py does that)."""
    text, comments = mask_comments(text)

    # M * v first, while the matrices are easy to find: constructors, inverse(), functions that
    # return a matrix and matrices outside functions, and in each function its own matrix
    # variables (a name can be a matrix in one function and a vector in another)
    mats = re.findall(r"\bmat[234]\s+(\w+)\s*\(", text) + ["mat2", "mat3", "inverse"]
    bodies = top_level_blocks(text)
    outside = "".join(text[a:b] for a, b in zip([0] + [e for _, e in bodies], [s for s, _ in bodies] + [len(text)]))
    mats += re.findall(r"\bmat[234]\s+(\w+)\s*=", outside)
    parts, last = [], 0
    for s, e in bodies:
        local = re.findall(r"\bmat[234]\s+(\w+)\s*[=;]", text[s:e])
        parts += [matrix_products(text[last:s], mats), matrix_products(text[s:e], mats + local)]
        last = e
    text = "".join(parts) + matrix_products(text[last:], mats)

    # array.length() -> the array's size from its declaration
    def length(m):
        sizes = set(re.findall(r"\b" + m.group(1) + r"\s*\[\s*(\d+)\s*\]\s*=", text))
        assert len(sizes) == 1, f"size of {m.group(1)} not found"
        return f"{sizes.pop()}"
    text = re.sub(r"\b(\w+)\.length\(\)", length, text)

    # array constructors -> initialiser lists
    while True:
        m = re.search(r"\b(?:float|int|uint|bool|[iub]?vec[234])\s*\[\s*\d*\s*\]\s*\(", text)
        if not m:
            break
        end = close_paren(text, m.end() - 1)
        text = text[:m.start()] + "{" + text[m.end():end - 1] + "}" + text[end:]

    # matrix constructors: the same arguments, in GLSL's order
    def mat_ctor(name, args):
        n = int(name[-1])
        assert len(args) == n * n, f"{name} with {len(args)} arguments"
        return f"lr_mat{n}({', '.join(args)})"
    text = rewrite_calls(text, r"mat[23]", mat_ctor)
    assert not re.search(r"\bmat4\s*\(", text), "mat4 constructors are not handled"

    # GLSL built-ins with another name or meaning in HLSL
    def builtin(name, args):
        if name in RENAMED:
            return f"{RENAMED[name]}({', '.join(args)})"
        if name == "atan":
            return f"atan2({', '.join(args)})" if len(args) == 2 else None
        assert len(args) == 2, name
        return f"(({args[0]}) {COMPARISONS[name]} ({args[1]}))"
    text = rewrite_calls(text, "|".join(sorted(list(RENAMED) + list(COMPARISONS) + ["atan"],
                                               key=len, reverse=True)), builtin)
    for f in ("dFdx", "dFdy", "fwidth", "texture", "textureLod", "texelFetch", "textureSize",
              "outerProduct", "matrixCompMult", "roundEven", "transpose", "determinant"):
        assert not re.search(r"(?<![\w.])" + f + r"\s*\(", text), f"{f} is not handled"

    # types
    text = re.sub(r"\b(" + "|".join(TYPE_MAP) + r")\b", lambda m: TYPE_MAP[m.group(1)], text)

    # one-argument vector constructors -> casts
    text = rewrite_calls(text, r"(?:float|int|uint|bool)[234]",
                         lambda name, args: f"(({name})({args[0]}))" if len(args) == 1 else None)

    text = global_consts(text)
    text = re.sub(r"(?<!\w)(" + "|".join(HLSL_RESERVED) + r")\b", r"lr_\1", text)
    return unmask(text, comments)
