using System.IO;

namespace SamaDash.Services;

public class AppUsage
{
    public string Path { get; set; } = "";
    /// <summary>Secondi in primo piano per fascia: giorno della settimana * 24 + ora.</summary>
    public double[] Hours { get; set; } = new double[168];
    public int Launches { get; set; }
    public DateTime LastSeen { get; set; }
}

public class UsageData
{
    public DateTime LastDecay { get; set; } = DateTime.Today;
    public Dictionary<string, AppUsage> Apps { get; set; } = [];
}

/// <summary>
/// Impara le abitudini: ogni 5 s registra quale app è in primo piano.
/// Tutto resta in %LOCALAPPDATA%\SamaDash\usage.json.
/// </summary>
public sealed class UsageTracker : IDisposable
{
    const int TickSeconds = 5;
    const string FileName = "usage.json";

    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "samadash.exe", "applicationframehost.exe", "shellexperiencehost.exe",
        "searchhost.exe", "startmenuexperiencehost.exe", "lockapp.exe", "textinputhost.exe",
        "everything.exe",
    };

    static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    readonly object gate = new();
    readonly UsageData data;
    readonly Timer timer;
    int ticksSinceSave;

    public UsageTracker()
    {
        data = Storage.Load<UsageData>(FileName) ?? new UsageData();
        timer = new Timer(_ => Tick(), null, TickSeconds * 1000, TickSeconds * 1000);
    }

    void Tick()
    {
        try
        {
            if (Native.IdleTime() > TimeSpan.FromMinutes(5)) return;
            var exe = Native.ForegroundExe();
            if (exe == null || IsExcluded(exe)) return;

            lock (gate)
            {
                Decay();
                var usage = Get(exe);
                usage.Hours[Slot(DateTime.Now)] += TickSeconds;
                usage.LastSeen = DateTime.Now;
            }
            if (++ticksSinceSave >= 12) Save();
        }
        catch { }
    }

    public void RecordLaunch(string exe)
    {
        if (IsExcluded(exe)) return;
        lock (gate)
        {
            var usage = Get(exe);
            usage.Launches++;
            usage.Hours[Slot(DateTime.Now)] += 60;
            usage.LastSeen = DateTime.Now;
        }
    }

    /// <summary>Exe ordinati per probabilità d'uso adesso.</summary>
    public List<string> Top(int count)
    {
        var now = DateTime.Now;
        int today = (int)now.DayOfWeek;
        lock (gate)
        {
            return data.Apps.Values
                .Where(u => File.Exists(u.Path))
                .Select(u =>
                {
                    double score = 0;
                    for (int d = -1; d <= 1; d++)
                    {
                        int hour = (now.Hour + d + 24) % 24;
                        double weight = d == 0 ? 1.5 : 1.0;
                        for (int day = 0; day < 7; day++)
                            score += u.Hours[day * 24 + hour] * weight * (day == today ? 1.0 : 0.3);
                    }
                    score += u.Hours.Sum() * 0.02 + u.Launches * 30;
                    return (u.Path, score);
                })
                .OrderByDescending(x => x.score)
                .Take(count)
                .Select(x => x.Path)
                .ToList();
        }
    }

    public void Save()
    {
        ticksSinceSave = 0;
        lock (gate)
        {
            try { Storage.Save(FileName, data); } catch { }
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        Save();
    }

    AppUsage Get(string exe)
    {
        var key = exe.ToLowerInvariant();
        if (!data.Apps.TryGetValue(key, out var usage))
            data.Apps[key] = usage = new AppUsage { Path = exe };
        return usage;
    }

    /// <summary>Le abitudini vecchie pesano meno: -5% al giorno.</summary>
    void Decay()
    {
        int days = (DateTime.Today - data.LastDecay).Days;
        if (days <= 0) return;
        double factor = Math.Pow(0.95, days);
        foreach (var usage in data.Apps.Values)
            for (int i = 0; i < usage.Hours.Length; i++)
                usage.Hours[i] *= factor;

        foreach (var key in data.Apps.Where(kv => kv.Value.LastSeen < DateTime.Now.AddDays(-60)).Select(kv => kv.Key).ToList())
            data.Apps.Remove(key);
        data.LastDecay = DateTime.Today;
    }

    static int Slot(DateTime t) => (int)t.DayOfWeek * 24 + t.Hour;

    static bool IsExcluded(string exe) =>
        Excluded.Contains(System.IO.Path.GetFileName(exe)) ||
        (exe.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase) &&
         !exe.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase));
}
