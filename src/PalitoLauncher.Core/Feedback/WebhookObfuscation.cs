using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Core.Feedback;

/// <summary>
/// Webhooks de Discord «ofuscados» para los JSON públicos del repo del juego (feedback.json, launcher.json, missions.json):
/// <c>pw1:&lt;base64url&gt;</c> en vez de la URL. Hay bots que recorren GitHub buscando el texto
/// «discord.com/api/webhooks/» para mandar spam; con esto ese texto no aparece en el repo.
///
/// NO es cifrado de verdad: la clave va dentro del launcher (y su código es público), así que quien quiera puede
/// descifrarlo a mano. Sirve contra los rastreadores automáticos, no contra alguien que os ataque a propósito; para eso
/// hace falta un servidor intermedio que guarde los webhooks (pendiente).
///
/// Formato: "pw1:" + base64url(nonce[8] ‖ texto XOR flujo ‖ etiqueta[6]); flujo = SHA-256(clave ‖ nonce ‖ bloque) por
/// bloques de 32 bytes; etiqueta = HMAC-SHA256(clave, nonce ‖ texto cifrado)[0..6] (detecta valores copiados a medias).
/// Cada vez que se ofusca sale un valor distinto (nonce aleatorio), y todos descifran a la misma URL.
/// </summary>
public static class WebhookObfuscation
{
    public const string Prefix = "pw1:";

    private const int NonceLength = 8;
    private const int TagLength = 6;
    private const int MaxEncodedLength = 4096;

    // Clave fija del formato pw1. Cambiarla invalida todos los valores pw1 publicados (habría que generar un pw2).
    private static readonly byte[] Key = Convert.FromHexString("0d0620d141285de978090d0594bea4aca055028dbef6c0836045a487bb5bf12c");

    private static readonly object WarnLock = new();
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

    /// <summary>¿Empieza por «pw1:»? (no comprueba que sea válido).</summary>
    public static bool IsObfuscated(string? text)
        => text is not null && text.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ofusca un webhook de Discord. Acepta la URL tal cual la copia Discord (o un pw1 ya hecho, que se rehace). Lanza
    /// ArgumentException si no es un webhook de Discord válido.
    /// </summary>
    public static string Encode(string webhookUrl)
    {
        var plain = IsObfuscated(webhookUrl) ? TryDecode(webhookUrl) : webhookUrl?.Trim();
        var normalized = SurveyProvider.NormalizeDiscordWebhook(plain);
        if (normalized is null) throw new ArgumentException("No es un webhook de Discord válido (https://discord.com/api/webhooks/…).", nameof(webhookUrl));

        var data = Encoding.UTF8.GetBytes(normalized);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipher = Xor(data, nonce);
        var tag = Tag(nonce, cipher);

        var all = new byte[NonceLength + cipher.Length + TagLength];
        nonce.CopyTo(all, 0);
        cipher.CopyTo(all, NonceLength);
        tag.CopyTo(all, NonceLength + cipher.Length);
        return Prefix + ToBase64Url(all);
    }

    /// <summary>La URL de un valor «pw1:…», o null si no lo es o está dañado (copiado a medias, editado…). Nunca lanza.</summary>
    public static string? TryDecode(string? text)
    {
        try
        {
            if (!IsObfuscated(text)) return null;
            var body = text!.Trim()[Prefix.Length..];
            if (body.Length == 0 || body.Length > MaxEncodedLength) return null;
            var all = FromBase64Url(body);
            if (all is null || all.Length <= NonceLength + TagLength) return null;

            var nonce = all.AsSpan(0, NonceLength).ToArray();
            var cipher = all.AsSpan(NonceLength, all.Length - NonceLength - TagLength).ToArray();
            var tag = all.AsSpan(all.Length - TagLength).ToArray();
            if (!CryptographicOperations.FixedTimeEquals(tag, Tag(nonce, cipher))) return null;

            return Encoding.UTF8.GetString(Xor(cipher, nonce));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Avisa (una vez por archivo) si <paramref name="value"/> es un webhook en texto plano: en un repo público lo
    /// encuentran los bots de spam. No cambia nada: el webhook se sigue usando.
    /// </summary>
    public static void WarnIfPlain(string? value, string fileName)
    {
        if (string.IsNullOrWhiteSpace(value) || IsObfuscated(value)) return;
        if (!value.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase)) return;
        lock (WarnLock)
        {
            if (!Warned.Add(fileName)) return;
        }
        Log.Warn($"El webhook de {fileName} está en texto plano: en un repo público lo encuentran bots que mandan spam. " +
                 "Ofúscalo en Ajustes › Herramientas de desarrollo › «Ofuscar un webhook» y pega el valor pw1:… en su lugar " +
                 "(docs/remoto/FEEDBACK.md, «Seguridad del webhook»).");
    }

    // ───────────────────────────── Interno ─────────────────────────────

    private static byte[] Xor(byte[] data, byte[] nonce)
    {
        var result = new byte[data.Length];
        Span<byte> block = stackalloc byte[32];
        var input = new byte[Key.Length + NonceLength + 4];
        Key.CopyTo(input, 0);
        nonce.CopyTo(input, Key.Length);

        for (int offset = 0, counter = 0; offset < data.Length; offset += 32, counter++)
        {
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(Key.Length + NonceLength), counter);
            SHA256.HashData(input, block);
            var count = Math.Min(32, data.Length - offset);
            for (var i = 0; i < count; i++) result[offset + i] = (byte)(data[offset + i] ^ block[i]);
        }
        return result;
    }

    private static byte[] Tag(byte[] nonce, byte[] cipher)
    {
        var message = new byte[nonce.Length + cipher.Length];
        nonce.CopyTo(message, 0);
        cipher.CopyTo(message, nonce.Length);
        return HMACSHA256.HashData(Key, message).AsSpan(0, TagLength).ToArray();
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text)
    {
        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        }
        var b64 = text.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            case 1: return null;
        }
        return Convert.FromBase64String(b64);
    }
}
