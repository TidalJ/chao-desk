using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace ChaoDesk.Host.Localization;

/// <summary>
/// The interface language: English, or Simplified Chinese from the tables in Localization/zh/*.txt (embedded;
/// one "English&lt;TAB&gt;中文" pair per line, \n and \t escaped). The English text itself is the key, so code reads
/// <c>Loc.T(…)</c> of the English text and XAML keeps plain English: every window's text, content, headers, tooltips
/// and title are swapped when it loads, and again for open windows when the language changes. A test checks that
/// every UI string has a Chinese entry.
/// </summary>
public static class Loc
{
    public static readonly string[] Modes = ["system", "en", "zh"];

    private static readonly Dictionary<string, string> Zh = Load();
    private static readonly Dictionary<string, string> Back = Zh.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
    private static bool _hooked;

    /// <summary>"en" or "zh": the language in use (the setting resolved against Windows).</summary>
    public static string Language { get; private set; } = "en";

    public static bool IsChinese => Language == "zh";

    /// <summary>Raised after the language changed; windows that build text in code rebuild it.</summary>
    public static event Action? Changed;

    /// <summary>"system" follows the Windows display language: Chinese for any zh-*, else English.</summary>
    public static string Resolve(string mode) => mode switch
    {
        "zh" => "zh",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en",
    };

    public static void Apply(string mode)
    {
        if (!_hooked && Application.Current != null)
        {
            _hooked = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => { if (s is Window w) Localize(w); }));
        }
        string language = Resolve(mode);
        if (language == Language) return;
        Language = language;
        if (Application.Current != null)
            foreach (Window w in Application.Current.Windows) Localize(w);
        Changed?.Invoke();
    }

    /// <summary>The text in the current language (English text is the key; missing entries stay English).</summary>
    public static string T(string english) => IsChinese && Zh.TryGetValue(english, out var zh) ? zh : english;

    /// <summary><see cref="T"/> of a composite format string, then formatted, e.g. F with "{0} items" and a count.</summary>
    public static string F(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(format), args);

    /// <summary>English or Chinese, for the rare text that is not worth a table entry (e.g. a language's own name).</summary>
    public static string Pick(string english, string chinese) => IsChinese ? chinese : english;

    /// <summary>Puts every literal string under <paramref name="root"/> into the current language (either way).</summary>
    public static void Localize(DependencyObject root)
    {
        switch (root)
        {
            case Window w:
                w.Title = Swap(w.Title);
                // Tells WPF the text is Chinese: Han characters then take Chinese glyphs (not Japanese ones from a
                // fallback font) and Chinese punctuation spacing.
                w.Language = System.Windows.Markup.XmlLanguage.GetLanguage(IsChinese ? "zh-CN" : "en-US");
                break;
            case TextBlock tb when tb.Inlines.Count <= 1 && BindingOperations.GetBindingExpressionBase(tb, TextBlock.TextProperty) == null:
                tb.Text = Swap(tb.Text); break;
            case Run run when BindingOperations.GetBindingExpressionBase(run, Run.TextProperty) == null:
                run.Text = Swap(run.Text); break;
            case HeaderedItemsControl h when h.Header is string header: h.Header = Swap(header); break;
            case HeaderedContentControl h when h.Header is string header: h.Header = Swap(header); break;
        }
        if (root is ContentControl { Content: string content } cc && BindingOperations.GetBindingExpressionBase(cc, ContentControl.ContentProperty) == null)
            cc.Content = Swap(content);
        if (root is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip) fe.ToolTip = Swap(tip);
            if (fe.ContextMenu != null) Localize(fe.ContextMenu);
        }
        if (root is TextBlock { Inlines.Count: > 1 } multi)
            foreach (var inline in multi.Inlines.ToList()) Localize(inline);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToList()) Localize(child);
    }

    /// <summary>A shown string in the current language, whichever language it is in now.</summary>
    private static string Swap(string? shown)
    {
        if (string.IsNullOrEmpty(shown)) return shown ?? "";
        string english = Back.TryGetValue(shown, out var en) ? en : shown;
        return T(english);
    }

    // ---- Tables -----------------------------------------------------------------------------------------

    internal static IReadOnlyDictionary<string, string> Chinese => Zh;

    private static Dictionary<string, string> Load()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(Loc).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Localization.zh.", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            foreach (var (english, chinese) in Parse(reader.ReadToEnd())) table[english] = chinese;
        }
        return table;
    }

    /// <summary>The pairs in one table file; blank lines and lines starting with # are skipped.</summary>
    internal static IEnumerable<(string English, string Chinese)> Parse(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1) continue;
            yield return (Unescape(line[..tab]), Unescape(line[(tab + 1)..]));
        }
    }

    private static string Unescape(string s) => s.Contains('\\') ? s.Replace("\\n", "\n").Replace("\\t", "\t") : s;
}
