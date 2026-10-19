using System.Text;
using System.Windows.Documents;

namespace PalitoLauncher.UI.Tests.TestHelpers;

/// <summary>Recorre un FlowDocument generado por MarkdownRenderer (bloques, listas anidadas e inlines).</summary>
internal static class FlowDocs
{
    public static IEnumerable<Block> AllBlocks(FlowDocument doc) => Walk(doc.Blocks);

    public static IEnumerable<Inline> AllInlines(FlowDocument doc)
        => AllBlocks(doc).OfType<Paragraph>().SelectMany(p => WalkInlines(p.Inlines));

    public static IEnumerable<Hyperlink> Links(FlowDocument doc) => AllInlines(doc).OfType<Hyperlink>();

    /// <summary>Texto visible: un párrafo por línea, LineBreak = "\n".</summary>
    public static string Text(FlowDocument doc)
        => string.Join("\n", AllBlocks(doc).OfType<Paragraph>().Select(Text));

    public static string Text(Paragraph p) => Text(p.Inlines);

    public static string Text(InlineCollection inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run: sb.Append(run.Text); break;
                case LineBreak: sb.Append('\n'); break;
                case Span span: sb.Append(Text(span.Inlines)); break;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<Block> Walk(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            if (block is List list)
            {
                foreach (var item in list.ListItems)
                    foreach (var child in Walk(item.Blocks))
                        yield return child;
            }
            else if (block is Section section)
            {
                foreach (var child in Walk(section.Blocks))
                    yield return child;
            }
        }
    }

    private static IEnumerable<Inline> WalkInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var child in WalkInlines(span.Inlines))
                    yield return child;
        }
    }
}
