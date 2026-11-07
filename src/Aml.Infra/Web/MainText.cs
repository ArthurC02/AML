using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Aml.Infra.Html;

namespace Aml.Infra.Web;

/// <summary>The readable text of a web page: its title and the main text, one paragraph per line.</summary>
public sealed record PageText(string? Title, string Text);

/// <summary>
/// Keeps a web page's main text and drops its menus, headers, footers, share buttons and link lists (web-text.md).
/// After Readability (Mozilla) and boilerpipe (Kohlschütter et al. 2010): elements that never hold content are removed,
/// paragraphs score their parent and grandparent by length and punctuation, the best container less its link share is
/// the main text, and link lists inside it are dropped. Every element's text and link counts are summed once, bottom up,
/// so the work is linear in the page; nothing recurses, so nesting depth cannot overflow.
/// </summary>
public static partial class MainText
{
    private const int MinParagraph = 20;
    private const double MostOfPage = 0.5;

    /// <summary>A page as fetched: its meta charset is followed (UTF-8, Big5 …).</summary>
    public static PageText FromHtml(byte[] html)
    {
        using var stream = new MemoryStream(html, writable: false);
        return From(HtmlExport.Parse((parser, ct) => parser.ParseDocumentAsync(stream, ct)));
    }

    public static PageText FromHtml(string html) => From(HtmlExport.Parse((parser, ct) => parser.ParseDocumentAsync(html, ct)));

    private sealed record Counts(int Visible, int Links, int Commas);

    private static PageText From(IHtmlDocument doc)
    {
        if (doc.Body is not { } body) return new PageText(Clean(doc.Title), "");
        HtmlExport.CheckDepth(body);
        var pageH1 = body.QuerySelectorAll("h1").FirstOrDefault(h => h.Closest("header,nav") is null);

        // Boilerplate is removed only while it is not most of the page: a wrapper named "no-sidebar" or a WebForms
        // <form> around everything stays.
        var counts = Count(body);
        var page = counts[body].Visible;
        bool Small(IElement e) => counts.TryGetValue(e, out var c) && c.Visible < page * MostOfPage;
        foreach (var e in body.QuerySelectorAll(string.Join(',', NeverContent)).ToList())
            if (Small(e)) e.Remove();
        foreach (var e in body.QuerySelectorAll("[class],[id]").ToList())
            if (Small(e) && NamedBoilerplate(e)) e.Remove();

        counts = Count(body);
        var main = Best(body, counts) ?? body;
        // A link list inside the main text (相關新聞, 分享, 上一則/下一則) is not text.
        foreach (var e in main.QuerySelectorAll("ul,ol,div,p,td,table,section,dl").ToList())
            if (counts.TryGetValue(e, out var c) && c.Links > 0 && LinkDensity(c) > 0.5) e.Remove();
        var title = Clean(main.QuerySelector("h1")?.TextContent) ?? Clean(pageH1?.TextContent) ?? Clean(doc.Title);
        return new PageText(title, Lines(main));
    }

    // Visible characters, characters inside links and commas of every element, children before parents: document order
    // reversed visits every descendant before its ancestor.
    private static Dictionary<IElement, Counts> Count(IElement root)
    {
        var counts = new Dictionary<IElement, Counts>();
        foreach (var e in root.QuerySelectorAll("*").Prepend(root).Reverse())
        {
            int visible = 0, links = 0, commas = 0;
            foreach (var child in e.ChildNodes)
                if (child is IText t) { visible += Visible(t.Data); commas += Commas(t.Data); }
                else if (child is IElement c && counts.TryGetValue(c, out var cc)) { visible += cc.Visible; links += cc.Links; commas += cc.Commas; }
            counts[e] = new Counts(visible, e.LocalName == "a" ? visible : links, commas);
        }
        return counts;
    }

    // Readability's scoring: a paragraph gives 1 + its commas + its length in hundreds (at most 3) to its parent, half to its
    // grandparent; the best container keeps its score less its share of link text.
    private static IElement? Best(IElement body, Dictionary<IElement, Counts> counts)
    {
        var scores = new Dictionary<IElement, double>();
        foreach (var e in body.QuerySelectorAll("p,pre,td,li,dd,blockquote,div,span,font,section,article"))
        {
            if (OwnText(e, counts) < MinParagraph) continue;
            var c = counts[e];
            var score = 1 + c.Commas + Math.Min(c.Visible / 100, 3);
            if (e.ParentElement is { } parent)
            {
                scores[parent] = scores.GetValueOrDefault(parent) + score;
                if (parent.ParentElement is { } grand) scores[grand] = scores.GetValueOrDefault(grand) + score / 2.0;
            }
        }
        return scores.Count == 0 ? null : scores.MaxBy(s => s.Value * (1 - LinkDensity(counts[s.Key]))).Key;
    }

    // Characters written in the element itself or its inline children (text set in a div with <br>, not in <p>, counts).
    private static int OwnText(IElement e, Dictionary<IElement, Counts> counts)
    {
        var n = 0;
        foreach (var child in e.ChildNodes)
            if (child is IText t) n += Visible(t.Data);
            else if (child is IElement c && !Blocks.Contains(c.LocalName) && counts.TryGetValue(c, out var cc)) n += cc.Visible;
        return n;
    }

    private static double LinkDensity(Counts c) => c.Visible == 0 ? 1 : Math.Min(1, (double)c.Links / c.Visible);

    private static int Visible(string s)
    {
        var n = 0;
        foreach (var c in s) if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    private static int Commas(string s)
    {
        var n = 0;
        foreach (var c in s) if (c is '，' or ',' or '。' or '、' or '；') n++;
        return n;
    }

    // A class or id token naming boilerplate (nav, menu, footer, share …): whole tokens, so "shareholder" is not "share".
    private static bool NamedBoilerplate(IElement e)
    {
        foreach (var token in Tokens().Split($"{e.ClassName} {e.Id}"))
            if (BoilerplateTokens.Contains(token)) return true;
        return false;
    }

    // Text with a line break around every block element and at every <br>, a space between table cells; a line break in
    // the source is a space, except in <pre>. Walked with a stack, not recursion.
    private static string Lines(IElement root)
    {
        var sb = new StringBuilder();
        var stack = new Stack<(INode Node, bool Closing)>();
        stack.Push((root, false));
        while (stack.TryPop(out var item))
        {
            if (item.Closing) { sb.Append('\n'); continue; }
            switch (item.Node)
            {
                case IText t: sb.Append(t.ParentElement?.Closest("pre") is null ? t.Data.Replace('\n', ' ').Replace('\r', ' ') : t.Data); break;
                case IElement { LocalName: "br" }: sb.Append('\n'); break;
                case IElement e:
                    var block = Blocks.Contains(e.LocalName);
                    if (block) { sb.Append('\n'); stack.Push((e, true)); }
                    else if (e.LocalName is "td" or "th") sb.Append(' ');
                    for (var i = e.ChildNodes.Length - 1; i >= 0; i--) stack.Push((e.ChildNodes[i], false));
                    break;
            }
        }
        return string.Join('\n', sb.ToString().Split('\n').Select(Clean).OfType<string>());
    }

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        var t = Spaces().Replace(s.Replace(' ', ' '), " ").Trim();
        return t.Length == 0 ? null : t;
    }

    private static readonly string[] NeverContent =
        ["script", "style", "noscript", "iframe", "svg", "button", "input", "select", "textarea", "nav", "header", "footer", "aside", "template", "object", "embed", "map", "dialog"];

    // Elements that start a new line; anything else (a, span, del, ruby …) runs on in its line.
    private static readonly HashSet<string> Blocks = new(
        ["address", "article", "aside", "blockquote", "body", "caption", "center", "dd", "details", "dialog", "dir", "div", "dl", "dt", "fieldset",
         "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "menu", "nav", "ol", "p", "pre",
         "section", "summary", "table", "tbody", "tfoot", "thead", "tr", "ul"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> BoilerplateTokens = new(
        ["nav", "navbar", "navigation", "menu", "submenu", "footer", "header", "sidebar", "breadcrumb", "breadcrumbs", "share", "sharing", "social",
         "cookie", "cookies", "banner", "advert", "ad", "ads", "related", "comment", "comments", "login", "search", "pager", "pagination", "toolbar",
         "skip", "copyright", "sitemap", "accesskey", "qrcode", "print", "fontsize"],
        StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\s_\-]+")]
    private static partial Regex Tokens();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();
}
