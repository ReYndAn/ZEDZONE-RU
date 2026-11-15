using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;

namespace ZedZoneRU
{
    /// <summary>
    /// English-source -> Russian dictionary built from the game's own localization CSV files
    /// plus the mod's extra.csv. Used for text the game renders without passing it through
    /// TextManager (building names from TerrainObjectAttr.englishName, guide hints, etc.).
    /// </summary>
    internal static class Translator
    {
        static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal);
        static readonly Dictionary<string, string> MapCI = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
        static readonly HashSet<string> Dumped = new(StringComparer.Ordinal);
        internal static readonly List<string> ItemTypeNames = new();
        static StreamWriter _dump;

        static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);
        static readonly Regex Latin3 = new("[A-Za-z]{3,}", RegexOptions.Compiled);
        static readonly Regex OuterColor = new(@"^(\s*<color=[^>]+>)(.*?)(</color>\s*)$", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

        internal static int Count => Map.Count;

        // Item-type labels the tooltip glues onto the next line ("Ёмкость" + "ВМЕСТИМОСТЬ").
        static readonly int[] ItemTypeIds = { 900212, 900213, 900214, 900215, 900216, 900219, 900227, 900236, 900238, 901225, 901226 };

        internal static void Init()
        {
            var locDir = Path.Combine(Paths.GameRootPath, "ZEDZONE_Data", "StreamingAssets", "Localization");
            var byId = new Dictionary<int, string>();
            // Priority: mod extra.csv > community file > official ru (fills gaps only).
            Load(Path.Combine(Plugin.Dir, "extra.csv"), byId);
            Load(Path.Combine(locDir, "Localization_en_to_ru-RU-x-community.csv"), byId);
            Load(Path.Combine(locDir, "Localization_en_to_ru.csv"), byId);
            foreach (var id in ItemTypeIds)
                if (byId.TryGetValue(id, out var t)) { t = Tags.Replace(t, "").Trim(); if (t.Length > 0) ItemTypeNames.Add(t); }
            ItemTypeNames.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        static void Load(string path, Dictionary<int, string> byId)
        {
            if (!File.Exists(path)) { Plugin.L.LogInfo("Not found: " + path); return; }
            var rows = Csv.Parse(File.ReadAllText(path, Encoding.UTF8));
            int added = 0;
            foreach (var r in rows)
            {
                if (r.Count < 3 || r[0] == "id") continue;
                var src = Unescape(r[1]);
                var tgt = Unescape(r[2]);
                // extra.csv: id "reN" = regular expression over the whole string, e.g. ^DAY (\d+)$ -> ДЕНЬ $1
                if (r[0].StartsWith("re", StringComparison.Ordinal))
                {
                    try { Patterns.Add((new Regex(r[1], RegexOptions.Compiled), r[2])); added++; }
                    catch (Exception e) { Plugin.L.LogWarning($"Bad regex {r[1]}: {e.Message}"); }
                    continue;
                }
                if (int.TryParse(r[0], out var id) && !byId.ContainsKey(id)) byId[id] = tgt;
                if (Add(src, tgt)) added++;
                // Guide hints are stored as "Title|Key|Body": index every part separately.
                if (src.IndexOf('|') >= 0)
                {
                    var sp = src.Split('|');
                    var tp = tgt.Split('|');
                    if (sp.Length == tp.Length)
                        for (int i = 0; i < sp.Length; i++) if (Add(sp[i], tp[i])) added++;
                }
            }
            Plugin.L.LogInfo($"Loaded {added} strings from {Path.GetFileName(path)}");
        }

        static bool Add(string src, string tgt)
        {
            var k = Norm(src);
            var v = tgt.Replace("\r\n", "\n").Trim();
            if (k.Length < 2 || v.Length == 0 || k == v) return false;
            if (!Latin3.IsMatch(Tags.Replace(k, ""))) return false;
            if (!Cyr.IsMatch(v)) return false;
            if (Map.ContainsKey(k)) return false;
            Map[k] = v;
            if (!MapCI.ContainsKey(k)) MapCI[k] = v;
            return true;
        }

        static readonly List<(Regex rx, string repl)> Patterns = new();

        static string Norm(string s) => s.Replace("\r\n", "\n").Trim();

        internal static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); i++; continue;
                        case 'r': i++; continue;
                        case 't': sb.Append('\t'); i++; continue;
                        case '"': sb.Append('"'); i++; continue;
                        case '\\': sb.Append('\\'); i++; continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string Translate(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 6000) return s;
            if (Cache.TryGetValue(s, out var c)) return c;
            var r = TranslateCore(s);
            if (Cache.Count > 20000) Cache.Clear();
            Cache[s] = r;
            if (ReferenceEquals(r, s) && Plugin.CfgDumpUntranslated.Value) DumpIfEnglish(s);
            return r;
        }

        static string TranslateCore(string s)
        {
            if (Cyr.IsMatch(s)) return s;              // already Russian
            if (!Latin3.IsMatch(s)) return s;          // numbers, symbols
            var r = Lookup(s);
            if (r != null) return r;
            var trimmed = s.Trim();
            foreach (var (rx, repl) in Patterns)
                if (rx.IsMatch(trimmed)) return s.Replace(trimmed, rx.Replace(trimmed, repl));

            // <color=..>English</color>
            var m = OuterColor.Match(s);
            if (m.Success)
            {
                var inner = Lookup(m.Groups[2].Value);
                if (inner != null) return m.Groups[1].Value + inner + m.Groups[3].Value;
            }

            // multi-line text assembled from several entries
            if (s.IndexOf('\n') >= 0)
            {
                var lines = s.Replace("\r\n", "\n").Split('\n');
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Trim().Length == 0) continue;
                    var t = Lookup(lines[i]);
                    if (t != null) { lines[i] = t; changed = true; }
                }
                if (changed) return string.Join("\n", lines);
            }
            return s;
        }

        static string Lookup(string s)
        {
            var k = Norm(s);
            if (k.Length == 0) return null;
            if (!Map.TryGetValue(k, out var v))
            {
                // building names are lower-case in data ("camp fire"), CSV may differ in case
                if (!MapCI.TryGetValue(k, out v)) return null;
            }
            int lead = 0; while (lead < s.Length && char.IsWhiteSpace(s[lead])) lead++;
            int trail = 0; while (trail < s.Length - lead && char.IsWhiteSpace(s[s.Length - 1 - trail])) trail++;
            return s.Substring(0, lead) + v + s.Substring(s.Length - trail);
        }

        static void DumpIfEnglish(string s)
        {
            if (Cyr.IsMatch(s) || !Latin3.IsMatch(Tags.Replace(s, ""))) return;
            if (Dumped.Count > 5000 || !Dumped.Add(s)) return;
            try
            {
                _dump ??= new StreamWriter(Path.Combine(Plugin.Dir, "untranslated.txt"), true, new UTF8Encoding(false)) { AutoFlush = true };
                _dump.WriteLine(s.Replace("\r", "").Replace("\n", "\\n"));
            }
            catch { }
        }
    }

    internal static class Csv
    {
        /// <summary>RFC4180-ish parser. Lines starting with '#' (outside quotes) are metadata and skipped.</summary>
        internal static List<List<string>> Parse(string text)
        {
            var rows = new List<List<string>>();
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            var row = new List<string>();
            var cell = new StringBuilder();
            bool inQ = false, atLineStart = true, skipLine = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (skipLine)
                {
                    if (c == '\n') { skipLine = false; atLineStart = true; }
                    continue;
                }
                if (atLineStart && !inQ && c == '#') { skipLine = true; continue; }
                atLineStart = false;
                if (inQ)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else inQ = false;
                    }
                    else cell.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '"': inQ = true; break;
                    case ',': row.Add(cell.ToString()); cell.Clear(); break;
                    case '\r': break;
                    case '\n':
                        row.Add(cell.ToString()); cell.Clear();
                        if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                        row = new List<string>(); atLineStart = true;
                        break;
                    default: cell.Append(c); break;
                }
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }
    }
}
