// Adapted from rdev/liquid-glass-react (MIT, MAX ROVENSKY), src/index.tsx.
// Original displacement textures and full license are embedded under Panel/Maps.
sampler2D scene : register(s0);
sampler2D displacement : register(s1);
sampler2D buttons : register(s2);
sampler2D bevelProfile : register(s3);
float4 geometry : register(c0); // panel DIP width/height/radius/enabled
float4 optics : register(c1); // displacement scale, unused (Gaussian prepass), soft, dark
float4 lightPointer : register(c2); // normalized pointer xy, pressed, slot+1
float4 finish : register(c18); // aberration, saturation factor, highlight, shader-map sign
float4 capture : register(c19); // panel origin/extent in the overscanned texture
float4 hover : register(c20); // normalized pointer xy, unused, hovered slot+1
float hoverAmount : register(c21);
float4 hoverBounds : register(c22); // single active action rect; negative width means pane
float4 edgeGradient : register(c23); // CSS gradient direction xy, interior stop positions zw
float4 sceneBounds : register(c24); // valid source texel centres in the cropped input
float sd(float2 p,float2 halfSize,float radius) {
    radius=min(radius,min(halfSize.x,halfSize.y));
    float2 q=abs(p)-halfSize+radius;
    return length(max(q,0))+min(max(q.x,q.y),0)-radius;
}
float3 overlay(float3 base,float light) {
    return lerp(2*base*light,1-2*(1-base)*(1-light),step(.5,base));
}
float2 edgeNormal(float2 p,float2 halfSize,float radius) {
    radius=min(radius,min(halfSize.x,halfSize.y));
    float2 q=abs(p)-halfSize+radius;
    float2 corner=max(q,0);
    return (max(q.x,q.y)>0?corner/max(length(corner),.001):(q.x>q.y?float2(1,0):float2(0,1)))*sign(p);
}
float4 main(float2 uv:TEXCOORD):COLOR {
    float2 panelUV=(uv-capture.xy)/capture.zw;
    float2 pos=panelUV*geometry.xy;
    float2 p=pos-geometry.xy*.5, halfSize=geometry.xy*.5;
    float radius=geometry.z, strength=optics.x;
    float outer=sd(p,halfSize,radius), shadow=0, hoverMask=1, hoverRim=0;
    float2 outerNormal=edgeNormal(p,halfSize,radius);
    float depth=max(0,-outer);
    // A fixed 256-entry CPU-baked ray profile supplies the curved bevel. The
    // actual rounded-rectangle normal supplies its direction, including corners.
    // No trigonometry, texture regeneration, or layout work during dragging.
    float thickness=clamp(radius*.75+2,3,18);
    float3 bevel=tex2D(bevelProfile,float2((saturate(depth/thickness)*255+.5)/256,.5)).rgb;
    if(hoverBounds.z>0) {
        float hoverEdge=sd(pos-hoverBounds.xy-hoverBounds.zw*.5,hoverBounds.zw*.5,12);
        hoverMask=1-smoothstep(-1,0,hoverEdge);
        hoverRim=exp(-abs(hoverEdge+1.3)/1.4)*hoverMask;
    }
    // The action field changes only with layout. One texture lookup replaces
    // fifteen rounded-distance and shadow calculations for every output pixel.
    float3 action=tex2D(buttons,saturate(panelUV)).rgb;
    float actionDistance=action.r*32-24; // GlassButtonMap: [-24,8] DIP
    float actionId=action.g*(255.0/16);
    float2 actionGradient=float2(ddx(actionDistance),ddy(actionDistance));
    float actionGradientLength=max(length(actionGradient),.001);
    // Filter the silhouette over one output pixel. Keep the actual distance and
    // normal independent of coverage, so the rounded shape does not shift.
    // The layout field has one texel per DIP. Preserve at least that footprint
    // at high DPI, where derivative quantization otherwise narrows the ramp.
    float actionAa=max(actionGradientLength*.5,.5);
    float coverage=(1-smoothstep(-actionAa,actionAa,actionDistance))*(actionId>.5?1:0);
    float pressed=lightPointer.z*(abs(actionId-lightPointer.w)<.25 && actionId>.5?1:0);
    shadow=action.b;
    // All buttons share the pane's continuous transmission field. Replacing the
    // field at a tile boundary made soft buttons look like displaced stickers.
    float2 mapUV=saturate(panelUV);
    float4 map=tex2D(displacement,mapUV);
    // SVG feDisplacementMap uses R for X and B for Y, not R/G.
    float2 field=map.rb-.5;
    float signScale=finish.w>.5?1:-1;
    // Blend within the existing displacement budget rather than adding another
    // offset: the reserved optical guard remains sufficient at every setting.
    // At the lip the ray bends inward; the selected field resumes in the core.
    field=lerp(field,-outerNormal*signScale*.5*bevel.r,bevel.g);
    float2 shift=field*strength/geometry.xy*capture.zw;
    // Same per-channel scales as upstream. Opaque JPEG/PNG maps have alpha=1:
    // upstream's alpha-based EDGE_MASK is actually 1 everywhere, not an SDF mask.
    float3 red=tex2D(scene,clamp(uv+shift*signScale,sceneBounds.xy,sceneBounds.zw)).rgb;
    float3 green=tex2D(scene,clamp(uv+shift*(signScale-finish.x*.05),sceneBounds.xy,sceneBounds.zw)).rgb;
    float3 blue=tex2D(scene,clamp(uv+shift*(signScale-finish.x*.1),sceneBounds.xy,sceneBounds.zw)).rgb;
    float3 weights=float3(.2126,.7152,.0722);
    // CSS saturation precedes the channel split, so saturate each source sample.
    float3 c=saturate(float3(lerp(dot(red,weights),red.r,finish.y),
        lerp(dot(green,weights),green.g,finish.y),lerp(dot(blue,weights),blue.b,finish.y)));
    float2 actionNormal=actionGradient/actionGradientLength;
    float2 normal=lerp(outerNormal,actionNormal,optics.z);
    float2 lamp=normalize(float2(-.7,-.8)+(hover.xy-.5)*1.1*hoverAmount);
    float facing=dot(normal,lamp);
    // Bright clear glass must not become a grey sheet. Glyph contrast is local
    // to the foreground, so light scenes can keep almost all their transmission.
    float bright=smoothstep(.5,1,dot(c,weights));
    c*=lerp(.94+.04*bright,.64,optics.w);
    // Upstream's two 1.5px masked gradients: white screen at opacity .2, then
    // white overlay. Their transparent endpoints and background-dependent blend
    // create changing edge light, rather than a constant white/dark outline.
    // Light-background counter-reflection sits behind the white lip. It is
    // confined inside the glass and varies with the surface normal; no outer
    // black backing, uniform stroke, or additional shadow render target.
    // Separate the front lip from the inward-facing bevel to suggest thickness,
    // then recover the clear core by 5 DIP. Cap negative reflection energy even
    // when Highlight=2: stronger highlights must not turn the bevel into a rim.
    float crease=(depth-2.2)/.85;
    float counterReflection=bright*exp(-crease*crease)
        *(.025+.085*saturate(dot(outerNormal,-lamp)))*saturate(finish.z);
    c*=1-counterReflection;
    float gradientLength=max(dot(abs(edgeGradient.xy),geometry.xy),1);
    float t=saturate(.5+dot(p,edgeGradient.xy)/gradientLength);
    float mouseX=abs(saturate(hover.x)-.5)*100;
    // CSS clamps each rgba alpha before interpolation, not the interpolated ramp.
    float2 stop1=saturate(float2(.12,.32)+mouseX*.008);
    float2 stop2=saturate(float2(.4,.6)+mouseX*.012);
    float2 alpha=stop1*saturate(t/edgeGradient.z);
    alpha=lerp(alpha,stop2,saturate((t-edgeGradient.z)/(edgeGradient.w-edgeGradient.z)));
    alpha*=1-saturate((t-edgeGradient.w)/(1-edgeGradient.w));
    float mask=1-smoothstep(.85,1.65,depth);
    float2 gradientAlpha=saturate(alpha)*finish.z;
    alpha=gradientAlpha*mask;
    c=1-(1-c)*(1-saturate(alpha.x*.2));
    c=lerp(c,overlay(c,1),saturate(alpha.y));
    // A restrained inset reflection carries the lip into the surface. It fades
    // within a few pixels and has no black backing or outer shadow surface.
    float inset=exp(-depth/1.5)*saturate(-outerNormal.y)*gradientAlpha.x*.12;
    c=1-(1-c)*(1-saturate(inset));
    // A weak return light behind the front lip gives the bevel depth without a
    // second outline. Opposite angular lobes vary continuously around corners.
    float alignment=dot(outerNormal,lamp);
    float glint=pow(saturate(abs(alignment)),8)*lerp(.35,1,step(0,alignment));
    c=1-(1-c)*(1-bevel.b*glint*.035*finish.z);
    c*=1-shadow*optics.z*.10;
    float inner=exp(-max(0,-actionDistance)/5)*coverage*optics.z;
    float face=facing*(1-2*pressed);
    c*=1-inner*saturate(-face)*.12;
    c=lerp(c,1,inner*saturate(face)*.15);
    float2 cursorDistance=(pos-hover.xy*geometry.xy)/float2(78,64);
    float spot=exp(-dot(cursorDistance,cursorDistance)*2);
    float reveal=hoverAmount*hoverMask*spot;
    // On an almost-white scene, white light alone has no visible headroom.
    // A faint local counter-reflection keeps the same cursor reveal readable.
    c*=1-reveal*bright*.03;
    c=1-(1-c)*(1-reveal*(.12+.22*hoverRim));
    c*=1-reveal*hoverRim*.07;
    // The reference uses a subtle overlay wash on press, never refracts text.
    c=lerp(c,overlay(c,.72),pressed*coverage*.18);
    // The WPF surface supplies the only anti-aliased silhouette. A second mask
    // here cut holes at the corners when the blur input bounds were inflated.
    return float4(saturate(c),1);
}
