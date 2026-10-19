using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using Xunit.Abstractions;
using PalitoLauncher.Helpers;
using PalitoLauncher.UI.Tests.TestHelpers;

namespace PalitoLauncher.UI.Tests;

/// <summary>Notas de las releases (Markdown de GitHub) → FlowDocument. Todo en el hilo de UI con el tema real cargado.</summary>
public sealed class MarkdownRendererTests
{
    private readonly ITestOutputHelper _output;

    public MarkdownRendererTests(ITestOutputHelper output) => _output = output;

    private static FlowDocument Render(string? markdown) => WpfHost.Run(() => MarkdownRenderer.Render(markdown));

    private static T OnUi<T>(Func<T> func) => WpfHost.Run(func);

    // ───────────── Bloques ─────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("<!-- solo un comentario -->")]
    public void EmptyNotes_ShowPlaceholder(string? markdown) => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render(markdown);
        var p = Assert.IsType<Paragraph>(Assert.Single(doc.Blocks));
        Assert.Equal("Sin notas para esta versión.", FlowDocs.Text(p));
        Assert.Equal(FontStyles.Italic, p.FontStyle);
    });

    [Fact]
    public void Headings_LevelsSizesAndClosingHashes() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render("# Uno\n## Dos ##\n### C#\n#### Cuatro\n#sinespacio\n    # sangrado");
        var blocks = doc.Blocks.Cast<Paragraph>().ToList();

        Assert.Equal(new[] { "Uno", "Dos", "C#", "Cuatro" }, blocks.Take(4).Select(FlowDocs.Text));
        Assert.Equal(new[] { 22.0, 18.0, 15.5, 15.5 }, blocks.Take(4).Select(p => p.FontSize));
        Assert.All(blocks.Take(4), p => Assert.Equal(FontWeights.SemiBold, p.FontWeight));

        // "#sinespacio" y un "#" con 4 espacios delante no son encabezados: texto normal (dos líneas del mismo párrafo).
        var text = Assert.Single(blocks.Skip(4));
        Assert.Equal("#sinespacio\n# sangrado", FlowDocs.Text(text));
        Assert.NotEqual(FontWeights.SemiBold, text.FontWeight);
    });

    [Theory]
    [InlineData("Título ##", "Título")]
    [InlineData("Título #   ", "Título")]
    [InlineData("C#", "C#")]
    [InlineData("F# y C#", "F# y C#")]
    [InlineData("###", "")]
    [InlineData("  a  ", "  a")]
    public void CleanHeadingText_StripsOnlyClosingSequence(string input, string expected)
        => Assert.Equal(expected, MarkdownRenderer.CleanHeadingText(input));

    [Theory]
    [InlineData("a<!-- x -->b", "ab")]
    [InlineData("a<!-- x -->b<!--y-->c", "abc")]
    [InlineData("a<!-- sin cierre", "a<!-- sin cierre")]
    [InlineData("a<!-- x -->b<!-- sin cierre", "ab<!-- sin cierre")]
    [InlineData("sin comentarios", "sin comentarios")]
    public void RemoveHtmlComments_SinglePass(string input, string expected)
        => Assert.Equal(expected, MarkdownRenderer.RemoveHtmlComments(input));

    [Fact]
    public void Lists_BulletsNestedAndNumbered() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render("- uno\n* dos\n  continúa\n  - anidado\n+ tres\n\n1. primero\n2) segundo");
        var lists = doc.Blocks.Cast<List>().ToList();
        Assert.Equal(2, lists.Count);

        var bullets = lists[0];
        Assert.Equal(TextMarkerStyle.Disc, bullets.MarkerStyle);
        Assert.Equal(3, bullets.ListItems.Count);
        var items = bullets.ListItems.ToList();
        Assert.Equal("uno", FlowDocs.Text((Paragraph)items[0].Blocks.FirstBlock));
        Assert.Equal("dos\ncontinúa", FlowDocs.Text((Paragraph)items[1].Blocks.FirstBlock));
        var nested = Assert.IsType<List>(items[1].Blocks.LastBlock);
        Assert.Equal("anidado", FlowDocs.Text((Paragraph)Assert.Single(nested.ListItems).Blocks.FirstBlock));
        Assert.Equal("tres", FlowDocs.Text((Paragraph)items[2].Blocks.FirstBlock));

        var numbered = lists[1];
        Assert.Equal(TextMarkerStyle.Decimal, numbered.MarkerStyle);
        Assert.Equal(new[] { "primero", "segundo" },
            numbered.ListItems.Select(i => FlowDocs.Text((Paragraph)i.Blocks.FirstBlock)));
    });

    [Fact]
    public void Inline_BoldItalicStrikeAndCode() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render("**negrita** *cursiva* __también__ ~~tachado~~ `código` y nombre_con_guiones");
        var inlines = FlowDocs.AllInlines(doc).ToList();

        Assert.Equal(new[] { "negrita", "también" }, inlines.OfType<Bold>().Select(b => FlowDocs.Text(b.Inlines)));
        Assert.Equal("cursiva", FlowDocs.Text(Assert.Single(inlines.OfType<Italic>()).Inlines));
        var strike = Assert.Single(inlines.OfType<Span>(), s => s.TextDecorations == TextDecorations.Strikethrough);
        Assert.Equal("tachado", FlowDocs.Text(strike.Inlines));

        var code = Assert.Single(inlines.OfType<Run>(), r => r.Text == "código");
        Assert.Equal(13, code.FontSize);
        Assert.NotEqual(doc.FontFamily, code.FontFamily); // monoespaciada

        // Los guiones bajos dentro de una palabra no abren cursiva.
        Assert.EndsWith("y nombre_con_guiones", FlowDocs.Text(doc));
    });

    [Fact]
    public void Inline_EscapesAndUnclosedDelimitersStayLiteral() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render(@"\*literal\* **sin cierre `tampoco ~~nada");
        Assert.DoesNotContain(FlowDocs.AllInlines(doc), i => i is Bold or Italic);
        Assert.Equal("*literal* **sin cierre `tampoco ~~nada", FlowDocs.Text(doc));
    });

    [Fact]
    public void CodeBlock_KeepsTextVerbatim() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render("Antes\n```csharp\nvar x = **1**;\n  [no](https://example.com)\n```\nDespués");
        var blocks = doc.Blocks.Cast<Paragraph>().ToList();
        Assert.Equal(3, blocks.Count);

        var code = blocks[1];
        Assert.Equal("var x = **1**;\n  [no](https://example.com)", FlowDocs.Text(code));
        Assert.NotEqual(doc.FontFamily, code.FontFamily);
        Assert.DoesNotContain(FlowDocs.AllInlines(doc), i => i is Bold or Hyperlink);
    });

    [Fact]
    public void CodeBlock_WithoutClosingFence_StillRendersAsCode() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render("```\nuno\ndos");
        Assert.Equal("uno\ndos", FlowDocs.Text(Assert.IsType<Paragraph>(Assert.Single(doc.Blocks))));
    });

    [Fact]
    public void QuoteAndRule() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render("> una cita\n\n---\n\ntexto");
        var blocks = doc.Blocks.Cast<Paragraph>().ToList();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("una cita", FlowDocs.Text(blocks[0]));
        Assert.Equal(3, blocks[0].BorderThickness.Left);
        Assert.Equal("", FlowDocs.Text(blocks[1]));
        Assert.Equal(1, blocks[1].BorderThickness.Bottom);
        Assert.Equal("texto", FlowDocs.Text(blocks[2]));
    });

    // ───────────── Enlaces ─────────────

    [Theory]
    [InlineData("[GitHub](https://github.com/JJJP-Dev/Coop2D)", "GitHub", "https://github.com/JJJP-Dev/Coop2D")]
    [InlineData("[con título](https://example.com/a \"Título\")", "con título", "https://example.com/a")]
    [InlineData("[ángulos](<https://example.com/b>)", "ángulos", "https://example.com/b")]
    [InlineData("![captura](https://example.com/c.png)", "captura", "https://example.com/c.png")]
    [InlineData("<https://example.com/e>", "https://example.com/e", "https://example.com/e")]
    [InlineData("Mira https://example.com/f_g.", "https://example.com/f_g", "https://example.com/f_g")]
    [InlineData("(ver https://example.com/h)", "https://example.com/h", "https://example.com/h")]
    [InlineData("http://example.com/i?x=1&y=(2)", "http://example.com/i?x=1&y=(2)", "http://example.com/i?x=1&y=(2)")]
    [InlineData("HTTPS://EXAMPLE.COM/J", "HTTPS://EXAMPLE.COM/J", "https://example.com/J")]
    public void WebLinks_BecomeHyperlinks(string markdown, string label, string uri) => WpfHost.Run(() =>
    {
        var link = Assert.Single(FlowDocs.Links(MarkdownRenderer.Render(markdown)));
        Assert.Equal(label, FlowDocs.Text(link.Inlines));
        Assert.Equal(new Uri(uri), link.NavigateUri);
    });

    /// <summary>
    /// Regresión: si el texto del enlace es una URL ("[https://x](https://x)" o "[](https://x)"), se creaba un Hyperlink
    /// dentro de otro; WPF lanza InvalidOperationException y Render mostraba TODAS las notas como texto plano (se perdían
    /// encabezados, listas y enlaces).
    /// </summary>
    [Theory]
    [InlineData("# Título\n\n[https://example.com/a](https://example.com/a)\n\n- **lista**")]
    [InlineData("# Título\n\n[](https://example.com/a)\n\n- **lista**")]
    public void LinkWhoseLabelIsAUrl_KeepsTheRestOfTheFormatting(string markdown) => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render(markdown);
        var link = Assert.Single(FlowDocs.Links(doc));
        Assert.Equal(new Uri("https://example.com/a"), link.NavigateUri);
        Assert.Equal("https://example.com/a", FlowDocs.Text(link.Inlines));
        Assert.Equal(22.0, Assert.IsType<Paragraph>(doc.Blocks.FirstBlock).FontSize);
        Assert.IsType<List>(doc.Blocks.LastBlock);
    });

    [Fact]
    public void LinkLabel_KeepsBoldItalicAndCode() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render("[**Descarga** la *versión* `1.2.0`](https://example.com/a)");
        var link = Assert.Single(FlowDocs.Links(doc));
        Assert.Equal(new Uri("https://example.com/a"), link.NavigateUri);
        Assert.Equal("Descarga la versión 1.2.0", FlowDocs.Text(link.Inlines));

        Assert.Equal("Descarga", FlowDocs.Text(Assert.Single(link.Inlines.OfType<Bold>()).Inlines));
        Assert.Equal("versión", FlowDocs.Text(Assert.Single(link.Inlines.OfType<Italic>()).Inlines));
        var code = Assert.Single(link.Inlines.OfType<Run>(), r => r.Text == "1.2.0");
        Assert.Equal(13, code.FontSize);
        Assert.NotEqual(doc.FontFamily, code.FontFamily); // monoespaciada
    });

    /// <summary>Ninguna sintaxis de enlace dentro de la etiqueta de otro crea un segundo Hyperlink: queda como texto.</summary>
    [Theory]
    [InlineData("[ver https://example.com/b](https://example.com/a)", "ver https://example.com/b")]
    [InlineData("[**https://example.com/b**](https://example.com/a)", "https://example.com/b")]
    [InlineData("[~~*https://example.com/b*~~](https://example.com/a)", "https://example.com/b")]
    [InlineData("[<https://example.com/b>](https://example.com/a)", "https://example.com/b")]
    [InlineData("[[interno](https://example.com/b)](https://example.com/a)", "interno")]
    [InlineData("[![insignia](https://example.com/b.svg)](https://example.com/a)", "insignia")]
    public void LinkInsideLinkLabel_IsOnlyText(string markdown, string label) => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render("## Notas\n\n" + markdown + "\n\n- fin");
        var link = Assert.Single(FlowDocs.Links(doc));
        Assert.Equal(new Uri("https://example.com/a"), link.NavigateUri);
        Assert.Equal(label, FlowDocs.Text(link.Inlines));
        Assert.Equal(18.0, Assert.IsType<Paragraph>(doc.Blocks.FirstBlock).FontSize); // no cayó al texto plano
        Assert.IsType<List>(doc.Blocks.LastBlock);
    });

    [Theory]
    [InlineData("[clic](javascript:alert(1))", "clic")]
    [InlineData("[sistema](file:///C:/Windows/System32)", "sistema")]
    [InlineData("[disco](C:\\Windows\\notepad.exe)", "disco")]
    [InlineData("[correo](mailto:alguien@example.com)", "correo")]
    [InlineData("[ftp](ftp://example.com/archivo.zip)", "ftp")]
    [InlineData("[relativo](docs/README.md)", "relativo")]
    [InlineData("[red](//servidor/compartido)", "red")]
    [InlineData("[datos](data:text/html,<b>hola</b>)", "datos")]
    [InlineData("<ftp://example.com/archivo.zip>", "<ftp://example.com/archivo.zip>")]
    [InlineData("ftp://example.com/archivo.zip", "ftp://example.com/archivo.zip")]
    [InlineData("javascript:alert(1)", "javascript:alert(1)")]
    [InlineData("file:///C:/Windows", "file:///C:/Windows")]
    public void NonWebLinks_AreOnlyText(string markdown, string visibleText) => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render(markdown);
        Assert.Empty(FlowDocs.Links(doc));
        Assert.Contains(visibleText, FlowDocs.Text(doc));
    });

    [Fact]
    public void EveryHyperlink_IsHttpOrHttps() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render(string.Join("\n", new[]
        {
            "# Notas [inicio](https://example.com/1)",
            "- [a](javascript:x) **[b](http://example.com/2)** <file:///C:/x> ~~https://example.com/3~~",
            "> cita con [c](ftp://x) y https://example.com/4",
            "1. ![d](C:\\img.png) [e](https://example.com/5)",
        }));
        var uris = FlowDocs.Links(doc).Select(l => l.NavigateUri).ToList();
        Assert.Equal(5, uris.Count);
        Assert.All(uris, u => Assert.True(u.Scheme is "http" or "https", u.ToString()));
    });

    // ───────────── Entradas patológicas ─────────────

    /// <summary>Entradas que castigan al analizador (retroceso, búsquedas de cierre, delimitadores sin pareja).</summary>
    public static TheoryData<string> PathologicalInputs => new()
    {
        "heading-a-3000-spaces-b",
        "junk-300k",
        "brackets-300k",
        "nested-brackets",
        "link-openers",
        "stars-300k",
        "underscores",
        "bold-far-apart",
        "backticks-300k",
        "tildes-300k",
        "angles-300k",
        "url-closing-parens",
        "html-comment-openers",
        "rule-like",
        "deep-list-indent",
        "heading-hashes",
    };

    [Theory]
    [MemberData(nameof(PathologicalInputs))]
    public void PathologicalInput_RendersWellUnderOneSecond(string name)
        => AssertRendersWithin(name, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Entradas válidas pero con decenas de miles de elementos (Run/Bold/Hyperlink): ahí manda el coste de construir el
    /// FlowDocument (~20 µs por elemento, ~1,2 s con 100 000 caracteres en Release). Solo protege contra un coste
    /// cuadrático, que con este tamaño serían minutos.
    /// </summary>
    [Theory]
    [InlineData("dense-lines")]
    [InlineData("dense-bold")]
    [InlineData("dense-mixed")]
    public void DenseInput_DoesNotHang(string name) => AssertRendersWithin(name, TimeSpan.FromSeconds(10));

    private void AssertRendersWithin(string name, TimeSpan budget)
    {
        var input = BuildPathological(name);
        Render("**calentamiento** [x](https://example.com) `y`"); // JIT y regex compiladas fuera de la medida

        var sw = Stopwatch.StartNew();
        var blocks = OnUi(() => MarkdownRenderer.Render(input).Blocks.Count);
        sw.Stop();

        _output.WriteLine($"{name}: {input.Length:N0} caracteres → {blocks} bloques en {sw.ElapsedMilliseconds} ms");
        Assert.True(blocks > 0);
        Assert.True(sw.Elapsed < budget, $"{name} tardó {sw.ElapsedMilliseconds} ms (máximo {budget.TotalMilliseconds} ms).");
    }

    [Fact]
    public void HeadingWithThousandsOfSpaces_KeepsBothWords() => WpfHost.Run(() =>
    {
        var doc = MarkdownRenderer.Render(BuildPathological("heading-a-3000-spaces-b"));
        var heading = Assert.IsType<Paragraph>(Assert.Single(doc.Blocks));
        Assert.Equal(22.0, heading.FontSize);
        Assert.Equal("a" + new string(' ', 3000) + "b", FlowDocs.Text(heading));
    });

    [Fact]
    public void HugeInput_IsTruncatedTo100kCharacters() => WpfHost.Run(() =>
    {
        var text = FlowDocs.Text(MarkdownRenderer.Render(new string('a', 300_000)));
        Assert.Equal(100_000 + 1, text.Length);
        Assert.EndsWith("a…", text);
    });

    private static string BuildPathological(string name) => name switch
    {
        "heading-a-3000-spaces-b" => "# a" + new string(' ', 3000) + "b",
        "junk-300k" => Junk(300_000, seed: 20261019),
        "brackets-300k" => new string('[', 300_000),
        "nested-brackets" => new string('[', 150_000) + new string(']', 150_000) + "(",
        "link-openers" => Repeat("[a](", 75_000),
        "stars-300k" => new string('*', 300_000),
        "underscores" => Repeat("_a", 150_000),
        "bold-far-apart" => Repeat("**" + new string('a', 2_100), 140), // cada "**" busca su cierre 2000 caracteres y falla
        "backticks-300k" => new string('`', 300_000),
        "tildes-300k" => new string('~', 300_000),
        "angles-300k" => new string('<', 300_000),
        "url-closing-parens" => "https://example.com/" + new string(')', 300_000),
        "html-comment-openers" => Repeat("<!--", 75_000),
        "rule-like" => Repeat("- ", 150_000) + "x",
        "deep-list-indent" => Repeat(new string(' ', 200) + "- elemento\n", 1_400),
        "heading-hashes" => "# " + new string('#', 150_000) + " x " + new string('#', 150_000),
        "dense-lines" => Repeat("*a\n", 100_000),
        "dense-bold" => Repeat("**a ", 75_000),
        "dense-mixed" => Repeat("texto **b** [c](https://example.com/d) `e` ", 7_000),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static string Repeat(string value, int count) => new StringBuilder(value.Length * count).Insert(0, value, count).ToString();

    /// <summary>Mezcla aleatoria (semilla fija) de todos los delimitadores que interpreta el renderer.</summary>
    private static string Junk(int length, int seed)
    {
        const string alphabet = "*_~`[]()<>!#-+=|\\ \t\n\nhttps://a.b/c1234567890.,;:\"'abcXYZ";
        var random = new Random(seed);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(alphabet[random.Next(alphabet.Length)]);
        return sb.ToString();
    }
}
