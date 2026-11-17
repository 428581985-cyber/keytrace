# KeyTrace

本地记录真实打字，找出打字训练器看不到的短板，再据此生成练习材料。Windows 上用 Raw Input 采全局击键，用 UI Automation 读上屏文字。数据只存本地。

**早期开发中，还不能用。** 采集部分已完成并在自己机器上跑着；归因层（击键 → `stats.json`）刚落地，词库生成和看板还没做。

## 为什么

keybr 这类工具只在它自己的练习题里测你。真实工作中打什么、错在哪、哪些键其实按得吃力，它不知道，也不练中文、数字和符号。

我自己的例子：练了 12 天，准确率 92%，`z` 键错 17%；真实输入里退格占 9%，中间还有不少隔了近一秒的停顿。看不见这些，就不知道练什么。

## 用法

```bash
keytrace run        # 常驻，托盘图标，Ctrl+Alt+P 暂停
keytrace pause
keytrace stop

keytrace stats                  # 最近 7 天 → ~/.keytrace/stats.json + typos.jsonl
keytrace stats --since all      # 全部历史
keytrace label                  # 导出人工标注表，用来校准"什么算打错"
keytrace export --format keybr   # 导出成 keybr-tui 能导入的格式
keytrace words                  # 按弱键生成中文双拼练习串
keytrace drill                  # 生成练习页（单文件 HTML，双击就练）
```

事件写在 `%USERPROFILE%\.keytrace\events\`，一行一个 JSON，格式见 [docs/event-schema.md](docs/event-schema.md)。
统计写在 `%USERPROFILE%\.keytrace\stats.json`，字段与判定规则见 [docs/stats-schema.md](docs/stats-schema.md)。

## 隐私

全部数据在本地，不联网，不上传。默认不自启，装了也要自己开。

这个工具的行为和键盘记录器一样，杀软会拦。源码是公开的，可以自己编译，也可以自己审计。

## 许可

MIT
