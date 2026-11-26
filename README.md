# KeyTrace

本地记录真实打字，找出打字训练器看不到的短板，再据此生成练习材料。Windows 上用 Raw Input 采全局击键，用 UI Automation 读上屏文字。

KeyTrace **100% 独立可用**：自带原生桌面实时窗口、系统托盘控制、零外链离线 HTML 统计看板与单文件双拼练习页，无需后端或联网。如需浏览器内的自适应键盘训练体验，也可按需选配独立的配套网页项目 **[keytrace-web](https://github.com/428581985-cyber/keytrace-web)**（AGPL-3.0 许可，通过在 `config.json` 中填写 `webPracticeUrl` 接入）。

> **隐私与安全声明**
> 1. 本工具会记录你的击键，**全部数据只存在本地、不联网、不上传**。默认不自启，且内置密码管理器/银行客户端排除名单与 `--no-text` 模式。
> 2. 它的行为特征接近键盘记录器，杀软可能会误报拦截；源码完全公开，你可以自己编译与审计。

## 项目状态

- **已实现**：
  - 全局采集（Raw Input + UIA 文本差分 + 托盘/热键控制 + 日志按天轮转压缩）与 Windows 双拼方案自动识别（内置及自定义槽位）
  - 原生桌面实时窗口（今日击键计数、24 小时分时段柱图、可选悬浮状态条）与托盘一键打开本地离线统计看板（`查看统计看板`）
  - 归因分析（`stats.json` / `typos.jsonl`、拼音层与选字层分开、确认纠错闸门、准确率护栏）与盲标导出（`label`）
  - 多格式导出（`keybr` / `csv` / `json`）、中文双拼与编程符号练习材料生成（`words`）、零外链离线看板（`dashboard`）、单文件双拼练习页及成绩回流自适应推荐（`drill` + `drill --record`）
  - `config.json` 配置化（`%USERPROFILE%\.keytrace\config.json`，含可选 `webPracticeUrl`）、v0.1 Release 打包脚本（`scripts/package-release.ps1`）与 GitHub Actions CI / Release 工作流
- **后续计划 / 待补齐**：
  - 200 条人工金标标定与公开样本集（`testdata/labeled/`）

## 为什么

keybr 这类工具只在它自己的练习题里测你。真实工作中打什么、错在哪、哪些键其实按得吃力，它不知道，也不练中文双拼、数字和符号。

我自己的例子：练了 12 天，准确率 92%，`z` 键错 17%；真实输入里退格占 9%，中间还有不少隔了近一秒的停顿。看不见这些，就不知道练什么。

## 下载与打包

### 从 GitHub Releases 下载

前往本仓库 Releases 页面下载 `KeyTrace-<version>-win-x64.zip` 与 `SHA256SUMS.txt`（单文件自包含构建，已内置 .NET 8 运行时与字表/图标资源，无需预先安装 .NET SDK）：

```powershell
# 校验压缩包哈希
Get-FileHash .\KeyTrace-v0.1.0-win-x64.zip -Algorithm SHA256
```

解压后即可直接使用：
- 终端运行 `keytrace run` 启动常驻录制（托盘左键打开主窗口，右键「查看统计看板」生成并打开离线 HTML 报告），或直接双击 `keytrace.exe` 打开桌面窗口。
- 如需开机以最高权限自启（避免错过管理员权限窗口的击键），运行解压目录中的 `scripts\install-task.bat`。

### 本地打包

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-release.ps1 -Version v0.1.0
```

默认输出 `dist/KeyTrace-v0.1.0-win-x64.zip` 与 `dist/SHA256SUMS.txt`（可用 `-Runtime` 指定目标 RID，默认 `win-x64`；不传 `-Version` 时默认 `dev`）。

### 可选配置（`config.json`）

配置文件位于 `%USERPROFILE%\.keytrace\config.json`，默认无需手动创建，缺失或损坏时自动回退默认值：

```json
{
  "showMiniHud": false,
  "captureText": true,
  "imeOverride": null,
  "extraExclusions": [],
  "webPracticeUrl": null
}
```

- `showMiniHud`：是否启用桌面迷你悬浮状态条（也可在托盘菜单直接切换）。
- `captureText`：是否采集 UIA 上屏文字；设为 `false` 等同 `--no-text` 纯键码模式。
- `imeOverride`：手动指定输入法方案（如 `"shuangpin/xiaohe"`、`"full-pinyin"`）；留空或 `"auto"` 时自动读取 Windows 注册表识别。
- `extraExclusions`：额外排除采集的进程名列表（如 `["my-vault.exe"]`），与内置密码管理器排除名单叠加生效。
- `webPracticeUrl`：可选配套网页训练器地址（例如本地或自部署的 **[keytrace-web](https://github.com/428581985-cyber/keytrace-web)**）；默认留空，仅在配置非空地址时才会在托盘菜单显示「打开网页练习」入口。

## 用法

```bash
keytrace run                         # 常驻录制，带托盘图标（可一键查看离线统计看板），Ctrl+Alt+P 暂停
keytrace ui                          # 打开桌面实时窗口（已有常驻端时优先唤醒）
keytrace pause
keytrace resume
keytrace stop

keytrace stats                       # 最近 7 天 → ~/.keytrace/stats.json + typos.jsonl
keytrace stats --since all           # 全部历史
keytrace stats --acc-threshold 95    # 准确率低于阈值时打印护栏提醒与错误键清单

keytrace label                       # 导出盲标表，用来校准"什么算打错"
keytrace export --format keybr       # 导出成 keybr-tui 能导入的 typing-data.json
keytrace export --format csv         # 导出逐键统计表 keys.csv（带 UTF-8 BOM）
keytrace export --format json        # 导出完整统计 stats.json

keytrace words                       # 按弱键生成中文双拼单字练习串（weak-zh.txt）
keytrace words --lang sym            # 按弱符号生成编程符号练习串（weak-sym.txt）

keytrace dashboard                   # 生成单文件离线 HTML 看板（report.html）
keytrace drill                       # 生成单文件双拼练习页（drill.html）
keytrace drill --record result.json  # 录入练完导出的 JSON，驱动下一轮自适应选键
```

事件写在 `%USERPROFILE%\.keytrace\events\`，一行一个 JSON，格式见 [docs/event-schema.md](docs/event-schema.md)。
统计写在 `%USERPROFILE%\.keytrace\stats.json`，字段与判定规则见 [docs/stats-schema.md](docs/stats-schema.md)。

## 构建与测试

环境要求：Windows + [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet build -c Release
dotnet run --project tests/KeyTrace.Tests -c Release
```

## 许可

[MIT](LICENSE)（第三方数据与思路署名见 [NOTICE](NOTICE)）。可选配套网页项目 [keytrace-web](https://github.com/428581985-cyber/keytrace-web) 为独立仓库，采用 AGPL-3.0 许可。
