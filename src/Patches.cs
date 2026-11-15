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
        static void SetText(ref string value)
        {
            if (!Plugin.CfgTranslate.Value || string.IsNullOrEmpty(value) || !Lang.Active) return;
            try { value = Translator.Translate(value); } catch { }
        }

        [HarmonyPatch(typeof(Text), nameof(Text.text), MethodType.Setter)]
        [HarmonyPostfix]
        static void AfterSetText(Text __instance)
        {
            if (!Plugin.CfgAutoFit.Value || !Lang.Active) return;
            AutoFit.Check(__instance);
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
            if (!Plugin.CfgTooltip.Value || !Lang.Active) return;
            try { descriptionStr = TooltipFormatter.Format(descriptionStr); }
            catch (Exception e) { Plugin.L.LogWarning("Tooltip format: " + e.Message); }
        }

        [HarmonyPatch(typeof(DescriptionTipPanel), nameof(DescriptionTipPanel.RefreshTextIfTarget))]
        [HarmonyPrefix]
        static void Refresh(ref string descriptionStr)
        {
            if (!Plugin.CfgTooltip.Value || !Lang.Active) return;
            try { descriptionStr = TooltipFormatter.Format(descriptionStr); }
            catch { }
        }
    }
}
