namespace KeyTrace.Analysis;

/// <summary>键在统计里的角色。阶段 4 选弱键时要靠它排掉退格和修饰键。</summary>
internal enum KeyRole
{
    Modifier,
    Letter,
    Digit,
    Symbol,
    Space,
    Enter,
    Tab,
    Edit,
    Navigate,
    Function,
    Other,
}

/// <summary>
/// vk / sc 到"能给人看的名字"和"这个键型出什么字符"的映射。
///
/// 只覆盖 US 布局：双拼方案全部建立在 QWERTY 之上，用户换成 Dvorak 时
/// 这套字符映射会错，但 vk 统计不受影响——那时候只需换这张表。
/// </summary>
internal static class KeyCatalog
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [8] = "Backspace",
        [9] = "Tab",
        [13] = "Enter",
        [16] = "Shift",
        [17] = "Ctrl",
        [18] = "Alt",
        [19] = "Pause",
        [20] = "CapsLock",
        [27] = "Esc",
        [32] = "Space",
        [33] = "PageUp",
        [34] = "PageDown",
        [35] = "End",
        [36] = "Home",
        [37] = "Left",
        [38] = "Up",
        [39] = "Right",
        [40] = "Down",
        [44] = "PrintScreen",
        [45] = "Insert",
        [46] = "Delete",
        [91] = "Win",
        [92] = "WinRight",
        [93] = "Menu",
        [144] = "NumLock",
        [145] = "ScrollLock",
        [186] = ";",
        [187] = "=",
        [188] = ",",
        [189] = "-",
        [190] = ".",
        [191] = "/",
        [192] = "`",
        [219] = "[",
        [220] = "\\",
        [221] = "]",
        [222] = "'",
    };

    /// <summary>同一个物理键的两个字符：下标 0 不按 Shift，下标 1 按 Shift。</summary>
    private static readonly Dictionary<int, (char Plain, char Shifted)> Chars = new()
    {
        [32] = (' ', ' '),
        [48] = ('0', ')'),
        [49] = ('1', '!'),
        [50] = ('2', '@'),
        [51] = ('3', '#'),
        [52] = ('4', '$'),
        [53] = ('5', '%'),
        [54] = ('6', '^'),
        [55] = ('7', '&'),
        [56] = ('8', '*'),
        [57] = ('9', '('),
        [186] = (';', ':'),
        [187] = ('=', '+'),
        [188] = (',', '<'),
        [189] = ('-', '_'),
        [190] = ('.', '>'),
        [191] = ('/', '?'),
        [192] = ('`', '~'),
        [219] = ('[', '{'),
        [220] = ('\\', '|'),
        [221] = (']', '}'),
        [222] = ('\'', '"'),
        // 小键盘数字与主键区等价；运算符单独给字符，便于符号统计
        [106] = ('*', '*'),
        [107] = ('+', '+'),
        [109] = ('-', '-'),
        [110] = ('.', '.'),
        [111] = ('/', '/'),
    };

    private static readonly HashSet<int> ModifierVks = new()
    {
        16, 17, 18, 91, 92, 20, 160, 161, 162, 163, 164, 165,
    };

    private static readonly HashSet<int> NavigateVks = new()
    {
        33, 34, 35, 36, 37, 38, 39, 40, 45,
    };

    /// <summary>字符反查键位，符号统计要用（同一个字符可能来自带不带 Shift 的两个状态）。</summary>
    private static readonly Lazy<Dictionary<char, (int Vk, bool Shift)>> ByChar = new(() =>
    {
        Dictionary<char, (int Vk, bool Shift)> map = [];
        foreach ((int vk, (char plain, char shifted)) in Chars)
        {
            map.TryAdd(plain, (vk, false));
            map.TryAdd(shifted, (vk, true));
        }

        for (int vk = 65; vk <= 90; vk++)
        {
            map.TryAdd(char.ToLowerInvariant((char)vk), (vk, false));
            map.TryAdd((char)vk, (vk, true));
        }

        return map;
    });

    public static bool TryKeyOf(char c, out (int Vk, bool Shift) key) => ByChar.Value.TryGetValue(c, out key);

    public static bool IsModifier(int vk) => ModifierVks.Contains(vk);

    public static string Name(int vk)
    {
        if (Names.TryGetValue(vk, out string? name))
        {
            return name;
        }

        if (vk is >= 65 and <= 90)
        {
            return ((char)vk).ToString();
        }

        // 主键区数字与小键盘数字
        if (vk is >= 48 and <= 57)
        {
            return ((char)vk).ToString();
        }

        if (vk is >= 96 and <= 105)
        {
            return ((char)('0' + vk - 96)).ToString();
        }

        if (vk is >= 112 and <= 135)
        {
            return "F" + (vk - 111);
        }

        return "vk" + vk;
    }

    public static KeyRole Role(int vk)
    {
        if (ModifierVks.Contains(vk))
        {
            return KeyRole.Modifier;
        }

        if (vk is >= 65 and <= 90)
        {
            return KeyRole.Letter;
        }

        if (vk is >= 48 and <= 57 or >= 96 and <= 105)
        {
            return KeyRole.Digit;
        }

        if (vk is 8 or 46)
        {
            return KeyRole.Edit;
        }

        if (vk == 32)
        {
            return KeyRole.Space;
        }

        if (vk == 13)
        {
            return KeyRole.Enter;
        }

        if (vk == 9)
        {
            return KeyRole.Tab;
        }

        if (NavigateVks.Contains(vk))
        {
            return KeyRole.Navigate;
        }

        if (vk is >= 112 and <= 135)
        {
            return KeyRole.Function;
        }

        if (Chars.ContainsKey(vk))
        {
            return KeyRole.Symbol;
        }

        return KeyRole.Other;
    }

    /// <summary>这个键会敲出什么字符；退格、方向键这类返回 null。</summary>
    public static char? Char(int vk, bool shift)
    {
        if (vk is >= 65 and <= 90)
        {
            char c = (char)vk;
            return shift ? c : char.ToLowerInvariant(c);
        }

        if (vk is >= 48 and <= 57)
        {
            return shift ? Chars[vk].Shifted : Chars[vk].Plain;
        }

        if (vk is >= 96 and <= 105)
        {
            return (char)('0' + vk - 96);
        }

        if (Chars.TryGetValue(vk, out (char Plain, char Shifted) pair))
        {
            return shift ? pair.Shifted : pair.Plain;
        }

        return null;
    }

    /// <summary>
    /// 两个字在键盘上是否相邻（含斜对角）。用来实现"相邻键替换判为打错"这条高置信度规则。
    /// 用行列近似而不是精确像素：键盘布局有无数种，近似足够区分
    /// "打错了隔壁键"和"整个词重写"。
    /// </summary>
    public static bool AreAdjacent(char a, char b)
    {
        if (!TryLocate(a, out (int Row, int Col) pa) || !TryLocate(b, out (int Row, int Col) pb))
        {
            return false;
        }

        return Math.Abs(pa.Row - pb.Row) <= 1 && Math.Abs(pa.Col - pb.Col) <= 1;
    }

    private static bool TryLocate(char c, out (int Row, int Col) pos)
    {
        const string Rows = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./";
        c = char.ToLowerInvariant(c);
        int index = Rows.IndexOf(c, StringComparison.Ordinal);
        if (index < 0)
        {
            pos = default;
            return false;
        }

        // 行首偏移：数字行 0、上排 0、中排 0.5 键、下排 1 键，还原真实错位
        int row = index < 13 ? 0 : index < 26 ? 1 : index < 37 ? 2 : 3;
        int col = index < 13 ? index : index < 26 ? index - 13 : index < 37 ? index - 26 : index - 37;
        double offset = row switch { 2 => 0.5, 3 => 1.0, _ => 0 };
        pos = (row, (int)Math.Round(col + offset));
        return true;
    }
}
