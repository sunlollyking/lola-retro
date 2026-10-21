// Lola Retro - Unity only, the first pass: the console's frame, made from what Unity rendered.
// The game's camera may render at the console's resolution (a low-resolution render texture),
// then every console pixel is one texel of it. Or it renders at a higher resolution, then each
// console pixel is the average of the texels it covers, as a coarser camera sensor would see
// it. Unity works in linear light, a console sends gamma-coded RGB: the frame is coded the way an
// emulator's frame is (sRGB), which is what the other passes expect.
// The texels are read directly (no sampler), so the input's own filter setting does not matter.
// With LR_INPUT_ARRAY the input is one slice of a texture array: one eye in XR, and HDRP's
// camera image, which is always an array.
// On an HDR display the input is in the form Unity's post-processing leaves it (see
// lola-retro-output-hdr.hlsl): LR_InputRow0..2 turn it back into Rec.709, 1.0 at paper white.
#include "lola-retro-unity.hlsl"

#ifdef LR_INPUT_ARRAY
Texture2DArray LR_InputArray;
float LR_InputSlice;
#define LR_INPUT_LOAD(p) LR_InputArray.Load(int4(p, int(LR_InputSlice), 0))
#else
Texture2D LR_Input;
#define LR_INPUT_LOAD(p) LR_Input.Load(int3(p, 0))
#endif
float4 LR_InputRect;     // the part of the input to use, in texels: left, bottom, width, height
float LR_InputLinear;    // 1: the input holds linear light (Unity's linear colour space)
float4 LR_InputRow0, LR_InputRow1, LR_InputRow2;   // its colour -> linear Rec.709 (identity in SDR)

struct LRVaryings
{
    float4 position : SV_POSITION;
    float2 vTexCoord : TEXCOORD0;
};

LRVaryings LRVert(uint vertexID : SV_VertexID)
{
    LRVaryings o;
    lr_fullscreen_triangle(vertexID, o.position, o.vTexCoord);
    return o;
}

float3 lr_input(float2 t)
{
    int2 p = (int2)clamp(floor(t), LR_InputRect.xy, LR_InputRect.xy + LR_InputRect.zw - 1.0);
    return LR_INPUT_LOAD(p).rgb;
}

float4 LRFrag(LRVaryings i) : SV_Target
{
    // the console pixel (rows from the top) and the texels it covers, a to e (Unity's rows, from
    // the bottom)
    float2 px = floor(i.vTexCoord * LR_OutputSize.xy);
    float2 scale = LR_InputRect.zw * LR_OutputSize.zw;               // texels per console pixel
    float2 a = LR_InputRect.xy + float2(px.x, LR_OutputSize.y - 1.0 - px.y) * scale;
    float2 e = a + scale;
    float3 c = 0.0;
    float2 t0 = floor(a), n = ceil(e) - t0;
    if (any(scale < 0.9999))
    {
        // smaller than the console's frame: the texel under the pixel's centre
        c = lr_input(a + 0.5 * scale);
    }
    else if (all(n <= 16.0))
    {
        // the average over the texels it covers, each weighted by how much of it is covered
        [loop] for (float y = 0.0; y < n.y; y += 1.0)
        {
            float wy = min(t0.y + y + 1.0, e.y) - max(t0.y + y, a.y);
            [loop] for (float x = 0.0; x < n.x; x += 1.0)
            {
                float wx = min(t0.x + x + 1.0, e.x) - max(t0.x + x, a.x);
                c += wx * wy * lr_input(t0 + float2(x, y));
            }
        }
        c /= scale.x * scale.y;
    }
    else
    {
        // much larger: 16 x 16 texels spread evenly over it
        [loop] for (float y = 0.0; y < 16.0; y += 1.0)
            [loop] for (float x = 0.0; x < 16.0; x += 1.0)
                c += lr_input(a + (float2(x, y) + 0.5) * scale / 16.0);
        c /= 256.0;
    }
    c = saturate(mul(float3x3(LR_InputRow0.xyz, LR_InputRow1.xyz, LR_InputRow2.xyz), c));
    if (LR_InputLinear > 0.5)
        c = lr_linear_to_srgb(c);
    return float4(c, 1.0);
}
