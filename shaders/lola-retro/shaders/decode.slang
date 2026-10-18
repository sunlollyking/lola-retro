#version 450
// Lola Retro - pass 2: the TV's decoder.
// Luma: low-pass with peaking ("sharpness"), plus a trap that removes the
// chroma band (done by re-modulating the decoded chroma, as a real LC trap does
// spectrally), or a 2-line comb filter.
// Chroma: synchronous demodulation and a narrow low-pass (consumer sets: ~0.6 MHz).
// PAL: delay-line averaging with the previous line.
// Filters are Gaussians in time, so they do not depend on the sample rate.

#include "common.inc"

#pragma stage vertex
layout(location = 0) in vec4 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 0) out vec2 vTexCoord;

void main()
{
    gl_Position = global.MVP * Position;
    vTexCoord = TexCoord;
}

#pragma stage fragment
layout(location = 0) in vec2 vTexCoord;
layout(location = 0) out vec4 FragColor;
layout(set = 0, binding = 2) uniform sampler2D Source;    // encoded signal
layout(set = 0, binding = 3) uniform sampler2D Original;  // used directly for RGB

// Encoded signal at sample s of row r (row r holds native line r, see the encoder)
vec2 sig(float s, float r)
{
    if (r < 0.0) return vec2(0.0);
    return textureLod(Source, vec2((s + 0.5) * params.SourceSize.z, (r + 0.5) * params.SourceSize.w), 0.0).rg;
}

// Original pixel at x position xs on the native line that starts at row y (k rows averaged)
vec3 origPix(float xs, float y, float k)
{
    if (xs < 0.0 || xs >= params.OriginalSize.x) return vec3(0.0);
    float u = (floor(xs) + 0.5) * params.OriginalSize.z;
    vec3 c = vec3(0.0);
    for (float r = 0.0; r < k; r += 1.0)
        c += textureLod(Original, vec2(u, (y + r + 0.5) * params.OriginalSize.w), 0.0).rgb;
    return c / k;
}

// Rotate a (cos, sin) pair by the angle whose (cos, sin) is r
vec2 rotate(vec2 e, vec2 r) { return vec2(e.x * r.x - e.y * r.y, e.y * r.x + e.x * r.y); }

void main()
{
    Timing T = getTiming(params.OriginalSize.xy, params.FrameCount);
    float SW = params.SourceSize.x;
    float fs = T.pclk * SW / params.OriginalSize.x;
    float n = floor(vTexCoord.x * SW);
    // row r holds native line r, as in the encoder: an upscaled frame only uses the top 1/k
    float r = floor(vTexCoord.y * params.SourceSize.y);
    float y = r * T.scale;                                  // the line's first row in the frame
    float st = (T.interlace > 0.5 ? 2.0 : 1.0) * T.scale;   // previous line of the same field (frame rows)
    float sr = st / T.scale;                                // the same in rows of this pass
    float cable = global.LR_CABLE;
    bool show = isShow(cable);                   // TV show: the antenna brings composite
    float Y;
    vec2 uv;

    if (y >= params.OriginalSize.y)
    {
        FragColor = vec4(0.0);
        return;
    }

    if (cable > 2.5 && !show)
    {
        // RGB: no NTSC/PAL at all, only the video amplifier bandwidth: ~6.5 MHz in a TV,
        // ~85 MHz in a 15-inch PC monitor (spec sheets of the 90s: 65-120 MHz)
        float xs = (n + 0.5) * T.pclk / fs;
        float sigPx = 0.1325 / (isPC(T.scan) ? 85.0 : 6.5) * T.pclk;

        // dither blend: the 1-pixel alternating part around the five console pixels
        // the amplifier taps can reach, taken out of every tap below
        float db = global.LR_DBLEND;
        float sp = T.scale;
        float j0 = floor(xs / sp);
        vec3 alt[5];
        for (int i = 0; i < 5; i++) alt[i] = vec3(0.0);
        if (db > 0.0)
        {
            vec3 p[13];
            for (int i = 0; i < 13; i++) p[i] = origPix((j0 + float(i - 6) + 0.5) * sp, y, T.scale);
            for (int i = 0; i < 5; i++)
            {
                vec3 a = vec3(0.0);
                for (int k = -4; k <= 4; k++) a += DBW[abs(k)] * dbSign(k) * (p[i + 4 + k] - p[i + 4]);
                alt[i] = db * a / DBN;
            }
        }

        vec3 acc = vec3(0.0);
        float ws = 0.0;
        for (int k = -3; k <= 3; k++)
        {
            float w = exp(-0.5 * float(k * k));
            float xt = xs + float(k) * sigPx;
            vec3 v = origPix(xt, y, T.scale);
            if (db > 0.0) v -= alt[clamp(int(floor(xt / sp) - j0) + 2, 0, 4)];
            acc += w * v;
            ws += w;
        }
        vec3 c = acc / ws;
        Y = dot(c, vec3(0.299, 0.587, 0.114));
        uv = vec2(0.492 * (c.b - Y), 0.877 * (c.r - Y));
    }
    else
    {
        bool pal = T.pal > 0.5;
        bool sv = cable > 1.5 && !show;
        bool comb = global.LR_COMB > 0.5 && !sv && !pal && cable > 0.5 && abs(T.lineFrac - 0.5) < 0.05;

        float fcY, fcC, notch;
        if (cable < 0.5)  { fcY = pal ? 3.6 : 2.8; notch = 0.95; fcC = 0.5; }
        else if (!sv)     { fcY = comb ? 4.2 : (pal ? 4.2 : 3.3); notch = comb ? 0.0 : 0.92; fcC = pal ? 0.8 : 0.6; }
        else              { fcY = pal ? 5.0 : 4.6; notch = 0.0; fcC = pal ? 1.0 : 0.9; }

        float tn = n / fs;
        float sh = global.LR_SHARP * 1.4;

        // All taps sit on whole samples. A tap between two samples would read a linear
        // blend of them, and that blend loses part of the subcarrier (colour came out
        // up to 9% weaker). The tap step is a whole number of samples, so the subcarrier
        // still advances by the same angle from tap to tap.

        // luma: (1 + sh) * G(s1) - sh * G(s2), unit gain at DC, boost in the middle.
        float s1 = 0.1325 / fcY;
        float s2 = s1 * 2.5;
        float mY = max(1.0, ceil(0.25 * s2 * fs)); // tap step in samples (at most s2 / 4)
        float nY = min(ceil(3.0 * s2 * fs / mY), 12.0);
        float c1 = -0.5 * mY * mY / (s1 * s1 * fs * fs);
        float c2 = -0.5 * mY * mY / (s2 * s2 * fs * fs);
        float a1 = 0.0, a2 = 0.0, w1s = 0.0, w2s = 0.0;
        int nYi = int(nY);
        for (int k = -nYi; k <= nYi; k++)
        {
            float u = float(k);
            float w1 = exp(c1 * u * u);
            float w2 = exp(c2 * u * u);
            float sp = n + u * mY;
            float s0 = sig(sp, r).x;
            if (comb) s0 = 0.5 * (s0 + sig(sp, r - sr).x);
            a1 += w1 * s0; a2 += w2 * s0; w1s += w1; w2s += w2;
        }
        Y = (1.0 + sh) * a1 / w1s - sh * a2 / w2s;

        // chroma: demodulate against the subcarrier, then low-pass.
        // The subcarrier's (cos, sin) is rotated from tap to tap instead of recomputed.
        float sc = 0.1325 / fcC;
        float mC = max(1.0, ceil(0.15 * sc * fs)); // tap step in samples (at most 0.15 sigma)
        float nC = min(ceil(3.0 * sc * fs / mC), 20.0);
        float cw = -0.5 * mC * mC / (sc * sc * fs * fs);
        float vs = vSign(y, T), vsp = vSign(y - st, T);
        float tint = pal ? 0.0 : radians(global.LR_TINT);
        if (cable < 0.5)
        {
            // RF: the echo is on the colour burst too, so the TV locks to the shifted phase
            float ea = -TAU * T.fsc * RF_ECHO_US;
            tint += atan(RF_ECHO * sin(ea), 1.0 + RF_ECHO * cos(ea));
        }
        else if (show && global.LR_GHOST > 0.0)
        {
            // TV show: the same for the antenna ghost of the broadcast
            float ea = -TAU * T.fsc * SHOW_GHOST_US;
            tint += atan(global.LR_GHOST * sin(ea), 1.0 + global.LR_GHOST * cos(ea));
        }
        float ph0 = phaseAt(tn, y, T) + tint;      // subcarrier phase at the centre tap
        float da = TAU * T.fsc * mC / fs;
        vec2 rp = vec2(cos(da), sin(da));
        vec2 e0 = vec2(cos(ph0), sin(ph0));
        float a0 = ph0 - nC * da;
        a0 -= TAU * floor(a0 / TAU + 0.5);         // keep the angle small for an accurate sin/cos
        vec2 e = vec2(cos(a0), sin(a0));           // (cos, sin) at the first tap
        vec2 acc = vec2(0.0), accp = vec2(0.0);    // sums of w * signal * (sin, cos)
        float wcs = 0.0;
        int nCi = int(nC);
        for (int k = -nCi; k <= nCi; k++)
        {
            float u = float(k);
            float w = exp(cw * u * u);
            float sp = n + u * mC;
            vec2 v0 = sig(sp, r);
            float s0 = sv ? v0.y : v0.x;
            if (comb) s0 = 0.5 * (v0.x - sig(sp, r - sr).x);
            acc += w * s0 * e.yx;
            if (pal)
            {
                vec2 vp = sig(sp, r - sr);
                accp += w * (sv ? vp.y : vp.x) * e.yx;
            }
            wcs += w;
            e = rotate(e, rp);
        }
        vec2 cs = 2.0 * vec2(acc.x, vs * acc.y);
        uv = cs / wcs;

        if (!sv && !comb && notch > 0.0)
        {
            // trap: take the chroma band back out of luma
            float hf = (1.0 + sh) * exp(-19.7392 * s1 * s1 * T.fsc * T.fsc) - sh * exp(-19.7392 * s2 * s2 * T.fsc * T.fsc);
            Y -= notch * hf * (uv.x * e0.y + vs * uv.y * e0.x);
        }
        if (pal)
        {
            // the previous line has its own subcarrier phase: rotate its sums by the difference
            float dl = phaseAt(tn, y - st, T) - (ph0 - tint);
            vec2 rd = vec2(cos(dl), sin(dl));
            vec2 csp = 2.0 * vec2(accp.x * rd.x + accp.y * rd.y, vsp * (accp.y * rd.x - accp.x * rd.y));
            uv = 0.5 * (uv + csp / wcs);
        }
        if (cable < 0.5)
        {
            // RF: the echo also lands on sync and burst, and the TV's automatic gain and
            // colour control scale the picture back, so flat areas keep their level and colour
            float ea = -TAU * T.fsc * RF_ECHO_US;
            Y /= 1.0 + RF_ECHO;
            uv /= length(vec2(1.0 + RF_ECHO * cos(ea), RF_ECHO * sin(ea)));
        }
        else if (show && global.LR_GHOST > 0.0)
        {
            // TV show: the same for the antenna ghost of the broadcast
            float ea = -TAU * T.fsc * SHOW_GHOST_US;
            Y /= 1.0 + global.LR_GHOST;
            uv /= length(vec2(1.0 + global.LR_GHOST * cos(ea), global.LR_GHOST * sin(ea)));
        }
    }

    uv *= global.LR_SAT * global.LR_CONTRAST;
    Y = Y * global.LR_CONTRAST + global.LR_BRIGHT;
    vec3 rgb = decodeRGB(Y, uv, decoderType(T, cable));
    FragColor = vec4(clamp(rgb, 0.0, 1.0), 1.0);
}
