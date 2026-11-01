using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ChaoDesk.Host.Panel;

/// <summary>A narrow alpha-derived underlay for white glyphs on bright glass.</summary>
internal sealed class GlassGlyphEffect : ShaderEffect
{
    private static readonly PixelShader Shader = CreateShader();
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
        "Input", typeof(GlassGlyphEffect), 0, SamplingMode.Bilinear);

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    private static PixelShader CreateShader()
    {
        var shader = new PixelShader { UriSource = new Uri("/ChaoDesk;component/Panel/GlassGlyph.ps", UriKind.Relative) };
        shader.Freeze();
        return shader;
    }

    public GlassGlyphEffect()
    {
        // WPF supplies screen-space UV derivatives even to the ps_2_0 software
        // path, keeping the stroke close to one output pixel at any glyph size.
        DdxUvDdyUvRegisterIndex = 0;
        PaddingLeft = PaddingRight = PaddingTop = PaddingBottom = 2;
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
    }
}
