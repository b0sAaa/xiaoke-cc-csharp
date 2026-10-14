// Claude Code hook：讓小克跟著 Claude Code 一起開關。
//   node hook.js start  —— SessionStart：登記會話，小克沒在跑就把她叫起來
//   node hook.js ping   —— UserPromptSubmit：重新整理會話的「線上」時間（順便兜底拉起小克）
//   node hook.js end    —— SessionEnd：登出會話；最後一個會話結束後小克自己下班
//   node hook.js notify —— Notification／PermissionRequest：Claude 等你批准／回覆時，留一份「待處理」讓小克提醒你
//   node hook.js clear  —— PostToolUse / Stop：你處理完了，清掉「待處理」
// 任何錯誤都靜默退出，絕不影響 Claude Code。
const fs = require('fs')
const path = require('path')
const { spawn } = require('child_process')

const dataDir = path.join(process.env.APPDATA || path.join(require('os').homedir(), 'AppData', 'Roaming'), 'xiaoke-cc')
const sessDir = path.join(dataDir, 'sessions')
const pidFile = path.join(dataDir, 'pid')
const attnDir = path.join(dataDir, 'attention')

function readStdin() {
  try { return JSON.parse(fs.readFileSync(0, 'utf8') || '{}') } catch (e) { return {} }
}

function alive() {
  try {
    const pid = Number(fs.readFileSync(pidFile, 'utf8'))
    if (!pid) return false
    process.kill(pid, 0)
    return true
  } catch (e) {
    return false
  }
}

function launch() {
  // 啟動 C# 版（XiaoKe/）。路徑可用環境變數 XIAOKE_EXE 覆寫
  const cs = path.join(__dirname, 'XiaoKe')
  const exe = [
    process.env.XIAOKE_EXE,
    path.join(cs, 'publish', 'XiaoKe.exe'),
    path.join(cs, 'bin', 'Release', 'net8.0-windows', 'XiaoKe.exe'),
    path.join(cs, 'bin', 'Debug', 'net8.0-windows', 'XiaoKe.exe'),
  ].find(p => p && fs.existsSync(p))
  if (!exe) return // 還沒建置：npm run build
  const child = spawn(exe, [], { detached: true, stdio: 'ignore', windowsHide: false, cwd: path.dirname(exe) })
  child.unref()
}

try {
  const mode = process.argv[2]
  const input = readStdin()
  const id = String(input.session_id || '').replace(/[^\w-]/g, '')
  if (!id) process.exit(0)
  fs.mkdirSync(sessDir, { recursive: true })
  const file = path.join(sessDir, id + '.json')
  const attn = path.join(attnDir, id + '.json')
  if (mode === 'clear') {
    fs.rmSync(attn, { force: true })
  } else if (mode === 'notify') {
    fs.mkdirSync(attnDir, { recursive: true })
    // PermissionRequest 和 Notification 常常幾乎同時來：5 秒內已經有提醒就不重複響
    let fresh = false
    try { fresh = Date.now() - fs.statSync(attn).mtimeMs < 5000 } catch (e) { /* 還沒有 */ }
    if (!fresh) {
      let type = input.notification_type || '', message = input.message || ''
      if (input.hook_event_name === 'PermissionRequest') {
        type = 'permission_prompt'
        const ti = input.tool_input || {}
        const detail = String(ti.command || ti.file_path || ti.url || ti.pattern || '').replace(/\s+/g, ' ').slice(0, 80)
        message = (input.tool_name || '') + (detail ? '：' + detail : '')
      }
      fs.writeFileSync(attn, JSON.stringify({ type, message, cwd: input.cwd || '', t: Date.now() }))
    }
    if (!alive()) launch()
  } else if (mode === 'end') {
    fs.rmSync(file, { force: true })
    fs.rmSync(attn, { force: true })
  } else {
    fs.rmSync(attn, { force: true }) // 使用者送出新訊息 = 已經處理過了
    fs.writeFileSync(file, JSON.stringify({ transcript: input.transcript_path || '', cwd: input.cwd || '', t: Date.now() }))
    if (!alive()) launch()
  }
} catch (e) { /* 靜默 */ }
process.exit(0)
