#!/usr/bin/env python3
"""把字表汇总成 keybr 能吃的音节字典。

思路：keybr 的语言模型是"字母序列 + 词频"，一个词就是屏幕上一串要打的字符。
小鹤双拼下一个汉字正好是两个字母（bm=便、hc=好），所以把**音节当词**：
  - alphabet = a–z（就是双拼的 26 个键）
  - 词 = 两字母音节
  - 词频 = 该音节下所有汉字的词频之和

这样 keybr 的音素模型和调度器完全不用改：它以为在练一门"两字母单词组成的语言"，
实际上练的是双拼的两键组合。汉字显示不了，但按键肌肉记忆正是要练的东西。

用法：
  python tools/gen_keybr_zh_dict.py data/zh-hanzi.tsv dictionary-zh-shuangpin.csv.gz
"""

import csv
import gzip
import sys
from collections import Counter


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 1

    freq: Counter = Counter()
    examples: dict[str, list[str]] = {}

    with open(sys.argv[1], encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            char, syllable, count, code = line.rstrip("\n").split("\t")
            freq[code] += int(count)
            examples.setdefault(code, []).append(char)

    # keybr 按词频排序取前 N 个词，所以高频音节排前面
    rows = sorted(freq.items(), key=lambda kv: -kv[1])

    with gzip.open(sys.argv[2], "wt", encoding="utf-8", newline="\n") as out:
        for code, count in rows:
            out.write(f"{code},{count}\n")

    print(f"写出 {len(rows)} 个音节 -> {sys.argv[2]}")
    print("抽样：", end="")
    for code, count in rows[:8]:
        print(f"{code}({''.join(examples[code][:3])}) ", end="")
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
