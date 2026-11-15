using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZedZoneRU
{
    /// <summary>
    /// Rewrites the item tooltip (DescriptionTipPanel) into a cleaner layout:
    ///  * "LABEL : value" / "LABEL value"  ->  "Label: value"  (sentence case, one colon style)
    ///  * item type glued to the next line ("ЁмкостьВМЕСТИМОСТЬ") is split onto its own grey line
    ///  * units: "4000ml" -> "4000 мл", "1Ч. (75s)" -> "1 ч (75 с)"
    ///  * weight / price / stack info moved into a compact footer: "Вес 0.4 кг · Цена 30"
    /// Only tooltips that contain a weight line (i.e. item tooltips) are touched.
    /// </summary>
    internal static class TooltipFormatter
    {
        const string Grey = "#9a9a9a";
        const string Dim = "#7f7f7f";
        const string Gold = "#e3b55a";
        const string Header = "#d9b26b";

        static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
        static readonly HashSet<string> Dumped = new(StringComparer.Ordinal);

        static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
        static readonly Regex WeightRx = new(@"^(?:ВЕС|Вес)\s*:?\s*([\d.,]+)\s*(?:кг)?$", RegexOptions.Compiled);
        static readonly Regex PriceRx = new(@"^(?:ЦЕНА|Цена)\s*:?\s*(.+)$", RegexOptions.Compiled);
        static readonly Regex StackRx = new(@"^(Не складывается в стопку|Лимит стопки.*|В стопке.*)$", RegexOptions.Compiled);
        static readonly Regex PropsHeaderRx = new(@"^СВОЙСТВА ПРЕДМЕТА\s*:?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex CapsLabelNoColon = new(@"^([А-ЯЁ][А-ЯЁ ]{3,}?)\s+(?=[\d+\-])", RegexOptions.Compiled);
        static readonly Regex GluedStat = new(@"^(.*?\S.*?)( {2,})((?:<[^>]+>)*[А-ЯЁ][А-ЯЁ ]{2,}?\s{2,}\d.*)$", RegexOptions.Compiled);
        static readonly Regex SlotRx = new(@"^(.+?)-(<color=#[0-9a-fA-F]+>)(ТОРС|ШТАНЫ|ОБУВЬ|ГОЛОВА|РЮКЗАК|ПЕРЧАТКИ)(</color>)\s*$", RegexOptions.Compiled);
        static readonly Regex ColoredLabel = new(@"^(\s*<color=#[0-9a-fA-F]+>[^<:]*[А-Яа-яЁё][^<:]*</color>) +(?=\S)", RegexOptions.Compiled);
        static readonly Regex Days = new(@"(\d)\s?ДН\.", RegexOptions.Compiled);
        static readonly Regex TrailingComma = new(@"\s*,\s*$", RegexOptions.Compiled);
        static readonly Regex Ml = new(@"(\d)\s?ml\b", RegexOptions.Compiled);
        static readonly Regex Hours = new(@"(\d)\s?Ч\.", RegexOptions.Compiled);
        static readonly Regex Secs = new(@"\((\d+(?:\.\d+)?)s\)", RegexOptions.Compiled);
        static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);

        internal static string Format(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            if (Cache.TryGetValue(raw, out var c)) return c;
            if (Plugin.CfgDumpTooltips.Value) Dump(raw);
            string r;
            try { r = FormatCore(raw) ?? raw; }
            catch (Exception e) { Plugin.L.LogWarning("Tooltip: " + e.Message); r = raw; }
            if (Cache.Count > 4000) Cache.Clear();
            Cache[raw] = r;
            return r;
        }

        static string Vis(string s) => Tags.Replace(s, "");

        static string FormatCore(string raw)
        {
            var src = raw.Replace("\r\n", "\n").Split('\n');
            bool isItem = false;
            foreach (var l in src) if (WeightRx.IsMatch(Vis(l).Trim())) { isItem = true; break; }
            if (!isItem) return null;

            // 1. split glued item-type label off the following line
            var lines = new List<string>();
            foreach (var l0 in src)
            {
                // "ВЫНОСЛИВОСТЬ ЗА АТАКУ : 0.75  ПРОЧНОСТЬ  92" -> two lines
                var gm = GluedStat.Match(l0);
                if (gm.Success && Vis(gm.Groups[1].Value).Trim().Length > 0)
                {
                    SplitGluedType(gm.Groups[1].Value, lines);
                    lines.Add(gm.Groups[3].Value);
                }
                else SplitGluedType(l0, lines);
            }

            var body = new List<string>();
            string weight = null, price = null;
            var footer = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var v = Vis(line).Trim();
                // the game sometimes emits the same stat line twice (e.g. "СЛОТ ОРУЖИЯ ОСНОВНОЕ")
                if (v.Length > 0 && i > 0 && !seen.Add(v)) continue;
                if (i == 0) seen.Add(v);

                var wm = WeightRx.Match(v);
                if (wm.Success) { weight = Num(wm.Groups[1].Value); continue; }
                var pm = PriceRx.Match(v);
                if (pm.Success) { price = pm.Groups[1].Value.Trim(); continue; }
                if (StackRx.IsMatch(v)) { footer.Add($"<color={Dim}>{Regex.Replace(v, @"\s*:\s*", ": ")}</color>"); continue; }
                var sm = SlotRx.Match(line);
                if (sm.Success)
                {
                    var left = Vis(sm.Groups[1].Value).Trim();
                    var slot = sm.Groups[3].Value.ToLowerInvariant();
                    // first line is the item name with the slot glued on: keep only the name,
                    // the slot is shown on the type line ("Одежда · торс")
                    body.Add(i == 0
                        ? sm.Groups[1].Value
                        : $"{CaseRange(sm.Groups[1].Value, 0, sm.Groups[1].Value.Length)}<color={Grey}> · {slot}</color>");
                    continue;
                }
                if (PropsHeaderRx.IsMatch(v))
                {
                    if (body.Count > 0 && Vis(body[body.Count - 1]).Trim().Length != 0) body.Add("");
                    body.Add($"<color={Header}>Свойства предмета</color>");
                    continue;
                }

                line = NormalizeLabel(line);
                line = TrailingSemicolon.Replace(line, "$1");
                line = Ml.Replace(line, "$1 мл");
                line = Hours.Replace(line, "$1 ч");
                line = Days.Replace(line, "$1 дн.");
                line = TrailingComma.Replace(line, "");
                line = Secs.Replace(line, "($1 с)");
                line = CapitalizeFirst(line);
                body.Add(line);
            }

            // collapse blank runs, trim edges
            var outL = new List<string>();
            foreach (var l in body)
            {
                bool blank = Vis(l).Trim().Length == 0;
                if (blank && (outL.Count == 0 || Vis(outL[outL.Count - 1]).Trim().Length == 0)) continue;
                outL.Add(l);
            }
            while (outL.Count > 0 && Vis(outL[outL.Count - 1]).Trim().Length == 0) outL.RemoveAt(outL.Count - 1);

            // footer: weight · price, then stack info
            var parts = new List<string>();
            if (weight != null) parts.Add($"<color={Grey}>Вес</color> {weight} кг");
            if (price != null) parts.Add($"<color={Grey}>Цена</color> <color={Gold}>{price}</color>");
            if (parts.Count > 0 || footer.Count > 0) outL.Add("");
            if (parts.Count > 0) outL.Add(string.Join($"<color={Dim}>  ·  </color>", parts));
            outL.AddRange(footer);

            return string.Join("\n", outL);
        }

        static void SplitGluedType(string line, List<string> into)
        {
            var v = Vis(line).TrimStart();
            foreach (var t in Translator.ItemTypeNames)
            {
                if (v.Length <= t.Length || !v.StartsWith(t, StringComparison.Ordinal)) continue;
                char next = v[t.Length];
                if (!char.IsUpper(next)) continue;
                int idx = line.IndexOf(t, StringComparison.Ordinal);
                if (idx < 0) break;
                int end = idx + t.Length;
                while (end < line.Length && line[end] == '<' && end + 1 < line.Length && line[end + 1] == '/')
                {
                    int close = line.IndexOf('>', end);
                    if (close < 0) break;
                    end = close + 1;
                }
                var left = line.Substring(0, end);
                var right = line.Substring(end);
                into.Add(left.Contains("<color") ? left : $"<color={Grey}>{left}</color>");
                into.Add(right);
                return;
            }
            into.Add(line);
        }

        static readonly HashSet<string> Abbr = new(StringComparer.Ordinal) { "ЛКМ", "ПКМ", "СКМ", "НАТО", "ОЗ", "СВЧ" };
        static readonly Regex TrailingSemicolon = new(@";\s*((?:</[^>]+>)*)\s*$", RegexOptions.Compiled);
        static readonly Regex SpacesBeforeClose = new(@"( +)(</color>)", RegexOptions.Compiled);

        /// <summary>
        /// "LABEL : value" / "LABEL  value" / "LABEL 100" -> "Label: value"; standalone "МОЖНО ПОЧИНИТЬ" -> "Можно починить".
        /// </summary>
        static string NormalizeLabel(string line)
        {
            line = SpacesBeforeClose.Replace(line, "$2$1");
            var v = Vis(line);
            int colonVis = v.IndexOf(':');
            if (colonVis > 0 && v.Substring(colonVis + 1).Trim().Length > 0 && colonVis <= 48)
            {
                int pos = FindTextChar(line, ':');
                if (pos > 0)
                {
                    int a = pos; while (a > 0 && line[a - 1] == ' ') a--;
                    int b = pos + 1; while (b < line.Length && line[b] == ' ') b++;
                    line = line.Substring(0, a) + ": " + line.Substring(b);
                    line = CaseRange(line, 0, a);
                    return CaseValue(line);
                }
                return line;
            }

            int sep = FindSpaceRun(line);
            if (sep > 0 && (IsCapsLabel(Vis(line.Substring(0, sep)).Trim())
                            || (Cyr.IsMatch(Vis(line.Substring(0, sep))) && StartsWithDigit(Vis(line.Substring(sep))))))
            {
                int b = sep; while (b < line.Length && line[b] == ' ') b++;
                line = line.Substring(0, sep) + ": " + line.Substring(b);
                line = CaseRange(line, 0, sep);
                return CaseValue(line);
            }

            var m = CapsLabelNoColon.Match(v.TrimStart());
            if (m.Success)
            {
                var label = m.Groups[1].Value.TrimEnd();
                int idx = line.IndexOf(label, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int after = idx + label.Length;
                    int end = after;
                    while (end < line.Length && line[end] == '<' && end + 1 < line.Length && line[end + 1] == '/')
                    {
                        int close = line.IndexOf('>', end); if (close < 0) break; end = close + 1;
                    }
                    int b = end; while (b < line.Length && line[b] == ' ') b++;
                    line = line.Substring(0, idx) + Sentence(label) + line.Substring(after, end - after) + ": " + line.Substring(b);
                }
                return line;
            }

            var cl = ColoredLabel.Match(line);
            if (cl.Success && v.IndexOf(':') < 0)
            {
                line = cl.Groups[1].Value + ": " + line.Substring(cl.Length);
                line = CaseRange(line, 0, cl.Groups[1].Length);
                return CaseValue(line);
            }

            var t = v.Trim();
            if (IsCapsLabel(t))
            {
                foreach (var ch in t) if (char.IsDigit(ch)) return line;
                return CaseRange(line, 0, line.Length);
            }
            return line;
        }

        static bool StartsWithDigit(string s)
        {
            s = s.TrimStart();
            return s.Length > 0 && (char.IsDigit(s[0]) || ((s[0] == '-' || s[0] == '+') && s.Length > 1 && char.IsDigit(s[1])));
        }

        static bool IsCapsLabel(string s)
        {
            if (s.Length == 0 || s.Length > 48 || Abbr.Contains(s) || !Cyr.IsMatch(s)) return false;
            int letters = 0;
            foreach (var ch in s) if (char.IsLetter(ch)) { letters++; if (char.IsLower(ch)) return false; }
            return letters >= 3;
        }

        /// <summary>Sentence-case letters outside tags in [from, to) if that part is ALL CAPS.</summary>
        static string CaseRange(string line, int from, int to)
        {
            if (to > line.Length) to = line.Length;
            if (!IsCapsLabel(Vis(line.Substring(from, to - from)).Trim())) return line;
            var sb = new StringBuilder(line.Length);
            sb.Append(line, 0, from);
            bool first = true, inTag = false;
            for (int i = from; i < to; i++)
            {
                char ch = line[i];
                if (ch == '<') inTag = true;
                if (inTag) { sb.Append(ch); if (ch == '>') inTag = false; continue; }
                if (char.IsLetter(ch)) { sb.Append(first ? char.ToUpper(ch) : char.ToLower(ch)); first = false; }
                else sb.Append(ch);
            }
            sb.Append(line, to, line.Length - to);
            return sb.ToString();
        }

        /// <summary>"Слот оружия: ОСНОВНОЕ" -> "Слот оружия: Основное" (single all-caps word values only).</summary>
        static string CaseValue(string line)
        {
            int p = line.IndexOf(": ", StringComparison.Ordinal);
            if (p < 0) return line;
            var val = Vis(line.Substring(p + 2)).Trim();
            if (val.Length < 4 || val.IndexOf(' ') >= 0) return line;
            foreach (var ch in val) if (char.IsDigit(ch)) return line;
            return CaseRange(line, p + 2, line.Length);
        }

        /// <summary>Index of the first run of 2+ spaces outside tags, after some visible text.</summary>
        static int FindSpaceRun(string s)
        {
            bool inTag = false, seenText = false;
            for (int i = 0; i < s.Length - 1; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (inTag) { if (c == '>') inTag = false; continue; }
                if (c == ' ' && s[i + 1] == ' ' && seenText) return i;
                if (c != ' ') seenText = true;
            }
            return -1;
        }

        static string Sentence(string s)
        {
            s = s.ToLower(CultureInfo.GetCultureInfo("ru-RU"));
            return s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1);
        }

        static string CapitalizeFirst(string line)
        {
            bool inTag = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '<') { inTag = true; continue; }
                if (inTag) { if (ch == '>') inTag = false; continue; }
                if (char.IsWhiteSpace(ch)) continue;
                if (char.IsLower(ch) && Cyr.IsMatch(ch.ToString()))
                    return line.Substring(0, i) + char.ToUpper(ch) + line.Substring(i + 1);
                return line;
            }
            return line;
        }

        /// <summary>Index of the first occurrence of ch outside of rich-text tags.</summary>
        static int FindTextChar(string s, char ch)
        {
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<') inTag = true;
                else if (s[i] == '>') { inTag = false; continue; }
                if (!inTag && s[i] == ch) return i;
            }
            return -1;
        }

        static string Num(string s)
        {
            if (double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d.ToString("0.###", CultureInfo.InvariantCulture);
            return s;
        }

        static void Dump(string raw)
        {
            if (Dumped.Count > 400 || !Dumped.Add(raw)) return;
            try
            {
                File.AppendAllText(Path.Combine(Plugin.Dir, "tooltips_raw.txt"),
                    raw.Replace("\r", "") + "\n==========\n", new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
