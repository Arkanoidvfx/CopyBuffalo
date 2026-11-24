// fxc /nologo /T ps_2_0 /E main /O3 /Fo Plate.ps Plate.hlsl
// The panel as a rigid plate: it tilts in perspective around the grabbed point. Straight lines stay straight.
sampler2D Input : register(s0);
float2 Size : register(c0);   // drawn area, DIP
float2 Inset : register(c1);  // panel inside that area, DIP
float2 Grab : register(c2);   // grabbed point, 0..1 of the panel
float2 Tilt : register(c3);   // carried: the side the plate leans away to, per DIP
float2 Flare : register(c4);  // dragged side: how much it opens towards the viewer (x: left/right side, y: top/bottom side)
float2 SideTo : register(c5); // 1 for the right / bottom side, 0 for the left / top one
float Sheen : register(c6);   // strength of the light band

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 panel = Size - 2 * Inset;
    float2 from = uv * Size - Inset - Grab * panel;                // DIP from the grabbed point
    float2 p = saturate((uv * Size - Inset) / panel);
    float2 side = lerp(1 - p, p, SideTo) * Flare;                  // grows towards the dragged side, zero at the opposite one
    float2 source = from / (1 - dot(Tilt, from)) / (1 + side.yx);  // a right side opens along y, a bottom side along x
    float4 color = tex2D(Input, ((source + Grab * panel) + Inset) / Size);
    float band = saturate(1 - abs(p.x + p.y * 0.35 - 0.67 - (Tilt.x + Tilt.y) * 5000) * 2.6);
    color.rgb += band * band * Sheen * color.a;
    return color;
}
