// Lola Retro - pass 6: electron beam, phosphor mask and glass, at screen resolution.
// Shared by crt.slang (SDR) and crt-hdr.slang (HDR).
// - Each source line is a beam with a Gaussian profile whose width grows with brightness
//   (per colour gun), so bright lines merge and dark lines show gaps.
// - Interlaced sources alternate fields; VGA 200-line modes are scanned twice.
// - Mask: aperture grille (with damper wires), slot mask, shadow mask or a projection screen.
// - Glass: curvature, halation, white point, vignette, room reflection, phosphor afterglow.
// Options for the GLSL port (tools/make_glsl.py), off in RetroArch:
//   LR_FIELDS       an Amiga frame with every line drawn twice is one line per row pair
//                   (the fields passes find out), not an interlaced frame
//   LR_NO_FEEDBACK  no afterglow (the emulator has no feedback textures)
//   LR_FIT_43       the tube stays 4:3 whatever shape the picture area has
//   LR_PACKED_SPOT  the beam pass has no alpha channel, the spot is packed onto red
//   LR_ROWS_UP      rows count from the bottom of the screen (OpenGL), for light from above

#include "common.inc"

#pragma stage vertex
layout(location = 0) in vec4 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 0) out vec2 vTexCoord;
layout(location = 1) flat out vec3 vWhite;
layout(location = 2) flat out vec4 vTiming;   // scan mode, PAL, interlaced, internal resolution
layout(location = 3) flat out float vPhos;    // phosphor set
layout(location = 4) flat out float vShare;   // share of the active line the source covers
#ifdef LR_FIELDS
layout(location = 5) flat out float vAmiga;
#endif

// Blackbody colour of the TV's white, relative to D65
vec3 kelvinRGB(float T)
{
    float t = T / 100.0;
    float r = t <= 66.0 ? 255.0 : 329.698727446 * pow(t - 60.0, -0.1332047592);
    float g = t <= 66.0 ? 99.4708025861 * log(t) - 161.1195681661 : 288.1221695283 * pow(t - 60.0, -0.0755148492);
    float b = t >= 66.0 ? 255.0 : (t <= 19.0 ? 0.0 : 138.5177312231 * log(t - 10.0) - 305.0447927307);
    return clamp(vec3(r, g, b), 1.0, 255.0) / 255.0;
}

void main()
{
    gl_Position = global.MVP * Position;
    vTexCoord = TexCoord;
    // keep white equally bright and only shift its tint
    vec3 c = pow(kelvinRGB(global.LR_WHITE) / kelvinRGB(6500.0), vec3(2.2));
    vWhite = c / dot(c, vec3(0.2126, 0.7152, 0.0722));
    Timing T = getTiming(params.OriginalSize.xy, params.FrameCount);
    vTiming = vec4(T.scan, T.pal, T.interlace, T.scale);
    vPhos = phosphorSet(T);
    vShare = activeShare(T, params.OriginalSize.xy);
#ifdef LR_FIELDS
    vAmiga = isAmiga(T) ? 1.0 : 0.0;
#endif
    // the white is set on the phosphor drives so that it ends up at the chosen colour temperature
    if (vPhos > 1.5) vWhite = inverse(phosphorMatrix(vPhos)) * vWhite;
}

#pragma stage fragment
layout(location = 0) in vec2 vTexCoord;
layout(location = 1) flat in vec3 vWhite;
layout(location = 2) flat in vec4 vTiming;
layout(location = 3) flat in float vPhos;
layout(location = 4) flat in float vShare;
#ifdef LR_FIELDS
layout(location = 5) flat in float vAmiga;
#endif
layout(location = 0) out vec4 FragColor;
layout(set = 0, binding = 2) uniform sampler2D LR_Beam;
layout(set = 0, binding = 3) uniform sampler2D LR_Glow;
#ifndef LR_NO_FEEDBACK
layout(set = 0, binding = 4) uniform sampler2D LR_CRTFeedback;
#endif
#ifdef LR_FIELDS
layout(set = 0, binding = 5) uniform sampler2D LR_Fields;
#endif

// Beam of source line i at position x along the line (from the beam pass):
// rgb = square root of its linear brightness, a = which spot made it (PC monitor);
// 0 outside the picture
vec4 beamAt(float i, float x, float lines)
{
    if (i < 0.0 || i >= lines) return vec4(0.0);
#ifdef LR_PACKED_SPOT
    // the spot is packed onto red (see the beam pass), so the two samples are read and blended
    // here instead of by the texture filter
    int w = textureSize(LR_Beam, 0).x;
    float t = x * float(w);
    int m = int(floor(t));
    vec3 a = texelFetch(LR_Beam, ivec2(clamp(m, 0, w - 1), int(i)), 0).rgb;
    vec3 b = texelFetch(LR_Beam, ivec2(clamp(m + 1, 0, w - 1), int(i)), 0).rgb;
    float qa = floor(a.r * 0.25), qb = floor(b.r * 0.25);
    a.r -= 4.0 * qa;
    b.r -= 4.0 * qb;
    return mix(vec4(a, qa / 255.0), vec4(b, qb / 255.0), fract(t));
#else
    vec2 bs = vec2(textureSize(LR_Beam, 0));
    return textureLod(LR_Beam, vec2(x + 0.5 / bs.x, (i + 0.5) / bs.y), 0.0);
#endif
}

// Rear-projection screen: lenticular sheet with thin black stripes, no RGB pattern
float lenticular(float x)
{
    float f = fract(x);
    return smoothstep(0.0, 0.18, f) * smoothstep(1.0, 0.82, f);
}

// Shadow mask: staggered round dots
vec3 shadowDots(vec2 mp)
{
    float s = mp.x * 3.0;
    float ci = floor(s), fx = fract(s);
    float fy = fract(mp.y * 1.732 + mod(ci, 2.0) * 0.5);
    float dt = smoothstep(0.52, 0.30, length(vec2(fx - 0.5, fy - 0.5)));
    float chn = mod(ci, 3.0);
    return vec3(chn < 0.5 ? dt : 0.0, (chn > 0.5 && chn < 1.5) ? dt : 0.0, chn > 1.5 ? dt : 0.0);
}

// Aperture grille / slot mask: vertical RGB stripes
vec3 stripes(float x)
{
    float s = x * 3.0;
    vec3 m;
    for (int ch = 0; ch < 3; ch++)
    {
        float d = abs(fract((s - (float(ch) + 0.5)) / 3.0 + 0.5) - 0.5) * 3.0;
        m[ch] = smoothstep(0.75, 0.35, d);
    }
    return m;
}

// Slot mask: horizontal breaks, staggered per triad column
float slotGate(float x, float y)
{
    float yy = fract(y * 0.5 + mod(floor(x), 2.0) * 0.5);
    return smoothstep(0.0, 0.12, yy) * smoothstep(1.0, 0.88, yy);
}

// Mask averaged over 2x2 points (a and b are the corners) to keep moire down.
// Parts that only depend on x are evaluated once per column.
vec3 maskAvg(vec2 a, vec2 b, float mt)
{
    if (mt > 3.5) return vec3(0.5 * (lenticular(a.x) + lenticular(b.x)));
    if (mt > 2.5) return 0.25 * (shadowDots(a) + shadowDots(vec2(b.x, a.y)) + shadowDots(vec2(a.x, b.y)) + shadowDots(b));
    vec3 ma = stripes(a.x), mb = stripes(b.x);
    if (mt > 1.5)
        return 0.25 * (ma * (slotGate(a.x, a.y) + slotGate(a.x, b.y)) + mb * (slotGate(b.x, a.y) + slotGate(b.x, b.y)));
    return 0.5 * (ma + mb);
}

vec3 shoulder(vec3 x)
{
    const float k = 0.8;
    vec3 e = max(x - k, 0.0);
    return min(x, vec3(k)) + (1.0 - k) * (1.0 - exp(-e / (1.0 - k)));
}

// Signed distance from p to a box of half size b with corners of radius r (negative inside)
float roundBox(vec2 p, vec2 b, float r)
{
    vec2 q = abs(p) - b + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// Direction away from that box at p
vec2 boxOut(vec2 p, vec2 b, float r)
{
    return sign(p) * normalize(max(abs(p) - b + r, 1e-3));
}

// A surface turned by phi radians from facing the viewer, toward dir (z toward the viewer)
vec3 tilt(vec2 dir, float phi) { return vec3(dir * sin(phi), cos(phi)); }

// Plastic lit by the room. Diffuse: light from above, a little from the right and in front (as in
// the photos of the real monitors), plus some from the rest of the room; 1 on a surface facing
// the viewer. Sheen of a satin finish (gloss 0.5: 4% reflection head-on, more at grazing angles;
// 1 is twice that and sharper), seen from 60 cm in front of the tube: the room reflected, blurred
// (a light ceiling above, a darker desk and floor below, so faces and edges turned up look
// lighter), and the ceiling lamp, 1.2 m above the monitor, in front of it and a little to the
// right, as a soft highlight. P: position in mm (y up, z toward the viewer), n: which way the
// surface faces, occ: how much of the room's light reaches it.
vec3 plastic(vec3 albedo, vec3 n, vec3 P, vec3 light, float occ, float gloss)
{
    const vec3 L = vec3(0.3, 0.45, 0.84);
    float diffuse = 0.45 + 0.55 * max(dot(n, L), 0.0) / 0.84;
    vec3 col = albedo * diffuse;
    if (gloss > 0.0)
    {
        vec3 v = normalize(vec3(0.0, 0.0, 600.0) - P);
        vec3 r = reflect(-v, n);
        float F = 0.04 + 0.96 * pow(1.0 - max(dot(v, n), 0.0), 5.0);
        float room = 0.45 * (0.4 + 2.3 * smoothstep(-0.3, 0.7, r.y));
        vec3 l = normalize(vec3(250.0, 1200.0, 900.0) - P);
        float s = mix(12.0, 80.0, gloss);
        float lamp = 0.55 / 0.84 * max(dot(n, l), 0.0) * (s + 8.0) / 8.0 * pow(max(dot(n, normalize(l + v)), 0.0), s);
        col += 2.0 * gloss * F * (room + lamp);
    }
    return light * occ * col;
}

// Signed distance to a pill (a horizontal capsule): straight part 2h long, radius r
float pill(vec2 q, float h, float r)
{
    q.x = max(abs(q.x) - h, 0.0);
    return length(q) - r;
}

// Smooth value noise, 0..1
float valueNoise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash13(vec3(i, 5.0)), hash13(vec3(i + vec2(1.0, 0.0), 5.0)), f.x),
               mix(hash13(vec3(i + vec2(0.0, 1.0), 5.0)), hash13(vec3(i + 1.0, 5.0)), f.x), f.y);
}

// Space (Around the tube 2): layers of stars that slowly come closer and drift, as if floating
// through space, over a faint nebula. Decoration, not a simulation. s: screen position from the
// centre in screen heights, t: seconds, pix: one screen pixel in screen heights.
vec3 space(vec2 s, float t, float pix)
{
    vec2 drift = vec2(sin(t * 0.013), sin(t * 0.0091 + 1.3)) * 0.08;
    vec2 np = (s + 0.2 * drift) * 1.6 + vec2(0.003 * t, 0.0);
    float n = 0.0, a = 0.5;
    for (int o = 0; o < 4; o++)
    {
        n += a * valueNoise(np);
        np = np * 2.03 + 17.0;
        a *= 0.5;
    }
    vec3 sum = mix(vec3(0.05, 0.07, 0.22), vec3(0.20, 0.05, 0.18), valueNoise(s * 0.7 + 40.0)) * 0.09 * smoothstep(0.4, 0.9, n);
    for (int i = 0; i < 5; i++)
    {
        float k = t * 0.008 + float(i) * 0.2;
        float z = fract(k);                                   // 0 far away, 1 close
        float seed = floor(k) * 5.0 + float(i);
        float cell = 0.016 * exp2(3.0 * z);                   // stars spread apart as they come closer
        vec2 q = (s + drift * (0.15 + z)) / cell + 97.0 * vec2(hash13(vec3(seed, 1.0, 2.0)), hash13(vec3(seed, 3.0, 4.0)));
        vec2 id = floor(q);
        float h = hash13(vec3(id, seed + 11.0));
        if (h < 0.55) continue;
        vec2 at = 0.2 + 0.6 * vec2(hash13(vec3(id, seed + 3.0)), hash13(vec3(id, seed + 7.0)));
        float d = length(fract(q) - at) * cell;               // in screen heights
        float r0 = mix(0.00026, 0.0007, z) * (0.7 + 0.6 * fract(h * 13.0));
        float r = max(r0, 0.6 * pix);                         // at least about a pixel, same light
        float b = mix(0.15, 1.5, z * z) * (0.12 + 0.88 * pow(fract(h * 37.0), 3.0)) * (r0 * r0) / (r * r);
        b *= smoothstep(0.0, 0.3, z) * (1.0 - smoothstep(0.85, 1.0, z));
        b *= 1.0 + 0.35 * (1.0 - z) * sin(t * (1.5 + 3.0 * fract(h * 53.0)) + h * 100.0);
        float hue = fract(h * 91.0);
        vec3 tint = hue < 0.5 ? vec3(0.78, 0.86, 1.0) : hue < 0.85 ? vec3(1.0) : vec3(1.0, 0.86, 0.68);
        sum += b * tint * (exp(-0.5 * d * d / (r * r)) + 0.07 * exp(-0.5 * d * d / (16.0 * r * r)));
    }
    return sum;
}

// Synthwave (Around the tube 3): a big striped sun behind the monitor, mountains, a neon grid
// floor that runs toward the viewer, a purple sky with a few stars. Decoration, not a simulation.
// s: screen position from the centre in screen heights (y up), t: seconds, pix: one screen pixel
// in screen heights.
vec3 synthwave(vec2 s, float t, float pix)
{
    const float hz = -0.12;                                       // the horizon
    float y = s.y - hz;
    vec3 col;
    if (y > 0.0)
    {
        // the sky: deep indigo above, magenta at the horizon
        col = mix(vec3(0.16, 0.008, 0.14), vec3(0.006, 0.0015, 0.025), smoothstep(0.0, 0.5, y));
        // a few stars high up, twinkling
        vec2 q = s / 0.014, id = floor(q);
        float h = hash13(vec3(id, 91.0));
        if (h > 0.92)
        {
            vec2 at = 0.2 + 0.6 * vec2(hash13(vec3(id, 92.0)), hash13(vec3(id, 93.0)));
            float d = length(fract(q) - at) * 0.014, r = max(0.0003, 0.6 * pix);
            col += vec3(0.85, 0.8, 1.0) * (0.3 * (0.7 + 0.3 * sin(t * (1.0 + 2.0 * fract(h * 41.0)) + h * 60.0))
                 * exp(-0.5 * d * d / (r * r)) * 9e-8 / (r * r) * smoothstep(0.1, 0.4, y));
        }
        // the sun: yellow at the top, orange, then hot pink toward the horizon, cut by bands that
        // thicken toward the horizon and slowly move down; a glow around it
        vec2 sp = s - vec2(0.0, hz + 0.10);
        float R = 0.56, sd = length(sp) - R, v = sp.y / R;
        vec3 sun = mix(vec3(1.0, 0.04, 0.38), vec3(1.0, 0.32, 0.07), smoothstep(-0.3, 0.35, v));
        sun = mix(sun, vec3(1.0, 0.78, 0.14), smoothstep(0.35, 0.95, v)) * 0.55;
        float band = 1.0;
        if (v < 0.45)
        {
            float k = fract(v * 7.0 + t * 0.12), w = max(0.01, 7.0 * pix / R);
            float gap = mix(0.06, 0.55, smoothstep(0.45, -0.4, v));
            band = smoothstep(gap - w, gap + w, k) * smoothstep(1.0, 1.0 - w, k);
        }
        float disk = 1.0 - smoothstep(-pix, pix, sd);
        col = mix(col, sun, disk * band);
        col += sun * (1.0 - disk) * (0.35 * exp(-max(sd, 0.0) / 0.05) + 0.1 * exp(-max(sd, 0.0) / 0.25));
        // mountains in front of it, dark, with a neon ridge (lower toward the middle, which the
        // monitor hides anyway)
        float mh = (0.012 + 0.05 * (0.7 * valueNoise(vec2(s.x * 7.0, 3.0)) + 0.3 * valueNoise(vec2(s.x * 19.0, 5.0))))
                 * smoothstep(0.15, 0.45, abs(s.x));
        float md = y - mh;
        col = mix(col, vec3(0.012, 0.002, 0.03), 1.0 - smoothstep(-pix, pix, md));
        col += vec3(1.0, 0.1, 0.7) * 0.2 * exp(-abs(md) / max(0.0025, pix)) * step(0.002, mh);
    }
    else
    {
        // the floor: a neon grid in perspective, running toward the viewer, fading into the haze
        float z = 1.0 / max(-y, 1e-4);
        vec2 g = vec2(s.x * z, z + 0.6 * t) * 1.5;               // grid units
        vec2 gd = abs(fract(g + 0.5) - 0.5);                     // to the nearest line
        vec2 gw = vec2(z + abs(s.x) * z * z, z * z) * 1.5 * pix; // one pixel in grid units
        vec2 line = (1.0 - smoothstep(0.03 - gw, 0.03 + gw, gd)) * (1.0 - smoothstep(0.15, 0.5, gw));
        vec2 glow = exp(-gd / (0.12 + gw)) * (1.0 - smoothstep(0.15, 0.5, gw));
        float lines = max(max(line.x, line.y), 0.3 * max(glow.x, glow.y));
        col = vec3(0.006, 0.0, 0.014) + vec3(1.0, 0.06, 0.65) * (0.55 * lines * exp(-0.08 * z));
    }
    // haze and a bright line at the horizon
    col += vec3(0.25, 0.015, 0.2) * exp(-abs(y) / 0.04) + vec3(1.0, 0.1, 0.6) * 0.25 * exp(-abs(y) / max(0.002, pix));
    return col;
}

// ---------------------------------------------------------------------------
// More scenes (Around the tube 4-11). Decoration, not a simulation, all drawn here (no pictures).
// fc: the screen pixel, counted from the centre (y up). The pixel-art scenes draw their pixels as
// squares of a whole number of screen pixels and scroll in whole screen pixels, so they stay sharp.
// ---------------------------------------------------------------------------

// a colour given as 0-255 sRGB, in linear light like the picture
vec3 rgb8(float r, float g, float b) { return pow(vec3(r, g, b) / 255.0, vec3(2.2)); }

// screen pixels per pixel of a picture this many lines tall on the screen
float pixSize(float lines, float pix) { return max(1.0, floor(1.0 / (lines * pix) + 0.5)); }

// the pixel column of a layer that moves left at v screen heights per second; pixels n screen pixels wide
float layerX(float fx, float t, float v, float n, float pix) { return floor((fx + floor(t * v / pix)) / n); }

// 4x4 ordered dither threshold, 0..15/16
float bayer2(vec2 a) { return 2.0 * a.x + 3.0 * a.y - 4.0 * a.x * a.y; }
float bayer4(vec2 p) { return (4.0 * bayer2(mod(p, 2.0)) + bayer2(mod(floor(p * 0.5), 2.0))) / 16.0; }

// Tiles (4): a 90s desktop wallpaper, a small pattern in the 16 Windows colours repeated over a
// 640x480 desktop, scrolling slowly and diagonally. Four patterns take turns every two minutes,
// dissolving into each other. p: the pattern's pixel.
vec3 tilePattern(int k, vec2 p)
{
    vec3 c;
    if (k == 0)
    {
        // cubes: hexagons of three faces, light on top, teal on the left, dark teal on the right
        vec2 q = (p + 0.5) / 16.0;
        q.x *= 0.8660254;                                     // 2:1 pixel slopes
        float j = floor(q.y / 1.5);
        vec2 best = vec2(1e9);
        for (int i = 0; i < 2; i++)
        {
            float jj = j + float(i), sh = 0.5 * mod(jj, 2.0);
            vec2 d = q - vec2((floor(q.x / 1.7320508 - sh + 0.5) + sh) * 1.7320508, 1.5 * jj);
            if (dot(d, d) < dot(best, best)) best = d;
        }
        if (best.y * 1.7320508 > abs(best.x)) c = rgb8(192.0, 192.0, 192.0);
        else if (best.x < 0.0) c = rgb8(0.0, 128.0, 128.0);
        else c = mod(p.x + p.y, 2.0) < 1.0 ? rgb8(0.0, 128.0, 128.0) : vec3(0.0);
    }
    else if (k == 1)
    {
        // triangles: squares cut along a diagonal that turns from square to square, navy and purple
        vec2 cell = floor(p / 16.0), f = p - cell * 16.0;
        float u = mod(cell.x + cell.y, 2.0) > 0.5 ? 15.0 - f.x : f.x;
        float dd = f.y - u;
        c = dd > 0.5 ? rgb8(0.0, 0.0, 128.0) : dd < -0.5 ? rgb8(128.0, 0.0, 128.0) : vec3(0.0);
    }
    else if (k == 2)
    {
        // blocks: raised bricks like 90s buttons, light on the top and left edges
        float row = floor(p.y / 16.0);
        vec2 f = vec2(mod(p.x + 16.0 * mod(row, 2.0), 32.0), p.y - row * 16.0);
        c = rgb8(192.0, 192.0, 192.0);
        if (f.x > 30.5 || f.y < 0.5) c = vec3(0.0);
        else if (f.x > 29.5 || f.y < 1.5) c = rgb8(128.0, 128.0, 128.0);
        else if (f.x < 0.5 || f.y > 14.5) c = vec3(1.0);
    }
    else
    {
        // weave: a straw mat, strips going across and up in turn, olive with maroon shadows
        vec2 b = floor(p / 16.0), f = p - b * 16.0;
        float w = mod(mod(b.x + b.y, 2.0) < 0.5 ? f.y : f.x, 4.0);
        c = w < 0.5 ? rgb8(128.0, 0.0, 0.0) : rgb8(128.0, 128.0, 0.0);
        if (w > 2.5 && mod(p.x + p.y, 2.0) < 1.0) c = rgb8(255.0, 255.0, 0.0);
    }
    return c;
}
vec3 tiles(vec2 fc, float t, float pix)
{
    float n = pixSize(480.0, pix);
    vec2 p = floor((fc - floor(t * 0.012 / pix) * vec2(1.0, -1.0)) / n);   // moving right and down
    float turn = t / 120.0;
    int k = int(mod(floor(turn), 4.0));
    vec3 c = tilePattern(k, p);
    if (fract(turn) * 120.0 - 118.0 > 2.0 * bayer4(p))                    // the last two seconds: dissolve
        c = tilePattern(int(mod(float(k + 1), 4.0)), p);
    return 0.4 * c;
}
vec3 tilesLight(float t)
{
    float turn = t / 120.0;
    int k = int(mod(floor(turn), 4.0)), k1 = int(mod(float(k + 1), 4.0));
    vec3 l[4] = vec3[4](vec3(0.86, 0.98, 0.98), vec3(0.9, 0.82, 1.0), vec3(1.0), vec3(1.0, 0.97, 0.8));
    return mix(l[k], l[k1], clamp(fract(turn) * 60.0 - 59.0, 0.0, 1.0));
}

// Bouncing lines (5): two four-cornered shapes whose corners bounce off the edges of the screen,
// each followed by fading copies of itself, on black (in the spirit of the 90s screensavers).
// hs: half the screen's size in screen heights.
vec3 bounceLines(vec2 s, vec2 hs, float t, float pix)
{
    vec3 col = vec3(0.0);
    float w = max(pix, 1.0 / 960.0);                          // half the line's width
    for (int sh = 0; sh < 2; sh++)
    {
        vec3 tint = 0.5 + 0.5 * cos(TAU * (t * 0.013 + 0.5 * float(sh) + vec3(0.0, 0.33, 0.67)));
        tint *= tint;
        vec2 ph[4], rate[4];
        for (int i = 0; i < 4; i++)
        {
            float h = float(sh * 4 + i);
            ph[i] = vec2(hash13(vec3(h, 1.0, 7.0)), hash13(vec3(h, 2.0, 7.0)));
            rate[i] = 0.035 + 0.04 * vec2(hash13(vec3(h, 3.0, 7.0)), hash13(vec3(h, 4.0, 7.0)));
        }
        for (int k = 0; k < 6; k++)
        {
            float tk = t - 0.22 * float(k);
            vec2 v[4];
            vec2 lo = vec2(1e9), hi = vec2(-1e9);
            for (int i = 0; i < 4; i++)
            {
                v[i] = (abs(fract(ph[i] + rate[i] * tk) * 2.0 - 1.0) * 2.0 - 1.0) * (hs - 0.02);
                lo = min(lo, v[i]);
                hi = max(hi, v[i]);
            }
            float m = w + 8.0 * pix;
            if (any(lessThan(s, lo - m)) || any(greaterThan(s, hi + m))) continue;
            float d = 1e9;
            for (int i = 0; i < 4; i++)
            {
                vec2 a = v[i], b = v[(i + 1) % 4];
                vec2 ba = b - a, pa = s - a;
                d = min(d, length(pa - ba * clamp(dot(pa, ba) / max(dot(ba, ba), 1e-8), 0.0, 1.0)));
            }
            col += tint * pow(0.72, float(k)) * (0.8 * clamp((w - d) / pix + 0.5, 0.0, 1.0) + 0.08 * exp(-d / (3.0 * pix)));
        }
    }
    return col;
}

// a sprite's own pixel (its centre), seen from screen pixel fc: the sprite stands at pos, in scene
// pixels of n screen pixels; it moves in whole screen pixels and keeps its big pixels
vec2 spritePix(vec2 fc, vec2 pos, float n) { return floor((fc - floor(pos * n)) / n) + 0.5; }

// Underwater (6): a calm sea floor from a 16-bit game: light falling in slanted rays that sway
// slowly, bubbles rising, seaweed swaying over the sand, a few fish far away. Drawn at 224 lines.
// hs: half the screen's size in screen heights.
vec3 underwater(vec2 fc, vec2 hs, float t, float pix)
{
    float n = pixSize(224.0, pix);
    vec2 p = floor(fc / n);
    float x = p.x + 0.5, y = p.y + 0.5;
    // the water, lighter toward the surface, in bands
    vec3 col = mix(rgb8(8.0, 30.0, 70.0), rgb8(40.0, 120.0, 156.0), clamp(floor((y + 100.0) / 26.0) / 7.0, 0.0, 1.0));
    // light falling in slanted rays, swaying slowly, fainter deeper down (dithered at the edges)
    float u = x + 0.5 * y;
    float ray = sin(u * 0.045 + 0.8 * sin(t * 0.13)) + 0.6 * sin(u * 0.11 - t * 0.07);
    if (ray > 0.95 || (ray > 0.75 && mod(p.x + p.y, 2.0) < 1.0))
        col = mix(col, rgb8(110.0, 190.0, 210.0), 0.3 * smoothstep(-60.0, 90.0, y));
    // fish far away, dark, crossing slowly
    float span = 2.0 * hs.x / (n * pix) + 40.0;
    for (int i = 0; i < 3; i++)
    {
        float h = hash13(vec3(float(i), 3.0, 63.0));
        vec2 fp = vec2(mod(h * span + t * (4.0 + 4.0 * h), span) - 0.5 * span,
                       -10.0 + 60.0 * fract(h * 7.0) + 4.0 * sin(t * 0.4 + h * 9.0));
        vec2 d = spritePix(fc, fp, n);
        if (d.x * d.x / 36.0 + d.y * d.y / 9.0 < 1.0 || (d.x < -5.0 && d.x > -10.0 && abs(d.y) < (-4.0 - d.x) * 0.7))
            col = mix(col, rgb8(6.0, 22.0, 52.0), 0.75);
    }
    // the sand, in little dunes; rocks; seaweed swaying over it
    float sand = -84.0 + 3.0 * sin(x * 0.06) + 2.0 * sin(x * 0.17 + 1.0);
    float rc = floor(x / 70.0), hr = hash13(vec3(rc, 9.0, 61.0));
    vec2 rd = vec2((x - rc * 70.0 - 20.0 - 30.0 * hr) / (8.0 + 6.0 * hr), (y - sand) / (5.0 + 4.0 * hr));
    if (hr > 0.4 && dot(rd, rd) < 1.0) col = rd.y > 0.6 ? rgb8(60.0, 84.0, 104.0) : rgb8(36.0, 56.0, 76.0);
    float sc = floor(x / 11.0), hw = hash13(vec3(sc, 8.0, 61.0));
    if (hw > 0.35)
    {
        float top = sand + 14.0 + 34.0 * fract(hw * 7.0);
        float k = clamp((y - sand) / (top - sand), 0.0, 1.0);
        float dx = abs(x - (sc * 11.0 + 5.5 + 3.0 * k * k * sin(t * 0.7 + hw * 20.0 + y * 0.08)));
        if (y < top && dx < 1.5) col = dx < 0.5 ? rgb8(50.0, 120.0, 90.0) : rgb8(22.0, 78.0, 66.0);
    }
    if (y < sand) col = y > sand - 2.0 ? rgb8(104.0, 120.0, 126.0) : hash13(vec3(p, 64.0)) > 0.9 ? rgb8(70.0, 84.0, 96.0) : rgb8(82.0, 96.0, 108.0);
    // bubbles rising from a few places
    float bc = floor(x / 48.0), hb = hash13(vec3(bc, 7.0, 61.0));
    if (hb > 0.72)
    {
        float yy = fc.y / n - t * 9.0 - 300.0 * hb, cell = floor(yy / 30.0);
        float hc = hash13(vec3(bc, cell, 62.0)), r = 2.2 + 1.4 * hc;
        vec2 bp = vec2(bc * 48.0 + 8.0 + 32.0 * hb + 1.5 * sin(cell * 1.7 + t * 1.3), (cell * 30.0 + 15.0) + t * 9.0 + 300.0 * hb);
        vec2 d = spritePix(fc, bp, n);
        float L = length(d);
        if (hc > 0.45 && L < r + 0.5 && y > sand + 2.0)
            col = mix(col, rgb8(170.0, 225.0, 240.0), L > r - 1.0 ? 0.5 : (d.x < -0.5 && d.y > 0.5 && L < 1.6 ? 0.6 : 0.08));
    }
    return 0.55 * col;
}

// Copper bars (7): Amiga demo raster bars, shiny bands swinging up and down around an invisible
// cylinder, stars flying sideways behind them, a checkerboard floor rushing toward the viewer.
// Drawn at 256 lines, in the Amiga's 4096 colours.
vec3 amiga12(vec3 c) { return pow(floor(clamp(c, 0.0, 1.0) * 15.0 + 0.5) / 15.0, vec3(2.2)); }
vec3 copper(vec2 fc, vec2 hs, float t, float pix)
{
    float n = pixSize(256.0, pix);
    vec2 p = floor(fc / n);
    float L = 1.0 / (n * pix);                                // the screen's height in lines
    float y = p.y + 0.5, hz = -0.2 * L;
    vec3 col = vec3(0.0);
    if (y > hz)
    {
        // stars flying to the left at three speeds, one on most lines
        if (hash13(vec3(p.y, 3.0, 17.0)) > 0.35)
        {
            float lay = floor(hash13(vec3(p.y, 4.0, 17.0)) * 3.0), w = 2.0 * hs.x * L;
            float x = mod(hash13(vec3(p.y, 5.0, 17.0)) * w - t * (14.0 + 22.0 * lay), w) - 0.5 * w;
            if (p.x == floor(x)) col = amiga12(vec3(0.3 + 0.35 * lay));
        }
        // the bars, the nearer ones in front, darker at the back
        const vec3 BAR[6] = vec3[6](vec3(1.0, 0.25, 0.2), vec3(1.0, 0.6, 0.1), vec3(1.0, 0.95, 0.3),
                                    vec3(0.3, 1.0, 0.35), vec3(0.2, 0.75, 1.0), vec3(0.75, 0.35, 1.0));
        float zb = -2.0;
        for (int i = 0; i < 6; i++)
        {
            float a = t * 1.1 + float(i) * TAU / 6.0, z = cos(a);
            float dy = abs(y - (0.17 * L + 0.17 * L * sin(a))) / 10.0;
            if (dy < 1.0 && z > zb)
            {
                zb = z;
                col = amiga12(BAR[i] * (1.0 - dy * dy) * (0.6 + 0.4 * z) + 0.25 * pow(1.0 - dy, 6.0));
            }
        }
        if (abs(y - hz - 0.5) < 0.6) col = amiga12(vec3(0.55, 0.65, 1.0));   // the horizon
    }
    else
    {
        // the floor: a checkerboard in perspective, fading out in the distance
        float dy = (hz - y) / L;
        float z = 0.09 / dy;
        vec2 g = vec2((p.x + 0.5) / L * z * 7.0, z * 7.0 + t * 1.6);
        float ch = mod(floor(g.x) + floor(g.y), 2.0);
        vec3 c = mix(vec3(0.85, 0.85, 1.0), vec3(0.2, 0.1, 0.55), ch);
        float cell = max(z * 7.0 / L, 0.09 / (dy * dy) * 7.0 / L);   // cell size against a pixel
        c = mix(c, vec3(0.52, 0.48, 0.78), smoothstep(0.25, 0.6, cell));
        col = amiga12(c * exp(-0.12 * z));
    }
    return 0.5 * col;
}

// Amiga demo (8): a screen from an early-90s Amiga demo, built the way they were: everything in
// 320x256 pixels and the Amiga's 4096 colours, all motion from sine waves. A glenz vector (a
// see-through solid of 24 triangles, white and blue) and a cube of vector balls that turns into a
// ball and back swing out from behind the monitor, one on each side; stars fly toward the viewer;
// a twister turns along the bottom; a DYCP scroller (every letter on its own sine) runs along the
// top in copper colours. Heavier than the other scenes.
const uint DEMO_FONT[88] = uint[88](
    0x00000000u, 0x00000000u, 0x3C66C3C3u, 0xFFC3C300u, 0xFCC6C6FCu, 0xC6C6FC00u, 0x3E63C0C0u, 0xC0633E00u,
    0xF8CCC6C6u, 0xC6CCF800u, 0xFEC0C0FCu, 0xC0C0FE00u, 0xFEC0C0FCu, 0xC0C0C000u, 0x3E60C0CEu, 0xC6663C00u,
    0xC6C6C6FEu, 0xC6C6C600u, 0x7E181818u, 0x18187E00u, 0x0E060606u, 0xC6C67C00u, 0xC6CCD8F0u, 0xD8CCC600u,
    0xC0C0C0C0u, 0xC0C0FE00u, 0xC3E7FFDBu, 0xC3C3C300u, 0xC6E6F6DEu, 0xCEC6C600u, 0x3C66C3C3u, 0xC3663C00u,
    0xFCC6C6FCu, 0xC0C0C000u, 0x3C66C3C3u, 0xCB663B00u, 0xFCC6C6FCu, 0xD8CCC600u, 0x7EC0C07Cu, 0x0606FC00u,
    0xFF181818u, 0x18181800u, 0xC6C6C6C6u, 0xC6C67C00u, 0xC6C6C6C6u, 0x6C381000u, 0xC3C3C3DBu, 0xFFE7C300u,
    0xC66C3838u, 0x6CC6C600u, 0xC3663C18u, 0x18181800u, 0xFE060C18u, 0x3060FE00u, 0x3C66CEDEu, 0xF6663C00u,
    0x18387818u, 0x18187E00u, 0x7CC6061Cu, 0x70C0FE00u, 0x7CC6063Cu, 0x06C67C00u, 0x0E1E3666u, 0xFE060600u,
    0xFEC0FC06u, 0x06C67C00u, 0x3C60C0FCu, 0xC6C67C00u, 0xFE060C18u, 0x30303000u, 0x7CC6C67Cu, 0xC6C67C00u,
    0x7CC6C67Eu, 0x060C7800u, 0x00000000u, 0x00181800u, 0x00000000u, 0x00181830u, 0x18181818u, 0x18001800u,
    0x0000007Eu, 0x00000000u, 0x00181800u, 0x18180000u, 0x18183000u, 0x00000000u, 0x7CC6061Cu, 0x18001800u);
const int DEMO_TEXT[319] = int[319](
    12, 15, 12, 1, 0, 18, 5, 20, 18, 15, 0, 16, 18, 5, 19, 5, 14, 20, 19, 37, 37, 37, 0, 20,
    8, 5, 0, 19, 3, 18, 5, 5, 14, 0, 1, 18, 15, 21, 14, 4, 0, 25, 15, 21, 18, 0, 13, 15,
    14, 9, 20, 15, 18, 39, 0, 0, 0, 7, 12, 5, 14, 26, 0, 22, 5, 3, 20, 15, 18, 19, 38, 0,
    22, 5, 3, 20, 15, 18, 0, 2, 1, 12, 12, 19, 38, 0, 1, 0, 20, 23, 9, 19, 20, 5, 18, 0,
    1, 14, 4, 0, 20, 8, 9, 19, 0, 12, 9, 20, 20, 12, 5, 0, 4, 25, 3, 16, 0, 19, 3, 18,
    15, 12, 12, 5, 18, 38, 0, 1, 12, 12, 0, 9, 14, 0, 30, 29, 27, 24, 29, 32, 33, 0, 1, 14,
    4, 0, 31, 27, 36, 33, 0, 3, 15, 12, 15, 21, 18, 19, 38, 0, 20, 8, 5, 0, 23, 1, 25, 0,
    20, 8, 5, 0, 1, 13, 9, 7, 1, 0, 4, 9, 4, 0, 9, 20, 37, 0, 0, 0, 7, 18, 5, 5,
    20, 9, 14, 7, 19, 0, 20, 15, 0, 1, 12, 12, 0, 1, 13, 9, 7, 1, 0, 6, 18, 5, 1, 11,
    19, 38, 0, 19, 3, 5, 14, 5, 18, 19, 0, 1, 14, 4, 0, 3, 18, 20, 0, 12, 15, 22, 5, 18,
    19, 39, 0, 0, 0, 12, 15, 12, 1, 0, 9, 19, 0, 1, 0, 18, 5, 20, 18, 9, 5, 22, 5, 18,
    38, 0, 18, 5, 20, 18, 15, 0, 2, 5, 3, 1, 21, 19, 5, 0, 9, 20, 0, 9, 19, 0, 18, 5,
    20, 18, 15, 37, 37, 37, 0, 0, 0, 20, 5, 24, 20, 0, 18, 5, 19, 20, 1, 18, 20, 19, 37, 37,
    37, 0, 0, 0, 0, 0, 0);
mat3 demoRot(vec3 a)
{
    vec3 c = cos(a), s = sin(a);
    return mat3(c.y * c.z, c.y * s.z, -s.y,
                s.x * s.y * c.z - c.x * s.z, s.x * s.y * s.z + c.x * c.z, s.x * c.y,
                c.x * s.y * c.z + s.x * s.z, c.x * s.y * s.z - s.x * c.z, c.x * c.y);
}
vec3 demoRainbow(float x) { return 0.5 + 0.5 * cos(TAU * (x + vec3(0.0, 0.33, 0.67))); }
// one pixel of the scroller's font: glyph g, column x and row y (0-7, top row 0)
bool demoFontPixel(int g, int x, int y)
{
    uint w = DEMO_FONT[g * 2 + (y >> 2)];
    return ((w >> uint((3 - (y & 3)) * 8 + 7 - x)) & 1u) == 1u;
}
vec3 amigaDemo(vec2 fc, vec2 hs, float t, float pix)
{
    float n = pixSize(256.0, pix);
    vec2 P = floor(fc / n);                                   // the Amiga pixel, from the centre, y up
    float L = 1.0 / (n * pix);                                // the screen's height in Amiga pixels
    vec2 s = (P + 0.5) / L;                                   // its centre, in screen heights
    vec3 col = vec3(0.0);

    // stars flying toward the viewer: layers that grow from the centre, three shades as on a 3-plane screen
    for (int i = 0; i < 4; i++)
    {
        float k = t * 0.22 + float(i) * 0.25, z = fract(k), seed = floor(k) * 4.0 + float(i);
        float cell = 0.02 * exp2(4.0 * z);
        vec2 off = 50.0 * vec2(hash13(vec3(seed, 1.0, 81.0)), hash13(vec3(seed, 2.0, 81.0)));
        vec2 id = floor(s / cell + off);
        float h = hash13(vec3(id, seed + 3.0));
        vec2 sp = (id - off + 0.25 + 0.5 * vec2(h, fract(h * 13.0))) * cell;   // the star, in screen heights
        if (h > 0.75 && all(equal(floor(sp * L), P)) && z > 0.15)
            col = max(col, vec3(z < 0.45 ? 0.27 : z < 0.75 ? 0.6 : 1.0));
    }

    // the two objects swing from one side of the monitor to the other, opposite each other
    float sw = sin(t * 0.42), A = max(hs.x - 0.22, 0.3);
    vec2 cg = vec2(A * sw, 0.06 * sin(t * 0.9));               // the glenz vector
    vec2 cb = vec2(-A * sw, 0.06 * sin(t * 0.9 + 2.0));        // the vector balls

    // glenz vector: a tetrakis hexahedron (a cube with a low pyramid on each face). A ray from the eye
    // enters through one face and leaves through another; white faces are solid, blue ones show the
    // back faces through them, as two bitplanes of front and back faces did
    const float H = 0.6, GS = 0.1;
    vec2 u = (s - cg) / GS;
    if (dot(u, u) < 8.0)
    {
        mat3 R = demoRot(vec3(t * 0.8, t * 1.1, t * 0.5));
        vec3 o = vec3(0.0, 0.0, 5.0) * R, d = vec3(u, -5.0) * R;   // the ray, in the object's frame
        float tin = -1e9, tout = 1e9;
        int kin = 0, kout = 0;
        for (int k = 0; k < 24; k++)
        {
            int f = k / 4, e = k % 4, ax = f / 2, ex = (ax + 1 + e / 2) % 3;
            vec3 nk = (ax == 0 ? vec3(1.0, 0.0, 0.0) : ax == 1 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0)) * (f % 2 == 0 ? 1.0 : -1.0)
                    + (ex == 0 ? vec3(1.0, 0.0, 0.0) : ex == 1 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0)) * (e % 2 == 0 ? H : -H);
            float nd = dot(nk, d), tt = (1.0 + H - dot(nk, o)) / nd;
            if (nd < 0.0) { if (tt > tin) { tin = tt; kin = k; } }
            else if (tt < tout) { tout = tt; kout = k; }
        }
        if (tin < tout)
        {
            bool frontWhite = kin % 4 < 2, backWhite = kout % 4 < 2;   // a checkerboard of triangles
            vec3 back = backWhite ? vec3(0.55, 0.6, 0.85) : vec3(0.05, 0.1, 0.45);
            col = frontWhite ? vec3(0.95, 0.95, 1.0) : mix(back, vec3(0.2, 0.45, 1.0), 0.4);
        }
    }

    // vector balls: 26 balls in a 3x3x3 cube that turns into a ball and back, nearer balls in front,
    // each shaded like a bob drawn in eight shades
    const float BS = 0.085;
    vec2 ub = (s - cb) / BS;
    if (dot(ub, ub) < 10.0)
    {
        mat3 R = demoRot(vec3(t * 0.9, t * 0.6, t * 0.35));
        float m = smoothstep(-0.4, 0.4, sin(t * 0.33)), zb = -1e9;
        vec2 bd = vec2(0.0);
        int bi = 0;
        for (int i = 0; i < 27; i++)
        {
            if (i == 13) continue;
            vec3 q = vec3(float(i % 3), float((i / 3) % 3), float(i / 9)) - 1.0;
            float fi = float(i < 13 ? i : i - 1), zz = 1.0 - (2.0 * fi + 1.0) / 26.0, ph = fi * 2.39996;
            vec3 p = R * mix(q, 1.45 * vec3(sqrt(1.0 - zz * zz) * vec2(cos(ph), sin(ph)), zz), m);
            float w = 1.0 / (1.0 - p.z * 0.18), r = 0.33 * w;
            vec2 dd = (ub - p.xy * w) / r;
            if (dot(dd, dd) < 1.0 && p.z > zb) { zb = p.z; bd = dd; bi = i; }
        }
        if (zb > -1e8)
        {
            vec3 nb = vec3(bd, sqrt(max(1.0 - dot(bd, bd), 0.0)));
            float l = dot(nb, normalize(vec3(-0.5, 0.6, 0.62)));
            float lev = floor((0.25 + 0.75 * max(l, 0.0)) * 7.0 + 0.5) / 7.0;
            vec3 base = bi / 9 == 0 ? vec3(1.0, 0.35, 0.2) : bi / 9 == 1 ? vec3(1.0, 0.8, 0.25) : vec3(0.35, 0.7, 1.0);
            col = base * lev + (l > 0.93 ? 0.6 : 0.0);
        }
    }

    // the twister along the bottom: a bar with a square cross-section, turned a little more at
    // every column, its four faces in copper colours with a checked pattern
    float Yc = -0.43 * L + 3.0 * sin(P.x * 0.045 + t * 1.3);
    if (abs(P.y + 0.5 - Yc) < 16.0)
    {
        float a = t * 1.7 + P.x * 0.03 * sin(t * 0.31);
        for (int i = 0; i < 4; i++)
        {
            float a0 = a + float(i) * 1.5707963, y0 = Yc + 15.5 * sin(a0), y1 = Yc + 15.5 * sin(a0 + 1.5707963);
            float y = P.y + 0.5;
            if (y1 > y0 && y >= y0 && y < y1)
            {
                float v = (y - y0) / (y1 - y0), shade = cos(a0 + 0.7853982);
                float chk = mod(floor(v * 4.0) + floor((P.x + t * 24.0) / 8.0), 2.0);
                vec3 fcol = demoRainbow(float(i) * 0.25 + 0.08 * sin(t * 0.2));
                col = fcol * (0.3 + 0.7 * shade) * (chk < 0.5 ? 1.0 : 0.65) * (v < 0.06 || v > 0.94 ? 0.4 : 1.0);
            }
        }
    }

    // the DYCP scroller along the top: 16x16 letters, each on its own sine, coloured line by line
    // like a copper list, with a shadow
    float base = floor(0.43 * L);
    float sx = P.x + floor(t * 70.0);
    float slot = floor(sx / 16.0);
    int ch = DEMO_TEXT[int(mod(slot, float(DEMO_TEXT.length())))];
    float oy = floor(7.0 * sin(slot * 0.55 + t * 3.2));
    int gx = int(floor((sx - slot * 16.0) / 2.0)), gy = int(floor((base + oy - P.y) / 2.0));
    if (gy >= 0 && gy < 8 && demoFontPixel(ch, gx, gy))
        col = demoRainbow(P.y * 0.022 - t * 0.35) * 0.9 + 0.1;
    else
    {
        float sx2 = sx - 2.0, slot2 = floor(sx2 / 16.0);
        int ch2 = DEMO_TEXT[int(mod(slot2, float(DEMO_TEXT.length())))];
        float oy2 = floor(7.0 * sin(slot2 * 0.55 + t * 3.2));
        int gx2 = int(floor((sx2 - slot2 * 16.0) / 2.0)), gy2 = int(floor((base + oy2 - P.y - 2.0) / 2.0));
        if (gy2 >= 0 && gy2 < 8 && demoFontPixel(ch2, gx2, gy2)) col *= 0.25;
    }
    // a copper bar above and below the scroller
    float cl = min(abs(P.y - (base - 26.0)), abs(P.y - (base + 10.0)));
    if (cl < 2.0) col = demoRainbow(P.x * 0.004 + t * 0.15) * (1.0 - 0.35 * cl);

    return 0.55 * amiga12(col);
}

// Hills (9): a 16-bit side-scroller landscape in layers, each moving slower the further away it
// is: sky, clouds, mountains, hills, trees and grass. Drawn at 224 lines.
vec3 hills(vec2 fc, float t, float pix)
{
    float n = pixSize(224.0, pix);
    float y = floor(fc.y / n) + 0.5;
    vec3 col = mix(rgb8(150.0, 205.0, 255.0), rgb8(48.0, 96.0, 208.0), clamp(floor((y + 40.0) / 19.0) / 7.0, 0.0, 1.0));
    // clouds
    float x = layerX(fc.x, t, 0.004, n, pix) + 0.5;
    float cell = floor(x / 90.0), hc = hash13(vec3(cell, 1.0, 41.0));
    vec2 a = vec2(x - cell * 90.0 - 45.0 - (hc - 0.5) * 30.0, y - (50.0 + 40.0 * fract(hc * 7.0)));
    float cl = min(min(length(a - vec2(-13.0, 0.0)) - 9.0, length(a - vec2(0.0, 4.0)) - 13.0), length(a - vec2(14.0, -1.0)) - 8.0);
    if (hc > 0.2 && cl < 0.0 && a.y > -6.0) col = a.y > -1.0 ? rgb8(255.0, 255.0, 255.0) : rgb8(200.0, 215.0, 245.0);
    // mountains: lit on the left, shaded on the right, snow on the high peaks
    x = layerX(fc.x, t, 0.009, n, pix) + 0.5;
    float mc = floor(x / 52.0), mf = x - mc * 52.0;
    float peak = 18.0 + 30.0 * hash13(vec3(mc, 2.0, 41.0));
    float mh = -38.0 + peak * (1.0 - abs(mf - 26.0) / 26.0);
    if (y < mh) col = (peak > 34.0 && y > peak - 47.0) ? rgb8(235.0, 240.0, 255.0) : mf < 26.0 ? rgb8(120.0, 128.0, 200.0) : rgb8(96.0, 102.0, 170.0);
    // hills, in stripes
    x = layerX(fc.x, t, 0.018, n, pix) + 0.5;
    float hh = -52.0 + 9.0 * sin(x * 0.045) + 5.0 * sin(x * 0.11 + 1.3);
    if (y < hh) col = y > hh - 2.0 ? rgb8(120.0, 205.0, 95.0) : mod(floor(y / 3.0), 2.0) < 0.5 ? rgb8(62.0, 150.0, 72.0) : rgb8(52.0, 134.0, 64.0);
    // trees
    x = layerX(fc.x, t, 0.032, n, pix) + 0.5;
    float tc = floor(x / 26.0), ht = hash13(vec3(tc, 3.0, 41.0));
    if (ht > 0.2)
    {
        float cy = -66.0 + 6.0 * fract(ht * 7.0), r = 8.0 + 4.0 * fract(ht * 13.0);
        vec2 d = vec2(x - tc * 26.0 - 13.0 - (ht - 0.5) * 8.0, y - cy);
        if (abs(d.x) < 1.5 && d.y < 0.0) col = rgb8(110.0, 70.0, 36.0);
        if (length(d) < r) col = -0.6 * d.x + 0.8 * d.y > 0.35 * r ? rgb8(88.0, 170.0, 70.0) : rgb8(34.0, 110.0, 48.0);
    }
    // grass and earth
    x = layerX(fc.x, t, 0.05, n, pix) + 0.5;
    float g = -84.0 + (hash13(vec3(floor(x), 4.0, 41.0)) > 0.6 ? 1.0 : 0.0);
    if (y < g)
    {
        col = y > g - 4.0 ? rgb8(96.0, 196.0, 64.0) : rgb8(150.0, 98.0, 52.0);
        if (y < g - 4.0 && hash13(vec3(floor(x), floor(y), 42.0)) > 0.93) col = rgb8(112.0, 70.0, 38.0);
    }
    return 0.45 * col;
}

// Night city (10): a skyline at night in three layers moving at their own speeds, windows lighting
// up and going dark, a red light blinking on an antenna, a moon. Drawn at 224 lines.
vec3 city(vec2 fc, float t, float pix)
{
    float n = pixSize(224.0, pix);
    float yi = floor(fc.y / n), y = yi + 0.5, sx = floor(fc.x / n);
    vec3 col = mix(rgb8(64.0, 36.0, 96.0), rgb8(6.0, 8.0, 34.0), clamp(floor((y + 40.0) / 19.0) / 7.0, 0.0, 1.0));
    float hs = hash13(vec3(sx, yi, 51.0));
    if (y > 10.0 && hs > 0.996) col = rgb8(200.0, 200.0, 255.0) * (0.7 + 0.3 * sin(t * 2.0 + hs * 900.0));
    vec2 m = vec2(sx + 0.5 - 118.0, y - 66.0);                  // the moon, right of the monitor
    if (length(m) < 12.0)
        col = (length(m - vec2(-3.0, 3.0)) < 3.5 || length(m - vec2(4.0, -4.0)) < 2.5) ? rgb8(215.0, 205.0, 175.0) : rgb8(245.0, 238.0, 205.0);
    // far buildings, with a few dim windows
    float x = layerX(fc.x, t, 0.006, n, pix);
    float b = floor(x / 22.0), lx = x - b * 22.0;
    float top = floor(-20.0 + 55.0 * hash13(vec3(b, 2.0, 52.0)));
    if (lx < floor(13.0 + 8.0 * hash13(vec3(b, 1.0, 52.0))) && yi < top)
    {
        col = rgb8(34.0, 32.0, 70.0);
        if (mod(lx, 3.0) == 1.0 && mod(top - yi, 3.0) == 2.0 && hash13(vec3(b * 31.0 + lx, yi, 53.0)) > 0.8) col = rgb8(130.0, 120.0, 80.0);
    }
    // nearer buildings: windows that light up and go dark
    x = layerX(fc.x, t, 0.014, n, pix);
    b = floor(x / 32.0); lx = x - b * 32.0;
    float w = floor(20.0 + 11.0 * hash13(vec3(b, 1.0, 54.0)));
    top = floor(-40.0 + 60.0 * hash13(vec3(b, 2.0, 54.0)));
    if (lx < w && yi < top)
    {
        col = rgb8(18.0, 18.0, 40.0);
        float wy = top - yi;
        if (mod(lx, 4.0) >= 1.0 && mod(lx, 4.0) <= 2.0 && lx < w - 1.0 && mod(wy, 5.0) >= 2.0 && mod(wy, 5.0) <= 3.0)
        {
            vec3 id = vec3(b * 17.0 + floor(lx / 4.0), floor(wy / 5.0), 55.0);
            float h = hash13(id);
            if (hash13(id + floor(t / (8.0 + 20.0 * h) + h)) > 0.55)
                col = 0.7 * (fract(h * 11.0) > 0.25 ? rgb8(255.0, 214.0, 130.0) : rgb8(150.0, 205.0, 255.0));
        }
    }
    // rooftops in front, an antenna with a red light
    x = layerX(fc.x, t, 0.03, n, pix);
    b = floor(x / 44.0); lx = x - b * 44.0;
    float hb = hash13(vec3(b, 1.0, 56.0));
    top = floor(-78.0 + 22.0 * hb);
    if (yi < top) col = rgb8(8.0, 8.0, 16.0);
    if (hb > 0.5 && lx == 22.0 && yi >= top && yi < top + 12.0) col = rgb8(40.0, 40.0, 52.0);
    if (hb > 0.5 && lx == 22.0 && yi == top + 12.0 && fract(t * 0.8 + hb * 7.0) < 0.5) col = rgb8(255.0, 40.0, 30.0);
    return 0.6 * col;
}

// Arcade carpet (11): neon shapes on black, like the carpets of 90s arcades and bowling alleys,
// glowing as under black light, drifting slowly.
float sdStar5(vec2 p, float r, float rf)
{
    const vec2 k1 = vec2(0.809016994, -0.587785252), k2 = vec2(-0.809016994, -0.587785252);
    p.x = abs(p.x);
    p -= 2.0 * max(dot(k1, p), 0.0) * k1;
    p -= 2.0 * max(dot(k2, p), 0.0) * k2;
    p.x = abs(p.x);
    p.y -= r;
    vec2 ba = rf * vec2(-k1.y, k1.x) - vec2(0.0, 1.0);
    float h = clamp(dot(p, ba) / dot(ba, ba), 0.0, r);
    return length(p - ba * h) * sign(p.y * ba.x - p.x * ba.y);
}
float sdTriangle(vec2 p, float r)
{
    const float k = 1.7320508;
    p.x = abs(p.x) - r;
    p.y = p.y + r / k;
    if (p.x + k * p.y > 0.0) p = vec2(p.x - k * p.y, -k * p.x - p.y) / 2.0;
    p.x -= clamp(p.x, -2.0 * r, 0.0);
    return -length(p) * sign(p.y);
}
vec3 carpet(vec2 s, float t, float pix)
{
    const float C = 0.14;                                     // the pattern's cell, in screen heights
    const vec3 NEON[6] = vec3[6](vec3(1.0, 0.1, 0.6), vec3(0.1, 0.9, 1.0), vec3(1.0, 0.9, 0.1),
                                 vec3(0.45, 1.0, 0.1), vec3(1.0, 0.45, 0.05), vec3(0.65, 0.25, 1.0));
    vec2 qd = s + floor(t * vec2(0.012, 0.007) / pix) * pix;   // drifting, in whole screen pixels
    vec2 q = qd;
    q.x += 0.5 * C * mod(floor(q.y / C), 2.0);
    vec2 id = floor(q / C), f = q / C - id - 0.5;
    float pc = pix / C;
    float h1 = hash13(vec3(id, 21.0)), h2 = hash13(vec3(id, 22.0)), h3 = hash13(vec3(id, 23.0));
    float ca = cos(h2 * TAU), sa = sin(h2 * TAU);
    vec2 pq = mat2(ca, sa, -sa, ca) * (f - (vec2(h3, fract(h3 * 7.0)) - 0.5) * 0.12);
    int kind = int(h1 * 6.0);
    float d, fill = 0.0;
    if (kind == 0)
    {
        float pl = length(pq) - 0.12;                          // a planet with a ring
        d = min(abs(pl), abs(length(pq * vec2(1.0, 3.2)) - 0.25) / 2.2);
        fill = step(pl, 0.0);
    }
    else if (kind == 1)
    {
        float st = sdStar5(pq, 0.2, 0.45);                     // a star
        d = abs(st);
        fill = step(st, 0.0);
    }
    else if (kind == 2)
    {
        float x = clamp(pq.x, -0.26, 0.26);                    // a squiggle
        vec2 e = pq - vec2(x, 0.06 * sin(x * 24.0));
        d = abs(pq.x) < 0.26 ? abs(e.y) / sqrt(1.0 + 2.07 * cos(x * 24.0) * cos(x * 24.0)) : length(e);
    }
    else if (kind == 3)
        d = abs(sdTriangle(pq, 0.17));                        // a triangle
    else if (kind == 4)
    {
        d = min(abs(length(pq) - 0.17), max(length(pq) - 0.04, 0.0));   // a ring with a dot
        fill = step(length(pq), 0.04);
    }
    else
    {
        d = min(min(length(pq - vec2(-0.14, 0.05)), length(pq - vec2(0.12, 0.1))), length(pq - vec2(0.02, -0.13))) - 0.045;
        fill = step(d, 0.0);                                   // three dots
        d = abs(d);
    }
    vec3 neon = NEON[int(fract(h1 * 13.0) * 6.0)];
    float fib = hash13(vec3(floor(qd / (2.0 * pix)), 30.0));  // the carpet's fibres
    vec3 col = vec3(0.006, 0.004, 0.012) * (0.6 + 0.8 * fib);
    float win = smoothstep(0.5, 0.38, max(abs(f.x), abs(f.y)));   // the glow ends inside the cell
    col += neon * (0.55 * smoothstep(0.018 + pc, 0.018 - pc, d) + 0.18 * win * exp(-d / 0.04) + 0.1 * fill) * (0.8 + 0.4 * fib);
    // specks of colour in the pile
    float hsp = hash13(vec3(floor(qd / 0.012), 24.0));
    if (hsp > 0.985) col += NEON[int(fract(hsp * 31.0) * 6.0)] * 0.25 * smoothstep(0.35, 0.2, length(fract(qd / 0.012) - 0.5));
    return col;
}

// Around the tube 2-11: the scene behind the monitor. hs: half the screen's size in screen heights.
vec3 scene(int k, vec2 s, vec2 fc, vec2 hs, float t, float pix)
{
    if (k == 2) return space(s, t, pix);
    if (k == 3) return synthwave(s, t, pix);
    if (k == 4) return tiles(fc, t, pix);
    if (k == 5) return bounceLines(s, hs, t, pix);
    if (k == 6) return underwater(fc, hs, t, pix);
    if (k == 7) return copper(fc, hs, t, pix);
    if (k == 8) return amigaDemo(fc, hs, t, pix);
    if (k == 9) return hills(fc, t, pix);
    if (k == 10) return city(fc, t, pix);
    return carpet(s, t, pix);
}

// The colour of the light a scene casts on the casing (Room light sets how much).
vec3 sceneLight(int k, float t)
{
    if (k == 2) return vec3(0.96, 0.98, 1.0);                 // space: white, like sunlight
    if (k == 3) return vec3(1.0, 0.72, 0.95);                 // synthwave: pink, like its sun
    if (k == 4) return tilesLight(t);
    if (k == 5) return vec3(0.85, 0.9, 1.0);
    if (k == 6) return vec3(0.7, 0.9, 1.0);                   // underwater: blue-green
    if (k == 7) return vec3(1.0, 0.85, 0.9);
    if (k == 8) return vec3(0.85, 0.85, 1.0);                 // Amiga demo
    if (k == 9) return vec3(1.0, 0.98, 0.92);
    if (k == 10) return vec3(0.72, 0.76, 1.0);
    return vec3(0.82, 0.6, 1.0);                              // carpet: black light
}

// What is around the tube (Around the tube), drawn where the glass ends. Linear light, like the
// picture. c: tube position (-1..1, after curvature); dist, qq: how far outside the tube edge, in
// the same units; tc: position on the tube before curvature (0..1); tubePx: tube size in pixels.
// Sizes are in mm (the tube is Monitor size), so the casing is as large as the real one.
vec3 backdrop(vec2 c, float dist, vec2 qq, vec2 tc, vec2 tubePx)
{
    vec2 tube = monTubeMM();
    float mmpp = tube.y / tubePx.y;                               // mm per screen pixel
    float aa = mmpp;                                              // one screen pixel
    vec2 p = (tc - 0.5) * tubePx * mmpp;                          // mm from the tube centre
#ifndef LR_ROWS_UP
    p.y = -p.y;                                                   // y up
#endif
    int scn = int(global.LR_BACKDROP + 0.5);                      // 1 a room, 2 and up a scene
    bool inScene = scn >= 2;
    float t = float(params.FrameCount % 1000000u) / 60.0 * global.LR_SCENE_SPEED;   // the scenes' clock

    // The casing, measured on photos of real monitors.
    // Amiga monitor (Commodore M1438S): warm off-white, a thick flat front, a 12.5 mm tunnel with
    //   mitred corners down to a thin black mask at the glass, a plain chin, a green light.
    // PC monitor (Compaq 151FS): aged cream, a wide front, a raised rim around a shallower
    //   recess, a blank plaque on the chin, a control strip below with buttons and a green light.
    // TV (after photos of 90s TVs): dark grey plastic, thin sides, the glass almost flush with
    //   the front, a tall chin with speaker grilles on both sides of the controls; sized to the
    //   tube (Monitor size), since TVs came from 14 to 29 inches.
    // Margins from the glass to the outline (side, top, bottom), recess width (with the mask),
    // mask width, the shell band around the front, corner radii of the outline and the recess.
    bool tv = !inMM(vTiming.x);
    bool amiga = !tv && global.LR_MON_MM > 0.5;
    bool pc = !tv && !amiga;
    vec3 m = amiga ? vec3(41.0, 41.0, 48.0) : pc ? vec3(44.0, 46.0, 39.0) : vec3(0.11, 0.13, 0.34) * tube.y;
    float rw = amiga ? 12.5 : pc ? 12.0 : max(0.03 * tube.y, 5.0), mw = tv ? 1.0 : 1.2, sh = amiga ? 4.0 : 3.0;
    float ro = tv ? 0.05 * tube.y : 7.0, rr = amiga ? 2.0 : tv ? 0.04 * tube.y : 6.0;
    vec3 albedo = amiga ? vec3(0.60, 0.56, 0.44) : pc ? vec3(0.66, 0.57, 0.33) : vec3(0.07, 0.07, 0.075);

    // the light: the room's (Room light: 1 a dim room with warm lamps, 20 daylight, whiter and
    // more even), brightest near the monitor. In a scene as much light as Room light gives, even,
    // in the scene's colour (white in space, pink in the synthwave scene, like its sun), so the
    // monitor shows in front of it.
    float day = smoothstep(1.0, 20.0, global.LR_ROOM);
    float near = mix(1.0 / (1.0 + dot(p, p) / (260.0 * 260.0)), 1.0, inScene ? 1.0 : 0.6 * day);
    vec3 light = (inScene ? sceneLight(scn, t) : mix(vec3(1.0, 0.93, 0.82), vec3(1.0), day))
               * (0.0167 * global.LR_ROOM * near);

    vec2 inner = 0.5 * tube;
    float d = dist * mix(1.0, tubePx.x / tubePx.y, smoothstep(-0.05, 0.05, qq.x - qq.y)) * 0.5 * tube.y;   // mm from the glass
    vec2 hs = inner + vec2(m.x, 0.5 * (m.y + m.z));               // half size of the outline
    vec2 at = p - vec2(0.0, 0.5 * (m.y - m.z));                   // from the centre of the outline
    float s = roundBox(at, hs, ro);                               // < 0 on the casing
    float u = -roundBox(p, inner + rw, rr);                       // > 0 in the recess
    float rec = smoothstep(-aa, aa, u);

    // Which way the surface faces. Away from the glass: rounded corners, mitred on the Amiga.
    vec2 nIn = boxOut(p, inner, 6.0);
    if (amiga)
    {
        vec2 e = abs(p) - inner;
        nIn = normalize(mix(vec2(0.0, sign(p.y)), vec2(sign(p.x), 0.0), smoothstep(-aa, aa, e.x - e.y)));
    }
    float phi;                                                    // < 0 toward the glass, > 0 outward
    vec2 dir = nIn;
    if (u > -5.0)
    {
        // a rounded lip, then the walls down to the glass: steep on the Amiga monitor (55 degrees),
        // shallower on the PC monitor (45 degrees) and the TV (40 degrees, a narrow chamfer)
        phi = (amiga ? -0.96 : pc ? -0.79 : -0.70) * smoothstep(-0.6, 1.2, u);
    }
    else
    {
        // the front's edge rolls over to a seam, then the casing's shell rolls away
        dir = boxOut(at, hs, ro);
        phi = s < -sh ? 0.87 * smoothstep(-sh - 2.0, -sh, s) : mix(0.44, 1.4, smoothstep(-1.5, 0.0, s));
    }
    vec3 n = tilt(dir, phi);
    if (pc)              // the top of the front slopes back a little
        n = normalize(n + vec3(0.0, 0.25 * smoothstep(inner.y + rw + 12.0, inner.y + m.y - sh, p.y), 0.0));

    // less of the room's light gets into the recess, least near the glass; the seam is dark;
    // the mask next to the glass is black
    float occ = (1.0 - rec * (0.05 + 0.15 * smoothstep(0.0, rw, u)))
              * (1.0 - 0.6 * exp(-0.5 * (s + sh) * (s + sh) / max(0.16, aa * aa)));
    vec3 alb = albedo * mix(1.0, 0.92, rec);
    if (!tv)             // the plastic has aged a little unevenly
        alb *= 1.0 - 0.03 * valueNoise(p / 30.0 + 7.0);
    float gloss = tv ? 0.6 : 0.5;
    alb = mix(alb, vec3(0.02), 1.0 - smoothstep(mw - aa, mw + aa, d));

    vec3 lamp = vec3(0.0);                                        // the power light
    vec3 stripCol = vec3(0.0);
    float strip = 0.0;
    float yb = -inner.y - m.z;                                    // bottom of the outline
    if (pc)
    {
        // a blank pill-shaped plaque on the chin, left: a grey raised rim around a light face
        vec2 pq = p - vec2(-0.406 * tube.x, -inner.y - rw - 12.0);
        float pd = pill(pq, 20.5, 7.5);
        if (pd < 1.5)
        {
            vec2 pdir = normalize(vec2(sign(pq.x) * max(abs(pq.x) - 20.5, 0.0), pq.y) + 1e-4);
            float on = 1.0 - smoothstep(-aa, aa, pd);
            float rim = on * smoothstep(-1.5 - aa, -1.5 + aa, pd);
            n = normalize(mix(n, tilt(pdir, 0.6), rim));
            alb = mix(alb, mix(vec3(0.86, 0.84, 0.79), vec3(0.55, 0.55, 0.56), rim), on);
            gloss = mix(gloss, min(gloss + 0.3, 1.0), on);   // the plaque is glossier
            occ *= 1.0 - 0.35 * (1.0 - on) * (1.0 - smoothstep(0.0, 1.5, pd)) * max(-pdir.y, 0.0);   // its shadow below
        }

        // the control strip, a separate part under the front: a recessed panel in the middle,
        // the power light and two buttons on the right, seams between the parts
        vec2 sp = p - vec2(0.0, yb - 6.0);
        strip = 1.0 - smoothstep(-aa, aa, roundBox(sp, vec2(110.5, 7.0), 2.0));
        if (strip > 0.0)
        {
            float sphi = 0.0;
            vec2 sdir = vec2(0.0, 1.0);
            float pan = roundBox(sp - vec2(8.5, -0.5), vec2(50.5, 4.5), 1.0);
            if (pan < 0.0)
            {
                sdir = boxOut(sp - vec2(8.5, -0.5), vec2(50.5, 4.5), 1.0);
                sphi = -0.8 * smoothstep(-1.0, 0.0, pan);         // its walls face inward
            }
            for (int k = 0; k < 2; k++)
            {
                vec2 bc = vec2(k == 0 ? 85.5 : 102.25, -0.5);
                vec2 bh = vec2(k == 0 ? 7.5 : 7.25, 4.5);
                float bd = roundBox(sp - bc, bh, 0.8);
                if (bd < 0.0)
                {
                    sdir = boxOut(sp - bc, bh, 0.8);
                    sphi = 0.6 * smoothstep(-0.8, 0.0, bd);        // bevelled buttons
                }
            }
            float seam = 0.0;
            for (int k = 0; k < 4; k++)
            {
                float x0 = k == 0 ? -42.0 : k == 1 ? 59.0 : k == 2 ? 77.0 : 94.0;
                seam = max(seam, exp(-0.5 * (sp.x - x0) * (sp.x - x0) / max(0.09, aa * aa)));
            }
            float socc = mix(0.7, 1.0, smoothstep(5.0, 2.5, sp.y))    // in the shadow of the front above
                       * (1.0 - 0.6 * seam);
            stripCol = plastic(albedo * 0.97, tilt(sdir, sphi), vec3(p, 0.0), light, socc, 0.5);
            float ld = roundBox(sp - vec2(70.0, -0.5), vec2(1.2, 0.6), 0.3);
            lamp += vec3(0.12, 1.0, 0.2) * (0.6 * (1.0 - smoothstep(-aa, aa, ld)) + 0.03 * exp(-0.5 * max(ld, 0.0) * max(ld, 0.0) / 9.0)) * strip;
        }
    }
    else if (tv)
    {
        // The TV's chin: a band set a little into the front, with a speaker grille on each side
        // and the controls between them: the power switch, the red light, four small buttons.
        float h = tube.y;
        float top = -inner.y - rw - 0.07 * h, bot = yb + 0.035 * h;
        vec2 bc = vec2(0.0, 0.5 * (top + bot)), bh = vec2(hs.x - 0.04 * h, 0.5 * (top - bot));
        float bd = roundBox(p - bc, bh, 2.0);
        float band = 1.0 - smoothstep(-aa, aa, bd);
        float panel = 0.36 * hs.x;                                // half width of the controls
        occ *= (1.0 - 0.6 * exp(-0.5 * bd * bd / max(0.2, aa * aa)))                       // its seam
             * (1.0 - 0.3 * band * (1.0 - smoothstep(0.0, 2.5, bh.y - (p.y - bc.y))))     // shadow at its top
             * (1.0 - 0.5 * band * exp(-0.5 * (abs(p.x) - panel) * (abs(p.x) - panel) / max(0.2, aa * aa)));
        alb *= mix(1.0, 0.85, band);
        // the grilles: holes 1.2 mm wide, 2.2 mm apart (seen as an even darker grey when they are
        // smaller than a pixel)
        float grill = (1.0 - smoothstep(-2.0 - aa, -2.0 + aa, bd)) * smoothstep(panel + 2.0 - aa, panel + 2.0 + aa, abs(p.x));
        if (grill > 0.0)
        {
            vec2 per = vec2(2.2, 3.81);
            float hd = min(length(mod(p, per) - 0.5 * per), length(mod(p + 0.5 * per, per) - 0.5 * per));
            float hole = mix(clamp((0.6 - hd) / aa + 0.5, 0.0, 1.0), 0.27, smoothstep(1.1, 2.2, aa));
            alb = mix(alb, vec3(0.006), hole * grill);
        }
        // the buttons: round, domed, in a dark ring
        for (int k = 0; k < 5; k++)
        {
            vec2 bp = p - vec2(k == 0 ? -0.6 * panel : panel * (0.04 + 0.16 * float(k)), bc.y);
            float br = (k == 0 ? 0.026 : 0.011) * h, bl = length(bp);
            if (bl < br + 2.0)
            {
                float on = 1.0 - smoothstep(br - aa, br + aa, bl);
                occ *= 1.0 - 0.6 * (1.0 - on) * exp(-0.5 * (bl - br) * (bl - br) / max(0.15, aa * aa));
                n = normalize(mix(n, tilt(bp / max(bl, 1e-3), 0.9 * bl / br), on));
            }
        }
        float ld = length(p - vec2(-0.3 * panel, bc.y));
        lamp += vec3(1.0, 0.06, 0.03) * (0.6 * (1.0 - smoothstep(1.0 - aa, 1.0 + aa, ld)) + 0.03 * exp(-0.5 * ld * ld / 9.0));
    }
    else
    {
        // the Amiga monitor's power light, under the front, right of the middle
        float ld = roundBox(p - vec2(0.356 * tube.x, yb + 2.2), vec2(1.3, 0.6), 0.4);
        lamp += vec3(0.12, 1.0, 0.2) * (0.6 * (1.0 - smoothstep(-aa, aa, ld)) + 0.03 * exp(-0.5 * max(ld, 0.0) * max(ld, 0.0) / 9.0));
    }

    // The screen lights the walls of the recess, which face the picture beside them, so they
    // change with it (Screen light on the casing). A point on a wall z mm in front of the phosphor
    // (which sits about 10 mm behind the front of the glass) sees the picture from about 0.7 z to
    // 3 z beyond the edge; a Lambert screen of luminance L gives it 0.67 L (the Amiga's 55-degree
    // walls) or 0.46 L (45 degrees) of illuminance, so the wall shines at 0.21 or 0.15 of L times
    // its albedo. On these slanted paths the tinted glass lets about 30% out, and the dot mask
    // and the lines leave about 70% of the picture's light: 0.045 and 0.03. Plastic scatters it,
    // it does not mirror the picture. Six samples of the glowing picture over that region.
    vec3 pic = vec3(0.0);
    float facing = smoothstep(-0.6, 1.2, u) * global.LR_SPILL;
    if (facing > 0.0)
    {
        float ov = 1.0 - global.LR_OVERSCAN;
        vec2 edge = clamp(c * 0.5 * ov + 0.5, 0.0, 1.0);          // the tube's edge on the picture
        vec2 nt = boxOut((tc - 0.5) * tube, inner, 15.0);         // outward, in texture orientation
        vec2 inward = -nt / tube * ov, side = vec2(-nt.y, nt.x) / tube * ov;   // per mm
        float z = (amiga ? 1.43 : pc ? 1.0 : 0.84) * max(d - mw, 0.0) + 10.0;
        vec3 gl = vec3(0.0);
        for (int i = 0; i < 2; i++)
            for (int j = -1; j <= 1; j++)
            {
                vec2 q = edge + inward * ((i == 0 ? 0.9 : 2.4) * z) + side * (1.3 * float(j) * z);
                gl += (j == 0 ? 0.25 : 0.125) * textureLod(LR_Glow, clamp(q, 0.0, 1.0), 0.0).rgb;
            }
        pic = gl * vWhite * (amiga ? 0.045 : pc ? 0.03 : 0.026) * facing;
    }

    vec3 casing = plastic(alb, n, vec3(p, 0.0), light, occ, gloss) + alb * pic;
    vec2 sc = (vTexCoord - 0.5) * params.OutputSize.xy * params.OutputSize.w;   // screen heights
    vec2 fc = floor(vTexCoord * params.OutputSize.xy) - floor(0.5 * params.OutputSize.xy);   // screen pixel
#ifndef LR_ROWS_UP
    sc.y = -sc.y;                                                 // y up
    fc.y = -1.0 - fc.y;
#endif
    vec3 behind = inScene ? scene(scn, sc, fc, 0.5 * params.OutputSize.xy * params.OutputSize.w, t, params.OutputSize.w)
                : vec3(0.20, 0.195, 0.18) * light;               // the wall, further from the light
    if (scn == 3)        // the casing's edge, turned away from the viewer, catches the sunset's light
        casing += behind * 0.2 * smoothstep(-sh - 1.0, 0.0, s);
    behind = mix(behind, stripCol, strip);
    return mix(behind, casing, 1.0 - smoothstep(-aa, aa, s)) + lamp;
}

void main()
{
    float scan = vTiming.x, pal = vTiming.y, interlace = vTiming.z;

    // tube geometry: barrel curvature and rounded corners
    vec2 tc = vTexCoord;
    vec2 tubePx = params.OutputSize.xy;                   // size of the tube face in screen pixels
    float px = 2.0 * params.OutputSize.z;
    float va = params.OutputSize.x * params.OutputSize.w;
#ifndef LR_FIT_43
    // RetroArch: the picture area is the tube, unless something is drawn around it; then
    // it may fill the screen (Aspect Ratio: Full)
    if (global.LR_BACKDROP > 0.5 && abs(0.75 * va - 1.0) > 0.02)
#endif
    {
        // the picture area may be any shape: the 4:3 tube sits in its middle
        vec2 fit = va > 4.0 / 3.0 ? vec2(4.0 / 3.0 / va, 1.0) : vec2(1.0, 0.75 * va);
        tc = (tc - 0.5) / fit + 0.5;
        tubePx *= fit;
        px /= fit.x;
    }
    // real size: the tube as large as the monitor it describes (Monitor size), on a screen whose
    // height is given; the picture area must be as tall as the screen (full screen)
    if (global.LR_SCREEN_H > 0.0)
    {
        float screenMM = global.LR_SCREEN_H * (global.LR_SCREEN_UNIT > 0.5 ? 25.4 : 10.0);
        float s = min(params.OutputSize.y * monTubeMM().y / (screenMM * tubePx.y), 1.0);
        tc = (tc - 0.5) / s + 0.5;
        tubePx *= s;
        px /= s;
    }
    vec2 c = tc * 2.0 - 1.0;
    c *= 1.0 + vec2(global.LR_CURV_X, global.LR_CURV_Y) * (c.yx * c.yx);
    float corner = global.LR_CORNER;
    vec2 qq = abs(c) - 1.0 + corner;
    float dist = length(max(qq, 0.0)) + min(max(qq.x, qq.y), 0.0) - corner;
    float inside = 1.0 - smoothstep(-px, px, dist);

    vec3 col = vec3(0.0);
    if (inside > 0.0)
    {
        vec2 ut = c * 0.5 + 0.5;
        vec2 ua = (ut - 0.5) * (1.0 - global.LR_OVERSCAN) + 0.5;
        float srcH = params.OriginalSize.y / vTiming.w;   // native lines
        float rpl = 1.0, ph = 0.0;                         // beam rows per line, row of line 0
#ifdef LR_FIELDS
        // Amiga emulators draw every line twice (720x568 for a 256-line game) and weave the
        // two fields of an interlaced screen into the same rows. Identical row pairs are one
        // line: the gaps between lines stay, as on the real monitor. (0.5/255: 8-bit frames.)
        if (interlace > 0.5 && vAmiga > 0.5)
        {
            vec2 fd = texelFetch(LR_Fields, ivec2(0, 0), 0).rg;   // largest pair difference, both pairings
            if (min(fd.x, fd.y) < 0.5 / 255.0)
            {
                interlace = 0.0;
                rpl = 2.0;
                ph = fd.x < 0.5 / 255.0 ? 0.0 : 1.0;
                srcH = floor((srcH - ph) * 0.5);
            }
        }
#endif

        // raster: TV modes sit in a 240/288-line field, VGA and 24 kHz arcade fill the screen
        bool vga = scan > 0.5 && scan < 1.5;
        bool mm = inMM(scan);                              // tube described in mm (PC monitor, 1084S)
        // position along the source line: a 15 kHz monitor in mm shows the whole active line,
        // so a source shorter than the line leaves side borders
        float xs = (ua.x - 0.5) / vShare + 0.5;
        float dbl = (vga && srcH <= 300.0) ? 2.0 : 1.0;
        float BH = srcH * dbl;
        float visLines = scan > 0.5 ? BH : (pal > 0.5 ? 288.0 : 240.0) * (interlace > 0.5 ? 2.0 : 1.0);
        bool il = interlace > 0.5 && global.LR_STEADY < 0.5;
        float stepL = il ? 2.0 : 1.0;
        float offL = il ? float(params.FrameCount % 2u) : 0.0;
        float sgk = (interlace > 0.5 && global.LR_STEADY > 0.5) ? 2.0 : 1.0;

        float Lf = ua.y * visLines - (visLines - BH) * 0.5;
        float u = (Lf - offL - 0.5) / stepL;
        float j0 = floor(u);

        // vertical convergence error grows toward the edges (the horizontal one is in the beam pass)
        float convV = global.LR_CONV * 0.5 * (0.3 + 0.7 * c.y * abs(c.y));
        // spot height in lines: TV in line units; a tube in mm from its line pitch, so the same
        // spot covers more lines in 768-line modes than in 400-line ones, and NTSC lines (fewer,
        // further apart) show more than PAL lines
        vec2 sig = vec2(global.LR_SIG_LO, max(global.LR_SIG_LO, global.LR_SIG_HI)) * sgk;
        if (mm)
            sig = vec2(global.LR_MON_SPOT_LO, max(global.LR_MON_SPOT_LO, global.LR_MON_SPOT_HI))
                * (FWHM_TO_SIGMA * monCornerGrowth(c) * visLines / monScanMM().y);

        // every line within 3.5 spot widths: the lines left out are at least kn + 1 away
        // (4 lines for 240p TV, more for wide spots: merged fields, 1024x768 on a PC monitor)
        int kn = int(clamp(ceil(3.5 * sig.y - 1.0), 1.0, 6.0));
        for (int k = -kn; k <= kn + 1; k++)
        {
            float j = j0 + float(k);
            float i = j * stepL + offL;
            vec4 bb = beamAt(floor(i / dbl) * rpl + ph, xs, srcH * rpl + ph);
            vec3 b = bb.rgb;
            vec3 lc = b * b;                                      // linear brightness
            // beam height: TV from the local brightness per gun; PC from the spot that made the light
            vec3 isg = 1.0 / mix(vec3(sig.x), vec3(sig.y), mm ? vec3(bb.a) : b);
            vec3 d = vec3(u - j) + vec3(convV, 0.0, -convV);
            col += lc * exp(-0.5 * d * d * isg * isg) * isg * 0.39894228;
        }
        col *= smoothstep(-0.002, 0.002, xs) * smoothstep(1.002, 0.998, xs);

        // phosphor mask
        float mt = global.LR_MASK_TYPE;
        float triads = global.LR_MASK_TRIADS;
        // tube in mm: from the mask pitch (a dot mask's pitch is diagonal, 0.866 of it horizontal)
        if (mm) triads = monTubeMM().y / (global.LR_MON_PITCH * (mt > 2.5 && mt < 3.5 ? 0.866 : 1.0));
        float pitchPx = tubePx.y / triads;
        float str = global.LR_MASK_STR * smoothstep(1.6, 2.6, pitchPx);
        if (mt > 0.5 && str > 0.0)
        {
            vec2 mp = ut * tubePx / pitchPx;
            float dp = 0.25 / pitchPx;
            vec3 m = maskAvg(mp - dp, mp + dp, mt);
            float avg = mt < 1.5 ? 0.367 : (mt < 2.5 ? 0.31 : (mt < 3.5 ? 0.19 : 0.84));
            col *= mix(vec3(1.0 - str), vec3(1.0), m) / mix(1.0 - str, 1.0, avg);
        }
        if (mt > 0.5 && mt < 1.5)
        {
            // aperture grille: the two horizontal damper wires
            float hp = tubePx.y;
            float sw = max(0.45, hp / 4000.0);
            float d1 = (ut.y - 1.0 / 3.0) * hp, d2 = (ut.y - 2.0 / 3.0) * hp;
            col *= 1.0 - 0.2 * (exp(-0.5 * d1 * d1 / (sw * sw)) + exp(-0.5 * d2 * d2 / (sw * sw)));
        }

        col *= vWhite;

        // halation: light diffused in the glass (energy-conserving)
        float gy = Lf / BH;
        float gin = smoothstep(-0.03, 0.03, gy) * smoothstep(1.03, 0.97, gy) * smoothstep(-0.03, 0.03, xs) * smoothstep(1.03, 0.97, xs);
        col = mix(col, textureLod(LR_Glow, vec2(xs, gy), 0.0).rgb * vWhite, global.LR_GLOW * gin);

        // the tube's phosphors are not exactly the display's primaries; colours outside
        // the display gamut are clipped in SDR and kept in HDR
        if (vPhos > 1.5) col = phosphorMatrix(vPhos) * col;

#ifdef LR_HDR
        // HDR: the display has headroom above paper white, so bright phosphor dots and
        // scanline centres keep their full brightness; the shoulder only starts near the peak
        float room = max(global.LR_NITS_PEAK / global.LR_NITS_WHITE, 1.0);
        col = room * shoulder(col / room);
#else
        col = shoulder(col);
#endif
        float r2 = clamp(dot(c, c) * 0.5, 0.0, 1.0);
        col *= 1.0 - global.LR_VIGNETTE * pow(r2, 1.6);
        col += global.LR_AMBIENT * global.LR_ROOM * vec3(0.80, 0.92, 0.87);
        col *= inside;
    }
    if (global.LR_BACKDROP > 0.5 && inside < 1.0)
        col += (1.0 - inside) * backdrop(c, max(dist, 0.0), qq, tc, tubePx);

#ifndef LR_NO_FEEDBACK
    // the phosphor's afterglow, on the tube only (what is around it is not phosphor)
    if (inside > 0.0)
    {
        vec3 prev = textureLod(LR_CRTFeedback, vTexCoord, 0.0).rgb;
        col = max(col, prev * global.LR_PERSIST);
    }
#endif
    FragColor = vec4(col, 1.0);
}
