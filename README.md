# 小克桌寵 · Claude 桌面端本體版

桌面右下角的一隻小克。

> 繁體中文版，基於 [Hatk3451/xiaoke-cc](https://github.com/Hatk3451/xiaoke-cc) 與 [aklnaaw/dsh-xiaoke-widget](https://github.com/aklnaaw/dsh-xiaoke-widget)。

跟著 **Claude 桌面端 / Claude Code** 一起開關的桌寵。

## 能看什麼

腳下一根 **Weekly · all models** 量槽，顯示 Claude 官方的每週額度；每輪對話結束冒個泡，告訴你這輪花了多少；點量槽能看今日和近 7 天的賬本（按 API 價折算，單位美元）。

## 怎麼操作

| 動作 | 效果 |
|---|---|
| 單擊小克 | 戳一下（有音效） |
| 雙擊小克 | 彈出輸入框，Enter 後交給 Claude 桌面版開新對話 |
| 拖曳小克 | 移動位置 |
| 點量槽 | 重新讀官方額度，並開關賬本氣泡 |
| 滑鼠停在量槽上 | 看詳細數字；讀不到時會顯示原因 |
| 右鍵 | 選單（見下） |
| 點系統匣圖示 | 戳一下；右鍵一樣是選單 |

**右鍵選單**：看看賬本、問 Claude…、讀取官方額度（開關）、顯示每週量槽（開關）、顯示 5 小時量槽（開關）、量槽顯示方式（已用／剩餘）、戳一下、大小、音效（開關，管所有聲音）、每輪結束冒泡（開關）、跟著 Claude Code 開關、開機自啟、編輯臺詞、編輯價格、重新載入臺詞和價格、回到右下角、退出。

## 官方額度（要連線才抓得到）

量槽的數字**只來自 Claude 官方**。抓不到的時候量槽會直接寫「抓不到」（關閉讀取時寫「未啟用」），原因會顯示在量槽的提示裡。

做法是用 Windows ConPTY 在背景開一個隱藏的 `claude`、送 `/usage`、讀畫面上的百分比後關掉：不花 token，啟動時、每輪結束後（至少隔 5 分鐘）和點量槽時各讀一次。需要先完成[安裝](#安裝)的第 2、3 步（登入命令列、信任資料夾）；官方端點偶爾會限流（rate limited），稍後再點量槽重讀即可。

每週量槽和 5 小時量槽可以各自開關（兩條都關就不顯示量槽，只剩小克）；「讀取官方額度」可整個關掉。量槽只顯示百分比，不顯示重置時間。賬本（今日、近 7 天花費）讀的是本機對話記錄，不需要連線，但只算得到本機 Claude Code 的用量，不含網頁、手機等。

## 安裝

需要 Windows、[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（建置另需 .NET 8 SDK）和 Node.js 18+（只用來跑 hook 和下載素材）。

**1. 下載並建置**

```bash
git clone https://github.com/b0sAaa/xiaoke-cc-csharp.git
cd xiaoke-cc-csharp
npm run assets        # 下載角色圖和音效（不在倉庫裡）
npm run build         # dotnet publish → XiaoKe/publish/XiaoKe.exe
npm run hooks:install # 讓小克跟著 Claude Code 開關
```

**2. 登入 Claude Code 命令列**（量槽要靠它才抓得到官方額度）

```bash
claude
/login
```

桌面版的登入和命令列是兩份，只登入桌面版不夠；命令列那份過期了也會讀不到，重新 `/login` 即可。

**3. 在剛才的資料夾裡開一次 `claude` 並選「信任」**

還在 `xiaoke-cc-csharp` 資料夾裡執行 `claude`，看到「信任這個資料夾嗎」選信任，然後退出。小克在背景讀額度時就是在這個資料夾裡開 `claude`，沒信任過會卡在確認畫面，量槽顯示「抓不到」，提示裡寫 `untrusted`。

**4. 開個 Claude Code 對話，小克就出來了。**

跳過第 2、3 步小克照常運作（花費氣泡、賬本、戳一下都正常），只有量槽會顯示「抓不到」。

## 致謝

小克的立繪、臺詞、音效全是原作者的功勞，這個軟體只是讓Claude換了個應用。
原作者 **aklnaaw**，再往上是 **MeteorNOX** 的小鯨魚掛件。
Claude 桌面端本體版由 **Hatk3451** 製作，本倉庫為繁體中文版，並精簡了部分功能。

立繪和音效原作者宣告不授予再分發，所以本倉庫不附帶，`npm run assets` 時從原倉庫下載，僅供個人使用。
程式碼 MIT，見 [LICENSE](LICENSE)。
