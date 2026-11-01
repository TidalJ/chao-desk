namespace ChaoDesk.Host.Panel;

/// <summary>Stable action identities; translated labels never determine dispatch.</summary>
public sealed record PanelAction(string Id, string Title, string Chinese, string Icon, string Keywords)
{
    // Tile labels stay compact; full titles remain available for tooltips and search.
    public string ShortTitle => Id switch
    {
        "restore" => "Reopen",
        "clipboard" => "Clipboard",
        "screenshot" => "Screenshot",
        "ocr" => "OCR",
        "translate" => "Translate",
        "pin" => "Pin",
        "scrollshot" => "Scrollshot",
        "launcher" => "Search",
        "quickshot" => "Fullscreen",
        "pinclipboard" => "Pin clip",
        "ocrclipboard" => "Read image",
        "togglepins" => "Show pins",
        "closepins" => "Close pins",
        "wallpaperpause" => "Pause",
        "lock" => "Lock",
        "settings" => "Settings",
        _ => Title
    };

    public string ShortChinese => Id switch
    {
        "restore" => "撤销关闭",
        "clipboard" => "剪贴板",
        "screenshot" => "截图",
        "ocr" => "识字",
        "translate" => "翻译",
        "pin" => "贴图",
        "scrollshot" => "长截图",
        "launcher" => "搜索",
        "quickshot" => "全屏截图",
        "pinclipboard" => "贴剪贴板",
        "ocrclipboard" => "图片识字",
        "togglepins" => "显隐贴图",
        "closepins" => "关闭贴图",
        "wallpaperpause" => "暂停壁纸",
        "lock" => "锁屏",
        "settings" => "设置",
        _ => Chinese
    };

    public static readonly PanelAction[] All =
    [
        new("restore", "Reopen closed tab", "撤销关闭", "M4,10 L10,4 M4,10 L10,16 M4,10 L15,10 C23,10 23,22 15,22", "undo browser tab chexiao"),
        new("clipboard", "Clipboard history", "剪贴板", "M8,5 L4,5 L4,22 L20,22 L20,5 L16,5 M8,3 L16,3 L16,8 L8,8 Z M8,12 L16,12 M8,17 L14,17", "paste history jian tie ban"),
        new("screenshot", "Screenshot", "截图", "M8,3 L3,3 L3,8 M16,3 L21,3 L21,8 M21,16 L21,21 L16,21 M8,21 L3,21 L3,16 M8,8 L16,8 L16,16 L8,16 Z", "capture snip jietu"),
        new("ocr", "Recognise text", "屏幕识字", "M8,3 L3,3 L3,8 M16,3 L21,3 L21,8 M21,16 L21,21 L16,21 M8,21 L3,21 L3,16 M7,9 L17,9 M12,9 L12,17 M9,17 L15,17", "ocr text shizi wenzi"),
        new("translate", "Translation", "翻译", "M3,5 L15,5 M9,3 L9,5 M6,5 C6,11 10,14 13,15 M13,5 C12,11 8,14 4,16 M14,22 L18,11 L22,22 M16,18 L20,18", "language fanyi"),
        new("pin", "Pin an area", "屏幕贴图", "M8,3 L16,3 L15,9 L19,13 L19,15 L5,15 L5,13 L9,9 Z M12,15 L12,23", "sticky image tietu"),
        new("scrollshot", "Scrolling screenshot", "长截图", "M6,3 L18,3 L18,21 L6,21 Z M9,8 L15,8 M9,12 L15,12 M10,16 L12,18 L14,16", "long capture changjietu"),
        new("launcher", "Search apps and files", "文件搜索", "M3,7 L3,4 L9,4 L12,7 L21,7 L21,21 L3,21 Z M10,12 L17,12 M10,16 L15,16", "files apps search wenjian sousuo"),
        new("quickshot", "Full screen", "全屏截图", "M3,4 L21,4 L21,18 L3,18 Z M8,22 L16,22 M12,18 L12,22", "full screen capture"),
        new("pinclipboard", "Pin clipboard", "剪贴板贴图", "M4,3 L16,3 L16,8 M4,3 L4,20 L10,20 M12,8 L20,8 L19,13 L22,16 L10,16 L13,13 Z M16,16 L16,23", "paste image pin"),
        new("ocrclipboard", "Read clipboard", "图片识字", "M4,3 L20,3 L20,21 L4,21 Z M7,8 L17,8 M12,8 L12,17 M9,17 L15,17", "ocr clipboard image"),
        new("togglepins", "Show / hide pins", "显隐贴图", "M2,12 Q12,0 22,12 Q12,24 2,12 Z M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12", "toggle pins"),
        new("closepins", "Close all pins", "关闭贴图", "M3,3 L18,3 L18,18 L3,18 Z M8,8 L13,13 M13,8 L8,13 M7,22 L22,22 L22,7", "close pins"),
        new("wallpaperpause", "Pause / resume", "暂停壁纸", "M7,4 L7,20 M17,4 L17,20", "pause resume wallpaper"),
        new("lock", "Lock computer", "锁定电脑", "M5,10 L19,10 L19,22 L5,22 Z M8,10 L8,6 A4,4 0 0 1 16,6 L16,10 M12,14 L12,18", "lock screen"),
        new("settings", "Settings", "应用设置", "M9.79,4.31 L10.26,2.15 L13.74,2.15 L14.21,4.31 L15.88,5.00 L17.74,3.81 L20.19,6.26 L19.00,8.12 L19.69,9.79 L21.85,10.26 L21.85,13.74 L19.69,14.21 L19.00,15.88 L20.19,17.74 L17.74,20.19 L15.88,19.00 L14.21,19.69 L13.74,21.85 L10.26,21.85 L9.79,19.69 L8.12,19.00 L6.26,20.19 L3.81,17.74 L5.00,15.88 L4.31,14.21 L2.15,13.74 L2.15,10.26 L4.31,9.79 L5.00,8.12 L3.81,6.26 L6.26,3.81 L8.12,5.00 Z M15.5,12 A3.5,3.5 0 1 0 8.5,12 A3.5,3.5 0 1 0 15.5,12", "settings preferences"),
    ];

    public static PanelAction? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) ||
        (Title + " " + Chinese + " " + ShortTitle + " " + ShortChinese + " " + Keywords).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}
