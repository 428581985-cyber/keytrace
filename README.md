# KeyTrace

本地记录真实打字，找出打字训练器看不到的短板，再据此生成练习材料。Windows 上用 Raw Input 采全局击键，用 UI Automation 读上屏文字。

> **隐私与安全声明**
> 1. 本工具会记录你的击键，**全部数据只存在本地、不联网、不上传**。默认不自启，且内置密码管理器/银行客户端排除名单与 `--no-text` 模式。
> 2. 它的行为特征接近键盘记录器，杀软可能会误报拦截；源码完全公开，你可以自己编译与审计。

## 项目状态

- **已实现**：全局采集（Raw Input + UIA 文本差分 + 托盘/热键控制 + 日志按天轮转压缩）、归因分析（`stats.json` / `typos.jsonl`、拼音层与选字层分开、确认纠错闸门、准确率护栏）、盲标导出（`label`）、多格式导出（`keybr` / `csv` / `json`）、中文双拼与编程符号练习材料生成（`words`）、零外链离线看板（`dashboard`）、单文件双拼练习页及成绩回流自适应推荐（`drill` + `drill --record`）。
- **待收尾**：200 条人工金标标定与公开样本集（`testdata/labeled/`）、`config.toml` 配置化与 v0.1 Release 打包。

## 为什么

keybr 这类工具只在它自己的练习题里测你。真实工作中打什么、错在哪、哪些键其实按得吃力，它不知道，也不练中文双拼、数字和符号。

我自己的例子：练了 12 天，准确率 92%，`z` 键错 17%；真实输入里退格占 9%，中间还有不少隔了近一秒的停顿。看不见这些，就不知道练什么。

## 用法

```bash
keytrace run                         # 常驻录制，带托盘图标，Ctrl+Alt+P 暂停
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

[MIT](LICENSE)（第三方数据与思路署名见 [NOTICE](NOTICE)）
