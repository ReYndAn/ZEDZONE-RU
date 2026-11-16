using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace ZedZoneRU
{
    /// <summary>
    /// Small targeted interface fixes ([Interface] Rework = true). Each rule names one concrete window,
    /// so nothing else in the game changes size or colour.
    /// </summary>
    internal static class UiPolish
    {
        static readonly HashSet<int> Done = new();
        static readonly Regex ColorTag = new(@"<color=#([0-9a-fA-F]{6})([0-9a-fA-F]{2})?>", RegexOptions.Compiled);

        /// <summary>Text rewrite before it reaches the component.</summary>
        internal static string BeforeSet(Text t, string value)
        {
            if (t == null || string.IsNullOrEmpty(value)) return value;
            // Character stats panel: bigger font, taller box, values in their own right-aligned column
            if (t.name == "CharacterStatusText" || t.name == "CharacterBodyDataText")
                return StatsPanel(t, value);
            // Guide/notes page is printed on light parchment: the game's light-blue keys and orange headers
            // (made for dark panels) are almost invisible there. Darken every light colour on this page only.
            if (IsParchment(t) && value.IndexOf("<color=", StringComparison.Ordinal) >= 0)
                return ColorTag.Replace(value, Darken);
            // Settings page footer: dark-red label on red buttons («ОЧИСТИТЬ КЭШ», «ОБРАТНАЯ СВЯЗЬ»),
            // dark green on green «ПОДТВЕРДИТЬ». The game repaints Text.color, so use a rich-text tag
            // (the «↺ Сбросить настройки» label on the same button is coloured the same way).
            if (IsSettingsFooterButton(t) && value.IndexOf("<color", StringComparison.Ordinal) < 0)
                return "<color=#fff2ea>" + value + "</color>";
            return value;
        }

        // ---- character stats panel (inventory, left column) ----
        // Status box grows downward by Grow canvas units; the bars block and the figure below move down
        // the same distance (the figure is also drawn smaller, see BodyFigureShrink).
        const float Grow = 16f;
        const int StatusFont = 6;   // the game uses 5 (10 px, mushy); 6 is Zpix's native 12 px at canvas scale 2
        const float StatusLs = 1.15f; // 11 rows fill the taller box with a little air between them
        static readonly Regex StatLine = new(@"^(\s*)<color=#a8a8a8>(.+?):</color>\s*(.*)$", RegexOptions.Compiled);
        const string SplitLabel = "<color=#b4b0b0>";

        static string StatsPanel(Text t, string value)
        {
            try
            {
                if (t.name == "CharacterStatusText")
                {
                    if (Done.Add(t.GetInstanceID()))
                    {
                        Taller(t.rectTransform, Grow);
                        var bg = t.transform.parent?.Find("CharacterStatusTextBGImage");
                        if (bg != null) Taller(bg.GetComponent<RectTransform>(), Grow);
                    }
                    if (t.fontSize < StatusFont) t.fontSize = StatusFont;
                    if (t.lineSpacing < StatusLs) t.lineSpacing = StatusLs;
                }
                else if (Done.Add(t.GetInstanceID()))
                {
                    // CharacterBodyDataText sits inside CharacterBodyPartsPanel (scaled 0.9)
                    var panel = t.transform.parent;
                    float k = panel != null && panel.localScale.y > 0.01f ? panel.localScale.y : 1f;
                    float d = Grow / k;
                    t.rectTransform.anchoredPosition -= new Vector2(0f, d);
                    var blood = panel?.Find("BloodBag");
                    if (blood != null) blood.GetComponent<RectTransform>().anchoredPosition -= new Vector2(0f, d);
                    var bg = panel?.Find("BodyPartsBGImage")?.GetComponent<RectTransform>();
                    if (bg != null)
                    {
                        float s = bg.localScale.y > 0.01f ? bg.localScale.y : 1f;
                        bg.sizeDelta -= new Vector2(0f, d / s);        // top edge comes down, bottom stays
                        bg.anchoredPosition -= new Vector2(0f, d / 2f);
                    }
                }
                return SplitColumns(t, value);
            }
            catch { return value; }
        }

        static void Taller(RectTransform rt, float d)
        {
            if (rt == null) return;
            rt.sizeDelta += new Vector2(0f, d);
            rt.anchoredPosition -= new Vector2(0f, d / 2f);   // keep the top edge in place
        }

        /// <summary>"&lt;label&gt;: value" lines → labels stay in this Text, values go to a right-aligned twin.</summary>
        static string SplitColumns(Text t, string value)
        {
            var lines = value.Replace("\r\n", "\n").Split('\n');
            var left = new StringBuilder();
            var right = new StringBuilder();
            bool any = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) { left.Append('\n'); right.Append('\n'); }
                var m = StatLine.Match(lines[i]);
                if (!m.Success) { left.Append(lines[i]); continue; }
                any = true;
                left.Append(m.Groups[1].Value).Append(SplitLabel).Append(m.Groups[2].Value).Append("</color>");
                right.Append(m.Groups[3].Value.Trim());
            }
            // re-set of a text we already split (OnEnable): keep the values column as it is
            if (!any && value.IndexOf(SplitLabel, StringComparison.Ordinal) >= 0) return value;
            var vt = ValuesText(t, any);
            if (vt == null) return value;
            vt.text = any ? right.ToString() : "";
            return any ? left.ToString() : value;
        }

        static Text ValuesText(Text t, bool create)
        {
            var tr = t.transform.Find("ZZRU_Values");
            Text vt = tr != null ? tr.GetComponent<Text>() : null;
            if (vt == null)
            {
                if (!create) return null;
                var go = new GameObject("ZZRU_Values");
                go.layer = t.gameObject.layer;
                go.transform.SetParent(t.transform, false);
                vt = go.AddComponent<Text>();
                var rt = vt.rectTransform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.pivot = t.rectTransform.pivot;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                vt.raycastTarget = false;
                vt.supportRichText = true;
                vt.alignment = TextAnchor.UpperRight;
                vt.horizontalOverflow = HorizontalWrapMode.Overflow;
                vt.verticalOverflow = VerticalWrapMode.Overflow;
                vt.color = new Color(0.95f, 0.95f, 0.95f, 1f);
            }
            vt.font = t.font;
            vt.fontSize = t.fontSize;
            vt.lineSpacing = t.lineSpacing;
            return vt;
        }

        static bool IsSettingsFooterButton(Text t)
        {
            if (t.name != "Text") return false;
            var p = t.transform.parent;
            // real names may carry "(Clone)" / " (1)" suffixes
            return p != null && p.parent != null &&
                   p.parent.name.StartsWith("GameSettingsExtend", StringComparison.Ordinal) &&
                   p.name.IndexOf("Button", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Layout/style tweaks after the text is set.</summary>
        internal static void AfterSet(Text t)
        {
            try
            {
                if (t == null) return;
                var n = t.name;
                // Settings: wrapped descriptions have very tight lines in the Cyrillic font.
                if (n == "PropertyDescriptionText" || n == "OptionDescriptionText" || n == "PageDescriptionText")
                {
                    if (t.lineSpacing < 1.12f) t.lineSpacing = 1.12f;
                    return;
                }
                // In-game "Load game" list: dark-red label on a red button, text glued to the thumbnail.
                if (n == "Text" && t.transform.parent != null && t.transform.parent.name == "DeleteButton" &&
                    t.GetComponentInParent<GameDataUI_IngameLoad>() != null)
                {
                    t.color = new Color(1f, 0.93f, 0.9f, 1f);
                    return;
                }
                if (n == "GameDataText" && t.GetComponentInParent<GameDataUI_IngameLoad>() != null)
                {
                    var rt = t.rectTransform;
                    if (Done.Add(rt.GetInstanceID()))
                        rt.anchoredPosition += new Vector2(6f, 0f);
                }
            }
            catch { }
        }

        static bool IsParchment(Text t)
        {
            if (t.name != "ContentText") return false;
            var p = t.transform.parent;
            return p != null && p.name == "LetterImage";
        }

        static string Darken(Match m)
        {
            var hex = m.Groups[1].Value;
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            float lum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            if (lum < 0.45f) return m.Value; // already dark enough for parchment
            float k = 0.25f / Math.Max(lum, 0.01f); // target luminance ≈ 0.25
            r = (int)Math.Min(255, r * k); g = (int)Math.Min(255, g * k); b = (int)Math.Min(255, b * k);
            return $"<color=#{r:x2}{g:x2}{b:x2}{m.Groups[2].Value}>";
        }
    }
}
