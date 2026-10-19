using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Services;

namespace PalitoLauncher.Helpers;

/// <summary>
/// Convierte las notas de una release (Markdown sencillo de GitHub) en un FlowDocument para mostrarlo en un
/// FlowDocumentScrollViewer. Soporta: encabezados # a ###, listas (-, *, +, 1.) con anidación por sangría,
/// **negrita**, *cursiva*, ~~tachado~~, `código`, bloques ```, citas &gt;, separadores ---, [texto](url), &lt;url&gt; y
/// URLs sueltas como enlaces (solo http/https). Las líneas en blanco separan párrafos y un salto de línea simple se
/// respeta. Tolerante con cualquier entrada: nunca lanza (en el peor caso muestra el texto tal cual) y su coste es
/// lineal en la longitud del texto (patrones sin retroceso catastrófico, búsquedas acotadas y tiempo máximo por regex:
/// si se agota, la línea se trata como texto normal). Crear y usar en el hilo de UI.
/// </summary>
public static class MarkdownRenderer
{
    private const int MaxInputLength = 100_000;
    private const int MaxInlineDepth = 6;
    private const int MaxSpanSearch = 2_000;

    // Red de seguridad: ninguna regex de bloque puede bloquear la UI más de esto por línea.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    // Patrones lineales (los marcadores de cierre "###" del encabezado se quitan en código, ver CleanHeadingText).
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})[ \t]+(.*)$", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex RuleRegex = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ListItemRegex = new(@"^([ \t]*)([-*+]|\d{1,3}[.)])[ \t]+(.*)$", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex QuoteRegex = new(@"^ {0,3}>[ \t]?(.*)$", RegexOptions.Compiled, RegexTimeout);

    /// <summary>Genera el documento. Texto nulo o vacío → "Sin notas para esta versión.".</summary>
    public static FlowDocument Render(string? markdown)
    {
        var doc = CreateDocument();
        try
        {
            new Builder(doc).Build(markdown ?? "");
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudieron interpretar las notas en Markdown; se muestran como texto.", ex);
            doc.Blocks.Clear();
            var text = markdown ?? "";
            if (text.Length > MaxInputLength) text = text[..MaxInputLength] + "…";
            doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0) });
        }

        if (doc.Blocks.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("Sin notas para esta versión."))
            {
                Foreground = GetBrush("TextDimBrush", Color.FromRgb(0x9A, 0xA3, 0xB2)),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0),
            });
        }
        return doc;
    }

    private static FlowDocument CreateDocument()
    {
        var doc = new FlowDocument
        {
            FontFamily = GetFont("AppFont", "Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            Foreground = GetBrush("TextBrush", Color.FromRgb(0xE8, 0xEA, 0xF0)),
            Background = Brushes.Transparent,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            LineHeight = 21,
            IsHyphenationEnabled = false,
        };
        return doc;
    }

    internal static Brush GetBrush(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        }
        catch (Exception ex)
        {
            Log.Debug($"Recurso {key} no disponible: {ex.Message}");
        }
        var b = new SolidColorBrush(fallback);
        b.Freeze();
        return b;
    }

    private static FontFamily GetFont(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is FontFamily font) return font;
        }
        catch (Exception ex)
        {
            Log.Debug($"Fuente {key} no disponible: {ex.Message}");
        }
        return new FontFamily(fallback);
    }

    /// <summary>Match que nunca lanza: si la regex agota su tiempo, la línea se trata como texto normal.</summary>
    private static Match SafeMatch(Regex regex, string input)
    {
        try
        {
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            Log.Debug("Una línea de las notas tardó demasiado en analizarse; se muestra como texto.");
            return Match.Empty;
        }
    }

    /// <summary>
    /// Quita la secuencia de cierre opcional de un encabezado ("## Título ##" → "Título"). Como en CommonMark, solo si va
    /// precedida de un espacio (o es todo el texto), para no recortar cosas como "C#". Lineal.
    /// </summary>
    internal static string CleanHeadingText(string text)
    {
        var s = (text ?? "").TrimEnd();
        int k = s.Length;
        while (k > 0 && s[k - 1] == '#') k--;
        if (k < s.Length && (k == 0 || s[k - 1] == ' ' || s[k - 1] == '\t'))
            s = s[..k].TrimEnd();
        return s;
    }

    /// <summary>Quita los comentarios HTML (&lt;!-- … --&gt;) en una sola pasada. Uno sin cierre se deja tal cual.</summary>
    internal static string RemoveHtmlComments(string text)
    {
        int start = text.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0) return text;

        var sb = new StringBuilder(text.Length);
        int pos = 0;
        while (start >= 0)
        {
            int end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0) break; // sin cierre: tampoco lo tendrán los siguientes
            sb.Append(text, pos, start - pos);
            pos = end + 3;
            start = pos < text.Length ? text.IndexOf("<!--", pos, StringComparison.Ordinal) : -1;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <summary>Estado del análisis por bloques.</summary>
    private sealed class Builder
    {
        private readonly FlowDocument _doc;
        private readonly List<(int Indent, List List)> _lists = new();
        private readonly Brush _dimBrush;
        private readonly Brush _codeBackground;
        private readonly Brush _borderBrush;
        private readonly Brush _linkBrush;
        private readonly FontFamily _monoFont;
        private Paragraph? _paragraph;
        private Paragraph? _lastListParagraph;
        private bool _pendingBlank;

        public Builder(FlowDocument doc)
        {
            _doc = doc;
            _dimBrush = GetBrush("TextDimBrush", Color.FromRgb(0x9A, 0xA3, 0xB2));
            _codeBackground = GetBrush("CodeBackgroundBrush", Color.FromRgb(0x26, 0x2C, 0x38));
            _borderBrush = GetBrush("BorderBrush", Color.FromRgb(0x2A, 0x30, 0x3C));
            _linkBrush = GetBrush("InfoBrush", Color.FromRgb(0x5A, 0xA9, 0xFF));
            _monoFont = GetFont("MonoFont", "Cascadia Mono, Consolas");
        }

        public void Build(string markdown)
        {
            var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
            if (text.Length > MaxInputLength) text = text[..MaxInputLength] + "…";
            text = RemoveHtmlComments(text);

            var lines = text.Split('\n');
            StringBuilder? code = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Replace("\t", "    ");

                if (code != null)
                {
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        AddCodeBlock(code.ToString());
                        code = null;
                    }
                    else
                    {
                        if (code.Length > 0) code.Append('\n');
                        code.Append(line);
                    }
                    continue;
                }

                var trimmed = line.Trim();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    CloseAll();
                    code = new StringBuilder();
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    CloseParagraph();
                    _pendingBlank = true;
                    continue;
                }

                var heading = SafeMatch(HeadingRegex, trimmed);
                if (heading.Success && line.Length - line.TrimStart().Length < 4)
                {
                    CloseAll();
                    AddHeading(heading.Groups[1].Value.Length, CleanHeadingText(heading.Groups[2].Value));
                    _pendingBlank = false;
                    continue;
                }

                if (SafeMatch(RuleRegex, line).Success)
                {
                    CloseAll();
                    AddRule();
                    _pendingBlank = false;
                    continue;
                }

                var item = SafeMatch(ListItemRegex, line);
                if (item.Success)
                {
                    CloseParagraph();
                    var indent = item.Groups[1].Value.Length;
                    var marker = item.Groups[2].Value;
                    AddListItem(indent, char.IsDigit(marker[0]), item.Groups[3].Value);
                    _pendingBlank = false;
                    continue;
                }

                var quote = SafeMatch(QuoteRegex, line);
                if (quote.Success)
                {
                    CloseAll();
                    AddQuote(quote.Groups[1].Value);
                    _pendingBlank = false;
                    continue;
                }

                // Texto normal: continuación de un elemento de lista o párrafo.
                if (_lists.Count > 0 && !_pendingBlank && _lastListParagraph != null)
                {
                    _lastListParagraph.Inlines.Add(new LineBreak());
                    AddInlines(_lastListParagraph.Inlines, trimmed, 0);
                }
                else
                {
                    CloseLists();
                    if (_paragraph == null)
                    {
                        _paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                        _doc.Blocks.Add(_paragraph);
                    }
                    else
                    {
                        _paragraph.Inlines.Add(new LineBreak());
                    }
                    AddInlines(_paragraph.Inlines, trimmed, 0);
                }
                _pendingBlank = false;
            }

            if (code != null) AddCodeBlock(code.ToString());
            CloseAll();
            TrimLastMargin();
        }

        private void CloseParagraph() => _paragraph = null;

        private void CloseLists()
        {
            _lists.Clear();
            _lastListParagraph = null;
        }

        private void CloseAll()
        {
            CloseParagraph();
            CloseLists();
        }

        private void AddHeading(int level, string text)
        {
            var (size, top) = level switch
            {
                1 => (22.0, 8.0),
                2 => (18.0, 6.0),
                _ => (15.5, 4.0),
            };
            var p = new Paragraph
            {
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, _doc.Blocks.Count == 0 ? 0 : top, 0, 8),
                LineHeight = size * 1.35,
            };
            AddInlines(p.Inlines, text, 0);
            _doc.Blocks.Add(p);
        }

        private void AddRule()
        {
            _doc.Blocks.Add(new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 14),
                BorderBrush = _borderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                FontSize = 2,
                LineHeight = 2,
            });
        }

        private void AddQuote(string text)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(12, 2, 0, 2),
                BorderBrush = _borderBrush,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Foreground = _dimBrush,
            };
            AddInlines(p.Inlines, text, 0);
            _doc.Blocks.Add(p);
        }

        private void AddCodeBlock(string code)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(10, 8, 10, 8),
                Background = _codeBackground,
                FontFamily = _monoFont,
                FontSize = 13,
                LineHeight = 18,
            };
            var lines = code.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                p.Inlines.Add(new Run(lines[i]));
            }
            _doc.Blocks.Add(p);
        }

        private void AddListItem(int indent, bool ordered, string content)
        {
            while (_lists.Count > 0 && _lists[^1].Indent > indent)
                _lists.RemoveAt(_lists.Count - 1);

            // Mismo nivel pero distinto tipo (viñetas ↔ numerada): empieza una lista nueva al lado.
            var wantedMarker = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
            if (_lists.Count > 0 && _lists[^1].Indent == indent && _lists[^1].List.MarkerStyle != wantedMarker)
                _lists.RemoveAt(_lists.Count - 1);

            List list;
            if (_lists.Count == 0 || indent > _lists[^1].Indent)
            {
                list = new List
                {
                    MarkerStyle = wantedMarker,
                    Margin = new Thickness(0, 0, 0, _lists.Count == 0 ? 10 : 2),
                    Padding = new Thickness(22, 0, 0, 0),
                };

                var parentItem = _lists.Count > 0 ? _lists[^1].List.ListItems.LastListItem : null;
                if (parentItem != null) parentItem.Blocks.Add(list);
                else _doc.Blocks.Add(list);

                _lists.Add((indent, list));
            }
            else
            {
                list = _lists[^1].List;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AddInlines(paragraph.Inlines, content.Trim(), 0);
            list.ListItems.Add(new ListItem(paragraph));
            _lastListParagraph = paragraph;
        }

        private void TrimLastMargin()
        {
            if (_doc.Blocks.LastBlock is Block last && last.Margin.Bottom > 0)
                last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        }

        // ───────────── En línea ─────────────

        private void AddInlines(InlineCollection target, string text, int depth)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (depth > MaxInlineDepth)
            {
                target.Add(new Run(text));
                return;
            }

            var sb = new StringBuilder();
            void Flush()
            {
                if (sb.Length == 0) return;
                target.Add(new Run(sb.ToString()));
                sb.Clear();
            }

            // Si un delimitador no tiene cierre a partir de una posición, tampoco lo tendrá más adelante.
            bool noBoldStar = false, noBoldUnderscore = false, noStrike = false, noAngleClose = false;
            // Presupuesto de búsqueda de enlaces: holgado para notas normales, lineal con entradas patológicas.
            int linkBudget = 8 * text.Length + 4 * MaxSpanSearch;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                // Escape: \* \_ \[ ...
                if (c == '\\' && i + 1 < text.Length && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
                {
                    sb.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                // `código` (admite ``varios`` acentos graves)
                if (c == '`')
                {
                    int ticks = CountRun(text, i, '`');
                    var fence = new string('`', ticks);
                    int end = IndexOfLimited(text, fence, i + ticks);
                    if (end >= 0)
                    {
                        Flush();
                        var code = text.Substring(i + ticks, end - i - ticks);
                        if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ') code = code[1..^1];
                        target.Add(new Run(code) { FontFamily = _monoFont, Background = _codeBackground, FontSize = 13 });
                        i = end + ticks;
                        continue;
                    }
                    sb.Append(fence);
                    i += ticks;
                    continue;
                }

                // **negrita** / __negrita__
                if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c
                    && !(c == '*' ? noBoldStar : noBoldUnderscore)
                    && (c == '*' || IsBoundaryBefore(text, i)))
                {
                    var delim = new string(c, 2);
                    int end = IndexOfLimited(text, delim, i + 2);
                    if (end < 0)
                    {
                        if (c == '*') noBoldStar = true; else noBoldUnderscore = true;
                    }
                    else if (end > i + 2 && (c == '*' || IsBoundaryAfter(text, end + 2)))
                    {
                        Flush();
                        var bold = new Bold();
                        AddInlines(bold.Inlines, text.Substring(i + 2, end - i - 2), depth + 1);
                        target.Add(bold);
                        i = end + 2;
                        continue;
                    }
                }

                // *cursiva* / _cursiva_
                if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] != c && !char.IsWhiteSpace(text[i + 1])
                    && (c == '*' || IsBoundaryBefore(text, i)))
                {
                    int end = FindClosingSingle(text, c, i + 1);
                    if (end > i + 1 && !char.IsWhiteSpace(text[end - 1]) && (c == '*' || IsBoundaryAfter(text, end + 1)))
                    {
                        Flush();
                        var italic = new Italic();
                        AddInlines(italic.Inlines, text.Substring(i + 1, end - i - 1), depth + 1);
                        target.Add(italic);
                        i = end + 1;
                        continue;
                    }
                }

                // ~~tachado~~
                if (c == '~' && i + 1 < text.Length && text[i + 1] == '~' && !noStrike)
                {
                    int end = IndexOfLimited(text, "~~", i + 2);
                    if (end < 0)
                    {
                        noStrike = true;
                    }
                    else if (end > i + 2)
                    {
                        Flush();
                        var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                        AddInlines(span.Inlines, text.Substring(i + 2, end - i - 2), depth + 1);
                        target.Add(span);
                        i = end + 2;
                        continue;
                    }
                }

                // [texto](url) y ![alt](url) (la imagen se muestra como enlace con su texto alternativo)
                if (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '['))
                {
                    int start = c == '!' ? i + 1 : i;
                    if (TryParseLink(text, start, ref linkBudget, out var label, out var url, out var next))
                    {
                        Flush();
                        AddLink(target, string.IsNullOrWhiteSpace(label) ? url : label, url, depth);
                        i = next;
                        continue;
                    }
                }

                // <https://...> (búsqueda acotada: una línea llena de "<" sin ">" no puede ser cuadrática)
                if (c == '<' && !noAngleClose && i + 1 < text.Length)
                {
                    int count = Math.Min(text.Length - (i + 1), MaxSpanSearch);
                    int end = text.IndexOf('>', i + 1, count);
                    if (end < 0 && count == text.Length - (i + 1)) noAngleClose = true;
                    if (end > i + 1)
                    {
                        var inner = text.Substring(i + 1, end - i - 1);
                        if (ShellService.IsWebUrl(inner) && !inner.Contains(' '))
                        {
                            Flush();
                            AddLink(target, inner, inner, depth, parseLabel: false);
                            i = end + 1;
                            continue;
                        }
                    }
                }

                // URL suelta
                if ((c == 'h' || c == 'H') && IsBoundaryBefore(text, i) && StartsWithUrl(text, i))
                {
                    int end = i;
                    int limit = Math.Min(text.Length, i + MaxSpanSearch);
                    while (end < limit && !char.IsWhiteSpace(text[end]) && text[end] != '<' && text[end] != '>')
                        end++;
                    end = TrimUrlEnd(text, i, end);
                    var url = text.Substring(i, end - i);
                    if (ShellService.IsWebUrl(url))
                    {
                        Flush();
                        AddLink(target, url, url, depth, parseLabel: false);
                        i = end;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }
            Flush();
        }

        private void AddLink(InlineCollection target, string label, string url, int depth, bool parseLabel = true)
        {
            url = url.Trim();
            if (!ShellService.IsWebUrl(url))
            {
                // Enlaces relativos o con otros esquemas: solo el texto.
                if (parseLabel) AddInlines(target, label, depth + 1);
                else target.Add(new Run(label));
                return;
            }

            var link = new Hyperlink
            {
                NavigateUri = new Uri(url, UriKind.Absolute),
                Foreground = _linkBrush,
                ToolTip = url,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            if (parseLabel) AddInlines(link.Inlines, label, depth + 1);
            else link.Inlines.Add(new Run(label));

            link.Click += (_, _) => ShellService.OpenUrl(url);
            link.RequestNavigate += (_, e) => e.Handled = true; // la navegación la hace Click
            target.Add(link);
        }

        /// <summary>
        /// [texto](url) desde <paramref name="start"/>. <paramref name="budget"/> limita el total de caracteres recorridos
        /// en todo el texto: con miles de "[" sin cerrar deja de buscar enlaces en vez de volverse cuadrático.
        /// </summary>
        private static bool TryParseLink(string text, int start, ref int budget, out string label, out string url, out int next)
        {
            label = url = "";
            next = start;
            if (budget <= 0 || start >= text.Length || text[start] != '[') return false;

            // Etiqueta con corchetes anidados.
            int depth = 0, i = start, limit = Math.Min(text.Length, start + MaxSpanSearch);
            int closeBracket = -1;
            for (; i < limit; i++)
            {
                char c = text[i];
                if (c == '\\') { i++; continue; }
                if (c == '[') depth++;
                else if (c == ']' && --depth == 0) { closeBracket = i; break; }
            }
            budget -= i - start + 1;
            if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(') return false;

            // URL con un nivel de paréntesis anidados.
            int parens = 0, closeParen = -1;
            limit = Math.Min(text.Length, closeBracket + 2 + MaxSpanSearch);
            for (i = closeBracket + 1; i < limit; i++)
            {
                char c = text[i];
                if (c == '(') parens++;
                else if (c == ')' && --parens == 0) { closeParen = i; break; }
            }
            budget -= i - closeBracket;
            if (closeParen < 0) return false;

            label = text.Substring(start + 1, closeBracket - start - 1);
            var target = text.Substring(closeBracket + 2, closeParen - closeBracket - 2).Trim();
            if (target.StartsWith('<') && target.Contains('>')) target = target[1..target.IndexOf('>')];
            var space = target.IndexOf(' ');
            if (space > 0) target = target[..space]; // quita el título opcional
            url = target;
            next = closeParen + 1;
            return true;
        }

        private static int FindClosingSingle(string text, char c, int from)
        {
            int limit = Math.Min(text.Length, from + MaxSpanSearch);
            for (int k = from; k < limit; k++)
            {
                if (text[k] == '\\') { k++; continue; }
                if (text[k] == '`')
                {
                    // No cerrar dentro de un `código`.
                    int ticks = CountRun(text, k, '`');
                    int end = IndexOfLimited(text, new string('`', ticks), k + ticks);
                    if (end < 0) return -1;
                    k = end + ticks - 1;
                    continue;
                }
                if (text[k] != c) continue;
                if (k + 1 < text.Length && text[k + 1] == c) { k++; continue; } // es un ** / __
                return k;
            }
            return -1;
        }

        private static int IndexOfLimited(string text, string value, int from)
        {
            if (from >= text.Length) return -1;
            int count = Math.Min(text.Length - from, MaxSpanSearch);
            return text.IndexOf(value, from, count, StringComparison.Ordinal);
        }

        private static int CountRun(string text, int index, char c)
        {
            int n = 0;
            while (index + n < text.Length && text[index + n] == c) n++;
            return n;
        }

        private static bool IsBoundaryBefore(string text, int index) => index == 0 || !char.IsLetterOrDigit(text[index - 1]);

        private static bool IsBoundaryAfter(string text, int index) => index >= text.Length || !char.IsLetterOrDigit(text[index]);

        private static bool StartsWithUrl(string text, int index)
            => string.Compare(text, index, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0
               || string.Compare(text, index, "https://", 0, 8, StringComparison.OrdinalIgnoreCase) == 0;

        /// <summary>Quita la puntuación final típica de una frase (".", ",", ")" sin pareja…). Lineal.</summary>
        private static int TrimUrlEnd(string text, int start, int end)
        {
            // Paréntesis contados una sola vez y actualizados al recortar (antes era cuadrático con muchos ")").
            int open = 0, close = 0;
            for (int k = start; k < end; k++)
            {
                if (text[k] == '(') open++;
                else if (text[k] == ')') close++;
            }

            while (end > start)
            {
                char last = text[end - 1];
                if (".,;:!?\"'*_~".Contains(last))
                {
                    end--;
                    continue;
                }
                if (last == ')' && close > open)
                {
                    close--;
                    end--;
                    continue;
                }
                break;
            }
            return end;
        }
    }
}
