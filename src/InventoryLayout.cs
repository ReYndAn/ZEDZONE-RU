using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ZedZoneRU
{
    /// <summary>
    /// Interface rework, inventory screen. Layout bugs of the game itself (the English version has them too):
    /// - the equipment window starts 4 units above the backpack window and covers the bottom of the tab bar;
    /// - the close buttons (16×16) are taller than the header rows they sit in, so they hang below them;
    /// - the title «СОРТ.» button repeats the sort button of the window's bottom bar and takes the room
    ///   of the container name, which then wraps into three lines and spills below the title row;
    /// - a weapon slot's durability bar is as wide as the weapon picture (46) while the slot is 42 wide,
    ///   and in the small slots it lies on the bottom edge of the frame.
    /// </summary>
    [HarmonyPatch]
    internal static class InventoryLayout
    {
        const float TopDrop = 4f;        // equipment window top: 20 -> 24, right under the tab bar
        const float HeaderDrop = 2f;     // equipment list header row: 24..36 -> 26..38, centred like the backpack title
        const float CloseScale = 0.75f;  // 16×16 -> 12×12, the height of the «СОРТ.» button next to it
        const float Gap = 2f;            // between the close button, the sort button and the edge
        const float BarSide = 4f, BarBottom = 3f; // durability bar inset inside the slot frame

        static readonly HashSet<int> Done = new();
        static readonly List<CharacterWeaponSlotUI> Slots = new();
        static readonly Dictionary<int, RectTransform> InnerFrame = new();
        static Canvas.WillRenderCanvases _frameHook;

        static bool On => Plugin.CfgRework.Value && Lang.Active;

        [HarmonyPatch(typeof(CharacterEquipmentPanel), nameof(CharacterEquipmentPanel.OpenPanel))]
        [HarmonyPostfix]
        static void AfterOpen(CharacterEquipmentPanel __instance)
        {
            if (!On || __instance == null) return;
            try
            {
                FixEquipmentPanel(__instance);
                var pp = PlayerPanel.instance;
                if (pp != null)
                {
                    FixPlayerClose(pp);
                    CollectSlots(pp);
                }
                var scroll = PlayerInventorysScrollPanel.instance;
                if (scroll != null) FixTitlesUnder(scroll.transform);
                HookFrame();
            }
            catch (Exception e) { Plugin.L.LogWarning("InventoryLayout: " + e.Message); }
        }

        // containers (vest, trousers, bags) are added to the backpack column while the inventory is open
        [HarmonyPatch(typeof(PlayerInventorysScrollPanel), nameof(PlayerInventorysScrollPanel.AddChildPanel))]
        [HarmonyPostfix]
        static void AfterAddChild(GameObject __0)
        {
            if (!On || __0 == null) return;
            try { FixTitlesUnder(__0.transform); }
            catch (Exception e) { Plugin.L.LogWarning("InventoryLayout titles: " + e.Message); }
        }

        // ---------------- equipment window ----------------

        static void FixEquipmentPanel(CharacterEquipmentPanel p)
        {
            var rt = p.transform.TryCast<RectTransform>();
            if (rt == null || !Done.Add(rt.GetInstanceID())) return;
            // the window loses TopDrop at the top, its bottom stays; centre-anchored children follow the
            // centre (down TopDrop/2), so each child is corrected relative to that
            const float parentShift = -TopDrop / 2f;
            Edges(rt, -TopDrop, 0f, 0f, false);
            for (int i = 0; i < rt.childCount; i++)
            {
                var c = rt.GetChild(i).TryCast<RectTransform>();
                if (c == null) continue;
                var n = c.name;
                if (n.StartsWith("MonitorBGImage", StringComparison.Ordinal) || n == "HealthBGImage" ||
                    n == "EquipmentsBGImage" || n == "ScanlineUI" || n == "CharacterBGImage")
                    Edges(c, -TopDrop, 0f, parentShift, IsQuarterTurn(c));
                else if (n == "CharacterRawImage")
                {
                    float h = c.rect.height;
                    Edges(c, -TopDrop, 0f, parentShift, false);
                    var raw = c.GetComponent<RawImage>();
                    if (raw != null && h > TopDrop)
                    {
                        // show the lower part of the camera picture instead of squeezing it
                        var uv = raw.uvRect;
                        raw.uvRect = new Rect(uv.x, uv.y, uv.width, uv.height * (h - TopDrop) / h);
                    }
                }
                else if (n == "EquipmentViewport")
                    Edges(c, -HeaderDrop, 0f, parentShift, false);
                else if (n == "CharacterStatusText" || n == "CharacterStatusTextBGImage")
                    Edges(c, -TopDrop, 0f, parentShift, false);   // bottom stays: bars below are aligned to it
            }
        }

        /// <summary>Move the top and bottom edges of a centre-anchored rect by absolute amounts (canvas units, + = up).</summary>
        static void Edges(RectTransform rt, float top, float bottom, float parentShift, bool quarterTurn)
        {
            float dSize = top - bottom;
            float dPos = (top + bottom) / 2f - parentShift;
            var s = rt.sizeDelta;
            rt.sizeDelta = quarterTurn ? new Vector2(s.x + dSize, s.y) : new Vector2(s.x, s.y + dSize);
            rt.anchoredPosition += new Vector2(0f, dPos);
        }

        static bool IsQuarterTurn(Transform t)
        {
            float z = Mathf.Repeat(t.localEulerAngles.z, 180f);
            return Mathf.Abs(z - 90f) < 1f;
        }

        // the equipment window's own close button: same size as the backpack one, centred on the header row
        static void FixPlayerClose(PlayerPanel pp)
        {
            var close = pp.transform.Find("CloseButton")?.TryCast<RectTransform>();
            var eq = pp.characterEquipmentPanel;
            var vp = eq != null ? eq.transform.Find("EquipmentViewport")?.TryCast<RectTransform>() : null;
            if (close == null || vp == null || !Done.Add(close.GetInstanceID())) return;
            close.localScale = new Vector3(CloseScale, CloseScale, 1f);
            // header row = top 12 units of the list; button right edge Gap inside the list's right edge
            var parent = close.parent;
            Vector3 topRight = parent.InverseTransformPoint(vp.TransformPoint(new Vector3(vp.rect.xMax, vp.rect.yMax, 0f)));
            float half = close.rect.width * CloseScale / 2f;
            var target = new Vector2(topRight.x - Gap - half, topRight.y - 6f);
            close.anchoredPosition += target - (Vector2)close.localPosition;
        }

        // ---------------- window title bars (backpack, vest, trousers, boxes…) ----------------
        // Each title row holds the name, «СОРТ.» and the close button. The bottom button bar of the same
        // window (InventoryButtonRow) has its own sort button, so the top one is hidden while the bottom
        // one is shown, and the name gets the room up to the close button.
        // Laid out every frame before drawing: the game re-lays the title out when the window is refreshed.

        sealed class TitleRow
        {
            public RectTransform Title, Close, Sort;
            public Transform Panel;
            public InventoryButtonRow Row;
            public readonly List<RectTransform> Names = new();
        }
        static readonly List<TitleRow> Titles = new();

        static void FixTitlesUnder(Transform root)
        {
            var buttons = root.GetComponentsInChildren<Button>(true);
            foreach (var b in buttons)
            {
                if (b == null || b.name != "CloseButton") continue;
                var title = b.transform.parent;
                if (title == null || title.name != "Title") continue;
                FixTitle(title.TryCast<RectTransform>());
            }
        }

        // boxes and other containers that are opened without the backpack column
        [HarmonyPatch(typeof(CommonInventoryPanel), nameof(CommonInventoryPanel.OpenPanel))]
        [HarmonyPostfix]
        static void AfterCommonOpen(CommonInventoryPanel __instance)
        {
            if (!On || __instance == null) return;
            try { FixTitle(__instance.titleRect); }
            catch (Exception e) { Plugin.L.LogWarning("InventoryLayout box title: " + e.Message); }
        }

        static void FixTitle(RectTransform title)
        {
            if (title == null || !Done.Add(title.GetInstanceID())) return;
            var close = title.Find("CloseButton")?.TryCast<RectTransform>();
            if (close == null) return;
            close.localScale = new Vector3(CloseScale, CloseScale, 1f);
            var row = new TitleRow
            {
                Title = title,
                Close = close,
                Sort = title.Find("SortButton")?.TryCast<RectTransform>(),
                Panel = title.parent,
            };
            for (int i = 0; i < title.childCount; i++)
            {
                var c = title.GetChild(i).TryCast<RectTransform>();
                // the name (containers) or the weight line (backpack): stretched across the row
                if (c != null && c.anchorMin.x == 0f && c.anchorMax.x == 1f && c.GetComponent<Text>() != null)
                    row.Names.Add(c);
            }
            Titles.Add(row);
            LayTitle(row);
            HookFrame();
        }

        static bool LayTitle(TitleRow r)
        {
            var title = r.Title;
            if (title == null) return false;                       // window destroyed
            if (!title.gameObject.activeInHierarchy) return true;
            float w = title.rect.width;

            // close button: right edge Gap inside the row
            float closeW = r.Close.rect.width * CloseScale;
            float closeX = w / 2f - Gap - closeW / 2f;
            PlaceX(r.Close, closeX, w);

            // «СОРТ.»: left of the close button, hidden while the bottom bar has a sort button
            if (r.Sort != null)
            {
                float k = BottomSortShown(r) ? 0f : 1f;
                if (r.Sort.localScale.x != k) r.Sort.localScale = new Vector3(k, k, 1f);
                float sortW = r.Sort.rect.width;
                PlaceX(r.Sort, closeX - closeW / 2f - Gap - sortW / 2f, w);
            }

            // name: up to the nearest visible button on its right
            float xMax = title.rect.xMax;
            for (int n = 0; n < r.Names.Count; n++)
            {
                var t = r.Names[n];
                if (t == null) continue;
                float start = title.rect.xMin + t.offsetMin.x;
                float right = xMax;
                for (int i = 0; i < title.childCount; i++)
                {
                    var c = title.GetChild(i).TryCast<RectTransform>();
                    if (c == null || !c.gameObject.activeSelf || c.localScale.x <= 0f || c.GetComponent<Button>() == null) continue;
                    float left = c.localPosition.x + c.rect.xMin * c.localScale.x;
                    if (left > start && left < right) right = left;
                }
                float offset = right < xMax ? right - Gap - xMax : t.offsetMax.x;
                if (Mathf.Abs(t.offsetMax.x - offset) > 0.01f) t.offsetMax = new Vector2(offset, t.offsetMax.y);
            }
            return true;
        }

        static bool BottomSortShown(TitleRow r)
        {
            if (r.Panel == null) return false;
            if (r.Row == null) r.Row = r.Panel.GetComponent<InventoryButtonRow>();
            Button b = r.Row != null ? r.Row.sortButton : null;
            if (b == null) b = r.Panel.Find("ButtonBar/SortIconButton")?.GetComponent<Button>();
            return b != null && b.gameObject.activeInHierarchy;
        }

        /// <summary>Put a rect's centre at x (relative to the parent's centre), whatever its horizontal anchor; centre it vertically.</summary>
        static void PlaceX(RectTransform rt, float x, float parentW)
        {
            float anchorX = (rt.anchorMin.x - 0.5f) * parentW;
            float pivotFix = (rt.pivot.x - 0.5f) * rt.rect.width * rt.localScale.x;
            var parent = rt.parent.TryCast<RectTransform>();
            float anchorY = parent != null ? (rt.anchorMin.y - 0.5f) * parent.rect.height : 0f;
            float pivotFixY = (rt.pivot.y - 0.5f) * rt.rect.height * rt.localScale.y;
            var target = new Vector2(x - anchorX + pivotFix, -anchorY + pivotFixY);
            if ((rt.anchoredPosition - target).sqrMagnitude > 0.0001f) rt.anchoredPosition = target;
        }

        // ---------------- weapon slot durability bars ----------------

        static void CollectSlots(PlayerPanel pp)
        {
            Slots.RemoveAll(s => s == null);
            foreach (var s in pp.GetComponentsInChildren<CharacterWeaponSlotUI>(true))
                if (s != null && !Slots.Exists(x => x.GetInstanceID() == s.GetInstanceID())) Slots.Add(s);
        }

        static void HookFrame()
        {
            if (_frameHook != null) return;
            _frameHook = new Action(EveryFrame);
            Canvas.add_preWillRenderCanvases(_frameHook);   // after the game's LateUpdate, before the UI is drawn
        }

        static void EveryFrame()
        {
            if (!Plugin.CfgRework.Value) return;
            FixBars();
            try { Titles.RemoveAll(r => !LayTitle(r)); }
            catch { }
        }

        // ProgressBarGroup re-places the bar under the weapon picture every frame; re-place it inside the frame
        static void FixBars()
        {
            try
            {
                if (Slots.Count == 0) return;
                foreach (var s in Slots)
                {
                    if (s == null || !s.isActiveAndEnabled) continue;
                    var grp = s.progressGroup;
                    if (grp == null || !grp.gameObject.activeInHierarchy) continue;
                    var g = grp.transform.TryCast<RectTransform>();
                    var inner = Inner(s);
                    if (g == null || inner == null) continue;
                    var slot = s.transform;
                    var r = inner.rect;
                    Vector3 lp = inner.localPosition;
                    float width = r.width - 2f * BarSide;
                    var bottomCentre = slot.TransformPoint(new Vector3(lp.x + r.center.x, lp.y + r.yMin + BarBottom, 0f));
                    g.pivot = new Vector2(0.5f, 0f);
                    g.rotation = slot.rotation;
                    g.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                    g.position = bottomCentre;
                }
            }
            catch { }
        }

        // the slot's inner (darker) frame: "BGImage (1)", "BGImage (2)"…
        static RectTransform Inner(CharacterWeaponSlotUI s)
        {
            int id = s.GetInstanceID();
            if (InnerFrame.TryGetValue(id, out var rt) && rt != null) return rt;
            rt = null;
            var t = s.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.name.StartsWith("BGImage (", StringComparison.Ordinal)) { rt = c.TryCast<RectTransform>(); break; }
            }
            InnerFrame[id] = rt;
            return rt;
        }
    }
}
