using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZedZoneRU
{
    /// <summary>
    /// Character stats panel ("ГОЛОВА СНИЖЕНИЕ УРОНА:17%", "СКОРОСТЬ ПЕРЕДВИЖЕНИЯ:103%", "СИЛ Lv.8 ...").
    /// The game glues caps labels and values with ':' and no space; long labels wrap in the narrow panel.
    /// Rewrite each line as a short sentence-case label + value: "Защита головы: 17%".
    /// </summary>
    internal static class StatsFormatter
    {
        const string LabelColor = "#a8a8a8";

        static readonly Regex Marker = new(@"СНИЖЕНИЕ УРОНА|ПОТЕРЯ ТЕПЛА|ВЕС СНАРЯЖЕНИЯ|ОБЪ[ЕЁ]М КРОВИ|\bLv\.\s*\d|\bур\.\d+\s|(?i:восст(?:ановление|\.) (?:здоровья|выносливости)):\s*\+[\d.]+/[sс]", RegexOptions.Compiled);

        // "<tags>ГОЛОВА СНИЖЕНИЕ УРОНА : 17%"
        static readonly Regex Armor = new(@"^(\s*(?:<[^>]+>)*)\s*(ГОЛОВА|ТОРС|БЕДРО|БЁДРА|БЕДРА|СТУПНИ|РУКА|РУКИ|НОГИ)\s+СНИЖЕНИЕ\s+УРОНА\s*:\s*(.*)$", RegexOptions.Compiled);
        static readonly Dictionary<string, string> ArmorPart = new()
        {
            ["ГОЛОВА"] = "головы", ["ТОРС"] = "торса", ["БЕДРО"] = "бёдер", ["БЁДРА"] = "бёдер", ["БЕДРА"] = "бёдер",
            ["СТУПНИ"] = "ступней", ["РУКА"] = "рук", ["РУКИ"] = "рук", ["НОГИ"] = "ног",
        };

        // "<tags>LABEL : value"
        static readonly Regex LabelValue = new(@"^(\s*(?:<[^>]+>)*)\s*([А-ЯЁа-яё][А-ЯЁа-яё. ]*[А-ЯЁа-яё])\s*:\s*(.*)$", RegexOptions.Compiled);
        static readonly Dictionary<string, string> Labels = new()
        {
            ["СКОРОСТЬ ПЕРЕДВИЖЕНИЯ"] = "Скорость хода",
            ["ТОЧНОСТЬ ПРИЦЕЛИВАНИЯ"] = "Точность стрельбы",
            ["ТОЧНОСТЬ СТРЕЛЬБЫ"] = "Точность стрельбы",
            // the "+1/с" value already says it is a rate; full "Восст. выносливости" + value is wider than the panel
            ["ВОССТ. ЗДОРОВЬЯ"] = "Восст. здоровья",
            ["ВОССТ. ВЫНОСЛИВОСТИ"] = "Восст. выносл.",
            ["СИЛА БЛИЖНЕГО БОЯ"] = "Сила удара",
            ["СКОРОСТЬ АТАКИ"] = "Скорость атаки",
            ["ПОТЕРЯ ТЕПЛА"] = "Потеря тепла",
            ["ВЕС СНАРЯЖЕНИЯ"] = "Вес снаряжения",
            ["ОБЪЁМ КРОВИ"] = "Кровь",
            ["ОБЪЕМ КРОВИ"] = "Кровь",
            ["ТЕМПЕРАТУРА ТЕЛА"] = "Температура тела",
            ["ВОССТАНОВЛЕНИЕ ЗДОРОВЬЯ"] = "Восст. здоровья",
            ["ВОССТАНОВЛЕНИЕ ВЫНОСЛИВОСТИ"] = "Восст. выносл.",
        };

        static readonly Regex Lv = new(@"\bLv\.\s*(\d+)", RegexOptions.Compiled);
        static readonly Regex Ml = new(@"(\d)\s*ml\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Celsius = new(@"(\d)\s*°\s*C\b", RegexOptions.Compiled);
        static readonly Regex Number = new(@"^\s*(\d+(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);

        static readonly HashSet<string> Dumped = new(StringComparer.Ordinal);

        internal static string Format(string s)
        {
            if (string.IsNullOrEmpty(s) || !Marker.IsMatch(s)) return s;
            Dump(s);
            var lines = s.Replace("\r\n", "\n").Split('\n');
            var outp = new List<string>(lines.Length);
            foreach (var l in lines) { var r = Line(l); if (r != null) outp.Add(r); }
            return string.Join("\n", outp);
        }

        static string Line(string l)
        {
            var m = Armor.Match(l);
            if (m.Success)
                return m.Groups[1].Value + Lbl("Защита " + ArmorPart[m.Groups[2].Value]) + " " + m.Groups[3].Value.Trim();

            m = LabelValue.Match(l);
            if (m.Success && Labels.TryGetValue(m.Groups[2].Value.Trim().ToUpperInvariant(), out var nice))
            {
                var v = m.Groups[3].Value.Trim();
                v = Ml.Replace(v, "$1 мл");
                v = Celsius.Replace(v, "$1 °C");
                v = Regex.Replace(v, @"/s\b", "/с");
                // the weight is already shown in the inventory
                if (nice == "Вес снаряжения") return null;
                return m.Groups[1].Value + Lbl(nice) + " " + v;
            }

            // "СИЛ Lv.8 [bar] 14004/16000" — keep the bar layout, only translate "Lv."
            if (Lv.IsMatch(l)) l = Lv.Replace(l, "ур.$1");
            return l;
        }

        static string Lbl(string s) => $"<color={LabelColor}>{s}:</color>";

        static void Dump(string s)
        {
            if (!Plugin.CfgDumpTooltips.Value || Dumped.Count > 40 || !Dumped.Add(s)) return;
            try
            {
                File.AppendAllText(Path.Combine(Plugin.Dir, "stats_raw.txt"),
                    s.Replace("\r", "").Replace("\n", "\\n") + "\n", new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
