using System.Net.Http;
using System.Text.Json;

namespace SamaDash.Services;

/// <summary>Conversione valute con i tassi BCE di api.frankfurter.app, salvati per 12 ore.</summary>
public static class Currency
{
    const string FileName = "rates.json";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    static RateTable? cache;

    public class RateTable
    {
        public DateTime Fetched { get; set; }
        public string Date { get; set; } = "";
        /// <summary>Quante unità di ogni valuta vale 1 EUR.</summary>
        public Dictionary<string, double> PerEur { get; set; } = [];
    }

    /// <summary>Es. "20 eur in usd" → "21,6 USD". Null se non è una conversione di valuta.</summary>
    public static async Task<(string Value, string Detail)?> TryConvertAsync(string input, CancellationToken token)
    {
        var match = Calculator.ConversionPattern().Match(input);
        if (!match.Success) return null;
        string from = match.Groups[2].Value.ToUpperInvariant(), to = match.Groups[3].Value.ToUpperInvariant();
        if (from.Length != 3 || to.Length != 3) return null;
        if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double amount))
            return null;

        var rates = await GetRatesAsync(token);
        if (rates == null || !rates.PerEur.TryGetValue(from, out double a) || !rates.PerEur.TryGetValue(to, out double b))
            return null;

        double value = amount / a * b;
        return ($"{Calculator.Format(Math.Round(value, 2))} {to}", $"Tasso BCE del {rates.Date}");
    }

    static async Task<RateTable?> GetRatesAsync(CancellationToken token)
    {
        cache ??= Storage.Load<RateTable>(FileName);
        if (cache != null && DateTime.Now - cache.Fetched < TimeSpan.FromHours(12)) return cache;

        try
        {
            var json = await Http.GetStringAsync("https://api.frankfurter.app/latest", token);
            using var doc = JsonDocument.Parse(json);
            var table = new RateTable
            {
                Fetched = DateTime.Now,
                Date = doc.RootElement.GetProperty("date").GetString() ?? "",
                PerEur = { ["EUR"] = 1 },
            };
            foreach (var rate in doc.RootElement.GetProperty("rates").EnumerateObject())
                table.PerEur[rate.Name] = rate.Value.GetDouble();
            Storage.Save(FileName, table);
            return cache = table;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return cache; // senza rete: ultimo tasso salvato
        }
    }
}
