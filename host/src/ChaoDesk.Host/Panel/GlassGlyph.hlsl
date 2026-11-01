// Compile as ps_2_0: WPF supplies the derivatives without GPU ddx/ddy opcodes.
sampler2D glyph : register(s0);
float4 uvDdxDdy : register(c0); // ddx(u), ddx(v), ddy(u), ddy(v)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 source = tex2D(glyph, uv); // Premultiplied source; preserve its core.
    float2 dx = uvDdxDdy.xy;
    float2 dy = uvDdxDdy.zw;
    float2 axisX = dx * .9;
    float2 axisY = dy * .9;
    float2 diagonalA = (dx + dy) * .64;
    float2 diagonalB = (dx - dy) * .64;

    // A compact eight-direction dilation follows the actual glyph silhouette.
    // Unlike a blurred shadow it does not spread a faint veil around the word.
    float expanded = source.a;
    expanded = max(expanded, tex2D(glyph, uv + axisX).a);
    expanded = max(expanded, tex2D(glyph, uv - axisX).a);
    expanded = max(expanded, tex2D(glyph, uv + axisY).a);
    expanded = max(expanded, tex2D(glyph, uv - axisY).a);
    expanded = max(expanded, tex2D(glyph, uv + diagonalA).a);
    expanded = max(expanded, tex2D(glyph, uv - diagonalA).a);
    expanded = max(expanded, tex2D(glyph, uv + diagonalB).a);
    expanded = max(expanded, tex2D(glyph, uv - diagonalB).a);

    // Composite behind the original premultiplied glyph. At source alpha 1 the
    // added term is exactly zero, so an opaque white core remains white.
    float behindAlpha = expanded * .78 * (1 - source.a);
    return float4(source.rgb + float3(.045, .055, .07) * behindAlpha,
        source.a + behindAlpha);
}
