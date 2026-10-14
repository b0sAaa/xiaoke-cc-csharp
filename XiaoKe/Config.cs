using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XiaoKe;

/// <summary>固定路徑（hook.js 也寫這裡）。</summary>
static class Paths
{
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xiaoke-cc");
    public static readonly string ConfigFile = Path.Combine(Data, "config.json");
    public static readonly string PidFile = Path.Combine(Data, "pid");
    public static readonly string Sessions = Path.Combine(Data, "sessions");
    public static readonly string Attention = Path.Combine(Data, "attention");
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

/// <summary>config.json。</summary>
sealed class Config
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double Scale { get; set; } = 1;
    public bool Sound { get; set; } = true;
    public double Volume { get; set; } = 0.5;
    public bool TurnBubble { get; set; } = true;
    public bool AttentionBubble { get; set; } = true;
    // 吸附螢幕左右邊緣：靠近就貼齊，貼左邊時角色水平翻轉（臉朝向螢幕內）
    public bool SnapEdge { get; set; } = true;
    public double SnapZone { get; set; } = 40; // 距離邊緣多少 DIP 內會吸附
    public string? SnapSide { get; set; }      // "left" / "right" / null（目前沒貼著邊緣）
    public bool Mirrored { get; set; }         // 角色是否翻轉：吸左邊變 true、吸右邊變 false，拉開不變
    // 每日預算提醒：預設關閉；要「打勾」且金額大於 0 才會檢查
    public bool BudgetAlert { get; set; }
    public double DailyBudget { get; set; }
    public string? BudgetAlertedKey { get; set; } // 「日期@預算」，同一天同一個預算只提醒一次
    public double TurnSeconds { get; set; } = 8;
    public bool FollowClaude { get; set; } = true;
    public bool UseReal { get; set; } = true;
    public bool ShowWeek { get; set; } = true;
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
    /// <summary>目前仍有效的官方數字；沒有就回傳 null，量槽顯示「抓不到」。</summary>
    OfficialQuota? Current();
}

