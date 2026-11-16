using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ZedZoneRU
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "community.zedzone.ru";
        public const string Name = "ZED ZONE Russian Fix";
        public const string Version = "0.3.0";

        internal static ManualLogSource L;
        internal static string Dir;

        internal static ConfigEntry<bool> CfgTranslate;
        internal static ConfigEntry<bool> CfgFixTrim;
        internal static ConfigEntry<bool> CfgTooltip;
        internal static ConfigEntry<bool> CfgAutoFit;
        internal static ConfigEntry<bool> CfgDumpUntranslated;
        internal static ConfigEntry<bool> CfgDumpTooltips;
        internal static ConfigEntry<bool> CfgUiAudit;
        internal static ConfigEntry<bool> CfgRework;

        public override void Load()
        {
            L = Log;
            Dir = Path.Combine(Paths.PluginPath, "ZedZoneRU");
            Directory.CreateDirectory(Dir);

            CfgTranslate = Config.Bind("General", "TranslateLeftoverText", true,
                "Переводить английский текст, который игра выводит мимо системы локализации (постройки, руководство и т.п.).");
            CfgFixTrim = Config.Bind("General", "FixDescriptionTrim", true,
                "Не обрезать начало описания предмета, если оно совпадает с названием (баг «Фонтан / -фейерверк.»).");
            CfgTooltip = Config.Bind("Tooltip", "ImprovedItemTooltip", true,
                "Переработанная подсказка о предмете: единый регистр, единицы измерения, вес и цена внизу.");
            CfgAutoFit = Config.Bind("General", "AutoFitLongText", true,
                "Уменьшать шрифт однострочных надписей, которые не помещаются в свою рамку (обрезанное «УДАЛИТ» и т.п.).");
            CfgDumpUntranslated = Config.Bind("Debug", "DumpUntranslated", true,
                "Записывать непереведённые строки в BepInEx/plugins/ZedZoneRU/untranslated.txt (для доработки перевода).");
            CfgDumpTooltips = Config.Bind("Debug", "DumpRawTooltips", true,
                "Записывать исходный текст подсказок в BepInEx/plugins/ZedZoneRU/tooltips_raw.txt.");
            CfgRework = Config.Bind("Interface", "Rework", true,
                "Переработка интерфейса: подсказка предмета, панель характеристик, цвета справки на пергаменте, интервалы в настройках, окно загрузки, двоеточия. false — только перевод (русский текст по-прежнему подгоняется под рамки).");
            CfgUiAudit = Config.Bind("Diagnostics", "UiAudit", false,
                "Отладка: записывать надписи, которые не помещаются в рамку, и размеры шрифтов (ui_audit.txt, ui_fonts.tsv).");

            try { Translator.Init(); }
            catch (Exception e) { L.LogError("Translator init failed: " + e); }

            var h = new Harmony(Guid);
            TryPatch(h, typeof(TextPatches));
            TryPatch(h, typeof(TrimPatch));
            TryPatch(h, typeof(TooltipPatches));
            TryPatch(h, typeof(CloseButtonNudge));
            TryPatch(h, typeof(BodyFigureShrink));
            TryPatch(h, typeof(ButtonsPanelCompact));
            TryPatch(h, typeof(InventoryLayout));
            try { AddComponent<UiAuditBehaviour>(); } catch (Exception e) { L.LogWarning("UiAudit: " + e.Message); }
            L.LogMessage($"{Name} {Version} loaded. Entries: {Translator.Count}");
        }

        static void TryPatch(Harmony h, Type t)
        {
            try { h.PatchAll(t); L.LogInfo("Patched " + t.Name); }
            catch (Exception e) { L.LogError("Patch " + t.Name + " failed: " + e); }
        }
    }
}
