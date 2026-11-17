# 统计格式 `stats.json`

> **状态**：`v1`，阶段 3 归因层产出。同目录还有 `typos.jsonl`（逐条明细）。
> 上游是 `events.jsonl`（见 `docs/event-schema.md`），下游是阶段 4 的选词与阶段 6 的看板。

---

## 怎么跑

```bash
keytrace stats                          # 最近 7 天 → %USERPROFILE%\.keytrace\stats.json
keytrace stats --since all              # 全部历史
keytrace stats --since 2026-11-01       # 从某天起
keytrace stats --out .test-events/stats.json   # 自测用，别覆盖正式产物
keytrace stats --pause-factor 2.5 --min-samples 30
```

两个产物：

| 文件 | 内容 |
| --- | --- |
| `stats.json` | 聚合统计，给看板和阶段 4 读 |
| `typos.jsonl` | 一条错误一行，给 200 条金标标注和排错用 |

---

## 一条 typo 是怎么判出来的

规则来自文献（`plan.md` 阶段 3），但落地时必须处理三件事才不至于算歪。

### 1. 词怎么切

一个"词"= 连续的字母/数字（含 `_`），或者单个符号，或者单个汉字。

- 空格、回车、Tab、方向键、切换应用、切换会话 → 关词
- 符号各自成词：`{` 自己就是一个词，所以"打了 `{` 又删掉"能被判出来

### 2. typo 的判定

对每个退格：

| 检查顺序 | 条件 | 归类 |
| --- | --- | --- |
| 1 | 同一段连续退格 > 3 次 | `rewording.longRun` |
| 2 | 退格前的 IKI > 停顿门槛 | `rewording.longPause` |
| 3 | 不是这段退格的第一次 | `collateral`（连带删掉，本身没打错） |
| 4 | 这个会话读不到上屏文字 | `blindDeletions`（不判） |
| 5 | 删掉又原样打回来 | `rewording.sameCharRetype`（内容没变，不算错） |
| 6 | 其余 | **typo** |

**停顿门槛 = 该键 IKI 中位数 × 3（上限锁在全局中位数的 2 倍）。**

为什么不用论文原式 `mean + 2SD`：IKI 是重尾分布。实测两天数据 mean 439.8 / SD 589.7 → 门槛 1619ms，1315 次退格里绝大多数都能通过，这条规则等于没写。改用 Inputlog 的 *individualized pause*（停顿 = IKI 超过个人中位数的 a 倍，a 取 2–3），命中率才合理。

上限锁 2 倍的缘故：`Enter`、`Tab`、`;` 这类"敲完就停下来想"的键，自身中位数被思考时间污染（`Tab` 平均 IKI 1298ms），不设上限等于给它们开天窗。

### 3. 错误归到哪个键

只看被删掉的那个字符属于哪个键（`vk`）。

- **相邻键替换**（`y→u`）：仅当"只按了一次退格、补上的字符与被删字符在键盘上相邻"时成立，置信度 0.9。连删两三个再重写是整词推翻，位置对得上不代表按错键，一律记 `ambiguous`
- **连锁退格**：只把第一次归到紧邻前一个字符，后面的算 `collateral`
- **中文拼音层**：这个词出现过 `comp`（组字中间态），且被删的是 ASCII 字母 → 记为 `pinyin`，计入打字统计
- **中文选字层**：汉字上屏后 3 秒内退格，缓冲区已空 → 记为 `selection`，**不计入**打字统计，也不归到任何键
- 没有 `comp` 的中文退格：不猜，按普通 typo 处理（宁可算错一层，也不假装能分清）

---

## 顶层字段

| 字段 | 说明 |
| --- | --- |
| `v` | 格式版本，当前 `1`。字段只加不改 |
| `generatedAt` | 生成时刻，带时区偏移 |
| `window` | 统计覆盖的日期范围、读了哪些文件、总事件数 |
| `window.skipped` | **被丢弃的事件，按原因分开计数**（见下） |
| `config` | 本次判定用的参数，换参数结论不可直接比 |
| `baseline` | 全局节奏基线 |
| `totals` | 总量与各类错误的条数 |
| `keys` | 逐键统计，弱键排名的来源 |
| `symbols` | 逐字符统计（只含非字母），符号练习的原料 |
| `errors` | 错误原因分布 |
| `chinese` | 中文专项 |
| `apps` | 逐应用 |

### `window.skipped`

丢掉多少、为什么丢，必须能查——否则"错误率 1.4%"没人能复现。

| 键 | 含义 |
| --- | --- |
| `paste` | `keys == 0`：粘贴、拖拽、语音输入 |
| `shortcut` | 含 Ctrl/Alt/Win 的按键 |
| `hotkey` | 采集层自己的 `Ctrl+Alt+P`（撤不回来，只能事后丢） |
| `glyph` | 去掉私有区字符后为空的 commit（图标字体） |
| `bulkCommits` / `bulkChars` | 字符数超过击键数的上屏：自动补全、接受建议、编辑器把整篇文本当新输入 |
| `synthetic` | `sc == 0`，`keybd_event` 合成的事件 |
| `deleteKey` | `Delete` 键（向前删，无法知道删了哪个字符） |
| `unparsable` | 版本不认识或 JSON 坏了 |

> `bulkCommits` 这一条是实测逼出来的：两天里有 80 条 commit 的字数超过击键数（最多 199 字配 2 次击键），全是 VS Code 的自动补全和整篇 diff。不排掉会让 CPM 从 38 涨到 98、错误率被摊薄。

### `baseline`

| 字段 | 说明 |
| --- | --- |
| `presses` | 参与基线计算的 IKI 样本数 |
| `ikiMeanMs` / `ikiP50Ms` / `ikiSdMs` | 全局节奏。**重尾，看 p50 别看 mean** |
| `pauseThresholdMs` | 全局停顿门槛（p50 × factor），逐键门槛逐键另算 |
| `activeMs` | 所有会话时长之和 |
| `textActiveMs` | **只算读得到文字的那些会话**。CPM 的分母只能是它 |
| `cpm` | `chars / textActiveMs`。混进盲区时间会把 CPM 算成个位数 |
| `typoRate` | `typos / keystrokes` |

### `totals`

| 字段 | 说明 |
| --- | --- |
| `keystrokes` | 计入打字的按键数（去掉修饰键、快捷键、合成事件） |
| `chars` | **真正打出来的**上屏字符数（批量上屏已排除） |
| `commits` / `sessions` / `sessionsText` | 上屏次数、会话数、其中有文字的会话数 |
| `backspaces` | 退格次数 |
| `typos` | 打错总数（含拼音层） |
| `pinyinErrors` / `selectionErrors` | 中文两层错分开计 |
| `rewording` | 改措辞：`longPause + longRun + earlierText + sameCharRetype` |
| `collateral` | 同一次纠错里被连带删掉、本身没错的字符 |
| `blindDeletions` | 读不到文字的会话里发生的退格，不判对错 |

**恒等式**（可用来自检）：
`backspaces = typos + rewording + collateral + blindDeletions + selectionErrors`

两天实测：1315 = 85 + 1086 + 98 + 40 + 6 ✓

### `keys[]`

按 `vk` 升序，每个键一项。

| 字段 | 说明 |
| --- | --- |
| `vk` / `name` / `role` | `role` ∈ `letter / digit / symbol / space / enter / tab / edit / navigate / function / modifier` |
| `presses` / `shifted` | 按下次数、其中按住 Shift 的次数 |
| `typos` / `pinyinTypos` | 归到这个键的打错数；`pinyinTypos` 是其中拼音层那部分 |
| `typoRate` | `typos / presses` |
| `ikiMeanMs` / `ikiP50Ms` / `ikiP90Ms` | 这个键的节奏 |
| `pauseThresholdMs` | 这个键自己的停顿门槛 |
| `dwellMeanMs` | 按住时长（第一次 `kd` 到对应 `ku`） |
| `ikiRatio` | `ikiP50 / 全局 ikiP50`，>1 表示比平均慢 |
| `weakScore` | 0–1，见下。样本不足为 `null` |

### `weakScore`

```
errPart  = min(1, typoRate / 0.15)
slowPart = clamp((ikiRatio - 1) / 1.5, 0, 1)
weakScore = 0.7 × errPart + 0.3 × slowPart
```

- 只对 `letter / digit / symbol` 计算，`Backspace` 和修饰键恒为 `0`——否则排名第一永远是退格
- `presses < minKeySamples`（默认 20）时为 `null`，**不进排名**。样本少的键错误率天然虚高
- 两项都做绝对归一，不按"当前最大值"归一：否则样本一少，最高分会随数据抖动
- 思路取自 keybr 的逐键置信度（错误优先、速度次之、样本不足不排名）；不做指数平滑，因为这是一次性离线统计

### `symbols[]`

只统计**非字母**字符（数字、符号），按字符拆而不按键拆：`[` 和 `{` 是同一个物理键，但练习材料要分开出。

| 字段 | 说明 |
| --- | --- |
| `char` / `vk` / `shift` | 字符、来自哪个键、是否需要 Shift |
| `count` / `typos` | 上屏次数、打错次数 |

### `chinese`

| 字段 | 说明 |
| --- | --- |
| `commits` / `chars` | 中文上屏次数、汉字数 |
| `comps` | 组字中间态事件数 |
| `compCoverage` | 有 `comp` 的 commit 占比 |
| `pinyinErrors` / `selectionErrors` | 拼音层 / 选字层错误数 |
| `keys[]` | 拼音层错误落在哪些键上 |

**`compCoverage` 是目前最大的短板**：两天数据只有 23%。也就是说四条中文上屏里只有一条能分清是拼音打错还是选字选错。这个数必须跟着统计一起发布，否则中文结论会被当成全量。

### `apps[]`

| 字段 | 说明 |
| --- | --- |
| `exe` | 进程名 |
| `keystrokes` / `commits` / `chars` | 击键、上屏、字符 |
| `textAvailable` | 这个应用是否读到过文字。`false` 表示只有击键，**它的击键不参与错误判定** |
| `typos` | 归到这个应用的打错数 |
| `backspacesPer100Keys` | 每 100 次击键的退格数 |

> 用"每百次击键"而不是"每百字"：读不到文字的应用 `chars` 为 0，按字算会除零，也会让 Code.exe 那种退格密集的场景失真。

---

## `typos.jsonl`

一行一条，含 `kind: typo / pinyin / selection`。

```json
{"ts":1794843865168,"sid":"20261116-233222-13","exe":"Code.exe","kind":"typo","vk":221,"key":"]","deleted":"]","replacement":"[","runLen":2,"iki":1142,"cause":"adjacentSubstitution","confidence":0.9,"word":"grid[x[]","wordAfter":"grid[x][y]","pos":7}
```

- `word` 是**退格之前**那个词的样子，`wordAfter` 是最后的样子——标金标就看这两个
- `cause` ∈ `adjacentSubstitution` / `ambiguous` / `wrongCandidate`
- `confidence`：相邻键替换 0.9，单次退格 0.6，连锁退格 0.5，选字错 0.7

---

## 已知限制

| 限制 | 现在怎么办 |
| --- | --- |
| `compCoverage` 只有 23%，中文两层错分不全 | 报出来，不猜。修法是采集层改进组字中间态的捕获，不是归因层硬凑 |
| WorkBuddy / 微信读不到文字，占击键 36% | 这些会话整段不判错（`blindDeletions`），只贡献速度数据 |
| 单字符符号（`{` `;`）的"打了又删"难分是手滑还是改结构 | 置信度 0.6，`cause: ambiguous`。等 200 条金标定阈值 |
| 精度/召回未标定 | 计划要求 precision ≥ 0.8、recall ≥ 0.7。目前只有合成用例测试，没有人工金标 |
| IKI 里混着思考时间 | 用中位数而不是均值，并把它权重压到 30% |
