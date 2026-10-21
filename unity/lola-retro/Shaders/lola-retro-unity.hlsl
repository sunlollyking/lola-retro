// Lola Retro - what every pass needs in Unity: the uniforms RetroArch gives a pass, the samplers,
// the GLSL functions HLSL lacks, and the triangle every pass draws.
#ifndef LOLA_RETRO_UNITY_INCLUDED
#define LOLA_RETRO_UNITY_INCLUDED

#include "lola-retro-params.hlsl"

// Per pass, as RetroArch's push constants: (width, height, 1 / width, 1 / height)
float4 LR_SourceSize;     // the texture this pass reads (the previous pass's output)
float4 LR_OriginalSize;   // the console's frame
float4 LR_OutputSize;     // this pass's output
int LR_FrameCountI;
#define LR_FrameCount ((uint)LR_FrameCountI)

float LR_UVTop;           // 1 where Unity's render targets start at the top (Direct3D, Metal, Vulkan)
float LR_OutputLinear;    // 1: the last pass writes linear light (Unity's linear colour space)

// GLSL's mod: x - y floor(x / y), also for negative x (HLSL's fmod truncates)
float lr_mod(float x, float y) { return x - y * floor(x / y); }
float2 lr_mod(float2 x, float y) { return x - y * floor(x / y); }
float3 lr_mod(float3 x, float y) { return x - y * floor(x / y); }
float4 lr_mod(float4 x, float y) { return x - y * floor(x / y); }
float2 lr_mod(float2 x, float2 y) { return x - y * floor(x / y); }
float3 lr_mod(float3 x, float3 y) { return x - y * floor(x / y); }
float4 lr_mod(float4 x, float4 y) { return x - y * floor(x / y); }

// GLSL's matrix constructors fill the columns first, HLSL's the rows
float2x2 lr_mat2(float a, float b, float c, float d) { return float2x2(a, c, b, d); }
float3x3 lr_mat3(float a0, float a1, float a2, float a3, float a4, float a5, float a6, float a7, float a8)
{
    return float3x3(a0, a3, a6, a1, a4, a7, a2, a5, a8);
}

float3x3 lr_inverse(float3x3 m)
{
    float3 r0 = cross(m[1], m[2]), r1 = cross(m[2], m[0]), r2 = cross(m[0], m[1]);
    return transpose(float3x3(r0, r1, r2)) / dot(m[0], r0);
}

// One triangle over the whole target. pos: clip space; tc: RetroArch's TexCoord, 0..1 across
// the target with the top row at y = 0 (Unity counts rows from the bottom).
void lr_fullscreen_triangle(uint id, out float4 pos, out float2 tc)
{
    float2 p = float2((id << 1) & 2, id & 2);
    pos = float4(p * 2.0 - 1.0, 0.5, 1.0);
    tc = float2(p.x, LR_UVTop > 0.5 ? p.y : 1.0 - p.y);
}

// The last pass's colour for Unity: RetroArch's picture goes to the display as it is, so in
// Unity's linear colour space it is written as the linear light that Unity turns back into it
float3 lr_srgb_to_linear(float3 c) { return lerp(c / 12.92, pow((c + 0.055) / 1.055, 2.4), step(0.04045, c)); }
float3 lr_linear_to_srgb(float3 c) { return lerp(c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - 0.055, step(0.0031308, c)); }
float4 lr_output(float4 c) { return LR_OutputLinear > 0.5 ? float4(lr_srgb_to_linear(c.rgb), c.a) : c; }

#endif
