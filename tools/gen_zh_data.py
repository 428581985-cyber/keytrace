#!/usr/bin/env python3
"""从上游数据生成仓库自带的中文精简表。

上游：
  mozillazg/pinyin-data  kMandarin_8105.txt   《通用规范汉字表》8105 字最常用读音  (MIT)
  fxsjy/jieba            dict.txt             词频词典                              (MIT)

不算梯子：这两个文件加起来 5 MB，没必要进仓库。这里只留"字 + 音节 + 词频"三列，
约 8000 行、100 KB 左右，够用且能当审阅对象。

用法：
  python tools/gen_zh_data.py km.txt jieba.txt data/zh-hanzi.tsv
"""

import sys
import unicodedata
from collections import Counter

# 声母 -> 键位。zh/ch/sh 各占一个键，y/w 当普通声母处理。
INITIALS = {
    "zh": "v", "ch": "i", "sh": "u",
    "b": "b", "p": "p", "m": "m", "f": "f", "d": "d", "t": "t",
    "n": "n", "l": "l", "g": "g", "k": "k", "h": "h",
    "j": "j", "q": "q", "x": "x", "r": "r", "z": "z", "c": "c", "s": "s",
    "y": "y", "w": "w",
}

# 韵母 -> 键位（小鹤双拼）。同键的韵母用逗号隔开。
FINALS = {
    "a": "a", "o": "o", "e": "e", "i": "i", "u": "u", "v": "v",
    "ai": "d", "ei": "w", "ao": "c", "ou": "z",
    "an": "j", "en": "f", "ang": "h", "eng": "g", "er": "r",
    "ia": "x", "ie": "p", "iao": "n", "iu": "q",
    "ian": "m", "in": "b", "iang": "l", "ing": "k", "iong": "s",
    "ua": "x", "uo": "o", "uai": "k", "ui": "v",
    "uan": "r", "uang": "l", "un": "y", "ue": "t",
    "ong": "s", "ve": "t", "vn": "y",
}

TONE_MARKS = "\u0300\u0301\u0302\u0304\u0306\u0308\u030c"


def strip_tone(text: str) -> str:
    """去掉声调符号；ü 先换成 v，否则 NFD 拆出来的 diaeresis 会被一起删掉。"""
    text = text.replace("ü", "v").replace("ǖ", "v").replace("ǘ", "v")
    text = text.replace("ǚ", "v").replace("ǜ", "v")
    decomposed = unicodedata.normalize("NFD", text)
    return "".join(c for c in decomposed if c not in TONE_MARKS)


def shuangpin(syllable: str) -> str:
    """音节 -> 两个键。规则见 flypy.cc 官网「零声母」一节。"""
    for prefix in ("zh", "ch", "sh"):
        if syllable.startswith(prefix):
            return INITIALS[prefix] + FINALS[syllable[len(prefix):]]
    if syllable[0] in INITIALS and len(syllable) > 1:
        return INITIALS[syllable[0]] + FINALS[syllable[1:]]

    # 零声母：单字母双写（啊 aa）、双字母照全拼（爱 ai）、三字母取首字母+韵母键（昂 ah）
    if len(syllable) == 1:
        return syllable + syllable
    if len(syllable) == 2:
        return syllable
    return syllable[0] + FINALS[syllable[1:]]


def read_pinyin(path: str) -> dict[str, str]:
    """U+4E2D: zhōng,zhòng # 中 -> {'中': 'zhong'}，只取第一个读音。"""
    table: dict[str, str] = {}
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        code, _, rest = line.partition(":")
        if not rest:
            continue
        readings = rest.split("#")[0].strip()
        first = readings.split(",")[0].strip()
        if not first or first in ("?", "->", "<-"):
            continue
        char = "".join(
            chr(int(part[2:], 16)) for part in code.split() if part.startswith("U+")
        )
        if len(char) != 1:
            continue
        syllable = strip_tone(first)
        if syllable and all(c in "abcdefghijklmnopqrstuvwxyzv" for c in syllable):
            table[char] = syllable
    return table


def read_freq(path: str) -> Counter:
    """jieba dict.txt：字 词频 词性。只留单字。"""
    freq: Counter = Counter()
    for line in open(path, encoding="utf-8"):
        parts = line.split()
        if len(parts) >= 2 and len(parts[0]) == 1:
            try:
                freq[parts[0]] += int(parts[1])
            except ValueError:
                continue
    return freq


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__)
        return 1

    pinyin = read_pinyin(sys.argv[1])
    freq = read_freq(sys.argv[2])

    rows = []
    for char, syllable in pinyin.items():
        if char not in freq:
            continue
        try:
            code = shuangpin(syllable)
        except KeyError:
            continue
        rows.append((freq[char], char, syllable, code))

    rows.sort(reverse=True)

    with open(sys.argv[3], "w", encoding="utf-8", newline="\n") as out:
        out.write("# 字\t音节\t词频\t小鹤双拼码\n")
        out.write("# 由 tools/gen_zh_data.py 生成，勿手改\n")
        out.write("# 数据来源：mozillazg/pinyin-data (MIT)、fxsjy/jieba (MIT)\n")
        for f, char, syllable, code in rows:
            out.write(f"{char}\t{syllable}\t{f}\t{code}\n")

    print(f"写出 {len(rows)} 字 -> {sys.argv[3]}")
    print("抽样：", end="")
    for f, char, syllable, code in rows[:8]:
        print(f"{char}({syllable}={code}) ", end="")
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
