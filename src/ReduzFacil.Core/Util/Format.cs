using System.Globalization;

namespace ReduzFacil.Core.Util;

/// <summary>Textos de tamanho, porcentagem e tempo no padrão brasileiro (vírgula decimal).</summary>
public static class Format
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        var text = i == 0 || v >= 100 ? v.ToString("0", PtBr) : v.ToString("0.0", PtBr);
        return $"{text} {Units[i]}";
    }

    /// <summary>Economia em %, de 0 a 100 (nunca negativa).</summary>
    public static int SavingPercent(long original, long final)
    {
        if (original <= 0) return 0;
        return (int)Math.Max(0, Math.Round((1 - (double)final / original) * 100));
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>Lê "1:23", "01:02:03", "83", "83,5" ou "1:23.5" como tempo. Vazio = null.</summary>
    public static bool TryParseTime(string? text, out TimeSpan? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0) return false;
            total = total * 60 + n;
        }
        value = TimeSpan.FromSeconds(total);
        return true;
    }

    public static string Number(double value, int decimals = 1) =>
        value.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), PtBr);

    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(text.Trim().Replace('.', ','), NumberStyles.Float, PtBr, out value);
    }
}
