using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace ZedZoneRU
{
    /// <summary>
    /// Debug tool ([Diagnostics] UiAudit = true): every 1.5 s looks at every visible UI Text and records
    /// labels that do not fit their rect (spill out sideways, overflow or get cut vertically), plus a
    /// census of font sizes per UI path. Output: BepInEx/plugins/ZedZoneRU/ui_audit.txt and ui_fonts.tsv.
    /// </summary>
    internal class UiAuditBehaviour : MonoBehaviour
    {
        public UiAuditBehaviour(IntPtr p) : base(p) { }
        float _next;

        void Update()
        {
            if (!Plugin.CfgUiAudit.Value) return;
            float t = Time.unscaledTime;
            if (t < _next) return;
            _next = t + 1.5f;
            UiAudit.Scan();
        }
    }

    internal static class UiAudit
    {
        static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        static readonly HashSet<string> Census = new(StringComparer.Ordinal);
        static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);
        static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
        static readonly Regex Clone = new(@"\(Clone\)|\s*\(\d+\)", RegexOptions.Compiled);
        static int _scan;

        internal static void Scan()
        {
            try
            {
                _scan++;
                var texts = UnityEngine.Object.FindObjectsOfType<Text>();
                var issues = new StringBuilder();
                var fonts = new StringBuilder();
                foreach (var t in texts)
                {
                    if (t == null || !t.enabled) continue;
                    var s = t.text;
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    var plain = Tags.Replace(s, "");
                    if (!Cyr.IsMatch(plain)) continue;
                    if (t.color.a < 0.05f) continue;
                    TryDumpTree(t, plain);

                    var rt = t.rectTransform;
                    var r = rt.rect;
                    float w = r.width, h = r.height;
                    if (w < 1f || h < 1f) continue;
                    string path = Path(t.transform);
                    string sample = plain.Replace("\n", "⏎");
                    if (sample.Length > 70) sample = sample.Substring(0, 70) + "…";

                    int used = t.fontSize;
                    if (t.resizeTextForBestFit)
                    {
                        try { used = t.cachedTextGenerator.fontSizeUsedForBestFit; } catch { }
                    }

                    // font census: one line per (path, size)
                    string ck = path + "|" + used;
                    if (Census.Add(ck))
                        fonts.Append(path).Append('\t').Append(t.fontSize).Append('\t').Append(used)
                             .Append('\t').Append(t.resizeTextForBestFit ? "bestfit" : "")
                             .Append('\t').Append(w.ToString("0")).Append('x').Append(h.ToString("0"))
                             .Append('\t').Append(sample).Append('\n');

                    // overflow checks (best-fit texts shrink by themselves — only flag when stuck at the minimum)
                    string issue = null;
                    float pw = t.preferredWidth, ph = t.preferredHeight;
                    if (t.resizeTextForBestFit)
                    {
                        if (used <= t.resizeTextMinSize && (pw > w + 2f || ph > h + 2f)) issue = "BESTFIT-MIN";
                    }
                    else if (t.horizontalOverflow == HorizontalWrapMode.Overflow && pw > w + 2f)
                        issue = $"WIDTH {pw:0}>{w:0}";
                    else if (ph > h + 2f)
                        issue = (t.verticalOverflow == VerticalWrapMode.Truncate ? "CUT " : "HEIGHT ") + $"{ph:0}>{h:0}";
                    if (issue == null) continue;

                    string key = path + "|" + issue.Split(' ')[0] + "|" + plain;
                    if (!Seen.Add(key)) continue;
                    issues.Append(issue).Append('\t').Append("font ").Append(t.fontSize)
                          .Append(t.resizeTextForBestFit ? $"(fit {used})" : "")
                          .Append('\t').Append(t.alignment).Append('\t').Append(path)
                          .Append('\t').Append(sample).Append('\n');
                }
                if (issues.Length > 0)
                    File.AppendAllText(System.IO.Path.Combine(Plugin.Dir, "ui_audit.txt"),
                        $"== scan {_scan} @ {Time.unscaledTime:0}s\n" + issues, new UTF8Encoding(false));
                if (fonts.Length > 0)
                    File.AppendAllText(System.IO.Path.Combine(Plugin.Dir, "ui_fonts.tsv"), fonts.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { Plugin.L.LogWarning("UiAudit: " + e.Message); }
        }

        // Texts whose window layout we want to see in full (ui_tree.txt): trigger text -> levels up to the dumped root
        static readonly (string trigger, int up)[] TreeTriggers =
        {
            ("ЭКИПИРОВАТЬ", 3), ("РАЗОБРАТЬ", 3), ("Защита головы", 3), ("ГОЛОВА СНИЖЕНИЕ", 3), ("СИЛ ур.", 3),
        };
        static readonly HashSet<string> TreeDone = new(StringComparer.Ordinal);

        static void TryDumpTree(Text t, string plain)
        {
            foreach (var (trigger, up) in TreeTriggers)
            {
                if (plain.IndexOf(trigger, StringComparison.Ordinal) < 0 || TreeDone.Contains(trigger)) continue;
                TreeDone.Add(trigger);
                var root = t.transform;
                for (int i = 0; i < up && root.parent != null; i++) root = root.parent;
                var sb = new StringBuilder();
                sb.Append("== ").Append(trigger).Append(" @ ").Append(Path(root)).Append('\n');
                DumpNode(root, 0, sb);
                File.AppendAllText(System.IO.Path.Combine(Plugin.Dir, "ui_tree.txt"), sb.ToString(), new UTF8Encoding(false));
            }
        }

        static void DumpNode(Transform tr, int depth, StringBuilder sb)
        {
            if (depth > 6) return;
            var pad = new string(' ', depth * 2);
            sb.Append(pad).Append(tr.name).Append(tr.gameObject.activeSelf ? "" : " [off]");
            var rt = tr.TryCast<RectTransform>();
            if (rt != null)
                sb.Append($"  rect {rt.rect.width:0}x{rt.rect.height:0} anch {rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}-{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}" +
                          $" pos {rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0} size {rt.sizeDelta.x:0},{rt.sizeDelta.y:0} piv {rt.pivot.x:0.#},{rt.pivot.y:0.#} scale {tr.localScale.x:0.##},{tr.localScale.y:0.##}" +
                          (Mathf.Abs(tr.localEulerAngles.z) > 0.1f ? $" rot {tr.localEulerAngles.z:0}" : ""));
            sb.Append('\n');
            foreach (var c in tr.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetIl2CppType().Name;
                if (n == "RectTransform" || n == "CanvasRenderer") continue;
                sb.Append(pad).Append("  · ").Append(n);
                var tx = c.TryCast<Text>();
                if (tx != null)
                    sb.Append($" font {tx.font?.name} {tx.fontSize}{(tx.resizeTextForBestFit ? $" fit {tx.resizeTextMinSize}-{tx.resizeTextMaxSize}" : "")} ls {tx.lineSpacing:0.##} {tx.alignment} h:{tx.horizontalOverflow} v:{tx.verticalOverflow} col {ColorUtility.ToHtmlStringRGBA(tx.color)} \"{Tags.Replace(tx.text ?? "", "").Replace("\n", "⏎")}\"");
                var le = c.TryCast<LayoutElement>();
                if (le != null) sb.Append($" min {le.minWidth:0},{le.minHeight:0} pref {le.preferredWidth:0},{le.preferredHeight:0} flex {le.flexibleWidth:0},{le.flexibleHeight:0} ignore {le.ignoreLayout}");
                var lg = c.TryCast<HorizontalOrVerticalLayoutGroup>();
                if (lg != null) sb.Append($" spacing {lg.spacing:0} pad {lg.padding.left},{lg.padding.right},{lg.padding.top},{lg.padding.bottom} ctrl {lg.childControlWidth},{lg.childControlHeight} expand {lg.childForceExpandWidth},{lg.childForceExpandHeight} align {lg.childAlignment}");
                var gl = c.TryCast<GridLayoutGroup>();
                if (gl != null) sb.Append($" cell {gl.cellSize.x:0}x{gl.cellSize.y:0} spacing {gl.spacing.x:0},{gl.spacing.y:0}");
                var csf = c.TryCast<ContentSizeFitter>();
                if (csf != null) sb.Append($" fit {csf.horizontalFit},{csf.verticalFit}");
                var img = c.TryCast<Image>();
                if (img != null) sb.Append($" sprite {img.sprite?.name} col {ColorUtility.ToHtmlStringRGBA(img.color)}");
                sb.Append('\n');
            }
            for (int i = 0; i < tr.childCount && i < 40; i++) DumpNode(tr.GetChild(i), depth + 1, sb);
        }

        static string Path(Transform tr)
        {
            var parts = new List<string>();
            for (int i = 0; tr != null && i < 7; i++, tr = tr.parent)
                parts.Add(Clone.Replace(tr.name, ""));
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
