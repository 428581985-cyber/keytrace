using KeyTrace.Collect;

namespace KeyTrace.Analysis;

internal sealed class AttributorOptions
{
    /// <summary>一个词内部允许的连续退格上限；超过就算改措辞，不算打错。</summary>
    public int TypoMaxBackspaces { get; init; } = 3;

    /// <summary>
    /// 停顿门槛 = 该键 IKI 中位数 × 这个倍数。
    /// 出处是 Inputlog 的" individualized pause "：pause 定义为 IKI 超过个人中位数的 a 倍，a 通常取 2–3。
    /// 不用论文里的 mean+2SD：IKI 是重尾分布，实测 96% 的退格都能通过那个门槛，规则等于没写。
    /// </summary>
    public double PauseFactor { get; init; } = 3.0;

    /// <summary>样本少于此数的键不单独定门槛，回落到全局中位数。</summary>
    public int MinKeySamples { get; init; } = 20;

    /// <summary>与采集层的会话切分保持一致：空闲超过 5 秒不算同一次输入。</summary>
    public long IdleMs { get; init; } = 5000;

    /// <summary>comp 到 commit 的最大间隔，超过就不再认为二者相关。</summary>
    public long CompWindowMs { get; init; } = 3000;

    /// <summary>汉字上屏后退格，超过这个间隔就不算选字错。</summary>
    public long SelectionWindowMs { get; init; } = 3000;

    /// <summary>词结束后延后判定，等可能的 comp/commit 到齐。上屏的空格会先关词，commit 后到。</summary>
    public long EvaluateDelayMs { get; init; } = 2000;
}

internal sealed class KeyBaseline
{
    public int Vk { get; init; }
    public int Samples { get; init; }
    public double P50 { get; init; }
}

/// <summary>全局与逐键的 IKI 基线。门槛在构造时算好，判定阶段只查表。</summary>
internal sealed class Baseline
{
    public int Presses { get; init; }
    public double GlobalP50 { get; init; }
    public double GlobalMean { get; init; }
    public double GlobalSd { get; init; }
    public double PauseFactor { get; init; }
    public Dictionary<int, KeyBaseline> Keys { get; init; } = [];

    /// <summary>这个键的停顿门槛；样本不够就用全局中位数。</summary>
    public double Threshold(int vk) =>
        PauseFactor * (Keys.TryGetValue(vk, out KeyBaseline? k) && k.Samples > 0 ? k.P50 : GlobalP50);
}

internal sealed class KeyStat
{
    public int Vk { get; init; }
    public int Presses { get; set; }
    public int Shifted { get; set; }
    public int Typos { get; set; }
    public int PinyinTypos { get; set; }
    public List<long> Iki { get; } = [];
    public List<long> Dwell { get; } = [];
}

internal sealed class SymbolStat
{
    public char Char { get; init; }
    public int Vk { get; set; }
    public bool Shift { get; set; }
    public int Count { get; set; }
    public int Typos { get; set; }
}

internal sealed class AppStat
{
    public string Exe { get; init; } = "";
    public int Keystrokes { get; set; }
    public int Commits { get; set; }
    public int Chars { get; set; }
    public int Typos { get; set; }
    public int Backspaces { get; set; }
    public bool TextAvailable { get; set; }
}

internal sealed class SkipCounters
{
    public int Paste { get; set; }
    public int Shortcut { get; set; }
    public int Modifier { get; set; }
    public int Hotkey { get; set; }
    public int Glyph { get; set; }
    public int Synthetic { get; set; }
    public int EmptyBackspace { get; set; }
    public int DeleteKey { get; set; }
    public int Unparsable { get; set; }

    public int Total => Paste + Shortcut + Modifier + Hotkey + Glyph + Synthetic + EmptyBackspace + DeleteKey + Unparsable;
}

internal sealed class ChineseCounters
{
    public int Commits { get; set; }
    public int CommitsWithComp { get; set; }
    public int Chars { get; set; }
    public int Comps { get; set; }
    public int PinyinErrors { get; set; }
    public int SelectionErrors { get; set; }
    public Dictionary<int, int> PinyinErrorKeys { get; } = [];
}

/// <summary>一条错误记录，写到 typos.jsonl。金标标注就靠它逐条看。</summary>
internal sealed class TypoRecord
{
    public long Ts { get; init; }
    public string Sid { get; init; } = "";
    public string Exe { get; init; } = "";
    public string Kind { get; init; } = "typo";
    public int Vk { get; init; }
    public string Key { get; init; } = "";
    public char? Deleted { get; init; }
    public char? Replacement { get; init; }
    public int RunLen { get; init; }
    public long? Iki { get; init; }
    public string Cause { get; init; } = "ambiguous";
    public double Confidence { get; init; }
    public string Word { get; init; } = "";
    public int Position { get; init; }
}

internal sealed class Counters
{
    public int Keystrokes { get; set; }
    public int Chars { get; set; }
    public int Commits { get; set; }
    public int Sessions { get; set; }
    public int Backspaces { get; set; }
    public int Typos { get; set; }
    public int PinyinErrors { get; set; }
    public int SelectionErrors { get; set; }
    public int RewordingLongPause { get; set; }
    public int RewordingLongRun { get; set; }
    public int Collateral { get; set; }
    public long ActiveMs { get; set; }

    public int Rewording => RewordingLongPause + RewordingLongRun;
}

internal sealed class AttributionResult
{
    public Baseline Baseline { get; init; } = new();
    public Counters Totals { get; } = new();
    public SkipCounters Skipped { get; } = new();
    public ChineseCounters Chinese { get; } = new();
    public Dictionary<int, KeyStat> Keys { get; } = [];
    public Dictionary<char, SymbolStat> Symbols { get; } = [];
    public Dictionary<string, AppStat> Apps { get; } = [];
    public List<TypoRecord> Records { get; } = [];
    public Dictionary<string, string> Meta { get; } = [];
}

/// <summary>
/// 归因层：把击键序列还原成"哪一下打错了"。
///
/// 两遍扫描：第一遍只求基线（各键 IKI 中位数），第二遍才判定。
/// 一遍做不到，因为"这个退格算不算打错"要拿门槛比，而门槛来自全体样本。
/// </summary>
internal sealed class Attributor
{
    private readonly AttributorOptions _opt;

    public Attributor(AttributorOptions? options = null) => _opt = options ?? new AttributorOptions();

    public AttributionResult Run(IReadOnlyList<EventRecord> events, int unparsable = 0)
    {
        // 调用方一般已经按 mono 排好（EventLog.Read 会排），这里再兜一次：
        // 顺序错了会把"上屏后退格"判成"删缓冲区的字母"，错得很隐蔽。
        List<EventRecord> ordered = [.. events];
        ordered.Sort((a, b) => a.Mono.CompareTo(b.Mono));

        AttributionResult result = new() { Baseline = ComputeBaseline(ordered) };
        result.Skipped.Unparsable = unparsable;

        new Walker(_opt, result).Walk(ordered);
        return result;
    }

    private Baseline ComputeBaseline(IReadOnlyList<EventRecord> events)
    {
        Dictionary<int, List<long>> samples = [];
        List<long> all = [];
        long previousMono = 0;
        int previousVk = 0;
        string previousSid = "";

        foreach (EventRecord e in events)
        {
            if (e.Ev != "kd" || e.Sc == 0 || KeyCatalog.IsModifier(e.Vk) || e.HasCommandModifier)
            {
                continue;
            }

            bool sameRun = e.Sid == previousSid && previousMono > 0;
            long iki = e.Mono - previousMono;
            if (sameRun && iki > 0 && iki <= _opt.IdleMs)
            {
                if (!samples.TryGetValue(e.Vk, out List<long>? list))
                {
                    list = [];
                    samples[e.Vk] = list;
                }

                list.Add(iki);
                all.Add(iki);
            }

            previousMono = e.Mono;
            previousVk = e.Vk;
            previousSid = e.Sid;
        }

        all.Sort();
        Dictionary<int, KeyBaseline> keys = [];
        foreach ((int vk, List<long> list) in samples)
        {
            list.Sort();
            keys[vk] = new KeyBaseline { Vk = vk, Samples = list.Count, P50 = Percentile(list, 0.5) };
        }

        return new Baseline
        {
            Presses = all.Count,
            GlobalP50 = Percentile(all, 0.5),
            GlobalMean = all.Count == 0 ? 0 : all.Average(),
            GlobalSd = StdDev(all),
            PauseFactor = _opt.PauseFactor,
            Keys = keys,
        };
    }

    internal static double Percentile(List<long> sorted, double q)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        double pos = q * (sorted.Count - 1);
        int low = (int)Math.Floor(pos);
        int high = (int)Math.Ceiling(pos);
        if (low == high)
        {
            return sorted[low];
        }

        double weight = pos - low;
        return sorted[low] * (1 - weight) + sorted[high] * weight;
    }

    internal static double StdDev(List<long> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        double mean = values.Average();
        double sum = 0;
        foreach (long v in values)
        {
            sum += (v - mean) * (v - mean);
        }

        return Math.Sqrt(sum / values.Count);
    }

    private sealed class Slot
    {
        public char Char { get; init; }
        public int Vk { get; init; }
    }

    private sealed class Deletion
    {
        public int Index { get; init; }
        public char Char { get; init; }
        public int Vk { get; init; }
        public long? Iki { get; init; }
        public int RunId { get; init; }
        public int RunPos { get; init; }
        public long Mono { get; init; }
        public long Ts { get; init; }
        public char? Replacement { get; set; }
    }

    private sealed class WordState
    {
        public List<Slot> Slots { get; } = [];
        public List<Deletion> Deletions { get; } = [];
        public bool HasComp { get; set; }
        public long StartMono { get; set; }
        public long CloseMono { get; set; }
        public string Exe { get; set; } = "";
        public string Sid { get; set; } = "";

        public string Text => new(Slots.Select(s => s.Char).ToArray());
    }

    private sealed class Walker
    {
        private readonly AttributorOptions _opt;
        private readonly AttributionResult _r;

        private readonly List<WordState> _closed = [];
        private WordState? _open;

        private readonly Dictionary<int, int> _runLengths = [];
        private int _runId;
        private int _runLen;

        private long _previousMono;
        private int _previousVk;
        private string _previousSid = "";

        private long? _compMono;
        private long? _lastCjkCommitMono;

        private readonly Dictionary<int, long> _downMono = [];
        private readonly Dictionary<string, (long First, long Last)> _sessionSpan = [];

        public Walker(AttributorOptions opt, AttributionResult result)
        {
            _opt = opt;
            _r = result;
        }

        public void Walk(IReadOnlyList<EventRecord> events)
        {
            foreach (EventRecord e in events)
            {
                if (e.Sid != _previousSid)
                {
                    CloseWord(e.Mono);

                    // 换会话就把上一段彻底定性：不会再有 comp/commit 来补了，
                    // 留着未判定的词会让"上屏后退格"错判成"删缓冲区里的字母"。
                    Flush(long.MaxValue);
                    _compMono = null;
                    _lastCjkCommitMono = null;
                    _previousSid = e.Sid;
                    _previousMono = 0;
                }

                if (!_sessionSpan.TryGetValue(e.Sid, out (long First, long Last) span))
                {
                    span = (e.Mono, e.Mono);
                }

                _sessionSpan[e.Sid] = (span.First, Math.Max(span.Last, e.Mono));

                switch (e.Ev)
                {
                    case "meta":
                        ReadMeta(e);
                        break;
                    case "kd":
                        OnKeyDown(e);
                        break;
                    case "ku":
                        OnKeyUp(e);
                        break;
                    case "comp":
                        OnComposition(e);
                        break;
                    case "commit":
                        OnCommit(e);
                        break;
                }

                Flush(e.Mono);
            }

            CloseWord(long.MaxValue);
            Flush(long.MaxValue);

            _r.Totals.Sessions = _sessionSpan.Count;
            foreach ((long first, long last) in _sessionSpan.Values)
            {
                _r.Totals.ActiveMs += Math.Max(0, last - first);
            }
        }

        private void ReadMeta(EventRecord e)
        {
            if (e.Text is null)
            {
                return;
            }

            foreach (string pair in e.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    _r.Meta[pair[..eq]] = pair[(eq + 1)..];
                }
            }
        }

        private void OnKeyDown(EventRecord e)
        {
            if (e.Sc == 0)
            {
                _r.Skipped.Synthetic++;
                return;
            }

            if (KeyCatalog.IsModifier(e.Vk))
            {
                _r.Skipped.Modifier++;
                _downMono.TryAdd(e.Vk, e.Mono);
                return;
            }

            if (e.HasCommandModifier)
            {
                // Ctrl+Alt+P 这类全局热键也会走到这里：没有上屏文本、带命令修饰，
                // 按事件格式文档的约定直接丢弃。
                _r.Skipped.Shortcut++;
                _previousMono = 0;
                return;
            }

            KeyStat key = KeyOf(e.Vk);
            key.Presses++;
            if (e.HasShift)
            {
                key.Shifted++;
            }

            AppStat app = AppOf(e.Exe);
            app.Keystrokes++;
            _r.Totals.Keystrokes++;

            long? iki = _previousMono > 0 && e.Mono > _previousMono ? e.Mono - _previousMono : null;
            if (iki is > 0)
            {
                if (iki > _opt.IdleMs)
                {
                    iki = null;
                }
                else
                {
                    key.Iki.Add(iki.Value);
                }
            }

            if (e.Vk == 8)
            {
                OnBackspace(e, iki, app);
            }
            else if (e.Vk == 46)
            {
                _r.Skipped.DeleteKey++;
            }
            else
            {
                char? ch = KeyCatalog.Char(e.Vk, e.HasShift);
                switch (KeyCatalog.Role(e.Vk))
                {
                    case KeyRole.Letter:
                    case KeyRole.Digit:
                    case KeyRole.Symbol:
                        AppendSlot(e, ch!.Value);
                        break;
                    default:
                        // 空格 / 回车 / 方向键都是词边界
                        CloseWord(e.Mono);
                        break;
                }
            }

            _previousMono = e.Mono;
            _previousVk = e.Vk;
        }

        private void AppendSlot(EventRecord e, char ch)
        {
            // 一旦开始敲新内容，"刚上屏的汉字"就不再是退格候选
            _lastCjkCommitMono = null;

            WordState word = EnsureOpen(e);
            Deletion? overwritten = word.Deletions.LastOrDefault(d => d.Index == word.Slots.Count && d.Replacement is null);
            if (overwritten is not null)
            {
                overwritten.Replacement = ch;
            }

            word.Slots.Add(new Slot { Char = ch, Vk = e.Vk });
        }

        private void OnBackspace(EventRecord e, long? iki, AppStat app)
        {
            _r.Totals.Backspaces++;
            app.Backspaces++;

            if (_previousVk == 8)
            {
                _runLen++;
            }
            else
            {
                _runId++;
                _runLen = 1;
            }

            _runLengths[_runId] = _runLen;

            WordState? word = _open ?? (_closed.Count > 0 ? _closed[^1] : null);
            if (word is null || word.Slots.Count == 0)
            {
                // 缓冲区里没有东西可删：要么删的是上屏的汉字，要么删的是更早的内容
                if (_lastCjkCommitMono is long commitMono && e.Mono - commitMono <= _opt.SelectionWindowMs)
                {
                    _r.Chinese.SelectionErrors++;
                    _r.Totals.SelectionErrors++;
                    _r.Records.Add(new TypoRecord
                    {
                        Ts = e.Ts,
                        Sid = e.Sid,
                        Exe = e.Exe,
                        Kind = "selection",
                        Iki = iki,
                        Cause = "wrongCandidate",
                        Confidence = 0.7,
                    });
                    _lastCjkCommitMono = null;
                }
                else
                {
                    _r.Skipped.EmptyBackspace++;
                }

                return;
            }

            int index = word.Slots.Count - 1;
            Slot popped = word.Slots[index];
            word.Slots.RemoveAt(index);
            word.Deletions.Add(new Deletion
            {
                Index = index,
                Char = popped.Char,
                Vk = popped.Vk,
                Iki = iki,
                RunId = _runId,
                RunPos = _runLen,
                Mono = e.Mono,
                Ts = e.Ts,
            });
        }

        private void OnComposition(EventRecord e)
        {
            if (string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            _r.Chinese.Comps++;
            _compMono = e.Mono;

            WordState? word = _open ?? (_closed.Count > 0 ? _closed[^1] : null);
            if (word is not null)
            {
                word.HasComp = true;
            }
        }

        private void OnCommit(EventRecord e)
        {
            string clean = UiTextSampler.StripPrivateUse(e.Text ?? string.Empty);
            if (clean.Length == 0)
            {
                _r.Skipped.Glyph++;
                return;
            }

            if (e.Keys == 0)
            {
                // 粘贴、拖拽、语音：文本变了但没有击键，不是打字
                _r.Skipped.Paste++;
                return;
            }

            int printable = clean.Count(c => !char.IsControl(c));
            _r.Totals.Chars += printable;
            _r.Totals.Commits++;

            AppStat app = AppOf(e.Exe);
            app.Commits++;
            app.Chars += printable;
            app.TextAvailable = true;

            CountSymbols(clean);

            if (!IsChinese(clean))
            {
                return;
            }

            _r.Chinese.Commits++;
            _r.Chinese.Chars += printable;

            bool hasComp = _compMono is long compMono && e.Mono - compMono <= _opt.CompWindowMs;
            if (hasComp)
            {
                _r.Chinese.CommitsWithComp++;
                foreach (WordState w in _closed)
                {
                    if (e.Mono - w.CloseMono <= _opt.CompWindowMs)
                    {
                        w.HasComp = true;
                    }
                }

                if (_open is not null)
                {
                    _open.HasComp = true;
                }
            }

            _compMono = null;
            _lastCjkCommitMono = e.Mono;
            CloseWord(e.Mono);
        }

        private void CountSymbols(string text)
        {
            foreach (char c in text)
            {
                if (char.IsControl(c) || IsChineseChar(c) || (c < 128 && char.IsLetter(c)))
                {
                    continue;
                }

                if (!_r.Symbols.TryGetValue(c, out SymbolStat? stat))
                {
                    stat = new SymbolStat { Char = c };
                    if (KeyCatalog.TryKeyOf(c, out (int Vk, bool Shift) loc))
                    {
                        stat.Vk = loc.Vk;
                        stat.Shift = loc.Shift;
                    }

                    _r.Symbols[c] = stat;
                }

                stat.Count++;
            }
        }

        private void OnKeyUp(EventRecord e)
        {
            if (_downMono.TryGetValue(e.Vk, out long down) && e.Mono >= down && e.Mono - down < _opt.IdleMs)
            {
                KeyOf(e.Vk).Dwell.Add(e.Mono - down);
                _downMono.Remove(e.Vk);
            }
            else
            {
                _downMono.Remove(e.Vk);
            }
        }

        private WordState EnsureOpen(EventRecord e)
        {
            if (_open is null)
            {
                _open = new WordState { StartMono = e.Mono, Exe = e.Exe, Sid = e.Sid };
            }

            return _open;
        }

        private void CloseWord(long mono)
        {
            if (_open is null)
            {
                return;
            }

            // 空格、回车、换行、切应用都关词。但要延后判定：上屏的空格比 commit 先到，
            // 立刻判会把拼音词当成英文词。
            _open.CloseMono = mono;
            _closed.Add(_open);
            _open = null;
        }

        private void Flush(long nowMono)
        {
            if (_closed.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _closed.Count;)
            {
                WordState w = _closed[i];
                if (nowMono == long.MaxValue || nowMono - w.CloseMono >= _opt.EvaluateDelayMs)
                {
                    Evaluate(w);
                    _closed.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }

        private void Evaluate(WordState word)
        {
            bool pinyin = word.HasComp;
            string finalText = word.Text;

            foreach (Deletion d in word.Deletions)
            {
                int runLen = _runLengths.TryGetValue(d.RunId, out int len) ? len : d.RunPos;
                double threshold = _r.Baseline.Threshold(d.Vk);

                if (runLen > _opt.TypoMaxBackspaces)
                {
                    _r.Totals.RewordingLongRun++;
                    continue;
                }

                if (d.Iki is long iki && iki > threshold)
                {
                    // 在退格之前停了太久：是在想措辞，不是在纠手指
                    _r.Totals.RewordingLongPause++;
                    continue;
                }

                if (d.RunPos > 1)
                {
                    // 同一次纠错里被连带删掉、但本身没打错的字符
                    _r.Totals.Collateral++;
                    continue;
                }

                bool adjacent = d.Replacement is char rep && KeyCatalog.AreAdjacent(d.Char, rep);
                bool isPinyinError = pinyin && IsAsciiLetter(d.Char);

                if (isPinyinError)
                {
                    _r.Chinese.PinyinErrors++;
                    _r.Totals.PinyinErrors++;
                    KeyOf(d.Vk).PinyinTypos++;
                    _r.Chinese.PinyinErrorKeys[d.Vk] =
                        _r.Chinese.PinyinErrorKeys.TryGetValue(d.Vk, out int n) ? n + 1 : 1;
                }

                KeyStat key = KeyOf(d.Vk);
                key.Typos++;
                _r.Totals.Typos++;

                AppStat app = AppOf(word.Exe);
                app.Typos++;

                _r.Records.Add(new TypoRecord
                {
                    Ts = d.Ts,
                    Sid = word.Sid,
                    Exe = word.Exe,
                    Kind = isPinyinError ? "pinyin" : "typo",
                    Vk = d.Vk,
                    Key = KeyCatalog.Name(d.Vk),
                    Deleted = d.Char,
                    Replacement = d.Replacement,
                    RunLen = runLen,
                    Iki = d.Iki,
                    Cause = adjacent ? "adjacentSubstitution" : "ambiguous",
                    Confidence = adjacent ? 0.9 : runLen == 1 ? 0.6 : 0.5,
                    Word = finalText,
                    Position = d.Index,
                });
            }

            word.Deletions.Clear();
        }

        private KeyStat KeyOf(int vk)
        {
            if (!_r.Keys.TryGetValue(vk, out KeyStat? stat))
            {
                stat = new KeyStat { Vk = vk };
                _r.Keys[vk] = stat;
            }

            return stat;
        }

        private AppStat AppOf(string exe)
        {
            string key = string.IsNullOrEmpty(exe) ? "unknown" : exe;
            if (!_r.Apps.TryGetValue(key, out AppStat? stat))
            {
                stat = new AppStat { Exe = key };
                _r.Apps[key] = stat;
            }

            return stat;
        }
    }

    internal static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>汉字与中日韩标点（含全角）。用码位粗判，不引 Unihan 表。</summary>
    internal static bool IsChineseChar(char c) => c is >= '\u3000' and <= '\u9fff' or >= '\uff00' and <= '\uffef';

    internal static bool IsChinese(string text) => text.Any(IsChineseChar);
}
