# 全拼与多双拼方案演进规划（社区扩展设计稿）

> 状态：已规划归档，待后续按需推进（当前版本优先保证小鹤双拼 + 英文体验与桌面端稳定性）。

---

## 1. 扩展目标

在保留现有 `zh-sp`（小鹤双拼）历史统计与练习记录 **100% 向后兼容** 的前提下，面向开源社区新增以下中文输入方案：

| 方案 | 网页 Language/Layout ID | KeyTrace 日志 `meta.ime` 标识 | 键长特征 | 额外物理键 |
| --- | --- | --- | --- | --- |
| 小鹤双拼（现有） | `zh-sp` (`xid=0xfe`) | `shuangpin/xiaohe` | 固定 2 键/字 | `A–Z` |
| 全拼 | `zh-qp` (`xid=0xfd`) | `full-pinyin` | 1～6 键变长/字 | `A–Z` |
| 自然码双拼 | `zh-sp-zrm` | `shuangpin/ziranma` | 固定 2 键/字 | `A–Z` |
| 微软双拼 | `zh-sp-ms` | `shuangpin/microsoft` | 固定 2 键/字 | `A–Z` + `;`（`ing`） |
| 搜狗双拼 | `zh-sp-sg` | `shuangpin/sogou` | 固定 2 键/字 | `A–Z` + `;`（视兼容模式） |

**设计底线**：不同拼音方案对同一个物理键承载的声韵母与按键分布完全不同（例如 `C` 键在小鹤是 `ao`、自然码是 `iao`、全拼是声母 `c`），各方案的练习曲线、热力图与日常统计必须严格隔离、互不污染。

---

## 2. 统一共享架构：单份标准拼音词库 + 声明式方案表

### 2.1 方案无关的词库源（消灭多份重复词表与全拼歧义）
- 现有 `words-zh-sp.json` / `zhWords.ts` / `zhMap.ts` 硬编码了“小鹤两字母码 -> 汉字”，扩展到全拼时会遇到音节边界歧义（例如 `fangan` 可能是 `fang-an` 方案，也可能是 `fan-gan` 反感；`xian` 可能是 `xian` 先，也可能是 `xi-an` 西安）。
- 改造 `keytrace/tools/gen_zh_words.py` 产出一份**标准拼音中间词表**：
  - 每条词包含：`{ word: "方案", syllables: ["fang", "an"], freq, isIdiom }`（`ü` 统一写成 `v`）。
  - 运行时按当前选定方案即时编码出实际按键序列，同时直接产出每字长度数组（例如全拼 `lengths = [4, 2]`，双拼固定 `[2, 2]`），**彻底消灭音节切分歧义**，也不再随方案数量成倍增加静态资源体积。

### 2.2 声明式 `PinyinScheme` 注册表（C# 与 TypeScript 对称实现）
每个方案仅用声明式映射定义，不再手写多套分支代码：
- `initials`：`b..z`、`zh`、`ch`、`sh` 映射
- `finals`：全部单/复/鼻韵母映射
- `zeroInitialRule`：
  - `xiaohe`：单字母双写（`aa`）、两字母照全拼（`ai`）、三字母首字母 + 韵母键（`ah`）
  - `ziranma`：同类两字母规则
  - `microsoft` / `sogou`：`o` 引导双字母编码
  - `full-pinyin`：直接保留完整无调音节
- `extraAlphabetKeys`：例如微软双拼将分号 `;` 纳入字母表参与热力图与弱键调度

由同一张声明表自动派生：
1. 音节编码器；
2. 虚拟键盘键帽标注（替代硬编码 `zhKeyLabels.ts`；全拼时不挂双拼韵母小标，仅显示清晰 `A–Z`）；
3. **动态双拼模糊音映射**（替代硬编码 `fuzzy-shuangpin.ts`，根据声母所在键动态适配小鹤/自然码/微软/搜狗各自键位，包括各方案中 `zh/ch/sh` 与 `an/ang`/`en/eng`/`in/ing`/`ian/iang`）。

---

## 3. 网页端（`keybr.com`）演进细节

### 3.1 `ZhLines` 变长字切分与 N 等分进度光标
- 当前 `packages/keybr-textinput-ui/lib/ZhLines.tsx` 在 `toGroups` 中用 `i += 2` 硬切每 2 字符为一个汉字，光标固定左/右 2 半段。
- 改造后：
  - 按词元元数据给出的 `lengths`（单字键长数组）把按键切片绑定到对应汉字（单字兜底 `lengths = [syllable.length]`）；
  - 字底横线光标从固定 2 段升级为 **N 等分进度条**（打 `中(zhong)` 为 5 小段、打 `我(wo)` 为 2 小段、双拼退化回 2 小段），每按一格独立标绿或标红；
  - 勾选“显示拼音/双拼码”时上方悬浮对应的完整全拼（`zhong`）或双拼码（`vs`）。
  - 底层 `@keybr/textinput` 逐键状态机无需推翻，继续自然采集逐键耗时、准确率与纠错。

### 3.2 `GuidedLesson` 弱键练习与词抽取
- 中文路径本就不走英文音素伪词模型，而是直接从词库抽取包含当前最弱键（或锁定弱键）的真词与成语（`packages/keybr-lesson/lib/guided.ts`）。
- 切换到全拼后，上述“最弱键锁定 + 必含目标字母抽词 + 标点分隔 + 达标切换下一个最弱键”逻辑可 **100% 无缝复用**。

### 3.3 全拼模糊音两阶段取舍
- **Phase A（随全拼首批上线）**：支持不改变键长的模糊音（例如 `l ↔ n`、`f ↔ h` 等），平翘舌（`zh/z`）与前后鼻音（`en/eng`、`an/ang`、`in/ing`）默认按标准全拼严格校验并给出清晰提示，避免状态机提前复杂化。
- **Phase B（社区有强需求时再加）**：在 `@keybr/textinput` 增加“音节级可变步长跳过/消费”，允许在全拼模式下忽略预期 `zh/ch/sh` 的 `h` 或额外宽容 `an/en/in` 末尾多敲的 `g`。

### 3.4 键盘设置界面简化
- `packages/page-practice/lib/settings/KeyboardSettings.tsx` 调整为两级联动：
  - **语言**：`中文` / `英文`
  - **中文输入方案**（选中文时显示）：`小鹤双拼` / `全拼` / `自然码` / `微软双拼` / `搜狗双拼`

---

## 4. KeyTrace 桌面端与双端日志同步改造

1. **内置字表与离线练习（`HanziTable.cs` / `ZhSelector` / `DrillPage.cs`）**
   - `data/zh-hanzi.tsv` 第 2 列已经存储标准无调全拼音节（`Syllable`），无需重新爬取字表。
   - 按 `PinyinScheme` 即时生成对应方案编码，让桌面端 `words` 命令与单文件离线练习页 `drill.html` 支持 1～6 键变长输入与所有双拼方案。
2. **输入法方案识别与热力图联动（`ImeConfig.cs` / `KeyboardHeatmapControl.cs`）**
   - 自动识别微软拼音注册表 `full-pinyin`、`shuangpin/xiaohe`、`shuangpin/ziranma`、`shuangpin/microsoft`、`shuangpin/sogou`，并保留用户手动覆盖（供搜狗/微信/Rime 等第三方输入法使用）。
   - 热力图按方案切换键帽注释（全拼纯字母、各双拼显示对应声韵母）。
3. **日常数据分桶同步（`keytrace-events.ts` / `KeytracePage.tsx`）**
   - 解析每日事件流里的 `meta` 事件（`ime=...`），按时间线把每次会话动态映射并聚合到对应方案的 `layoutId`（不再非黑即白地只有 `zh-sp` 与 `en-us`）。
   - `/keytrace` 监控页根据日志里实际出现过的方案动态生成语言/方案页签与“去练习”锁定跳转。
