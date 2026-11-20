# 导出格式

> 导出器是给社区留的扩展口：别人想把 KeyTrace 接到自己的工具上，就靠这一层。
> 一种格式一个文件，新增格式不动现有代码。

---

## 现有格式

### `keybr`

```bash
keytrace export --format keybr --out typing-data.json
```

产出 **数组**，一个元素 = 一个 session（我们按"一天一个"切）。字段：

| 字段 | 取值 | 说明 |
| --- | --- | --- |
| `layout` | `en-us` | 固定。keybr 按语言 + 布局族过滤，非 `en-us` 会被跳过 |
| `textType` | `generated` | 表示这是打字练习产出的文本 |
| `timeStamp` | UTC ISO8601 | 当天零点（本地）换算成 UTC |
| `length` | 字符数 | = histogram 里 `hitCount` 之和 |
| `time` | 毫秒 | 当天**读得到文字的会话**时长之和（`textActiveMs`） |
| `errors` | 次数 | = histogram 里 `missCount` 之和 |
| `speed` | 字符/分钟 | `length / time × 60000` |
| `histogram[]` | — | 逐键：`codePoint` / `hitCount` / `missCount` / `timeToType` |

映射关系：

| keybr | KeyTrace |
| --- | --- |
| `codePoint` | `a`–`z` 取小写字母码位，`0`–`9` 取数字码位。**只导出这两类** |
| `hitCount` | `keys[].presses` |
| `missCount` | `keys[].typos`（只算确认的纠错） |
| `timeToType` | `keys[].ikiP50Ms` |

**为什么不导出符号**：我们把 `[` 和 `{` 算在同一个物理键上，分不出 `codePoint`；硬塞进去会让 keybr 的模型学到错的分布。符号和中文的练习材料由阶段 4 自己出。

**为什么一天一个 session**：keybr 的一次练习就是一个 session，把两个月合成一个，它就没法看到"这一课有没有进步"。

### 导入 keybr-tui

```bash
keybr-tui --import typing-data.json          # 会先写 .bak 备份
keybr-tui --import typing-data.json --force  # 强制覆盖
```

keybr-tui 是终端版、MIT、离线可用，GitHub Releases 有 Windows 预编译包（不需要装 Rust）。
**网页版 keybr.com 没有导入本地统计的入口**，所以导出只对终端版有意义。

---

### `csv`

```bash
keytrace export --format csv --out keys.csv
keytrace export --format csv --since all --min-samples 20
```

按 `vk` 升序导出逐键统计表（UTF-8 带 BOM、CRLF 换行），Windows 下双击可直接用 Excel 打开，也可直接喂给 pandas / R 做切片分析。

固定 15 列：

```
vk,name,role,presses,shifted,typos,typosUnreplaced,pinyinTypos,typoRate,ikiMeanMs,ikiP50Ms,ikiP90Ms,dwellMeanMs,ikiRatio,weakScore
```

- 含 `,` 或 `"` 的键名（如 `,`）按 RFC-4180 用双引号包裹
- `weakScore` 为 `null`（样本不足或零错误）时输出空单元格
- 支持 `--in`、`--since`、`--pause-factor`、`--min-samples`

---

### `json`

```bash
keytrace export --format json --out stats.json
```

复用 `StatsBuilder.WriteJson` 输出与 `keytrace stats` 同构的完整统计 JSON（字段口径见 `docs/stats-schema.md`），适合脚本或自定义看板直接消费，但不另写 `typos.jsonl`。

---

## 怎么新增一种格式

1. 在 `src/KeyTrace/Analysis/` 下新建 `XxxExport.cs`，提供 `Build(...)` 返回字符串 + `Write(path, json)`
2. 在 `Program.cs` 的 `ExportData` 里把 `--format` 的名字接到这个类
3. 在 `tests/` 里补 3–5 条断言：字段名、单位换算、空数据时的行为
4. 在本文件里加一节，写清"哪些字段是猜的、哪些是实测的"

不要做插件框架、不要动态加载。等真有三个人来提交格式再说——插件系统是最容易被过度设计的地方。
