#!/usr/bin/env python3
"""为 keybr 生成「中文双拼」词组词表（普通词 + 四字成语）。

输入
  --hanzi          zh-hanzi.tsv  制表符分隔：字 / 音节 / 词频 / 小鹤双拼两键（第 4 列）
  --dict           dict.txt      jieba 词频词典：词 词频 词性（词性 i = 成语）
  --phrase-pinyin  pinyin.txt    mozillazg/phrase-pinyin-data：词: 带调拼音（可选）
  --out            words-zh-sp.json

规则
  1. 普通词：2–3 个汉字，按词频取前 N 个
  2. 成语：4 个汉字且词性含 i，按词频取前 M 个
  3. 发音优先查词组表（多音字消歧正确），查不到才逐字查 hanzi 表；
     两条路都查不到则整词丢弃
  4. 过滤：纯数字、含英文字母/标点
  5. 合并后按词频降序，同一个双拼码串只保留词频最高的那一个
  6. 输出全部为小写字母，长度必为偶数（每汉字 = 2 键）

示例
  python tools/gen_zh_words.py \
      --hanzi data/zh-hanzi.tsv \
      --dict /tmp/dict.txt \
      --phrase-pinyin /tmp/phrase-pinyin.txt \
      --out ../keybr.com/packages/keybr-content-words/lib/data/words-zh-sp.json \
      --words 4000 --idioms 1500 --idiom-ratio 0.25
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

from gen_zh_data import shuangpin, strip_tone

CJK = re.compile(r"^[\u4e00-\u9fff]+$")


def load_hanzi(path: Path) -> dict[str, str]:
    """字 -> 小鹤双拼两键。跳过 # 注释行。"""
    table: dict[str, str] = {}
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line or line.startswith("#"):
                continue
            cols = line.split("\t")
            if len(cols) < 4:
                continue
            char, _syllable, _freq, code = cols[0], cols[1], cols[2], cols[3]
            if len(char) == 1 and len(code) == 2:
                table[char] = code
    return table


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


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="生成 keybr 中文双拼词组词表",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    ap.add_argument("--hanzi", type=Path, default=Path("data/zh-hanzi.tsv"), help="zh-hanzi.tsv 路径")
    ap.add_argument("--dict", type=Path, default=Path("/tmp/dict.txt"), help="jieba dict.txt 路径")
    ap.add_argument(
        "--phrase-pinyin",
        type=Path,
        default=None,
        help="mozillazg/phrase-pinyin-data 的 pinyin.txt；不传则退回逐字查表",
    )
    ap.add_argument(
        "--out",
        type=Path,
        default=Path(
            "../keybr.com/packages/keybr-content-words/lib/data/words-zh-sp.json"
        ),
        help="输出的 words-zh-sp.json 路径",
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
