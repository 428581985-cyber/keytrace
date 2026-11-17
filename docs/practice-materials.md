# 练习材料怎么来的

> 两种语言分开处理：英文能借 keybr 的算法，中文必须自己按双拼键位算。

---

## 英文：借 keybr

`keytrace export --format keybr` 把逐键统计导成 keybr 能吃的 `typing-data.json`，由它自己的调度器决定下一课练哪个键。映射细节见 `docs/export-formats.md`。

不 fork keybr 的原因：AGPL-3.0 网络 copyleft，搬进来整个项目得跟着换许可；而它要的每键统计我们已经有了。

## 中文：自己算

keybr 的文本引擎是"按音素马尔可夫链生成伪词"——`flurn`、`trand` 这种能读但不存在的词。
中文没有对应物：要练某个键，得先知道**哪些字的双拼码里有这个键**。

```
「好」hǎo   = h + c   （ao 在 C 键）  → a 弱？不相关；c 弱？该练
「便」biàn  = b + m                   → b、m 弱？该练
「双」shuāng = u + l                  → sh 在 U，uang 在 L
```

所以算法是"按码反查真字"，不是"按音素造词"。

### 数据

| 文件 | 内容 | 来源 | 许可 |
| --- | --- | --- | --- |
| `data/zh-hanzi.tsv` | 7258 字：音节 + 词频 + 双拼码 | 由 `tools/gen_zh_data.py` 生成 | 衍生自 MIT 数据 |
| 上游拼音 | 《通用规范汉字表》8105 字最常用读音 | `mozillazg/pinyin-data` | MIT |
| 上游词频 | 单字词频 | `fxsjy/jieba` 的 `dict.txt` | MIT |

原始上游两个文件共 5 MB，不进仓库；仓库里只放生成后的 108 KB 精简表。重新生成：

```bash
curl -O https://raw.githubusercontent.com/mozillazg/pinyin-data/master/kMandarin_8105.txt
curl -O https://raw.githubusercontent.com/fxsjy/jieba/master/jieba/dict.txt
python tools/gen_zh_data.py kMandarin_8105.txt dict.txt data/zh-hanzi.tsv
```

### 小鹤双拼的编码规则

照 **flypy.cc 官网**「零声母」一节实现，不是抄二手项目：

- 有声母：声母键 + 韵母键（`zh/ch/sh` 分别占 `v/i/u`；`y/w` 当普通声母）
- 零声母：单字母双写（啊 `aa`）、**双字母照全拼**（爱 `ai`、恩 `en`）、三字母取首字母 + 韵母键（昂 `ah`）

> 网上至少有一个开源项目把「爱」写成 `ad`、「恩」写成 `ef`——那是把微软双拼的零声母规则套到小鹤上了。测试里专门锁了这几条，别再照着抄错。

### 选字算法

`keytrace words` 从内置字表里筛：

1. **命中弱键越多越前**（一个字的两个键都命中 > 只命中一个）
2. 同样命中数按**词频**排，常用字先练
3. 混入 **20%** 不含弱键的高频字，避免练成"只会打难字"的偏科（keybr 的做法）
4. 混入的字**均匀插入**，不是堆在末尾——堆在末尾等于"先难后易"，起不到缓解作用

```bash
keytrace words                    # 自动取 stats.json 前 5 个弱键
keytrace words --weak c,v,f --count 60
```

输出 `~/.keytrace/weak-zh.txt`，每行 10 个字，空格分隔，直接粘进 keybr 自定义文本或 monkeytype。

### 已知取舍

| 取舍 | 原因 |
| --- | --- |
| 只做单字，不做词组 | 双拼一次两键一字，单字就是最直接的练习单位。词组留给后面 |
| 偶尔出现生僻字（如「诏」） | 它同时命中两个弱键，被排到前面。练键不看词义，能接受 |
| 只认 a–z | 双拼码只有字母 |
| 只取每个字最常用的读音 | 多音字按最常用算，会让少数词练错键位（如「长」zhǎng/cháng） |

---

## 练习页（`keytrace drill`）

```bash
keytrace drill                  # 60 个字，自动取 stats.json 前五弱键
keytrace drill --weak c,v --count 80
```

产出 `~/.keytrace/drill.html`，单文件、双击就开、断网可用。

**练的是键，不是选字**：一屏一个字，把它的双拼两键按出来（「便」= `b` `m`）。
不让输入法参与——输入法会把按键吞掉，成绩就没法归因到具体的键。

- 按对：绿色闪一下，进下一个
- 按错：红色，显示正确双拼码，420ms 后进下一个（不卡住）
- `H` 键显示/隐藏双拼码，`Esc` 跳过

练完给：正确率、用时、速度（键/分）、**按错过的键和次数**，以及一段可直接贴回来的 JSON。
那段 JSON 就是"动态调整"的输入——没有成绩回流，所谓自适应算法都是空谈。

下一步（未做）：把贴回来的结果存进 `~/.keytrace/drills/`，下次出题时按
"最近错得多的键"加权，并让已经稳定的键逐步退出。
