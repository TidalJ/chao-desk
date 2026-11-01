using ChaoDesk.Host.Localization;
using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Panel;

/// <summary>Shared labels and symbols for the quick panel and its settings editor.</summary>
public static class PanelActionPresentation
{
    public static string ResolvedKind(PanelItem item) => item.Kind == "shortcut"
        ? WidgetCatalog.IsWebUrl(item.Target) ? "url" : "program" : item.Kind;

    public static string ItemName(PanelItem item) => PanelAction.Find(item.ActionId) is { } action
        ? Loc.Pick(action.ShortTitle, action.ShortChinese) : item.Name;

    public static string ItemIcon(PanelItem item) => PanelAction.Find(item.ActionId)?.Icon ?? KindIcon(ResolvedKind(item));

    public static string KindTitle(string kind) => kind switch
    {
        "program" or "shortcut" => Loc.T("Launch app"),
        "file" => Loc.T("Open file"), "folder" => Loc.T("Open folder"), "command" => Loc.T("Run command"),
        "url" => Loc.Pick("Open website", "打开网址"), "keys" => Loc.Pick("Send shortcut", "模拟按键"),
        "text" => Loc.Pick("Send text", "发送文本"), "script" => Loc.Pick("Run script", "执行脚本"),
        "builtin" => Loc.Pick("Built-in tools", "内置工具"),
        _ => Loc.Pick("Custom action", "自定义操作")
    };

    public static string KindIcon(string kind) => kind switch
    {
        "url" => "M10,14 L14,10 M8,16 L6,18 A4,4 0 0 1 0,12 L5,7 A4,4 0 0 1 11,7 M13,17 A4,4 0 0 0 19,17 L24,12 A4,4 0 0 0 18,6 L16,8",
        "file" => "M5,2 L14,2 L20,8 L20,22 L5,22 Z M14,2 L14,8 L20,8 M9,12 L16,12 M9,16 L16,16",
        "folder" => "M2,6 L10,6 L12,9 L22,9 L22,21 L2,21 Z M2,6 L2,4 L9,4 L11,6 L20,6 L20,9",
        "command" => "M2,4 L22,4 L22,20 L2,20 Z M6,8 L10,12 L6,16 M13,16 L18,16",
        "keys" => "M2,5 L22,5 L22,19 L2,19 Z M5,9 L7,9 M10,9 L12,9 M15,9 L18,9 M5,13 L7,13 M10,13 L12,13 M15,13 L18,13 M7,16 L17,16",
        "text" => "M4,4 L20,4 M12,4 L12,21 M8,21 L16,21 M4,4 L4,8 M20,4 L20,8",
        "script" => "M8,6 L2,12 L8,18 M16,6 L22,12 L16,18 M14,3 L10,21",
        "builtin" => "M3,3 L10,3 L10,10 L3,10 Z M14,3 L21,3 L21,10 L14,10 Z M3,14 L10,14 L10,21 L3,21 Z M14,14 L21,14 L21,21 L14,21 Z",
        _ => "M3,4 L21,4 L21,20 L3,20 Z M3,8 L21,8 M8,12 L8,17 L13,14.5 Z"
    };

    public static string Error(string error) => error switch
    {
        "PanelFull" => Loc.Pick("15 actions maximum. Remove an action first.", "最多 15 项，请先移除一项。"),
        "InvalidUrl" => Loc.Pick("Enter a valid http or https website address.", "请输入有效的 http 或 https 网址。"),
        "InvalidKeys" => Loc.Pick("Enter a key combination such as Ctrl+Shift+T.", "请输入有效组合键，例如 Ctrl+Shift+T。"),
        "PathNotFound" => Loc.Pick("That app, file or folder could not be found.", "找不到该软件、文件或文件夹。"),
        "FileNotFound" => Loc.T("Choose an existing file."), "FolderNotFound" => Loc.T("Choose an existing folder."),
        "InvalidPath" => Loc.Pick("Choose a file or enter its full path.", "请选择文件，或填写完整路径。"),
        "InvalidWorkingDirectory" or "WorkingDirectoryNotFound" => Loc.Pick("Choose an existing working folder.", "请填写现有工作目录。"),
        "InvalidScriptEngine" => Loc.Pick("Choose PowerShell or CMD.", "请选择 PowerShell 或 CMD。"),
        "InvalidArguments" => Loc.Pick("Check the launch arguments.", "请检查启动参数。"),
        "EmptyTarget" => Loc.Pick("Enter the action content before saving.", "请先填写操作内容。"),
        "InvalidTarget" => Loc.Pick("Remove unsupported null characters from the content.", "请移除内容中的空字符。"),
        _ => Loc.Pick("Could not save this action. Check its fields.", "无法保存，请检查操作内容。")
    };
}
