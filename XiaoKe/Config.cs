using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XiaoKe;

/// <summary>固定路徑。資料夾與 Electron 版的 userData 相同（hook.js 也寫這裡）。</summary>
static class Paths
{
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xiaoke-cc");
    public static readonly string ConfigFile = Path.Combine(Data, "config.json");
    public static readonly string PidFile = Path.Combine(Data, "pid");
    public static readonly string Sessions = Path.Combine(Data, "sessions");
    public static readonly string UsageCache = Path.Combine(Data, "usage-cache.json");
    public static readonly string ErrorLog = Path.Combine(Data, "xiaoke-cs-error.log");
    public static readonly string ClaudeProjects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static readonly string AppDir = AppContext.BaseDirectory;
    public static string LinesFile => Path.Combine(AppDir, "lines.json");
    public static string PricingFile => Path.Combine(AppDir, "pricing.json");

    /// <summary>找 assets 資料夾：先找輸出目錄旁，找不到再往上層找（例如直接從 bin\Release 執行）。</summary>
    public static string? FindAssets()
    {
        var dir = new DirectoryInfo(AppDir);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "assets");
            if (File.Exists(Path.Combine(p, "xiaoke1.png"))) return p;
        }
        return null;
    }
}

/// <summary>config.json：欄位名稱與 Electron 版完全相同。</summary>
sealed class Config
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double Scale { get; set; } = 1;
    public bool Sound { get; set; } = true;
    public double Volume { get; set; } = 0.5;
    public bool TurnBubble { get; set; } = true;
    public bool TurnSound { get; set; } = true;
    public double TurnSeconds { get; set; } = 8;
    public string Currency { get; set; } = "USD";
    public double CnyRate { get; set; } = 7.1;
    public bool FollowClaude { get; set; } = true;
    public int ResetDay { get; set; } = 4;
    public int ResetHour { get; set; } = 1;
    public double? WeeklyBudget { get; set; }
    public double? CalibrateUsedPct { get; set; }
    public bool UseReal { get; set; } = true;
    public bool ShowFive { get; set; }
    public bool ShowUsed { get; set; } = true;

    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static Config Load(string? file = null)
    {
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(file ?? Paths.ConfigFile), Opts) ?? new Config(); }
        catch { return new Config(); } // 首次啟動或檔案壞了
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* ignore */ }
    }

    /// <summary>最近一次重置時刻（本地時間 resetDay 星期幾 resetHour 點），毫秒時間戳。</summary>
    public long PeriodStart(DateTime? nowLocal = null)
    {
        var now = nowLocal ?? DateTime.Now;
        var d = now.Date.AddHours(ResetHour);
        d = d.AddDays(-(((int)d.DayOfWeek - ResetDay + 7) % 7));
        if (d > now) d = d.AddDays(-7);
        return new DateTimeOffset(d).ToUnixTimeMilliseconds();
    }
}

static class Log
{
    public static void Info(string msg)
    {
        try { Directory.CreateDirectory(Paths.Data); File.AppendAllText(Paths.ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [info] {msg}{Environment.NewLine}"); } catch { /* ignore */ }
    }

    public static void Error(string where, Exception e)
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.AppendAllText(Paths.ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {e}\n");
        }
        catch { /* ignore */ }
    }
}

/// <summary>官方額度（Claude Code /usage），由 OfficialUsage.cs 的 ProbeOfficialUsage 提供。</summary>
sealed record QuotaWindow(double Used, long ResetsAt);
sealed record OfficialQuota(QuotaWindow? Week, QuotaWindow? Five);

interface IOfficialUsage
{
    /// <summary>目前仍有效的官方數字；沒有就回傳 null，畫面退回「估算 + 點量槽校準」。</summary>
    OfficialQuota? Current();
}

