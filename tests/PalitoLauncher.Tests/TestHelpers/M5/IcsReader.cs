using System.Text;

namespace PalitoLauncher.Tests.TestHelpers.M5;

/// <summary>Lector mínimo de iCalendar (RFC 5545) para comprobar lo que genera ManifestLogic.BuildIcs.</summary>
internal static class IcsReader
{
    /// <summary>Líneas físicas (separadas por CRLF). Falla si hay un CR o LF suelto o si no termina en CRLF.</summary>
    public static IReadOnlyList<string> PhysicalLines(string ics)
    {
        Assert.EndsWith("\r\n", ics);
        var body = ics[..^2];
        var lines = body.Split("\r\n");
        foreach (var line in lines)
        {
            Assert.DoesNotContain('\r', line);
            Assert.DoesNotContain('\n', line);
        }
        return lines;
    }

    /// <summary>Líneas lógicas: deshace el plegado (CRLF seguido de un espacio o tabulador).</summary>
    public static IReadOnlyList<string> LogicalLines(string ics)
    {
        var result = new List<string>();
        foreach (var line in PhysicalLines(ics))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                Assert.NotEmpty(result);
                result[^1] += line[1..];
            }
            else
            {
                result.Add(line);
            }
        }
        return result;
    }

    /// <summary>(nombre, valor) de cada línea lógica; el valor sin desescapar.</summary>
    public static IReadOnlyList<(string Name, string Value)> Properties(string ics)
        => LogicalLines(ics).Select(l =>
        {
            var colon = l.IndexOf(':');
            Assert.True(colon > 0, $"Línea sin ':' → {l}");
            return (l[..colon], l[(colon + 1)..]);
        }).ToList();

    /// <summary>Valor (sin desescapar) de la única propiedad con ese nombre dentro del bloque indicado (VEVENT por defecto).</summary>
    public static string? Get(string ics, string name, string block = "VEVENT")
    {
        string? value = null;
        var stack = new Stack<string>();
        foreach (var (n, v) in Properties(ics))
        {
            if (n == "BEGIN") { stack.Push(v); continue; }
            if (n == "END") { stack.Pop(); continue; }
            if (n != name || stack.Count == 0 || stack.Peek() != block) continue;
            Assert.Null(value); // no repetida
            value = v;
        }
        return value;
    }

    /// <summary>Desescapa un valor TEXT: \\ \; \, \n \N.</summary>
    public static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\')
            {
                // En TEXT, ';' y ',' sin escapar no son válidos.
                Assert.False(c is ';' or ',', $"Carácter sin escapar en «{value}»");
                sb.Append(c);
                continue;
            }
            Assert.True(i + 1 < value.Length, "Barra invertida final");
            var next = value[++i];
            sb.Append(next switch
            {
                '\\' => '\\',
                ';' => ';',
                ',' => ',',
                'n' or 'N' => '\n',
                _ => throw new Xunit.Sdk.XunitException($"Escape no válido \\{next} en «{value}»"),
            });
        }
        return sb.ToString();
    }

    /// <summary>Comprueba que BEGIN/END están bien anidados y devuelve los bloques en el orden en que se abren.</summary>
    public static IReadOnlyList<string> Blocks(string ics)
    {
        var opened = new List<string>();
        var stack = new Stack<string>();
        foreach (var (n, v) in Properties(ics))
        {
            if (n == "BEGIN")
            {
                stack.Push(v);
                opened.Add(v);
            }
            else if (n == "END")
            {
                Assert.NotEmpty(stack);
                Assert.Equal(stack.Pop(), v);
            }
            else
            {
                Assert.NotEmpty(stack); // ninguna propiedad fuera de VCALENDAR
            }
        }
        Assert.Empty(stack);
        return opened;
    }
}
