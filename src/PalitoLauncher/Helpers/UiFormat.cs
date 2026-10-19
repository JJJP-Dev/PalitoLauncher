using System.Globalization;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Helpers;

/// <summary>
/// Formatos de texto para la UI. Delegan en FileSystemUtil (misma presentación que el Core) y, si algo falla, usan un
/// formato local equivalente para que un error de formato nunca rompa la pantalla.
/// </summary>
public static class UiFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Bytes(long bytes)
    {
        try { return FileSystemUtil.FormatBytes(bytes); }
        catch (Exception ex)
        {
            Log.Warn("FormatBytes falló; se usa el formato local.", ex);
            return FallbackBytes(bytes);
        }
    }

    public static string Speed(double bytesPerSecond)
    {
        try { return FileSystemUtil.FormatSpeed(bytesPerSecond); }
        catch (Exception ex)
        {
            Log.Warn("FormatSpeed falló; se usa el formato local.", ex);
            return bytesPerSecond <= 0 ? "—" : FallbackBytes((long)bytesPerSecond) + "/s";
        }
    }

    public static string Duration(TimeSpan duration)
    {
        try { return FileSystemUtil.FormatDuration(duration); }
        catch (Exception ex)
        {
            Log.Warn("FormatDuration falló; se usa el formato local.", ex);
            if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours} h {duration.Minutes:00} min";
            if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes} min {duration.Seconds:00} s";
            return $"{Math.Max(0, (int)Math.Ceiling(duration.TotalSeconds))} s";
        }
    }

    /// <summary>Porcentaje entero a la baja ("45"), para textos como "CONTINUAR (45 %)".</summary>
    public static string Percent(double? fraction)
    {
        var f = Math.Clamp(fraction ?? 0, 0, 1);
        return ((int)Math.Floor(f * 100)).ToString(Inv);
    }

    /// <summary>Fecha corta local "dd/MM/yyyy".</summary>
    public static string Date(DateTimeOffset? value)
        => value is { } v ? v.ToLocalTime().ToString("dd/MM/yyyy", Inv) : "";

    /// <summary>Fecha y hora corta local "dd/MM HH:mm".</summary>
    public static string ShortDateTime(DateTimeOffset? value)
        => value is { } v ? v.ToLocalTime().ToString("dd/MM HH:mm", Inv) : "";

    /// <summary>Hora local "HH:mm".</summary>
    public static string Time(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm", Inv);

    private static string FallbackBytes(long bytes)
    {
        if (bytes < 1024) return $"{Math.Max(0, bytes)} B";
        double kb = bytes / 1024d;
        if (kb < 1024) return kb.ToString("0.0", Inv) + " KB";
        double mb = kb / 1024d;
        if (mb < 1024) return mb.ToString("0.0", Inv) + " MB";
        return (mb / 1024d).ToString("0.00", Inv) + " GB";
    }
}
