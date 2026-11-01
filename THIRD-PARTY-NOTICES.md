# Third-party notices

Chao Desk's own code, wallpaper engine and pictures are original to this project (the pond photos and fish
sheets were generated with AI tools for it). The app uses, and its installer ships, these third-party components:

| Component | Use | Licence |
|---|---|---|
| [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) | Notification-area icon | MIT |
| [Serilog](https://github.com/serilog/serilog), [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | Logging | Apache-2.0 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) (Microsoft.ML.OnnxRuntime) | Text recognition | MIT |
| [Microsoft Edge WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Wallpaper rendering | Microsoft WebView2 SDK licence (redistributable) |
| [PP-OCRv5](https://github.com/PaddlePaddle/PaddleOCR) models, as ONNX from [RapidOCR](https://github.com/RapidAI/RapidOCR) | Text recognition models (not in git; downloaded at build time) | Apache-2.0 |
| [Unicode Unihan database](https://www.unicode.org/charts/unihan.html) | Pinyin table (`Launcher/pinyin.txt`, generated from it) | Unicode License v3 |
| [Inno Setup Simplified Chinese translation](https://github.com/jrsoftware/issrc/blob/is6/Files/Languages/Unofficial/ChineseSimplified.isl) (maintained by Zhenghan Yang, from [kira-96/Inno-Setup-Chinese-Simplified-Translation](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation)) | Installer texts in Chinese (`installer/ChineseSimplified.isl`, re-saved as UTF-8 with a BOM) | [Inno Setup licence](https://jrsoftware.org/files/is/license.txt) |
| Microsoft Visual C++ Redistributable DLLs | Needed by ONNX Runtime; shipped app-local | Microsoft Visual Studio redistribution terms |

Build and test tools (not shipped): .NET SDK, xUnit, Inno Setup, Node.js.

## Liquid Glass React

The quick panel embeds the three displacement textures from `rdev/liquid-glass-react`
(`src/utils.ts`, commit `ac48eab18d1f7f444ae30002d240cae29c863a21`), ports its shader
displacement generator to C#, and adapts its RGB displacement/filter composition to HLSL.
Source: https://github.com/rdev/liquid-glass-react — MIT, Copyright 2025 MAX ROVENSKY.
The complete license is embedded in ChaoDesk.dll as `Panel/Maps/LICENSE.txt` and is
reproduced in `host/src/ChaoDesk.Host/Panel/Maps/LICENSE.txt`.

## Liquid Glass Studio

The quick panel's precomputed bevel refraction profile adapts the edge-distance and
Snell-law construction from `iyinchao/liquid-glass-studio` (`src/shaders/fragment-main.glsl`,
commit `f7b28c36305a862f5cffed3ddd51511cf1204f56`). The C# lookup normalizes and precomputes
the optical profile; its blending and inner reflection channels are specific to Chao Desk.
Source: https://github.com/iyinchao/liquid-glass-studio — MIT, Copyright 2024 Charles Yin.
The complete license is embedded in ChaoDesk.dll and reproduced in
`host/src/ChaoDesk.Host/Panel/Maps/LIQUID-GLASS-STUDIO-LICENSE.txt`.
