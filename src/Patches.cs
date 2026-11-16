using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ZedZoneRU
{
    /// <summary>Is the game currently running in Russian (community or official)?</summary>
    internal static class Lang
    {
        static bool _active;
        static float _next = -1f;
        static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);

        internal static bool Active
        {
            get
            {
                float t;
                try { t = Time.unscaledTime; } catch { return _active; }
                if (t >= _next)
                {
                    _next = t + 1f;
                    try
                    {
                        var tm = TextManager.instance;
                        // text id 27 = "CRAFT"; Cyrillic means a Russian language is selected
                        var probe = tm != null ? tm.GetText(27) : null;
                        _active = probe != null && Cyr.IsMatch(probe);
                    }
                    catch { /* TextManager not ready yet */ }
                }
                return _active;
            }
        }
    }

    [HarmonyPatch]
    internal static class TextPatches
    {
        [HarmonyPatch(typeof(Text), nameof(Text.text), MethodType.Setter)]
        [HarmonyPrefix]
        static void SetText(Text __instance, ref string value)
        {
            if (!Plugin.CfgTranslate.Value || string.IsNullOrEmpty(value) || !Lang.Active) return;
            try { value = Translator.Translate(value); } catch { }
            if (Plugin.CfgRework.Value) try { value = UiPolish.BeforeSet(__instance, value); } catch { }
        }

        [HarmonyPatch(typeof(Text), nameof(Text.text), MethodType.Setter)]
        [HarmonyPostfix]
        static void AfterSetText(Text __instance)
        {
            if (!Lang.Active) return;
            FontScaler.Apply(__instance);
            SlotGrow.Apply(__instance);
            if (Plugin.CfgRework.Value) UiPolish.AfterSet(__instance);
            if (Plugin.CfgAutoFit.Value) AutoFit.Check(__instance);
        }

        // Labels baked into prefabs never go through the setter.
        [HarmonyPatch(typeof(Text), "OnEnable")]
        [HarmonyPostfix]
        static void OnEnable(Text __instance)
        {
            if (!Plugin.CfgTranslate.Value || !Lang.Active) return;
            try
            {
                var cur = __instance.text;
                if (string.IsNullOrEmpty(cur)) return;
                var t = Translator.Translate(cur);
                if (Plugin.CfgRework.Value) t = UiPolish.BeforeSet(__instance, t);
                if (!ReferenceEquals(t, cur) && t != cur) __instance.text = t;
                else if (Plugin.CfgAutoFit.Value) AutoFit.Check(__instance);
            }
            catch { }
        }
    }

    /// <summary>
    /// Russian words are longer than English ones: single-line labels overflow their rect
    /// ("УДАЛИТ", "РУКОВОДСТВ"). Turn on Unity best-fit for such labels so they shrink to fit.
    /// </summary>
    /// <summary>
    /// "fsN" rules from extra.csv: shrink the font of specific texts that don't fit their frame
    /// (e.g. the save slot, which gets one line longer in Russian). Remembers the original size,
    /// so repeated updates don't shrink it further and other texts in a reused object are restored.
    /// </summary>
    internal static class FontScaler
    {
        static readonly System.Collections.Generic.Dictionary<int, int> Original = new();

        internal static void Apply(Text t)
        {
            try
            {
                if (t == null) return;
                int id = t.GetInstanceID();
                float scale = Translator.FontScale(t.text);
                if (Original.TryGetValue(id, out var orig))
                {
                    int want = scale < 1f ? Math.Max(8, (int)Math.Round(orig * scale)) : orig;
                    if (t.fontSize != want) t.fontSize = want;
                    if (scale >= 1f) Original.Remove(id);
                }
                else if (scale < 1f)
                {
                    Original[id] = t.fontSize;
                    t.fontSize = Math.Max(8, (int)Math.Round(t.fontSize * scale));
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// "grN" rules: the Russian save slot has 4 lines instead of 3. Make the slot (GameDataUI)
    /// taller so the text fits with padding; the list layout then spaces the slots apart.
    /// Original sizes are remembered, so repeated refreshes don't grow it again.
    /// </summary>
    internal static class SlotGrow
    {
        static readonly System.Collections.Generic.Dictionary<int, float> OrigH = new();
        static readonly System.Collections.Generic.HashSet<int> Logged = new();
        static int _logs;

        static float Orig(RectTransform rt)
        {
            int id = rt.GetInstanceID();
            if (!OrigH.TryGetValue(id, out var h)) OrigH[id] = h = rt.sizeDelta.y;
            return h;
        }

        static bool FixedH(RectTransform rt) => Mathf.Abs(rt.anchorMin.y - rt.anchorMax.y) < 0.001f;

        internal static void Apply(Text t)
        {
            try
            {
                if (t == null) return;
                float pad = Translator.GrowPadding(t.text);
                if (pad < 0f) return;
                var ui = t.GetComponentInParent<GameDataUI>();
                if (ui == null) return;
                // only the main-menu list; the load/save windows have their own fixed layout
                if (ui.TryCast<GameDataUI_IngameLoad>() != null || ui.TryCast<GameDataUI_IngameSave>() != null) return;
                var root = ui.GetComponent<RectTransform>();
                var trt = t.rectTransform;
                if (Logged.Add(root.GetInstanceID()) && Logged.Count <= 1) Dump(root, t);

                // how much taller the text needs to be, measured against its original height
                float textH = trt.rect.height - (trt.sizeDelta.y - Orig(trt));
                // preferredHeight is unreliable here (reports 51 for 4 rendered lines in a 56 box),
                // so count lines: the original slot is laid out for 3 lines.
                int lines = t.text.TrimEnd().Split('\n').Length;
                float lineH = textH / 3f;
                float delta = lines > 3 ? (lines - 3) * lineH + pad : 0f;

                // every fixed-height rect from the text up to the slot root grows by delta
                for (var tr = trt.transform; tr != null; tr = tr.parent)
                {
                    var rt = tr.GetComponent<RectTransform>();
                    if (rt != null && FixedH(rt))
                        rt.sizeDelta = new Vector2(rt.sizeDelta.x, Orig(rt) + delta);
                    if (tr == root.transform) break;
                }
                // fixed-height backgrounds/frames directly under the slot that were as tall as the slot
                float rootOrig = Orig(root);
                for (int i = 0; i < root.childCount; i++)
                {
                    var rt = root.GetChild(i).GetComponent<RectTransform>();
                    if (rt == null || !FixedH(rt) || trt.IsChildOf(rt)) continue;
                    if (Mathf.Abs(Orig(rt) - rootOrig) < 2f)
                        rt.sizeDelta = new Vector2(rt.sizeDelta.x, Orig(rt) + delta);
                }
                // layout-driven list: LayoutElement / GridLayoutGroup
                var le = root.GetComponent<LayoutElement>();
                if (le != null)
                {
                    int id = le.GetInstanceID();
                    if (!OrigH.TryGetValue(id, out var lp)) OrigH[id] = lp = Mathf.Max(le.preferredHeight, le.minHeight);
                    if (lp > 0f)
                    {
                        if (le.preferredHeight > 0f) le.preferredHeight = lp + delta;
                        if (le.minHeight > 0f) le.minHeight = lp + delta;
                    }
                }
                // NB: "as RectTransform" is always null for IL2CPP proxies — use GetComponent
                var parent = root.parent != null ? root.parent.GetComponent<RectTransform>() : null;
                if (parent != null)
                {
                    var grid = parent.GetComponent<GridLayoutGroup>();
                    if (grid != null)
                    {
                        int id = grid.GetInstanceID();
                        if (!OrigH.TryGetValue(id, out var gh)) OrigH[id] = gh = grid.cellSize.y;
                        if (grid.cellSize.y < gh + delta) grid.cellSize = new Vector2(grid.cellSize.x, gh + delta);
                    }
                    LayoutRebuilder.MarkLayoutForRebuild(parent);
                }
                if (_logs++ < 6)
                {
                    var g = parent != null ? parent.GetComponent<GridLayoutGroup>() : null;
                    Plugin.L.LogInfo($"SlotGrow: lines={lines} textH={textH:0.#} delta={delta:0.#} root={root.rect.height:0.#} " +
                                     $"cell={(g != null ? g.cellSize.y.ToString("0.#") : "-")} active={root.gameObject.activeInHierarchy}");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("SlotGrow: " + e.Message); }
        }

        static void Dump(RectTransform root, Text t)
        {
            try
            {
                var sb = new System.Text.StringBuilder("Save slot layout:\n");
                void Walk(Transform tr, int depth)
                {
                    var rt = tr.GetComponent<RectTransform>();
                    sb.Append(new string(' ', depth * 2)).Append(tr.name);
                    if (rt != null) sb.Append($" size={rt.rect.width:0}x{rt.rect.height:0} anchY={rt.anchorMin.y:0.##}-{rt.anchorMax.y:0.##}");
                    sb.Append('\n');
                    if (depth < 3) for (int i = 0; i < tr.childCount; i++) Walk(tr.GetChild(i), depth + 1);
                }
                var p = root.parent;
                if (p != null)
                {
                    sb.Append($"parent {p.name}: grid={p.GetComponent<GridLayoutGroup>() != null} vlg={p.GetComponent<VerticalLayoutGroup>() != null} csf={p.GetComponent<ContentSizeFitter>() != null}\n");
                }
                sb.Append($"slot LayoutElement={root.GetComponent<LayoutElement>() != null}; text pref={t.preferredHeight:0} font={t.fontSize}\n");
                Walk(root, 0);
                Plugin.L.LogInfo(sb.ToString());
            }
            catch { }
        }
    }

    /// <summary>
    /// The save-list window's close button sits on the frame corner; nudge it inward once.
    /// </summary>
    [HarmonyPatch]
    internal static class CloseButtonNudge
    {
        static readonly System.Collections.Generic.HashSet<int> Done = new();
        static readonly Vector2 Offset = new(-6f, -6f);

        [HarmonyPatch(typeof(GameDataPanel), nameof(GameDataPanel.OpenPanel), new[] { typeof(GameDataPanel.GameDataListMode) })]
        [HarmonyPostfix]
        static void OpenMode(GameDataPanel __instance) => Nudge(__instance);

        [HarmonyPatch(typeof(GameDataPanel), nameof(GameDataPanel.OpenPanel), new[] { typeof(bool) })]
        [HarmonyPostfix]
        static void OpenStory(GameDataPanel __instance) => Nudge(__instance);

        static void Nudge(GameDataPanel p)
        {
            try
            {
                if (p == null || !Plugin.CfgRework.Value) return;
                // only the main-menu list; in-game load/save windows have a different frame
                if (p.TryCast<GameDataPanel_IngameLoad>() != null || p.TryCast<GameDataPanel_IngameSave>() != null) return;
                var buttons = p.GetComponentsInChildren<Button>(true);
                var names = new System.Text.StringBuilder();
                foreach (var b in buttons)
                {
                    if (b == null) continue;
                    var n = b.gameObject.name;
                    names.Append(n).Append("; ");
                    if (n.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("exit", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("back", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (b.GetComponentInParent<GameDataUI>() != null) continue;
                    var rt = b.GetComponent<RectTransform>();
                    if (rt == null || !Done.Add(rt.GetInstanceID())) continue;
                    rt.anchoredPosition += Offset;
                    Plugin.L.LogInfo($"Close button '{n}' nudged by {Offset}");
                }
                if (Done.Count == 0) Plugin.L.LogInfo("GameDataPanel buttons: " + names);
            }
            catch (Exception e) { Plugin.L.LogWarning("CloseButtonNudge: " + e.Message); }
        }
    }

    /// <summary>
    /// Character window: the Russian stat lines are longer and run into the body figure.
    /// Shrink the figure (all body-part images) around its bottom-centre, so it frees space at the top.
    /// </summary>
    [HarmonyPatch]
    internal static class BodyFigureShrink
    {
        // interface rework: a bit smaller still, so the bigger stats font above gets room
        static float Scale => Plugin.CfgRework.Value ? 0.7f : 0.8f;
        static readonly System.Collections.Generic.HashSet<int> Done = new();

        [HarmonyPatch(typeof(CharacterBodyPartsPanel), nameof(CharacterBodyPartsPanel.Init))]
        [HarmonyPostfix]
        static void AfterInit(CharacterBodyPartsPanel __instance)
        {
            try
            {
                if (__instance == null || !Lang.Active) return;
                if (__instance.TryCast<CharacterBodyPartsPanel_OnHit>() != null) return; // HUD hit indicator
                var parts = __instance.bodyPartUIAry;
                if (parts == null || parts.Length == 0) return;
                if (!Done.Add(__instance.GetInstanceID())) return;

                var rts = new System.Collections.Generic.List<RectTransform>();
                foreach (var p in parts)
                {
                    if (p == null) continue;
                    var rt = p.GetComponent<RectTransform>();
                    if (rt != null) rts.Add(rt);
                }
                if (rts.Count == 0) return;
                var parent = rts[0].parent;
                // parts must share a parent for a common anchor
                foreach (var rt in rts) if (rt.parent != parent) { Plugin.L.LogInfo("Body figure: parts have different parents, skipped"); return; }

                float minY = float.MaxValue, minX = float.MaxValue, maxX = float.MinValue;
                foreach (var rt in rts)
                {
                    var lp = rt.localPosition; var r = rt.rect;
                    minY = Mathf.Min(minY, lp.y + r.yMin * rt.localScale.y);
                    minX = Mathf.Min(minX, lp.x + r.xMin * rt.localScale.x);
                    maxX = Mathf.Max(maxX, lp.x + r.xMax * rt.localScale.x);
                }
                var anchor = new Vector3((minX + maxX) / 2f, minY, 0f);
                foreach (var rt in rts)
                {
                    var lp = rt.localPosition;
                    rt.localPosition = anchor + (lp - anchor) * Scale;
                    rt.localScale = rt.localScale * Scale;
                }
                Plugin.L.LogInfo($"Body figure: {rts.Count} parts under '{parent.name}' scaled to {Scale}");
            }
            catch (Exception e) { Plugin.L.LogWarning("BodyFigureShrink: " + e.Message); }
        }
    }

    /// <summary>
    /// Item action menu (ButtonsPanel): labels used best fit 7–12, so short ones («РЕМОНТ») were huge and long
    /// ones tiny, in an oversized frame. Interface rework: one font size, compact rows, frame as wide as the labels.
    /// </summary>
    [HarmonyPatch]
    internal static class ButtonsPanelCompact
    {
        const int Font = 8;          // the inventory's regular label size
        const float RowH = 14f, MinW = 96f, MaxW = 153f, PadX = 14f;

        // original best-fit range of each pooled label (we lower the max, so remember the prefab's value)
        static readonly System.Collections.Generic.Dictionary<int, (int min, int max)> Orig = new();

        /// <summary>
        /// Translation-only version: keep the game's window, but the labels use best fit with a large
        /// maximum, so short Russian words come out huge and long ones tiny. Give every row the size
        /// of the longest one.
        /// </summary>
        static void SameSize(ButtonsPanel panel)
        {
            try
            {
                var gridTr = panel.transform.Find("Grid");
                if (gridTr == null) return;
                var texts = new System.Collections.Generic.List<Text>();
                int min = 0, max = 0;
                for (int i = 0; i < gridTr.childCount; i++)
                {
                    var c = gridTr.GetChild(i);
                    if (!c.gameObject.activeSelf) continue;
                    var t = c.GetComponentInChildren<Text>(true);
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    int id = t.GetInstanceID();
                    if (!Orig.ContainsKey(id) && t.resizeTextForBestFit)
                        Orig[id] = (t.resizeTextMinSize, t.resizeTextMaxSize);
                    if (Orig.TryGetValue(id, out var o)) { min = min == 0 ? o.min : Math.Min(min, o.min); max = Math.Max(max, o.max); }
                    texts.Add(t);
                }
                if (texts.Count < 2 || max == 0) return;
                foreach (var t in texts)
                {
                    t.resizeTextForBestFit = true;
                    t.resizeTextMinSize = min;
                    t.resizeTextMaxSize = max;
                }
                Canvas.ForceUpdateCanvases();
                int common = max;
                foreach (var t in texts)
                {
                    // the generator works in screen pixels (font size × canvas scale)
                    float ppu = t.pixelsPerUnit > 0.01f ? t.pixelsPerUnit : 1f;
                    int used = (int)Math.Floor(t.cachedTextGenerator.fontSizeUsedForBestFit / ppu + 0.01f);
                    if (used > 0) common = Math.Min(common, used);
                }
                common = Math.Max(common, min);
                foreach (var t in texts) t.resizeTextMaxSize = common;
            }
            catch (Exception e) { Plugin.L.LogWarning("ButtonsPanel same size: " + e.Message); }
        }

        [HarmonyPatch(typeof(ButtonsPanel), nameof(ButtonsPanel.ShowButtons))]
        [HarmonyPostfix]
        static void AfterShow(ButtonsPanel __instance)
        {
            if (!Lang.Active || __instance == null) return;
            if (!Plugin.CfgRework.Value) { SameSize(__instance); return; }
            try
            {
                var root = __instance.transform;
                var gridTr = root.Find("Grid");
                var frameTr = root.Find("Frame");
                if (gridTr == null || frameTr == null) return;
                var grid = gridTr.GetComponent<GridLayoutGroup>();
                var frame = frameTr.GetComponent<RectTransform>();
                if (grid == null || frame == null) return;

                int rows = 0; float maxW = 0f;
                var texts = new System.Collections.Generic.List<Text>();
                for (int i = 0; i < gridTr.childCount; i++)
                {
                    var c = gridTr.GetChild(i);
                    if (!c.gameObject.activeSelf) continue;
                    rows++;
                    var t = c.GetComponentInChildren<Text>(true);
                    if (t == null) continue;
                    t.resizeTextForBestFit = false;
                    t.fontSize = Font;
                    texts.Add(t);
                    if (!string.IsNullOrEmpty(t.text)) maxW = Mathf.Max(maxW, t.preferredWidth);
                }
                if (rows == 0) return;
                float w = Mathf.Clamp(Mathf.Ceil(maxW + PadX), MinW, MaxW);
                // a label that still does not fit shrinks a little instead of being cut
                foreach (var t in texts)
                    if (t.preferredWidth > w - 4f) { t.resizeTextForBestFit = true; t.resizeTextMinSize = 6; t.resizeTextMaxSize = Font; }

                grid.cellSize = new Vector2(w, RowH);
                float h = rows * RowH + 4f;
                frame.sizeDelta = new Vector2(w + 4f, h);
                frame.anchoredPosition = new Vector2(frame.anchoredPosition.x, 2f - h / 2f);
            }
            catch (Exception e) { Plugin.L.LogWarning("ButtonsPanelCompact: " + e.Message); }
        }
    }

    internal static class AutoFit
    {
        static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);

        internal static void Check(Text t)
        {
            try
            {
                if (t == null || t.resizeTextForBestFit) return;
                var s = t.text;
                if (string.IsNullOrEmpty(s) || s.Length > 48 || s.IndexOf('\n') >= 0 || !Cyr.IsMatch(s)) return;
                // the interaction card sizes itself to its labels; best fit + wrap breaks lines there
                if (t.GetComponentInParent<InteractUI>() != null) return;
                float w = t.rectTransform.rect.width;
                if (w < 8f) return;
                float pw = t.preferredWidth;
                if (pw <= w + 1f) return;
                int size = t.fontSize;
                t.resizeTextMaxSize = size;
                t.resizeTextMinSize = Math.Max(6, (int)(size * 0.6f));
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                t.resizeTextForBestFit = true;
            }
            catch { }
        }
    }

    /// <summary>
    /// ItemAttr.TrimRepeatedPrefix removes the item name from the start of its description.
    /// In Russian this breaks compound words: name "Фонтан" + desc "Фонтан-фейерверк." -> "-фейерверк."
    /// If what remains is not a new sentence, show the full description instead.
    /// </summary>
    [HarmonyPatch(typeof(ItemAttr), nameof(ItemAttr.TrimRepeatedPrefix))]
    internal static class TrimPatch
    {
        static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

        static void Postfix(string name, string description, ref string __result)
        {
            if (!Plugin.CfgFixTrim.Value || description == null || __result == null || __result == description) return;
            if (!Lang.Active) return;
            var rest = Tags.Replace(__result, "").TrimStart();
            if (rest.Length == 0) { __result = description; return; }
            char c = rest[0];
            if (char.IsLower(c) || c == '-' || c == '–' || c == '—' || c == ',' || c == '«' || c == '(' || c == ':')
                __result = description;
        }
    }

    [HarmonyPatch]
    internal static class TooltipPatches
    {
        [HarmonyPatch(typeof(DescriptionTipPanel), nameof(DescriptionTipPanel.ShowDescription))]
        [HarmonyPrefix]
        static void Show(ref string descriptionStr)
        {
            if (!Plugin.CfgTooltip.Value || !Plugin.CfgRework.Value || !Lang.Active) return;
            try { descriptionStr = TooltipFormatter.Format(descriptionStr); }
            catch (Exception e) { Plugin.L.LogWarning("Tooltip format: " + e.Message); }
        }

        [HarmonyPatch(typeof(DescriptionTipPanel), nameof(DescriptionTipPanel.RefreshTextIfTarget))]
        [HarmonyPrefix]
        static void Refresh(ref string descriptionStr)
        {
            if (!Plugin.CfgTooltip.Value || !Plugin.CfgRework.Value || !Lang.Active) return;
            try { descriptionStr = TooltipFormatter.Format(descriptionStr); }
            catch { }
        }
    }
}
