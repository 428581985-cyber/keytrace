#!/usr/bin/env python3
"""为 keybr 生成中文词组词表（支持小鹤双拼与全拼，共用标准拼音中间词表）。

输入（两种模式二选一）
  模式 A（完整重建，需要外部 jieba dict.txt + phrase-pinyin-data）：
    --hanzi          zh-hanzi.tsv  制表符分隔：字 / 音节 / 词频 / 小鹤双拼两键
    --dict           dict.txt      jieba 词频词典：词 词频 词性（词性 i = 成语）
    --phrase-pinyin  pinyin.txt    mozillazg/phrase-pinyin-data：词: 带调拼音（可选）

  模式 B（离线复用已有 zhWords.ts 多音字消歧结果）：
    --hanzi                    zh-hanzi.tsv
    --bootstrap-from-zhwords   packages/keybr-textinput-ui/lib/zhWords.ts

输出（按需组合）
  --out               words-zh-sp.json（默认双拼模式输出；未传任何 --out-qp-* 时启用）
  --out-intermediate  标准方案无关中间词表 JSON: [{word, syllables, freq, isIdiom}]
  --out-qp-words      packages/keybr-content-words/lib/data/words-zh-qp.json
  --out-qp-dict       packages/keybr-generators/dictionaries/dictionary-zh-qp.csv.gz
  --out-qp-lexicon    packages/keybr-textinput/lib/zh-quanpin-lexicon.ts

规则
  1. 普通词：2–3 个汉字，成语：4 个汉字（词性含 i）
  2. 全拼规范：`j/q/x/y` 后一律存 `u`；`l/n + ü/üe` 规范存 `lv/nv/lve/nve`；
     剔除非标准叹词音节（`hm/hng/ng/m/n`）与单儿化音 `r`
  3. 全拼导出内置常用 `v` 键词/成语保底补充包（去重防碰撞），确保含 `v` 词 ≥ 80 条
  4. `dictionary-zh-qp.csv.gz` 仅写入长度 ≥ 4 的多字全拼词，确保 26 字母全覆盖且 f > 0
"""

from __future__ import annotations

import argparse
import gzip
import json
import re
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

from gen_zh_data import shuangpin, strip_tone

CJK = re.compile(r"^[\u4e00-\u9fff]+$")
ZH_WORDS_LINE = re.compile(r'^\s*"([a-z]+)":\s*"([^"]+)",?\s*$')

# 清洗规则：剔除非标准叹词与单儿化音
NON_STANDARD_SYLLABLES = frozenset({"hm", "hng", "ng", "m", "n", "r"})

# zh-hanzi.tsv 中因多音字首读音缺失的标准普通话音节补丁
EXTRA_SYLLABLE_CHARS: dict[str, str] = {
    "fo": "佛",
    "dei": "得",
    "lo": "咯",
}

# 常用 v 键（lv / nv / lve / nve）词组与四字成语补充包。
# 所有音节严格采用规范全拼（lv / nv / lve / nve，j/q/x/y 后用 u）。
# 合并时按全拼串去重，已存在的词条优先保留，保证 0 碰撞。
CURATED_V_WORDS: tuple[tuple[str, tuple[str, ...], int, bool], ...] = (
    # --- lv 普通词（绿/旅/律/率/虑/履/滤/侣/铝/屡/驴）---
    ("概率", ("gai", "lv"), 680, False),
    ("纪律", ("ji", "lv"), 670, False),
    ("汇率", ("hui", "lv"), 660, False),
    ("焦虑", ("jiao", "lv"), 660, False),
    ("功率", ("gong", "lv"), 650, False),
    ("自律", ("zi", "lv"), 640, False),
    ("过滤", ("guo", "lv"), 640, False),
    ("旋律", ("xuan", "lv"), 630, False),
    ("情侣", ("qing", "lv"), 630, False),
    ("绿茶", ("lv", "cha"), 620, False),
    ("速率", ("su", "lv"), 620, False),
    ("旅途", ("lv", "tu"), 610, False),
    ("顾虑", ("gu", "lv"), 610, False),
    ("滤镜", ("lv", "jing"), 610, False),
    ("旅程", ("lv", "cheng"), 600, False),
    ("忧虑", ("you", "lv"), 600, False),
    ("绿化", ("lv", "hua"), 590, False),
    ("旅馆", ("lv", "guan"), 590, False),
    ("税率", ("shui", "lv"), 590, False),
    ("伴侣", ("ban", "lv"), 590, False),
    ("准确率", ("zhun", "que", "lv"), 590, False),
    ("胜率", ("sheng", "lv"), 580, False),
    ("比率", ("bi", "lv"), 580, False),
    ("履历", ("lv", "li"), 580, False),
    ("心率", ("xin", "lv"), 580, False),
    ("屡次", ("lv", "ci"), 570, False),
    ("成功率", ("cheng", "gong", "lv"), 570, False),
    ("绿洲", ("lv", "zhou"), 560, False),
    ("旅店", ("lv", "dian"), 560, False),
    ("韵律", ("yun", "lv"), 560, False),
    ("帧率", ("zhen", "lv"), 560, False),
    ("履约", ("lv", "yue"), 560, False),
    ("铝合金", ("lv", "he", "jin"), 560, False),
    ("绿地", ("lv", "di"), 550, False),
    ("倍率", ("bei", "lv"), 550, False),
    ("思虑", ("si", "lv"), 550, False),
    ("旅行团", ("lv", "xing", "tuan"), 550, False),
    ("覆盖率", ("fu", "gai", "lv"), 550, False),
    ("过滤器", ("guo", "lv", "qi"), 550, False),
    ("绿灯", ("lv", "deng"), 540, False),
    ("文旅", ("wen", "lv"), 540, False),
    ("旅行者", ("lv", "xing", "zhe"), 540, False),
    ("履职", ("lv", "zhi"), 540, False),
    ("滤网", ("lv", "wang"), 540, False),
    ("屡屡", ("lv", "lv"), 540, False),
    ("绿豆", ("lv", "dou"), 530, False),
    ("差旅", ("chai", "lv"), 530, False),
    ("旅游业", ("lv", "you", "ye"), 530, False),
    ("良率", ("liang", "lv"), 530, False),
    ("滤芯", ("lv", "xin"), 530, False),
    ("绿叶", ("lv", "ye"), 520, False),
    ("律动", ("lv", "dong"), 520, False),
    ("斜率", ("xie", "lv"), 520, False),
    ("绿树", ("lv", "shu"), 510, False),
    ("旅社", ("lv", "she"), 510, False),
    ("格律", ("ge", "lv"), 510, False),
    ("曲率", ("qu", "lv"), 510, False),
    ("多虑", ("duo", "lv"), 510, False),
    ("翠绿", ("cui", "lv"), 500, False),
    ("商旅", ("shang", "lv"), 500, False),
    ("戒律", ("jie", "lv"), 500, False),
    ("律所", ("lv", "suo"), 500, False),
    ("碧绿", ("bi", "lv"), 490, False),
    ("旅居", ("lv", "ju"), 490, False),
    ("音律", ("yin", "lv"), 490, False),
    ("履新", ("lv", "xin"), 490, False),
    ("铝材", ("lv", "cai"), 490, False),
    ("墨绿", ("mo", "lv"), 480, False),
    ("履带", ("lv", "dai"), 480, False),
    ("毛驴", ("mao", "lv"), 480, False),
    ("浅绿", ("qian", "lv"), 470, False),
    ("深绿", ("shen", "lv"), 470, False),
    ("他律", ("ta", "lv"), 470, False),
    ("嫩绿", ("nen", "lv"), 460, False),
    ("军律", ("jun", "lv"), 460, False),
    # --- nv 普通词 ---
    ("女生", ("nv", "sheng"), 680, False),
    ("男女", ("nan", "nv"), 670, False),
    ("女友", ("nv", "you"), 650, False),
    ("美女", ("mei", "nv"), 640, False),
    ("女神", ("nv", "shen"), 630, False),
    ("女王", ("nv", "wang"), 620, False),
    ("女朋友", ("nv", "peng", "you"), 620, False),
    ("儿女", ("er", "nv"), 600, False),
    ("仙女", ("xian", "nv"), 590, False),
    ("女主角", ("nv", "zhu", "jue"), 590, False),
    ("女装", ("nv", "zhuang"), 580, False),
    ("女主", ("nv", "zhu"), 580, False),
    ("孙女", ("sun", "nv"), 580, False),
    ("女排", ("nv", "pai"), 570, False),
    ("闺女", ("gui", "nv"), 570, False),
    ("女同学", ("nv", "tong", "xue"), 570, False),
    ("女足", ("nv", "zu"), 560, False),
    ("女婿", ("nv", "xu"), 560, False),
    ("才女", ("cai", "nv"), 550, False),
    ("女工", ("nv", "gong"), 550, False),
    ("女主人", ("nv", "zhu", "ren"), 550, False),
    ("女篮", ("nv", "lan"), 540, False),
    ("侄女", ("zhi", "nv"), 540, False),
    ("女侠", ("nv", "xia"), 530, False),
    ("女兵", ("nv", "bing"), 530, False),
    ("女皇", ("nv", "huang"), 530, False),
    ("女巫", ("nv", "wu"), 520, False),
    ("修女", ("xiu", "nv"), 520, False),
    ("长女", ("zhang", "nv"), 520, False),
    ("女伴", ("nv", "ban"), 510, False),
    ("侍女", ("shi", "nv"), 510, False),
    ("女仆", ("nv", "pu"), 500, False),
    ("舞女", ("wu", "nv"), 500, False),
    ("歌女", ("ge", "nv"), 490, False),
    ("幼女", ("you", "nv"), 490, False),
    ("次女", ("ci", "nv"), 480, False),
    # --- lve / nve 普通词 ---
    ("忽略", ("hu", "lve"), 680, False),
    ("攻略", ("gong", "lve"), 660, False),
    ("省略", ("sheng", "lve"), 650, False),
    ("掠夺", ("lve", "duo"), 630, False),
    ("略微", ("lve", "wei"), 620, False),
    ("虐待", ("nve", "dai"), 620, False),
    ("领略", ("ling", "lve"), 610, False),
    ("掠过", ("lve", "guo"), 610, False),
    ("肆虐", ("si", "nve"), 610, False),
    ("谋略", ("mou", "lve"), 600, False),
    ("略有", ("lve", "you"), 600, False),
    ("粗略", ("cu", "lve"), 590, False),
    ("方略", ("fang", "lve"), 590, False),
    ("缩略图", ("suo", "lve", "tu"), 590, False),
    ("简略", ("jian", "lve"), 580, False),
    ("略显", ("lve", "xian"), 580, False),
    ("掠影", ("lve", "ying"), 580, False),
    ("暴虐", ("bao", "nve"), 580, False),
    ("大略", ("da", "lve"), 570, False),
    ("略去", ("lve", "qu"), 570, False),
    ("略高", ("lve", "gao"), 560, False),
    ("省略号", ("sheng", "lve", "hao"), 560, False),
    ("疟疾", ("nve", "ji"), 560, False),
    ("概略", ("gai", "lve"), 550, False),
    ("略低", ("lve", "di"), 550, False),
    ("残虐", ("can", "nve"), 550, False),
    ("胆略", ("dan", "lve"), 540, False),
    ("略胜", ("lve", "sheng"), 540, False),
    ("劫掠", ("jie", "lve"), 540, False),
    ("才略", ("cai", "lve"), 530, False),
    ("经略", ("jing", "lve"), 520, False),
    ("凌虐", ("ling", "nve"), 520, False),
    ("要略", ("yao", "lve"), 510, False),
    ("苛虐", ("ke", "nve"), 490, False),
    # --- v 键四字成语 ---
    ("绿水青山", ("lv", "shui", "qing", "shan"), 640, True),
    ("如履薄冰", ("ru", "lv", "bo", "bing"), 630, True),
    ("千篇一律", ("qian", "pian", "yi", "lv"), 630, True),
    ("青山绿水", ("qing", "shan", "lv", "shui"), 620, True),
    ("男女老少", ("nan", "nv", "lao", "shao"), 620, True),
    ("略胜一筹", ("lve", "sheng", "yi", "chou"), 620, True),
    ("严于律己", ("yan", "yu", "lv", "ji"), 610, True),
    ("金童玉女", ("jin", "tong", "yu", "nv"), 610, True),
    ("略知一二", ("lve", "zhi", "yi", "er"), 610, True),
    ("助纣为虐", ("zhu", "zhou", "wei", "nve"), 610, True),
    ("屡禁不止", ("lv", "jin", "bu", "zhi"), 600, True),
    ("灯红酒绿", ("deng", "hong", "jiu", "lv"), 600, True),
    ("金科玉律", ("jin", "ke", "yu", "lv"), 600, True),
    ("攻城掠地", ("gong", "cheng", "lve", "di"), 600, True),
    ("屡试不爽", ("lv", "shi", "bu", "shuang"), 590, True),
    ("绿树成荫", ("lv", "shu", "cheng", "yin"), 590, True),
    ("如履平地", ("ru", "lv", "ping", "di"), 590, True),
    ("儿女情长", ("er", "nv", "qing", "chang"), 590, True),
    ("浮光掠影", ("fu", "guang", "lve", "ying"), 590, True),
    ("屡教不改", ("lv", "jiao", "bu", "gai"), 580, True),
    ("花红柳绿", ("hua", "hong", "liu", "lv"), 580, True),
    ("不绝如缕", ("bu", "jue", "ru", "lv"), 580, True),
    ("屡战屡败", ("lv", "zhan", "lv", "bai"), 570, True),
    ("绿草如茵", ("lv", "cao", "ru", "yin"), 570, True),
    ("清规戒律", ("qing", "gui", "jie", "lv"), 570, True),
    ("屡战屡胜", ("lv", "zhan", "lv", "sheng"), 560, True),
    ("削足适履", ("xue", "zu", "shi", "lv"), 560, True),
    ("条分缕析", ("tiao", "fen", "lv", "xi"), 550, True),
)


@dataclass(frozen=True)
class LexiconEntry:
    word: str
    syllables: tuple[str, ...]
    freq: int
    is_idiom: bool

    @property
    def quanpin(self) -> str:
        return "".join(self.syllables)

    @property
    def lengths_prefix(self) -> str:
        return "".join(str(len(s)) for s in self.syllables)

    def to_json_obj(self) -> dict[str, object]:
        return {
            "word": self.word,
            "syllables": list(self.syllables),
            "freq": self.freq,
            "isIdiom": self.is_idiom,
        }


def load_hanzi_full(
    path: Path,
) -> tuple[dict[str, str], dict[str, str], dict[str, str]]:
    """加载 zh-hanzi.tsv，返回：
    - char_to_sp: 字 -> 小鹤双拼两键
    - char_to_syl: 字 -> 规范全拼音节
    - syl_to_chars: 规范全拼音节 -> 高频汉字串（最多 4 字，剔除叹词并补齐标准音节）
    """
    char_to_sp: dict[str, str] = {}
    char_to_syl: dict[str, str] = {}
    syl_buckets: dict[str, list[tuple[int, str]]] = {}

    with path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            cols = line.split("\t")
            if len(cols) < 4:
                continue
            char, syllable, freq_str, code = cols[0], cols[1], cols[2], cols[3]
            if len(char) != 1 or len(code) != 2:
                continue
            char_to_sp[char] = code
            norm = normalize_quanpin_syllable(syllable)
            if norm is None:
                continue
            char_to_syl[char] = norm
            try:
                freq = int(freq_str)
            except ValueError:
                freq = 1
            syl_buckets.setdefault(norm, []).append((freq, char))

    syl_to_chars: dict[str, str] = {}
    for syl, items in syl_buckets.items():
        items.sort(key=lambda pair: (-pair[0], pair[1]))
        syl_to_chars[syl] = "".join(ch for _, ch in items[:4])

    for syl, chars in EXTRA_SYLLABLE_CHARS.items():
        if syl not in syl_to_chars:
            syl_to_chars[syl] = chars

    return char_to_sp, char_to_syl, syl_to_chars


def load_hanzi(path: Path) -> dict[str, str]:
    """字 -> 小鹤双拼两键。跳过 # 注释行。"""
    char_to_sp, _, _ = load_hanzi_full(path)
    return char_to_sp


def normalize_quanpin_syllable(
    syllable: str,
    valid_syllables: frozenset[str] | None = None,
) -> str | None:
    """将带调/无调音节规整为标准键盘全拼音节：
    - 去除声调，`ü` 转为 `v`
    - 剔除非标准叹词（hm/hng/ng/m/n）与单儿化音 r
    - j/q/x/y 后一律用 u（ju/jue/juan/jun 等）
    - l/n + ue 规范为 lve/nve
    """
    plain = strip_tone(syllable).strip().lower()
    if not plain or not plain.isascii() or not plain.isalpha():
        return None
    if plain in NON_STANDARD_SYLLABLES:
        return None
    if plain[0] in ("j", "q", "x", "y") and "v" in plain:
        plain = plain.replace("v", "u")
    if plain == "lue":
        plain = "lve"
    elif plain == "nue":
        plain = "nve"
    if len(plain) > 6:
        return None
    if valid_syllables is not None and plain not in valid_syllables:
        return None
    return plain


def load_dict(path: Path) -> list[tuple[str, int, str]]:
    """返回 (词, 词频, 词性)，按词频降序。"""
    rows: list[tuple[str, int, str]] = []
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split(" ")
            if len(parts) < 2:
                continue
            word = parts[0]
            try:
                freq = int(parts[1])
            except ValueError:
                continue
            pos = parts[2] if len(parts) > 2 else ""
            rows.append((word, freq, pos))
    rows.sort(key=lambda r: (-r[1], r[0]))
    return rows


def load_phrases(path: Path) -> dict[str, list[str]]:
    """词 -> 音节列表（带调，已去注释）。行格式 `银行: yín háng`。

    音节数必须与字数一致，否则整条丢弃（如儿化音条目）。
    """
    table: dict[str, list[str]] = {}
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            word, sep, rest = line.partition(":")
            if not sep:
                continue
            syllables = rest.split("#")[0].split()
            if not syllables or len(syllables) != len(word):
                continue
            table[word] = syllables
    return table


def syllable_to_code(syllable: str) -> str | None:
    """带调音节 -> 双拼两键；无法解析返回 None。"""
    plain = strip_tone(syllable)
    if not plain.isascii() or not plain.isalpha() or not plain.islower():
        return None
    try:
        code = shuangpin(plain)
    except KeyError:
        return None
    return code if len(code) == 2 else None


def encode(
    word: str,
    table: dict[str, str],
    phrases: dict[str, list[str]] | None,
) -> tuple[str, bool] | None:
    """编码一个词，返回 (双拼码, 是否用了词组读音)；失败返回 None。

    优先用词组表（多音字按词消歧），词组里没有才逐字回落。
    """
    if not CJK.match(word):
        return None

    if phrases is not None and word in phrases:
        codes = []
        for syllable in phrases[word]:
            code = syllable_to_code(syllable)
            if code is None:
                break
            codes.append(code)
        else:
            return "".join(codes), True

    codes = []
    for char in word:
        code = table.get(char)
        if code is None:
            return None
        codes.append(code)
    return "".join(codes), False


def encode_syllables(
    word: str,
    char_to_syl: dict[str, str],
    phrases: dict[str, list[str]] | None,
    valid_syllables: frozenset[str],
) -> tuple[tuple[str, ...], bool] | None:
    """返回 (标准全拼音节元组, 是否命中词组表)；失败返回 None。"""
    if not CJK.match(word):
        return None

    if phrases is not None and word in phrases:
        syls = []
        for raw in phrases[word]:
            norm = normalize_quanpin_syllable(raw, valid_syllables)
            if norm is None:
                break
            syls.append(norm)
        else:
            return tuple(syls), True

    syls = []
    for char in word:
        syl = char_to_syl.get(char)
        if syl is None or syl not in valid_syllables:
            return None
        syls.append(syl)
    return tuple(syls), False


def select(
    rows: list[tuple[str, int, str]],
    table: dict[str, str],
    *,
    phrases: dict[str, list[str]] | None = None,
    idiom: bool,
    length: tuple[int, ...],
    limit: int,
    hit: list[int] | None = None,
) -> list[tuple[str, int, str, str]]:
    """筛选并编码，返回 (词, 词频, 双拼码, 词性)，已按词频降序。

    hit 若给出，会累加 [词组命中数, 逐字回退数]。
    """
    out: list[tuple[str, int, str, str]] = []
    for word, freq, pos in rows:
        if len(word) not in length:
            continue
        is_idiom = "i" in pos.split("|")
        if is_idiom != idiom:
            continue
        result = encode(word, table, phrases)
        if result is None:
            continue
        code, from_phrase = result
        if hit is not None:
            hit[0 if from_phrase else 1] += 1
        out.append((word, freq, code, pos))
        if len(out) >= limit:
            break
    return out


def select_entries(
    rows: list[tuple[str, int, str]],
    char_to_syl: dict[str, str],
    valid_syllables: frozenset[str],
    *,
    phrases: dict[str, list[str]] | None = None,
    idiom: bool,
    length: tuple[int, ...],
    limit: int,
    hit: list[int] | None = None,
) -> list[LexiconEntry]:
    """筛选并生成方案无关的标准拼音 LexiconEntry 列表。"""
    out: list[LexiconEntry] = []
    for word, freq, pos in rows:
        if len(word) not in length:
            continue
        is_idiom = "i" in pos.split("|")
        if is_idiom != idiom:
            continue
        result = encode_syllables(word, char_to_syl, phrases, valid_syllables)
        if result is None:
            continue
        syls, from_phrase = result
        if hit is not None:
            hit[0 if from_phrase else 1] += 1
        out.append(LexiconEntry(word=word, syllables=syls, freq=freq, is_idiom=is_idiom))
        if len(out) >= limit:
            break
    return out


def dedupe(items: list[tuple[str, int, str, str]]) -> list[tuple[str, int, str, str]]:
    """按词频降序去重（同码串保留最高频）；再按词频降序输出。"""
    seen: set[str] = set()
    kept: list[tuple[str, int, str, str]] = []
    for item in sorted(items, key=lambda r: (-r[1], r[0])):
        if item[2] in seen:
            continue
        seen.add(item[2])
        kept.append(item)
    return kept


def build_sp_to_syllable(valid_syllables: frozenset[str]) -> dict[str, str]:
    """构建小鹤双拼两键 -> 标准全拼音节的严格双射（排除仅作语气词、在双拼中与 luo 同码的 lo）。"""
    mapping: dict[str, str] = {}
    for syl in sorted(valid_syllables - {"lo"}):
        code = shuangpin(syl)
        if code in mapping:
            raise ValueError(f"双拼逆映射冲突: {code} -> {mapping[code]} / {syl}")
        mapping[code] = syl
    return mapping


def bootstrap_from_zhwords(
    zhwords_path: Path,
    valid_syllables: frozenset[str],
) -> list[LexiconEntry]:
    """离线复用已有多音字消歧的 zhWords.ts，还原 100% 精确的标准拼音中间词表。"""
    sp_to_syl = build_sp_to_syllable(valid_syllables)
    normal_raw: list[tuple[str, tuple[str, ...]]] = []
    idiom_raw: list[tuple[str, tuple[str, ...]]] = []

    with zhwords_path.open("r", encoding="utf-8") as f:
        for line in f:
            match = ZH_WORDS_LINE.match(line)
            if not match:
                continue
            sp_code, word = match.group(1), match.group(2)
            if not CJK.match(word) or len(sp_code) != len(word) * 2:
                raise ValueError(f"异常词条: {sp_code} -> {word}")
            syls: list[str] = []
            for i in range(len(word)):
                pair = sp_code[i * 2 : i * 2 + 2]
                syl = sp_to_syl.get(pair)
                if syl is None:
                    raise ValueError(f"未知双拼音节 {pair}（词条 {word} / {sp_code}）")
                syls.append(syl)
            if len(word) == 4:
                idiom_raw.append((word, tuple(syls)))
            else:
                normal_raw.append((word, tuple(syls)))

    # 依据原词表词频序赋予平滑 Zipf 词频，便于构建自然频率分布的声学模型
    entries: list[LexiconEntry] = []
    for rank, (word, syls) in enumerate(normal_raw):
        freq = max(200, round(100000 / ((rank + 10) ** 0.7)))
        entries.append(LexiconEntry(word=word, syllables=syls, freq=freq, is_idiom=False))
    for rank, (word, syls) in enumerate(idiom_raw):
        freq = max(150, round(25000 / ((rank + 10) ** 0.7)))
        entries.append(LexiconEntry(word=word, syllables=syls, freq=freq, is_idiom=True))
    return entries


def merge_and_dedupe_quanpin(
    base_entries: list[LexiconEntry],
    valid_syllables: frozenset[str],
    *,
    include_v_supplement: bool = True,
) -> list[LexiconEntry]:
    """合并基础词表与 curated v 键补充包，按全拼串去重（0 碰撞），按词频降序输出。"""
    seen_codes: set[str] = set()
    seen_words: set[str] = set()
    kept: list[LexiconEntry] = []

    for entry in sorted(base_entries, key=lambda e: (-e.freq, e.word)):
        qp = entry.quanpin
        if qp in seen_codes or entry.word in seen_words:
            continue
        assert all(s in valid_syllables for s in entry.syllables), f"非法音节: {entry}"
        seen_codes.add(qp)
        seen_words.add(entry.word)
        kept.append(entry)

    if include_v_supplement:
        for word, syls, freq, is_idiom in CURATED_V_WORDS:
            if not CJK.match(word) or len(word) != len(syls):
                raise ValueError(f"v 补充词条格式错误: {word} {syls}")
            norm_syls = tuple(
                normalize_quanpin_syllable(s, valid_syllables) or "" for s in syls
            )
            if any(not s for s in norm_syls):
                raise ValueError(f"v 补充词条含非法音节: {word} {syls}")
            qp = "".join(norm_syls)
            if qp in seen_codes or word in seen_words:
                continue
            seen_codes.add(qp)
            seen_words.add(word)
            kept.append(
                LexiconEntry(
                    word=word,
                    syllables=norm_syls,
                    freq=freq,
                    is_idiom=is_idiom,
                )
            )

    kept.sort(key=lambda e: (-e.freq, e.word))
    return kept


def write_qp_words_json(path: Path, entries: list[LexiconEntry]) -> None:
    codes = [e.quanpin for e in entries]
    assert len(codes) == len(set(codes)), "全拼词表存在重复码串"
    assert all(c.isascii() and c.isalpha() and c.islower() for c in codes), "含非小写字母"
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as f:
        json.dump(codes, f, ensure_ascii=False, indent=2)
        f.write("\n")


def write_qp_dict_gz(path: Path, entries: list[LexiconEntry]) -> int:
    """写出供 keybr 声学模型构建的 dictionary-zh-qp.csv.gz（多字全拼词，长度 ≥ 4）。"""
    filtered = [e for e in entries if len(e.quanpin) >= 4]
    letter_set = {ch for e in filtered for ch in e.quanpin}
    missing = set("abcdefghijklmnopqrstuvwxyz") - letter_set
    if missing:
        raise ValueError(f"dictionary-zh-qp 缺少字母: {sorted(missing)}")

    path.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(path, "wt", encoding="utf-8", newline="\n") as f:
        for e in filtered:
            f.write(f"{e.quanpin},{e.freq}\n")
    return len(filtered)


def write_qp_lexicon_ts(
    path: Path,
    entries: list[LexiconEntry],
    syl_to_chars: dict[str, str],
) -> None:
    """生成 packages/keybr-textinput/lib/zh-quanpin-lexicon.ts。"""
    lines: list[str] = [
        "// Generated by keytrace/tools/gen_zh_words.py — do not edit manually.",
        "// Full Pinyin (zh-qp) canonical lexicon & syllable fallback lookup.",
        "",
        "export type ZhQuanpinWord = {",
        "  readonly code: string;",
        "  readonly word: string;",
        "  readonly chars: readonly string[];",
        "  readonly lengths: readonly number[];",
        "  readonly syllables: readonly string[];",
        "  readonly isIdiom: boolean;",
        "};",
        "",
        "export const ZH_QP_LEXICON: Readonly<Record<string, string>> = {",
    ]
    for e in entries:
        val = f"{e.lengths_prefix}:{e.word}"
        lines.append(f'  "{e.quanpin}": "{val}",')
    lines.append("};")
    lines.append("")
    lines.append(
        "export const ZH_QP_SYLLABLE_TO_CHARS: Readonly<Record<string, string>> = {"
    )
    for syl in sorted(syl_to_chars):
        lines.append(f'  "{syl}": "{syl_to_chars[syl]}",')
    lines.append("};")
    lines.append("")
    lines.extend(
        [
            "const MAX_SYLLABLE_LENGTH = 6;",
            "const wordCache = new Map<string, ZhQuanpinWord>();",
            "",
            "export function resolveQuanpinWord(code: string): ZhQuanpinWord | null {",
            "  const key = code.toLowerCase();",
            "  const cached = wordCache.get(key);",
            "  if (cached != null) {",
            "    return cached;",
            "  }",
            "  if (!Object.hasOwn(ZH_QP_LEXICON, key)) {",
            "    return null;",
            "  }",
            "  const raw = ZH_QP_LEXICON[key];",
            '  const sep = raw.indexOf(":");',
            "  if (sep <= 0) {",
            "    return null;",
            "  }",
            "  const lengthsPart = raw.slice(0, sep);",
            "  const word = raw.slice(sep + 1);",
            "  const chars = Array.from(word);",
            "  const lengths: number[] = [];",
            "  const syllables: string[] = [];",
            "  let offset = 0;",
            "  for (let i = 0; i < lengthsPart.length; i++) {",
            "    const len = lengthsPart.charCodeAt(i) - 48;",
            "    lengths.push(len);",
            "    syllables.push(key.slice(offset, offset + len));",
            "    offset += len;",
            "  }",
            "  const resolved: ZhQuanpinWord = Object.freeze({",
            "    code: key,",
            "    word,",
            "    chars: Object.freeze(chars),",
            "    lengths: Object.freeze(lengths),",
            "    syllables: Object.freeze(syllables),",
            "    isIdiom: chars.length === 4,",
            "  });",
            "  wordCache.set(key, resolved);",
            "  return resolved;",
            "}",
            "",
            "export function splitQuanpinFallback(code: string): readonly string[] {",
            "  const n = code.length;",
            "  if (n === 0) {",
            "    return [];",
            "  }",
            "  const lower = code.toLowerCase();",
            "  const unmatched = new Int32Array(n + 1);",
            "  const count = new Int32Array(n + 1);",
            "  const next = new Int32Array(n + 1);",
            "",
            "  for (let i = n - 1; i >= 0; i--) {",
            "    let bestUnmatched = unmatched[i + 1] + 1;",
            "    let bestCount = count[i + 1] + 1;",
            "    let bestNext = i + 1;",
            "",
            "    const maxLen = Math.min(MAX_SYLLABLE_LENGTH, n - i);",
            "    for (let len = maxLen; len >= 1; len--) {",
            "      const candidate = lower.slice(i, i + len);",
            "      if (Object.hasOwn(ZH_QP_SYLLABLE_TO_CHARS, candidate)) {",
            "        const candUnmatched = unmatched[i + len];",
            "        const candCount = count[i + len] + 1;",
            "        if (",
            "          candUnmatched < bestUnmatched ||",
            "          (candUnmatched === bestUnmatched && candCount < bestCount)",
            "        ) {",
            "          bestUnmatched = candUnmatched;",
            "          bestCount = candCount;",
            "          bestNext = i + len;",
            "        }",
            "      }",
            "    }",
            "",
            "    unmatched[i] = bestUnmatched;",
            "    count[i] = bestCount;",
            "    next[i] = bestNext;",
            "  }",
            "",
            "  const result: string[] = [];",
            "  let offset = 0;",
            "  while (offset < n) {",
            "    const end = next[offset];",
            "    result.push(code.slice(offset, end));",
            "    offset = end;",
            "  }",
            "  return result;",
            "}",
            "",
            "export function isQuanpinIdiom(code: string): boolean {",
            "  const entry = resolveQuanpinWord(code);",
            "  return entry != null && entry.isIdiom;",
            "}",
            "",
        ]
    )

    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


def run_quanpin_pipeline(args: argparse.Namespace) -> int:
    _, char_to_syl, syl_to_chars = load_hanzi_full(args.hanzi)
    if not syl_to_chars:
        print(f"错误：{args.hanzi} 没有可用数据", file=sys.stderr)
        return 1
    valid_syllables = frozenset(syl_to_chars.keys())

    if args.bootstrap_from_zhwords is not None:
        base_entries = bootstrap_from_zhwords(args.bootstrap_from_zhwords, valid_syllables)
    else:
        rows = load_dict(args.dict)
        phrases = None
        if args.phrase_pinyin is not None:
            phrases = load_phrases(args.phrase_pinyin)
            if not phrases:
                print(f"错误：{args.phrase_pinyin} 没有可用数据", file=sys.stderr)
                return 1
        hit = [0, 0]
        idioms = select_entries(
            rows,
            char_to_syl,
            valid_syllables,
            phrases=phrases,
            idiom=True,
            length=(4,),
            limit=args.idioms,
            hit=hit,
        )
        word_limit = args.words
        if args.idiom_ratio > 0 and idioms:
            target = round(len(idioms) * (1 - args.idiom_ratio) / args.idiom_ratio)
            word_limit = min(word_limit, target)
        words = select_entries(
            rows,
            char_to_syl,
            valid_syllables,
            phrases=phrases,
            idiom=False,
            length=(2, 3),
            limit=word_limit,
            hit=hit,
        )
        base_entries = words + idioms

    entries = merge_and_dedupe_quanpin(
        base_entries,
        valid_syllables,
        include_v_supplement=not args.no_v_supplement,
    )
    # 声学模型词典保持自然语料分布（不叠加人工 v 补充词，避免 3-gram 归一化把 v 键频率虚高抬升）
    natural_entries = merge_and_dedupe_quanpin(
        base_entries,
        valid_syllables,
        include_v_supplement=False,
    )

    v_count = sum(1 for e in entries if "v" in e.quanpin)
    idiom_count = sum(1 for e in entries if e.is_idiom)
    letter_counts = Counter(ch for e in entries for ch in set(e.quanpin))
    missing_letters = sorted(set("abcdefghijklmnopqrstuvwxyz") - set(letter_counts))

    assert not missing_letters, f"全拼词表缺少字母: {missing_letters}"
    assert v_count >= 80 or args.no_v_supplement, f"含 v 键词数不足 80: {v_count}"

    if args.out_intermediate is not None:
        args.out_intermediate.parent.mkdir(parents=True, exist_ok=True)
        with args.out_intermediate.open("w", encoding="utf-8", newline="\n") as f:
            json.dump(
                [e.to_json_obj() for e in entries],
                f,
                ensure_ascii=False,
                indent=2,
            )
            f.write("\n")
        print(f"已写入中间词表 {args.out_intermediate} ({len(entries)} 条)")

    if args.out_qp_words is not None:
        write_qp_words_json(args.out_qp_words, entries)
        print(f"已写入 {args.out_qp_words} ({len(entries)} 条)")

    if args.out_qp_dict is not None:
        dict_count = write_qp_dict_gz(args.out_qp_dict, natural_entries)
        print(f"已写入 {args.out_qp_dict} ({dict_count} 条自然语料词，长度均 >= 4)")

    if args.out_qp_lexicon is not None:
        write_qp_lexicon_ts(args.out_qp_lexicon, entries, syl_to_chars)
        print(
            f"已写入 {args.out_qp_lexicon} ({len(entries)} 词，{len(syl_to_chars)} 标准音节)"
        )

    print(
        f"全拼汇总：总计 {len(entries)} 词（普通词 {len(entries) - idiom_count}，"
        f"成语 {idiom_count}），含 v 词 {v_count} 条，26 字母全覆盖"
    )
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="生成 keybr 中文双拼 / 全拼词组词表",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    ap.add_argument(
        "--hanzi",
        type=Path,
        default=Path("data/zh-hanzi.tsv"),
        help="zh-hanzi.tsv 路径",
    )
    ap.add_argument(
        "--dict",
        type=Path,
        default=Path("/tmp/dict.txt"),
        help="jieba dict.txt 路径",
    )
    ap.add_argument(
        "--phrase-pinyin",
        type=Path,
        default=None,
        help="mozillazg/phrase-pinyin-data 的 pinyin.txt；不传则退回逐字查表",
    )
    ap.add_argument(
        "--bootstrap-from-zhwords",
        type=Path,
        default=None,
        help="离线复用已有 zhWords.ts 还原标准拼音中间词表",
    )
    ap.add_argument(
        "--out",
        type=Path,
        default=Path(
            "../keybr.com/packages/keybr-content-words/lib/data/words-zh-sp.json"
        ),
        help="双拼模式输出的 words-zh-sp.json 路径",
    )
    ap.add_argument(
        "--out-intermediate",
        type=Path,
        default=None,
        help="输出方案无关的标准拼音中间词表 JSON",
    )
    ap.add_argument(
        "--out-qp-words",
        type=Path,
        default=None,
        help="输出全拼 words-zh-qp.json 路径",
    )
    ap.add_argument(
        "--out-qp-dict",
        type=Path,
        default=None,
        help="输出全拼声学模型词典 dictionary-zh-qp.csv.gz 路径",
    )
    ap.add_argument(
        "--out-qp-lexicon",
        type=Path,
        default=None,
        help="输出全拼运行时词表 zh-quanpin-lexicon.ts 路径",
    )
    ap.add_argument(
        "--no-v-supplement",
        action="store_true",
        help="禁用内置常用 v 键词/成语补充包",
    )
    ap.add_argument("--words", type=int, default=4000, help="普通词上限")
    ap.add_argument("--idioms", type=int, default=1500, help="成语上限")
    ap.add_argument(
        "--idiom-ratio",
        type=float,
        default=0.25,
        help="目标成语占比；成语不够时按实际比例，普通词数也按此比例回算（0 表示不限制）",
    )
    args = ap.parse_args(argv)

    qp_mode = any(
        x is not None
        for x in (
            args.bootstrap_from_zhwords,
            args.out_intermediate,
            args.out_qp_words,
            args.out_qp_dict,
            args.out_qp_lexicon,
        )
    )
    if qp_mode:
        return run_quanpin_pipeline(args)

    table = load_hanzi(args.hanzi)
    rows = load_dict(args.dict)
    if not table:
        print(f"错误：{args.hanzi} 没有可用数据", file=sys.stderr)
        return 1

    phrases = None
    if args.phrase_pinyin is not None:
        phrases = load_phrases(args.phrase_pinyin)
        if not phrases:
            print(f"错误：{args.phrase_pinyin} 没有可用数据", file=sys.stderr)
            return 1

    # [词组命中, 逐字回退]
    hit = [0, 0]

    idioms = select(
        rows, table, phrases=phrases, idiom=True, length=(4,), limit=args.idioms, hit=hit
    )

    word_limit = args.words
    if args.idiom_ratio > 0 and idioms:
        # 让 成语 / (成语 + 普通词) ≈ idiom-ratio，但不突破 --words 上限
        target = round(len(idioms) * (1 - args.idiom_ratio) / args.idiom_ratio)
        word_limit = min(word_limit, target)

    words = select(
        rows,
        table,
        phrases=phrases,
        idiom=False,
        length=(2, 3),
        limit=word_limit,
        hit=hit,
    )

    merged = dedupe(words + idioms)
    ratio = len(idioms) / len(merged) if merged else 0.0

    codes = [item[2] for item in merged]
    assert all(c.isascii() and c.islower() for c in codes), "输出含非小写字母"
    assert all(len(c) % 2 == 0 for c in codes), "输出存在奇数长度码串"

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", encoding="utf-8", newline="\n") as f:
        json.dump(codes, f, ensure_ascii=False, indent=2)

    print(f"普通词 {len(words)} 条，成语 {len(idioms)} 条")
    print(f"去重后总计 {len(merged)} 条，成语占比 {ratio:.1%}")
    if phrases is not None:
        total_hit = hit[0] + hit[1]
        share = hit[0] / total_hit if total_hit else 0.0
        print(f"读音来源：词组表 {hit[0]} 条（{share:.1%}），逐字回退 {hit[1]} 条")
    print(f"已写入 {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
