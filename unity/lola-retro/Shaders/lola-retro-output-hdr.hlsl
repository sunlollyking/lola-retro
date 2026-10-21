// Lola Retro - Unity only, the last pass on an HDR display (instead of present). The CRT pass for
// HDR displays (crt-hdr) leaves linear Rec.709 light, 1.0 a full white field at paper white, and
// phosphor dots and line centres brighter, up to the display's peak (as in the RetroArch
// version's hdr presets). Unity's own last pass codes the picture for the display (PQ or scRGB),
// so this pass only puts the light in the form Unity has there: with URP's post-processing on
// (and in HDRP), in the display's colour space (Rec.2020 or P3) and in nits; without it, as it is.
// LR_OutputRow0..2: that conversion (Rec.709 -> the display's space, times paper white in nits),
// set by the C# side.
#include "lola-retro-unity.hlsl"

Texture2D Source;
float4 LR_OutputRow0, LR_OutputRow1, LR_OutputRow2;

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

float4 LRFrag(LRVaryings i) : SV_Target
{
    // the CRT pass's output is this pass's size: the texel under the pixel (rows from the bottom)
    int2 p = int2(floor(i.vTexCoord * LR_OutputSize.xy));
    float3 c = Source.Load(int3(p.x, int(LR_OutputSize.y) - 1 - p.y, 0)).rgb;
    float3x3 m = float3x3(LR_OutputRow0.xyz, LR_OutputRow1.xyz, LR_OutputRow2.xyz);
    return float4(max(mul(m, c), 0.0), 1.0);
}
