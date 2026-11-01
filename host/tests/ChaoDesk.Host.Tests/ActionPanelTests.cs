using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Capture;
using ChaoDesk.Host.Clips;
using ChaoDesk.Host.Desktop;
using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Panel;
using ChaoDesk.Host.Settings;
using Xunit;

namespace ChaoDesk.Host.Tests;

public class ActionPanelTests
{
    [Fact]
    public void ActionsHaveStableUniqueIdsAndSearchInBothLanguages()
    {
        Assert.Equal(PanelAction.All.Length, PanelAction.All.Select(a => a.Id).Distinct().Count());
        Assert.Equal("ocr", Assert.Single(PanelAction.All, a => a.Matches("shizi")).Id);
        Assert.Contains(PanelAction.All, a => a.Id == "clipboard" && a.Matches("剪贴"));
        Assert.DoesNotContain(PanelAction.All, a => a.Matches("not-a-real-tool"));
    }

    [Fact]
    public void ClearGlassDoesNotEnableTheSystemAcrylicTint()
    {
        Assert.Equal(NativePanelBackdrop.None, NativePanelBackdrop.RequestedType("soft", false, 26300));
        Assert.Equal(NativePanelBackdrop.None, NativePanelBackdrop.RequestedType("liquid", false, 26300));
        Assert.Equal(NativePanelBackdrop.None, NativePanelBackdrop.RequestedType("frosted", false, 22621));
        Assert.Equal(NativePanelBackdrop.None, NativePanelBackdrop.RequestedType("solid", false, 26300));
        Assert.Equal(NativePanelBackdrop.None, NativePanelBackdrop.RequestedType("soft", true, 26300));
        Assert.Equal(NativePanelBackdrop.None, NativePanelBackdrop.RequestedType("soft", false, 19045));
    }

    [Fact]
    public async Task NativePanelRendersBothThemesAndClipboardWithoutDesktopCapture()
    {
        string? requested = Environment.GetEnvironmentVariable("CHAODESK_PANEL_SNAPSHOTS");
        if (string.IsNullOrEmpty(requested)) return; // Runs in its own test process, like UiSnapshots.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "ChaoDeskPanel-" + Guid.NewGuid().ToString("N"));
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // Use the shipping resources without running App.OnStartup and its user services.
                var document = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "host", "src", "ChaoDesk.Host", "App.xaml"));
                System.Xml.Linq.XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = document.Descendants(presentation + "ResourceDictionary").First();
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "settings", "clr-namespace:ChaoDesk.Host.Settings;assembly=ChaoDesk");
                foreach (var dictionary in resources.Descendants(presentation + "ResourceDictionary"))
                    if (dictionary.Attribute("Source") is { } source) source.Value = "/ChaoDesk;component/" + source.Value;
                app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
                void Pump(int ms)
                {
                    var wait = new System.Windows.Threading.DispatcherFrame();
                    var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                    clock.Tick += (_, _) => { clock.Stop(); wait.Continue = false; };
                    clock.Start(); System.Windows.Threading.Dispatcher.PushFrame(wait);
                }
                // End-to-end GDI exclusion: inspect pixels from our own solid test window, never save a desktop image.
                var underlay = new Window { Width = 420, Height = 420, Left = 70, Top = 70, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(0,160,224)), Topmost = true, ShowInTaskbar = false };
                var excluded = new Window { Width = 210, Height = 210, Left = 110, Top = 110, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.Magenta, Topmost = true, ShowInTaskbar = false };
                try
                {
                    underlay.Show(); excluded.Show(); Pump(120);
                    bool acceptUpload = false; int uploadAttempts = 0;
                    using var source = new GlassBackdrop(new System.Windows.Interop.WindowInteropHelper(excluded).Handle, excluded.Dispatcher,
                        _ => { uploadAttempts++; return acceptUpload; }, ex => throw ex);
                    using var sampler = new GlassBackdrop.Sampler();
                    using var first = sampler.Read(new System.Windows.Interop.WindowInteropHelper(excluded).Handle, true)!;
                    int center = (first.Height / 2 * first.Width + first.Width / 2) * 4;
                    Assert.InRange(first.Pixels[center], 204, 244); Assert.InRange(first.Pixels[center + 1], 140, 180); Assert.InRange(first.Pixels[center + 2], 0, 20);
                    underlay.Background = new SolidColorBrush(Color.FromRgb(224,96,0)); excluded.Left += 30; Pump(140);
                    using var changed = sampler.Read(new System.Windows.Interop.WindowInteropHelper(excluded).Handle, true)!;
                    center = (changed.Height / 2 * changed.Width + changed.Width / 2) * 4;
                    Assert.InRange(changed.Pixels[center + 2], 204, 244); Assert.NotEqual(first.X, changed.X);
                    using var nearEdge=sampler.Read(new System.Windows.Interop.WindowInteropHelper(excluded).Handle,true,160)!;
                    excluded.Left=340; Pump(50);
                    using var awayFromEdge=sampler.Read(new System.Windows.Interop.WindowInteropHelper(excluded).Handle,true,160)!;
                    Assert.Equal(nearEdge.Width,awayFromEdge.Width);
                    Assert.Equal(nearEdge.Height,awayFromEdge.Height);
                    // A static, owned backdrop isolates movement from actual content changes.
                    // One physical pixel per 30 ms used to upload a new full texture every step.
                    underlay.Left=0; underlay.Top=0; underlay.Width=1000; underlay.Height=900; Pump(120);
                    using var slowSampler = new GlassBackdrop.Sampler();
                    var excludedHandle = new System.Windows.Interop.WindowInteropHelper(excluded).Handle;
                    using var anchor = slowSampler.Read(excludedHandle, true, 160)!;
                    double dpi = VisualTreeHelper.GetDpi(excluded).DpiScaleX;
                    int redundantFrames=0;
                    var slowMotion=System.Diagnostics.Stopwatch.StartNew();
                    for (int pixel=1;pixel<=48;pixel++) {
                        excluded.Left=340+pixel/dpi; Pump(30);
                        using var sampled=slowSampler.Read(excludedHandle, margin:160);
                        if (sampled != null) {
                            redundantFrames++;
                            Assert.Equal(anchor.X,sampled.X); Assert.Equal(anchor.Y,sampled.Y);
                        }
                    }
                    Assert.InRange(redundantFrames,0,1); // At most one initial comparison buffer fill.
                    underlay.Background=Brushes.Lime; Pump(100);
                    using var liveChange=slowSampler.Read(excludedHandle,margin:160);
                    Assert.NotNull(liveChange); Assert.Equal(anchor.X,liveChange.X);
                    int green=(liveChange.Height/2*liveChange.Width+liveChange.Width/2)*4;
                    Assert.True(liveChange.Pixels[green+1]>230 && liveChange.Pixels[green]<20);
                    // Rejected uploads must be retried even when the source is now static.
                    source.Start(); Pump(180);
                    Assert.True(uploadAttempts>=3); Assert.Equal(0,source.Delivered);
                    acceptUpload=true; Pump(180); Assert.True(source.Delivered>0);
                    Directory.CreateDirectory(requested);
                    File.WriteAllText(Path.Combine(requested,"glass-slow-motion-metrics.txt"),
                        $"Owned static backdrop; 48 one-physical-pixel moves, 30 ms requested interval\nElapsed including live-change/retry checks: {slowMotion.ElapsedMilliseconds} ms\nRedundant texture frames: {redundantFrames} (old recenter-every-move path: 48)\nCapture origin unchanged; live background color change detected; rejected upload retried.\nNot a GPU-FPS or manual native-drag measurement.\n");
                }
                finally { excluded.Close(); underlay.Close(); }
                // Hardware WPF optical output: the same grid changes along the bevel when refraction is enabled.
                byte[] pattern = new byte[256*256*4];
                for (int y=0;y<256;y++) for(int x=0;x<256;x++)
                { int at=(y*256+x)*4; bool even=(x/6+y/6)%2==0; pattern[at]=(byte)(even?225:30); pattern[at+1]=(byte)(even?180:50); pattern[at+2]=(byte)(even?45:210); pattern[at+3]=255; }
                var texture=BitmapSource.Create(256,256,96,96,PixelFormats.Bgra32,null,pattern,256*4);
                var lens=new GlassLensEffect { Geometry=new System.Windows.Media.Media3D.Point4D(256,256,18,1), Optics=new System.Windows.Media.Media3D.Point4D(22,.4,0,0) };
                lens.SetSlot(0, new Rect(40,50,80,76));
                lens.CommitSlots(256,256);
                // Use the shipping full-resolution blur + cropped lens pipeline.
                var opticalScene=new GlassScene(lens);
                var opticalSurface=new System.Windows.Controls.Grid { Clip=new RectangleGeometry(new Rect(0,0,256,256),18,18) }; opticalSurface.Children.Add(opticalScene);
                var opticalWindow=new Window { Width=256,Height=256,Left=80,Top=80,Background=Brushes.Lime,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Topmost=true,ShowInTaskbar=false,Content=opticalSurface };
                double fixtureScale=1;
                void SetOpticalTexture(BitmapSource source) => opticalScene.SetFrame(source,
                    new Rect(-64*fixtureScale,-64*fixtureScale,384*fixtureScale,384*fixtureScale),
                    new Rect(0,0,256*fixtureScale,256*fixtureScale));
                try
                {
                    opticalWindow.Show(); Pump(200);
                    var fixtureDpi=VisualTreeHelper.GetDpi(opticalWindow); fixtureScale=fixtureDpi.DpiScaleX;
                    opticalScene.Configure(new Size(256,256),fixtureDpi,3,70,2);
                    SetOpticalTexture(texture); Pump(100);
                    using var sampler=new GlassBackdrop.Sampler();
                    int Channel(GlassBackdrop.Frame f,double x,double y) => f.Pixels[((int)(y*f.Height/256)*f.Width+(int)(x*f.Width/256))*4];
                    var edgeResults = new List<string>();
                    int previousCenter=-1;
                    // Reference gradients vary along the edge and blend with the
                    // scene; no uniform white stroke or dark surrounding band.
                    foreach(byte gray in new byte[] { 12,72,128,160,192,208,224,240,255 }) {
                        byte[] solid=Enumerable.Repeat(gray,256*256*4).ToArray();
                        SetOpticalTexture(BitmapSource.Create(256,256,96,96,PixelFormats.Bgr32,null,solid,256*4));
                        lens.Optics=new System.Windows.Media.Media3D.Point4D(0,0,0,0);
                        lens.Finish=new System.Windows.Media.Media3D.Point4D(0,1,1,0); Pump(160);
                        using var edge=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                        int centerTone=Channel(edge,128,128);
                        Assert.True(centerTone>previousCenter,"Bright-scene transmission must stay monotonic, without gray-level inversion.");
                        previousCenter=centerTone;
                        if(gray<=128) Assert.InRange(Math.Abs(centerTone-gray*.94),0,1);
                        int weak=Channel(edge,24,.8), strong=Channel(edge,232,.8);
                        Assert.True(strong-weak>=(gray>=240?1:4),$"The edge gradient flattened at background {gray}: {weak}/{strong}");
                        Assert.InRange(weak-centerTone,-2,80); Assert.InRange(strong-centerTone,0,100);
                        Assert.InRange(Math.Abs(Channel(edge,128,5)-centerTone),0,3);
                        edgeResults.Add($"Background {gray}: quiet edge={weak}, lit edge={strong}, center={centerTone}");
                        if(gray==255) {
                            int counter=Channel(edge,253.8,128);
                            Assert.InRange(centerTone,248,251); // The clear core must not become a grey sheet.
                            Assert.InRange(centerTone-counter,15,30);
                            Assert.InRange(Math.Abs(Channel(edge,251,128)-centerTone),0,3);
                            edgeResults.Add($"White background: inner counter-reflection={counter}, 5 DIP inside={Channel(edge,251,128)}");
                            lens.Finish=new System.Windows.Media.Media3D.Point4D(0,1,2,0); Pump(100);
                            using var maximumHighlight=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                            Assert.InRange(Math.Abs(Channel(maximumHighlight,253.8,128)-counter),0,2);
                            edgeResults.Add($"White background at Highlight=2: counter-reflection={Channel(maximumHighlight,253.8,128)}, center={Channel(maximumHighlight,128,128)}");
                            lens.Finish=new System.Windows.Media.Media3D.Point4D(0,1,1,0);
                        }
                        if(gray==72) {
                            lens.Hover=new System.Windows.Media.Media3D.Point4D(0,.5,0,0); Pump(100);
                            using var lightLeft=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                            lens.Hover=new System.Windows.Media.Media3D.Point4D(1,.5,0,0); Pump(100);
                            using var lightRight=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                            Assert.True(Math.Abs(Channel(lightLeft,.8,180)-Channel(lightRight,.8,180))>4);
                            lens.Hover=new System.Windows.Media.Media3D.Point4D(.5,.5,0,0);
                        }
                    }
                    // The same pane may straddle a dark app and a white document.
                    // Local response must not dim the dark half when the other is white.
                    byte[] split=new byte[256*256*4];
                    for(int y=0;y<256;y++) for(int x=0;x<256;x++) {
                        int at=(y*256+x)*4; byte gray=x<128?(byte)72:(byte)255;
                        split[at]=split[at+1]=split[at+2]=gray;
                    }
                    SetOpticalTexture(BitmapSource.Create(256,256,96,96,PixelFormats.Bgr32,null,split,256*4)); Pump(160);
                    using(var mixed=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!) {
                        Assert.InRange(Channel(mixed,64,160),67,69);
                        Assert.InRange(Channel(mixed,192,160),248,251);
                        edgeResults.Add($"Mixed background: dark half={Channel(mixed,64,160)}, white half={Channel(mixed,192,160)}");
                    }
                    // Hover reveal follows its point and remains inside the active action.
                    byte[] mid=Enumerable.Repeat((byte)72,256*256*4).ToArray();
                    SetOpticalTexture(BitmapSource.Create(256,256,96,96,PixelFormats.Bgr32,null,mid,256*4));
                    // A soft corner crossing a pixel must change continuously, not
                    // switch its full bevel lighting on at distance zero.
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(0,0,1,0);
                    lens.Finish=new System.Windows.Media.Media3D.Point4D(0,1,0,0);
                    byte[]? priorCorner=null;
                    int largestCornerStep=0;
                    var cornerSteps=new List<string>();
                    for(int phase=0;phase<=16;phase++) {
                        lens.SetSlot(0,new Rect(40+phase*.125,50,80,76));
                        lens.CommitSlots(256,256); Pump(50);
                        using var corner=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                        var samples=new List<byte>();
                        for(int y=(int)(50*corner.Height/256.0);y<(int)(68*corner.Height/256.0);y++)
                            for(int x=(int)(40*corner.Width/256.0);x<(int)(60*corner.Width/256.0);x++)
                                samples.Add(corner.Pixels[(y*corner.Width+x)*4]);
                        var currentCorner=samples.ToArray();
                        if(priorCorner!=null) {
                            int phaseMax=0, peak=0;
                            for(int i=0;i<currentCorner.Length;i++) {
                                int delta=Math.Abs(currentCorner[i]-priorCorner[i]);
                                if(delta>phaseMax) { phaseMax=delta; peak=i; }
                            }
                            largestCornerStep=Math.Max(largestCornerStep,phaseMax);
                            cornerSteps.Add($"Phase {phase}: max={phaseMax}, pixel={peak}, {priorCorner[peak]}->{currentCorner[peak]}");
                        }
                        priorCorner=currentCorner;
                    }
                    File.WriteAllLines(Path.Combine(requested,"glass-soft-corner-metrics.txt"),cornerSteps);
                    Assert.InRange(largestCornerStep,1,10);
                    edgeResults.Add($"Soft corner: 17 positions at 0.125 DIP increments, largest settled channel step={largestCornerStep}/255");
                    lens.SetSlot(0,new Rect(40,50,80,76));
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(0,0,0,0);
                    lens.Finish=new System.Windows.Media.Media3D.Point4D(0,1,1,0);
                    lens.SetSlot(1,new Rect(140,50,80,76));
                    lens.CommitSlots(256,256);
                    lens.Hover=new System.Windows.Media.Media3D.Point4D(70.0/256,85.0/256,0,1);
                    lens.HoverBounds=new System.Windows.Media.Media3D.Point4D(40,50,80,76);
                    foreach(byte gray in new byte[] {72,255}) {
                        byte[] hoverScene=Enumerable.Repeat(gray,256*256*4).ToArray();
                        SetOpticalTexture(BitmapSource.Create(256,256,96,96,PixelFormats.Bgr32,null,hoverScene,256*4));
                        lens.HoverAmount=0; Pump(120);
                        using var beforeHover=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                        lens.HoverAmount=1; Pump(120);
                        using var afterHover=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                        int reveal=Channel(afterHover,70,85)-Channel(beforeHover,70,85);
                        Assert.True(gray==72 ? reveal>=12 : reveal<=-3);
                        Assert.InRange(Math.Abs(Channel(afterHover,170,85)-Channel(beforeHover,170,85)),0,2);
                        edgeResults.Add($"Hover on {gray}: active brightness change={reveal}/255, neighbouring action unchanged within 2/255");
                    }
                    lens.HoverAmount=0; SetOpticalTexture(texture);
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(22,.4,0,0);
                    lens.Finish=new System.Windows.Media.Media3D.Point4D(2,1.4,1,0); Pump(160);
                    Directory.CreateDirectory(requested);
                    File.WriteAllLines(Path.Combine(requested,"glass-edge-hover-metrics.txt"),edgeResults);
                    // Compare actual GPU output at fixed screen positions while
                    // moving one physical pixel. No desktop image is persisted.
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(0,0,0,0);
                    lens.Finish=new System.Windows.Media.Media3D.Point4D(0,1,0,0); Pump(100);
                    using(var stationary=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!) {
                        double displayScale=VisualTreeHelper.GetDpi(opticalWindow).DpiScaleX;
                        int maxError=0;
                        for(int step=1;step<=24;step++) {
                            opticalWindow.Left=80+step/displayScale;
                            opticalScene.UpdateMapping(new Rect(step,0,256*displayScale,256*displayScale));
                            Pump(30);
                            using var shifted=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                            for(int y=80;y<160;y+=3) for(int x=80;x<160;x+=3) {
                                int current=(y*shifted.Width+x)*4, reference=(y*stationary.Width+x+step)*4;
                                for(int channel=0;channel<3;channel++) maxError=Math.Max(maxError,Math.Abs(shifted.Pixels[current+channel]-stationary.Pixels[reference+channel]));
                            }
                        }
                        Assert.InRange(maxError,0,3);
                        File.WriteAllText(Path.Combine(requested,"glass-pixel-motion-metrics.txt"),$"24 one-physical-pixel GPU-output comparisons at fixed screen positions\nMaximum channel error: {maxError}/255\nSettled frames at requested 30 ms intervals, not transient DWM/native-drag latency.\n");
                    }
                    opticalWindow.Left=80;
                    // Rebase a textured scene through the shipping upload path.
                    // Different capture origins must render the same screen pixels.
                    var stagedTexture=new GlassTexture();
                    double rebaseDpi=VisualTreeHelper.GetDpi(opticalWindow).DpiScaleX;
                    int textureSize=(int)Math.Round(384*rebaseDpi);
                    void Rebase(int origin) {
                        byte[] data=System.Buffers.ArrayPool<byte>.Shared.Rent(textureSize*textureSize*4);
                        using var upload=new GlassBackdrop.Frame(data,textureSize,textureSize,origin,0);
                        for(int y=0;y<textureSize;y++) for(int x=0;x<textureSize;x++) {
                            int at=(y*textureSize+x)*4; bool even=(((x+origin+256)/7+y/9)&1)==0;
                            data[at]=(byte)(even?40:210); data[at+1]=(byte)(even?190:30); data[at+2]=(byte)(even?65:220); data[at+3]=255;
                        }
                        Assert.True(stagedTexture.TryUpload(upload));
                        opticalScene.SetFrame(stagedTexture.Bitmap!,
                            new Rect(-64*rebaseDpi+origin,-64*rebaseDpi,textureSize,textureSize),
                            new Rect(0,0,256*rebaseDpi,256*rebaseDpi));
                    }
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(22,0,0,0);
                    Rebase(0); Pump(100);
                    using(var baseline=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!) {
                        int error=0;
                        foreach(int origin in new[] {-48,-16,16,48,65,0}) {
                            Rebase(origin); Pump(60);
                            using var actual=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                            for(int y=60;y<220;y+=3) for(int x=60;x<220;x+=3) {
                                int at=(y*actual.Width+x)*4;
                                for(int channel=0;channel<3;channel++) error=Math.Max(error,Math.Abs(actual.Pixels[at+channel]-baseline.Pixels[at+channel]));
                            }
                        }
                        Assert.InRange(error,0,3);
                        File.WriteAllText(Path.Combine(requested,"glass-rebase-metrics.txt"),$"Six capture-origin switches through GlassTexture, refraction enabled\nMaximum settled output channel error: {error}/255\nPrevious-origin bitmap immutability is checked separately; this is not manual native-drag validation.\n");
                    }
                    SetOpticalTexture(texture);
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(22,.4,0,0);
                    lens.Finish=new System.Windows.Media.Media3D.Point4D(2,1.4,1,0); Pump(120);
                    using var bent=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                    foreach (var point in new[] { new Point(2,2),new Point(253,2),new Point(2,253),new Point(253,253) }) {
                        int at=((int)(point.Y*bent.Height/256)*bent.Width+(int)(point.X*bent.Width/256))*4;
                        Assert.True(bent.Pixels[at+1]>230 && bent.Pixels[at]<25 && bent.Pixels[at+2]<25,"Only the single outer clip should cut away the corner");
                    }
                    foreach (var point in new[] { new Point(18,3),new Point(237,3),new Point(18,252),new Point(237,252) }) {
                        int at=((int)(point.Y*bent.Height/256)*bent.Width+(int)(point.X*bent.Width/256))*4;
                        Assert.True(bent.Pixels[at]>25 || bent.Pixels[at+2]>25,"The glass must fill the inside of all four rounded corners");
                    }
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(0,.4,0,0); Pump(200);
                    using var flat=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                    int different=0, tileDifferent=0;
                    for(int n=0;n<bent.Width*bent.Height*4;n+=4)
                        if(Math.Abs(bent.Pixels[n]-flat.Pixels[n])+Math.Abs(bent.Pixels[n+1]-flat.Pixels[n+1])+Math.Abs(bent.Pixels[n+2]-flat.Pixels[n+2])>30)
                        {
                            different++;
                            double x=(n/4%bent.Width)*256.0/bent.Width, y=(n/4/bent.Width)*256.0/bent.Height;
                            if(x>40 && x<120 && y>50 && y<126) tileDifferent++;
                        }
                    Assert.True(tileDifferent>100, $"Expected continuous pane displacement under actions, measured {tileDifferent} pixels.");
                    Assert.True(different>1000,$"Expected visible optical displacement, measured {different} pixels.");
                    lens.SetSlot(1, new Rect(140,50,80,76));
                    lens.CommitSlots(256,256);
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(12,.8,1,0); Pump(180);
                    using var soft=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                    lens.Pointer=new System.Windows.Media.Media3D.Point4D(.3,.2,1,1); Pump(180);
                    using var pressed=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                    int activeChanges=0, otherChanges=0, materialChanges=0;
                    for(int y=0;y<soft.Height;y++) for(int x=0;x<soft.Width;x++)
                    {
                        int at=(y*soft.Width+x)*4;
                        int delta=Math.Abs(soft.Pixels[at]-pressed.Pixels[at])+Math.Abs(soft.Pixels[at+1]-pressed.Pixels[at+1])+Math.Abs(soft.Pixels[at+2]-pressed.Pixels[at+2]);
                        double dx=x*256.0/soft.Width, dy=y*256.0/soft.Height;
                        if(delta>12 && dx>40 && dx<120 && dy>50 && dy<126) activeChanges++;
                        if(delta>12 && dx>140 && dx<220 && dy>50 && dy<126) otherChanges++;
                        if(Math.Abs(soft.Pixels[at]-bent.Pixels[at])+Math.Abs(soft.Pixels[at+1]-bent.Pixels[at+1])+Math.Abs(soft.Pixels[at+2]-bent.Pixels[at+2])>20) materialChanges++;
                    }
                    Assert.True(activeChanges>100, $"Soft press must change its own lens: {activeChanges}");
                    Assert.Equal(0,otherChanges);
                    Assert.True(materialChanges>1000, $"Materials must differ: {materialChanges}");
                    lens.Pointer=new System.Windows.Media.Media3D.Point4D(.3,.2,0,0);
                    lens.Optics=new System.Windows.Media.Media3D.Point4D(70,0,0,0);
                    byte[]? lastMode = null;
                    foreach (string variant in new[] { "standard", "polar", "prominent", "shader" }) {
                        lens.SetMode(variant,256,256);
                        lens.Finish=new System.Windows.Media.Media3D.Point4D(2,1.4,1,variant=="shader"?1:0); Pump(140);
                        using var result=sampler.Read(new System.Windows.Interop.WindowInteropHelper(opticalWindow).Handle,true)!;
                        var actual=result.Pixels.AsSpan(0,result.Width*result.Height*4).ToArray();
                        if(lastMode != null) Assert.False(actual.SequenceEqual(lastMode), "Changing the refraction map must affect rendered pixels");
                        lastMode=actual;
                    }
                }
                finally { opticalWindow.Close(); }
                using var messages = new MessageWindow();
                var history = new ClipboardHistory(folder, 100);
                history.Add(new ClipItem { Kind = ClipKind.Text, Text = "把复杂的事情，做得简单一点。", Source = "Microsoft Edge", Hash = "panel-snapshot-one" });
                history.Add(new ClipItem { Kind = ClipKind.Text, Text = "https://github.com/TidalJ/chao-desk", Source = "Microsoft Edge", Hash = "panel-snapshot-two" });
                using var monitor = new ClipboardMonitor(messages, history);
                string material = "soft";
                var actions = new PanelLayout();
                var initialActionIds = actions.Items.Select(item => item.Id).ToHashSet();
                int settingsOpened=0;
                var panel = new ActionPanelWindow(history, monitor, () => true, _ => { }, (_, _) => throw new NotSupportedException(), () => material, actions,
                    openSettings:()=>settingsOpened++);
                object PanelField(string name) => typeof(ActionPanelWindow).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(panel)!;
                var panelLens = (GlassLensEffect)PanelField("_lens");
                new System.Windows.Interop.WindowInteropHelper(panel).EnsureHandle();
                Loc.Apply("zh");
                var output = requested; Directory.CreateDirectory(output);
                foreach (var theme in new[] { "light", "dark" })
                {
                    ThemeManager.Apply(theme);
                    UiSnapshots.Render(panel, Path.Combine(output, "panel-" + theme + ".png"));
                    // Layout PNG uses a synthetic matte; hardware corner coverage is checked above.
                    var png = BitmapFrame.Create(new Uri(Path.Combine(output, "panel-" + theme + ".png")), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var alphaPixels = new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0);
                    byte[] pixel = new byte[4]; alphaPixels.CopyPixels(new Int32Rect(10, alphaPixels.PixelHeight / 2, 1, 1), pixel, 4, 0);
                    Assert.Equal((byte)255,pixel[3]);
                    Assert.Equal(Colors.White, ((SolidColorBrush)panel.Foreground).Color);
                    Assert.DoesNotContain(Descendants((DependencyObject)panel.Content), e => e is System.Windows.Controls.TextBox);
                    var homeButtons=Descendants((DependencyObject)panel.Content).OfType<System.Windows.Controls.Button>().Where(b => b.MinHeight == 76).ToArray();
                    Assert.Equal(5, homeButtons.Length);
                    Assert.Equal(new[] { "撤销关闭", "剪贴板", "截图", "识字", "贴图" },homeButtons.Select(System.Windows.Automation.AutomationProperties.GetName));
                    Assert.All(homeButtons,button=>Assert.Null(button.ContextMenu));
                    Assert.Single(Descendants((DependencyObject)panel.Content).OfType<System.Windows.Controls.Button>(),
                        button=>System.Windows.Automation.AutomationProperties.GetAutomationId(button)=="PanelSettings");
                    var panelSurface=(FrameworkElement)PanelField("_surface");
                    Assert.True(ActionPanelWindow.IsPanelDragSurface((DependencyObject)PanelField("_heading"),panelSurface));
                    Assert.True(ActionPanelWindow.IsPanelDragSurface((DependencyObject)homeButtons[0].Parent,panelSurface));
                    Assert.False(ActionPanelWindow.IsPanelDragSurface(Descendants(homeButtons[0]).OfType<System.Windows.Controls.TextBlock>().Single(),panelSurface));
                    Assert.False(ActionPanelWindow.IsPanelDragSurface(homeButtons[^1],panelSurface));
                    var action = Descendants((DependencyObject)panel.Content).OfType<System.Windows.Controls.Button>().First(b => b.MinHeight == 76);
                    var point = action.TranslatePoint(new Point(20,20), (UIElement)PanelField("_surface"));
                    typeof(ActionPanelWindow).GetMethod("UpdateGlassPointer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(panel,[point]);
                    Assert.Equal(1,panelLens.Hover.W);
                    Assert.True(panelLens.HoverBounds.Z>0);
                    typeof(ActionPanelWindow).GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(panel, ["translate"]);
                    Assert.Equal(0,panelLens.HoverAmount);
                    Assert.Equal(0,panelLens.Hover.W);
                    Assert.True(panelLens.HoverBounds.Z<0);
                    UiSnapshots.Render(panel, Path.Combine(output, "panel-translation-" + theme + ".png"));
                    Pump(30);
                    var emptySlots=(BitmapSource)((ImageBrush)panelLens.ButtonMap).ImageSource;
                    byte[] slotPixel=new byte[4];
                    emptySlots.CopyPixels(new Int32Rect((int)point.X,(int)point.Y,1,1),slotPixel,4,0);
                    Assert.Equal(0,slotPixel[1]); // No old soft button remains behind the editor.
                    typeof(ActionPanelWindow).GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(panel, ["home"]);
                }
                ThemeManager.Apply("light");
                Loc.Apply("en");
                UiSnapshots.Render(panel,Path.Combine(output,"panel-home-en.png"));
                foreach(var tile in Descendants((DependencyObject)panel.Content).OfType<System.Windows.Controls.Button>().Where(b=>b.MinHeight==76)) {
                    var label=Descendants(tile).OfType<System.Windows.Controls.TextBlock>().Single();
                    var text=new FormattedText(label.Text,System.Globalization.CultureInfo.GetCultureInfo("en"),FlowDirection.LeftToRight,
                        new Typeface(label.FontFamily,label.FontStyle,label.FontWeight,label.FontStretch),label.FontSize,Brushes.White,VisualTreeHelper.GetDpi(label).PixelsPerDip);
                    Assert.True(text.WidthIncludingTrailingWhitespace<=label.ActualWidth+.5,$"The compact label '{label.Text}' must fit its tile.");
                }
                Loc.Apply("zh");
                GlassLegibilityChecks.Run(panel, output);
                void Navigate(string page) => typeof(ActionPanelWindow).GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(panel, [page]);
                Assert.Equal(5,actions.Items.Count);
                actions.SaveShortcut(null, "Calendar", "https://example.com/calendar");
                actions.SaveShortcut(null, "A long shortcut name", "https://example.com/long");
                foreach(var extra in PanelAction.All) { if(actions.IsFull) break; actions.AddAction(extra.Id); }
                Navigate("home"); UiSnapshots.Render(panel, Path.Combine(output, "panel-fifteen.png"));
                var fullGrid = Descendants((DependencyObject)panel.Content).OfType<System.Windows.Controls.Primitives.UniformGrid>().Single();
                Assert.Equal(3, fullGrid.Columns); Assert.Equal(15, fullGrid.Children.Count);
                Assert.True(panel.Height > panel.Width);
                Assert.True(fullGrid.RenderSize.Height <= 410.1);
                Assert.DoesNotContain(fullGrid.Children.OfType<System.Windows.Controls.Button>(),b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="PanelAddAction");
                foreach (var item in actions.Items.Where(x => !initialActionIds.Contains(x.Id)).ToArray()) actions.Remove(item.Id);
                Navigate("home");
                var settingsHost=UiSnapshots.FakeHost.Create();
                var settingsWindow = new SettingsWindow(settingsHost) { Width = 960, Height = 640 };
                settingsWindow.ShowPage("panel");
                PanelSettingsEditorChecks.Run(settingsWindow,settingsHost,output);
                settingsWindow.Height=900;
                ((System.Windows.Controls.TabControl)settingsWindow.FindName("PanelSettingsTabs")).SelectedIndex=1;
                UiSnapshots.Render(settingsWindow, Path.Combine(output, "panel-options.png"));
                var adjust=Descendants((DependencyObject)settingsWindow.Content).OfType<System.Windows.Controls.Expander>().Single(e => e.Header?.ToString()==Loc.Pick("Adjust glass appearance", "调节玻璃效果"));
                adjust.IsExpanded=true;
                UiSnapshots.Render(settingsWindow,Path.Combine(output,"panel-glass-adjustments.png"));
                var sliders=Descendants(adjust).OfType<System.Windows.Controls.Slider>().ToArray();
                Assert.Equal(6,sliders.Length);
                sliders[0].Value=126; Assert.Equal(126,settingsHost.PanelGlass.Displacement);
                settingsWindow.Close();
                PanelInteractionChecks.Run(panel,()=>settingsOpened,Pump,output);
                Assert.False(panel.AllowsTransparency); // Native DWM window, never the layered screenshot route.
                if (Environment.OSVersion.Version.Build >= 22621 && !SystemParameters.HighContrast)
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(panel).EnsureHandle();
                    Assert.True(NativePanelBackdrop.Apply(panel, "liquid", ThemeManager.IsDark));
                    Assert.Equal(0, NativeMethods.DwmGetWindowAttribute(handle, NativePanelBackdrop.Attribute, out int nativeMaterial, sizeof(int)));
                    Assert.Equal(NativePanelBackdrop.None, nativeMaterial);
                }
                // Regression: each offered material must stay on one renderer after Show, remain
                // capturable, and keep its controls instead of rebuilding after a desktop frame arrives.
                foreach (string mode in new[] { "liquid", "soft", "smoked" })
                {
                    material = mode; panel.RefreshMaterial(); panel.Toggle(); panel.UpdateLayout();
                    var controls = Descendants((DependencyObject)panel.Content).OfType<System.Windows.Controls.Button>().Where(b => b.MinHeight == 76).ToArray();
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
                    Assert.True(GetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(panel).Handle, out uint affinity));
                    Assert.Equal(0x11u, affinity);
                    Assert.True(panel.GlassFrames >= 2, "The refractive surface must receive live frames.");
                    Assert.Equal(0, NativeMethods.DwmGetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(panel).Handle, NativePanelBackdrop.Attribute, out int appliedType, sizeof(int)));
                    Assert.Equal(NativePanelBackdrop.None, appliedType);
                    Assert.Equal(controls, Descendants((DependencyObject)panel.Content).OfType<System.Windows.Controls.Button>().Where(b => b.MinHeight == 76).ToArray());
                    if (mode == "liquid") {
                        panel.ShowMaterialPreview();
                        var session=PanelField("_glass");
                        var liveScene=(GlassScene)PanelField("_glassScene");
                        foreach(string next in new[] { "soft", "smoked", "liquid" }) {
                            var imageBefore=liveScene.Source;
                            material=next; panel.RefreshMaterial();
                            Assert.Same(session,PanelField("_glass"));
                            Assert.Same(imageBefore,liveScene.Source);
                            Assert.Equal(controls,Descendants((DependencyObject)panel.Content).OfType<System.Windows.Controls.Button>().Where(b=>b.MinHeight==76).ToArray());
                            Assert.Equal(next=="soft"?1:0,panelLens.Optics.Z);
                            Assert.Equal(next=="smoked"?1:0,panelLens.Optics.W);
                            Pump(30);
                        }
                        long beforeMove=panel.GlassUploads;
                        var physicalDpi=VisualTreeHelper.GetDpi(panel);
                        var lensDomain=panel.GlassLensBounds;
                        double lensPixels=lensDomain.Width*physicalDpi.DpiScaleX*lensDomain.Height*physicalDpi.DpiScaleY;
                        double capturePixels=panel.CapturedBounds.Width*panel.CapturedBounds.Height;
                        Assert.True(lensPixels<capturePixels,"Movement overscan must not enlarge the lens pass.");
                        File.WriteAllText(Path.Combine(output,"glass-lens-domain-metrics.txt"),
                            $"Capture: {panel.CapturedBounds.Width} x {panel.CapturedBounds.Height} physical pixels\nLens domain: {lensDomain.Width*physicalDpi.DpiScaleX:0.##} x {lensDomain.Height*physicalDpi.DpiScaleY:0.##}\nLens domain area reduced by {1-lensPixels/capturePixels:P1}. This is geometry, not measured GPU time.\nSource pixel density preserved; full-capture Gaussian prepass remains.\n");
                        var fixedLensMapping=panel.CaptureMapping;
                        panel.BeginGlassMotion();
                        var motion=System.Diagnostics.Stopwatch.StartNew();
                        for(int step=0;step<60;step++) {
                            panel.Left=220+Math.Sin(step*.15)*100; panel.Top=170+Math.Cos(step*.15)*60;
                            Pump(12);
                            var origin=panel.PaneScreenBounds.TopLeft;
                            var captured=panel.CapturedBounds; var mapping=panel.RawCaptureMapping;
                            Assert.Equal(fixedLensMapping,panel.CaptureMapping);
                            Assert.Equal(lensDomain,panel.GlassLensBounds);
                            Assert.InRange(Math.Abs(captured.Left+mapping.X*captured.Width-origin.X),0,1.1);
                            Assert.InRange(Math.Abs(captured.Top+mapping.Y*captured.Height-origin.Y),0,1.1);
                        }
                        panel.EndGlassMotion();
                        long delivered=panel.GlassUploads-beforeMove;
                        Assert.True(delivered>=1,$"Motion must recapture when leaving the guard: {delivered} uploads in {motion.ElapsedMilliseconds} ms");
                        File.WriteAllText(Path.Combine(output,"glass-motion-metrics.txt"),$"Programmatic WPF position updates: 60\nElapsed: {motion.ElapsedMilliseconds} ms\nAccepted texture uploads during motion: {delivered}\nScreen anchoring checked at all 60 positions. This is not a manual native-drag or GPU-FPS measurement.\n");
                    }
                    panel.Hide();
                }
                panel.Close(); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ExceptionDispatchInfo.Capture(ex).SourceException); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "host", "ChaoDesk.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
