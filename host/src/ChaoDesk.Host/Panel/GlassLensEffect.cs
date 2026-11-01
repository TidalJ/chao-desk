using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;

namespace ChaoDesk.Host.Panel;
internal sealed class GlassLensEffect : ShaderEffect
{
    private static readonly PixelShader Shader = CreateShader();
    private static PixelShader CreateShader()
    {
        var shader = new PixelShader { UriSource = new Uri("/ChaoDesk;component/Panel/GlassLens.ps", UriKind.Relative) };
        shader.Freeze();
        return shader;
    }
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassLensEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty MapProperty = RegisterPixelShaderSamplerProperty("Map", typeof(GlassLensEffect), 1, SamplingMode.Bilinear);
    public static readonly DependencyProperty ButtonMapProperty = RegisterPixelShaderSamplerProperty("ButtonMap", typeof(GlassLensEffect), 2, SamplingMode.Bilinear);
    public static readonly DependencyProperty BevelProfileProperty = RegisterPixelShaderSamplerProperty("BevelProfile", typeof(GlassLensEffect), 3, SamplingMode.Bilinear);
    public static readonly DependencyProperty FinishProperty = Constant("Finish", 18, new Point4D(2, 1.4, 1, 0));
    public static readonly DependencyProperty CaptureProperty = Constant("Capture", 19, new Point4D(0, 0, 1, 1));
    public static readonly DependencyProperty GeometryProperty = Constant("Geometry", 0, new Point4D(324, 440, 8, 1));
    public static readonly DependencyProperty OpticsProperty = Constant("Optics", 1, new Point4D(20, .45, 0, 0));
    public static readonly DependencyProperty PointerProperty = Constant("Pointer", 2, new Point4D(.3, .2, 0, 0));
    public static readonly DependencyProperty HoverProperty = Constant("Hover", 20, new Point4D(.5, .5, 0, 0));
    public static readonly DependencyProperty HoverAmountProperty = DependencyProperty.Register("HoverAmount", typeof(double), typeof(GlassLensEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(21)));
    public static readonly DependencyProperty HoverBoundsProperty = Constant("HoverBounds", 22, new Point4D(0,0,-1,-1));
    public static readonly DependencyProperty EdgeGradientProperty = Constant("EdgeGradient", 23, new Point4D(.7071067812,.7071067812,.33,.66));
    public static readonly DependencyProperty SceneBoundsProperty = Constant("SceneBounds", 24, new Point4D(0,0,1,1));
    private readonly Rect?[] _slots = new Rect?[15];
    private bool _slotsDirty = true;
    private int _slotWidth, _slotHeight;
    private static DependencyProperty Constant(string name, int register, Point4D initial) => DependencyProperty.Register(name, typeof(Point4D), typeof(GlassLensEffect), new UIPropertyMetadata(initial, PixelShaderConstantCallback(register)));
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point4D Geometry { get => (Point4D)GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    public Point4D Optics { get => (Point4D)GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }
    public Point4D Pointer { get => (Point4D)GetValue(PointerProperty); set => SetValue(PointerProperty, value); }
    public Point4D Hover
    {
        get => (Point4D)GetValue(HoverProperty);
        set
        {
            SetValue(HoverProperty,value);
            // Upstream CSS: angle 135 + mouseOffset.x * 1.2 degrees. Compute
            // trigonometry once per pointer update, not once per screen pixel.
            double x=(Math.Clamp(value.X,0,1)-.5)*100, y=(Math.Clamp(value.Y,0,1)-.5)*100;
            double angle=(135+x*1.2)*Math.PI/180;
            SetValue(EdgeGradientProperty,new Point4D(Math.Sin(angle),-Math.Cos(angle),Math.Max(10,33+y*.3)/100,Math.Min(90,66+y*.4)/100));
        }
    }
    public double HoverAmount { get => (double)GetValue(HoverAmountProperty); set => SetValue(HoverAmountProperty, value); }
    public Point4D HoverBounds { get => (Point4D)GetValue(HoverBoundsProperty); set => SetValue(HoverBoundsProperty, value); }
    public Point4D Finish { get => (Point4D)GetValue(FinishProperty); set => SetValue(FinishProperty, value); }
    public Point4D Capture { get => (Point4D)GetValue(CaptureProperty); set => SetValue(CaptureProperty, value); }
    public Point4D SceneBounds { get => (Point4D)GetValue(SceneBoundsProperty); set => SetValue(SceneBoundsProperty, value); }
    public Brush Map { get => (Brush)GetValue(MapProperty); set => SetValue(MapProperty, value); }
    public Brush ButtonMap { get => (Brush)GetValue(ButtonMapProperty); set => SetValue(ButtonMapProperty, value); }
    public Brush BevelProfile { get => (Brush)GetValue(BevelProfileProperty); set => SetValue(BevelProfileProperty, value); }
    private string? _mode;
    private int _mapWidth, _mapHeight;
    public void SetMode(string mode, int width = 324, int height = 440)
    {
        width = Math.Clamp(width, 1, 1024); height = Math.Clamp(height, 1, 2048);
        if (_mode == mode && (mode != "shader" || (_mapWidth == width && _mapHeight == height))) return;
        string name = mode switch { "polar" => "polarDisplacementMap.jpg", "prominent" => "prominentDisplacementMap.png", _ => "displacementMap.jpg" };
        BitmapSource bitmap = mode == "shader" ? GlassShaderMap.Create(width,height) : new BitmapImage(new Uri("pack://application:,,,/ChaoDesk;component/Panel/Maps/"+name));
        bitmap.Freeze(); var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill }; brush.Freeze();
        Map = brush; _mode = mode; _mapWidth = width; _mapHeight = height;
    }
    public void SetSlot(int index, Rect? rect)
    {
        if (_slots[index] == rect) return;
        _slots[index] = rect; _slotsDirty = true;
    }
    public void CommitSlots(int width,int height)
    {
        width=Math.Clamp(width,1,1024); height=Math.Clamp(height,1,2048);
        if (!_slotsDirty && width==_slotWidth && height==_slotHeight) return;
        var brush=new ImageBrush(GlassButtonMap.Create(width,height,_slots)) { Stretch=Stretch.Fill };
        brush.Freeze(); ButtonMap=brush;
        _slotWidth=width; _slotHeight=height; _slotsDirty=false;
    }
    public GlassLensEffect()
    {
        PixelShader = Shader; UpdateShaderValue(InputProperty); UpdateShaderValue(GeometryProperty); UpdateShaderValue(OpticsProperty); UpdateShaderValue(PointerProperty);
        UpdateShaderValue(MapProperty); UpdateShaderValue(FinishProperty); UpdateShaderValue(CaptureProperty); SetMode("standard");
        UpdateShaderValue(HoverProperty); UpdateShaderValue(HoverAmountProperty);
        UpdateShaderValue(HoverBoundsProperty);
        UpdateShaderValue(EdgeGradientProperty);
        UpdateShaderValue(SceneBoundsProperty);
        UpdateShaderValue(ButtonMapProperty); CommitSlots(1,1);
        UpdateShaderValue(BevelProfileProperty); BevelProfile = GlassBevelProfile.Brush;
    }
}
