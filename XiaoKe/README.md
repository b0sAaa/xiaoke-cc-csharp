# 小克 · C# / WPF 版

Electron 版的 .NET 8 WPF 改寫（分支 `csharp-rewrite`）。功能、設定檔（`%APPDATA%\xiaoke-cc\config.json`）、
用量快取（`usage-cache.json`）、hook 協定（`sessions\`、`pid`）都與 Electron 版相容，兩個版本不要同時開。

## 需求
- Windows 10/11、.NET 8 Desktop Runtime（framework-dependent，不內含執行階段）
- 上層資料夾的 `assets/`（角色圖與音效，原作者不授權再分發，所以不在 git 裡）。沒有的話先在上層執行 `npm run assets`

## 建置 / 發行
```
dotnet build XiaoKe/XiaoKe.csproj -c Release
dotnet publish XiaoKe/XiaoKe.csproj -c Release --no-self-contained -o XiaoKe/publish
```
建置時會把 `../assets`、`../lines.json`、`../pricing.json` 複製到輸出目錄；「編輯臺詞 / 價格」開的是輸出目錄裡那份。
`hook.js` 會依序找 `XIAOKE_EXE`、`XiaoKe/publish/XiaoKe.exe`、`XiaoKe/bin/Release/...`，找不到才退回 Electron。

## 開發用開關
- `XiaoKe.exe --selftest [輸出資料夾]`：依序截圖待機 / 戳一下 / 賬本 / 一輪結束 / 校準 / 快速輸入框後退出
- `XiaoKe.exe --verify-usage 輸出.json [--root 目錄] [--cache 快取檔] [--noscan]`：用正式版的用量邏輯算一次統計，供和 `usage.js` 對照
- 環境變數 `XK_MEMLOG=檔案`：每 5 秒記錄記憶體

## 官方額度（/usage）
用 Windows ConPTY 開一個隱藏的 claude（停用 hook）、送 `/usage`、解析畫面，不花 token。啟動時、每輪結束後（至少隔 5 分鐘）、點量槽時各讀一次；
失敗原因（未信任資料夾、未登入、官方端點限流 rate limited…）會顯示在量槽的提示裡，並退回「估算 + 點量槽校準」。
- 若出現「untrusted」：在專案資料夾（含 hook.js 那層）手動開一次 claude 並信任；也可用環境變數 `XIAOKE_PROBE_CWD` 指定資料夾。
- `XiaoKe.exe --probe-once 輸出.json [--text 畫面文字檔]`：真的讀一次，或只測解析器。
