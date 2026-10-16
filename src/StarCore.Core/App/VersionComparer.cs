using System.Text;

namespace StarCore.Core.App;

/// <summary>
/// 版本号比较。
///
/// 为什么不能直接字符串比较：`"1.9" &gt; "1.10"` 在字符串序下成立，但版本语义里 1.10 更新；
/// `"21.1.9"` 与 `"21.1.72"` 同理。加载器版本号（Forge 的 47.4.27、NeoForge 的 21.1.72）
/// 与 Minecraft 版本号（1.20.1、26.4-snapshot-4、18w49a）都吃这个亏。
///
/// 做法是逐段切分：连续数字算一段并按**数值**比较，连续字母算一段并按字典序比较，
/// 其它字符作为分隔忽略。数字段排在文字段之前（1.20 比 1.20-pre1 新）。
/// </summary>
public static class VersionComparer
{
    /// <summary>升序比较：a 比 b 新则返回正数。</summary>
    public static int Compare(string a, string b)
    {
        var ta = Tokenize(a);
        var tb = Tokenize(b);
        var n = Math.Min(ta.Count, tb.Count);

        for (var i = 0; i < n; i++)
        {
            var (na, sa) = ta[i];
            var (nb, sb) = tb[i];

            if (na >= 0 && nb >= 0)
            {
                var c = na.CompareTo(nb);
                if (c != 0) return c;
                continue;
            }

            var sc = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (sc != 0)
            {
                if (na >= 0) return 1;
                if (nb >= 0) return -1;
                return sc;
            }
        }

        // 前缀相同则更长的更新（1.20.1 > 1.20）
        return ta.Count.CompareTo(tb.Count);
    }

    /// <summary>返回新的列表，按版本从新到旧排列。</summary>
    public static List<T> SortDescending<T>(IEnumerable<T> items, Func<T, string> selector)
    {
        var list = items.ToList();
        list.Sort((x, y) => Compare(selector(y), selector(x)));
        return list;
    }

    public static List<string> SortDescending(IEnumerable<string> versions)
        => SortDescending(versions, v => v);

    /// <summary>由新到旧的比较器，可直接喂给 List.Sort / OrderBy 的自定义比较。</summary>
    public static IComparer<string> Descending { get; } = Comparer<string>.Create((a, b) => Compare(b, a));

    private static List<(int Num, string Text)> Tokenize(string v)
    {
        var result = new List<(int, string)>();
        var cur = new StringBuilder();

        void Flush()
        {
            if (cur.Length == 0) return;
            var text = cur.ToString();
            result.Add(int.TryParse(text, out var n) ? (n, text) : (-1, text));
            cur.Clear();
        }

        foreach (var ch in v ?? "")
        {
            if (char.IsDigit(ch))
            {
                if (cur.Length > 0 && !char.IsDigit(cur[^1])) Flush();
                cur.Append(ch);
            }
            else if (char.IsLetter(ch))
            {
                if (cur.Length > 0 && char.IsDigit(cur[^1])) Flush();
                cur.Append(ch);
            }
            else Flush();
        }

        Flush();
        return result;
    }
}
