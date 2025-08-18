using System;
using System.Linq;
using System.Text;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using QualityInsights.Logging;

namespace QualityInsights.UI
{
    public class MainTabWindow_QualityLog : MainTabWindow
    {
        private enum ViewMode { Log, Table }
        private static ViewMode s_viewMode = ViewMode.Table;   // remembers last used while game runs
        private static int  s_sortCol = 0;                     // table sort column
        private static bool s_sortAsc = false;                 // sort direction

        private Vector2 scroll;
        private string search = string.Empty;
        private QualityCategory? filterQuality = null;
        private string? filterSkill = null;  // defName from entries

        // cache for string truncation measurements
        private static readonly Dictionary<string, string> TruncCache = new();
        public override Vector2 RequestedTabSize => new(980f, 640f);

        public override void DoWindowContents(Rect rect)
        {
            var comp = Current.Game.GetComponent<QualityLogComponent>();
            var rows = comp.entries.AsEnumerable();

            // ===== Header (filters + export/reload) =====
            var header = new Rect(0, 0, rect.width, 32f);

            // Search
            Widgets.Label(header.LeftPart(0.12f), "Search:");
            search = Widgets.TextField(header.LeftPart(0.34f).RightPart(0.88f), search);

            float x = rect.width * 0.34f + 8f;

            // Small helper to make narrower buttons that fit text
            float ButtonAuto(ref float xx, string label, float pad = 16f)
            {
                var sz = Text.CalcSize(label).x + pad;
                if (Widgets.ButtonText(new Rect(xx, 0, sz, 32f), label)) { xx += sz + 8f; return sz; }
                xx += sz + 8f; return -1f;
            }

            // Quality dropdown (auto width)
            {
                string lab = filterQuality?.ToString() ?? "All qualities";
                float w = Text.CalcSize(lab).x + 24f;
                if (Widgets.ButtonText(new Rect(x, 0, w, 32f), lab))
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (QualityCategory q in Enum.GetValues(typeof(QualityCategory)))
                    {
                        var localQ = q;
                        opts.Add(new FloatMenuOption(localQ.ToString(), () => filterQuality = localQ));
                    }
                    opts.Add(new FloatMenuOption("All", () => filterQuality = null));
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                x += w + 8f;
            }

            // Skill dropdown (built from entries; auto width)
            {
                var skillsPresent = comp.entries
                    .Select(e => e.skillDef ?? "Unknown")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string lab = filterSkill ?? "All skills";
                float w = Text.CalcSize(lab).x + 24f;
                if (Widgets.ButtonText(new Rect(x, 0, w, 32f), lab))
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (var sd in skillsPresent)
                    {
                        var local = sd;
                        opts.Add(new FloatMenuOption(local, () => filterSkill = local));
                    }
                    opts.Add(new FloatMenuOption("All", () => filterSkill = null));
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                x += w + 8f;
            }

            // Export / Reload on the far right
            if (Widgets.ButtonText(new Rect(rect.width - 220f, 0, 100f, 32f), "QI_ExportCSV".Translate()))
                ExportCSV(comp);
            if (Widgets.ButtonText(new Rect(rect.width - 110f, 0, 110f, 32f), "Reload")) { /* live list */ }

            // ===== Apply filters =====
            if (!string.IsNullOrWhiteSpace(search))
            {
                rows = rows.Where(e =>
                    (!string.IsNullOrEmpty(e.pawnName) && e.pawnName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(e.thingDef) && e.thingDef.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
            }
            if (filterQuality.HasValue) rows = rows.Where(e => e.quality == filterQuality);
            if (!string.IsNullOrEmpty(filterSkill)) rows = rows.Where(e => string.Equals(e.skillDef, filterSkill, StringComparison.OrdinalIgnoreCase));

            var list = rows.ToList();

            // ===== Body (minus footer area) =====
            const float footerH = 34f;
            var body = new Rect(0, 40f, rect.width, rect.height - 40f - footerH);
            if (s_viewMode == ViewMode.Table) DrawTable(body, list);
            else                              DrawLog(body, list);

            // ===== Footer: compact view toggle =====
            var footer = new Rect(0, rect.height - footerH, rect.width, footerH);
            float fx = 4f;
            if (Widgets.ButtonText(new Rect(fx, footer.y + 3f, 80f, 28f), s_viewMode == ViewMode.Table ? "Table ✓" : "Table"))
                s_viewMode = ViewMode.Table;
            fx += 88f;
            if (Widgets.ButtonText(new Rect(fx, footer.y + 3f, 70f, 28f), s_viewMode == ViewMode.Log ? "Log ✓" : "Log"))
                s_viewMode = ViewMode.Log;
        }

        // ----------------- LOG VIEW -----------------
        private void DrawLog(Rect outRect, List<QualityLogEntry> list)
        {
            list = list.OrderByDescending(e => e.gameTicks).ToList();

            var rowH = 26f;
            var viewRect = new Rect(0, 0, outRect.width - 16f, list.Count * (rowH + 2f) + 6f);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            float y = 0f;
            foreach (var e in list)
            {
                var r = new Rect(0, y, viewRect.width, rowH);
                if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);

                string pawn  = e.pawnName ?? "Unknown";
                string skill = e.skillDef ?? "Unknown";
                string stuff = string.IsNullOrEmpty(e.stuffDef) ? string.Empty : $"[{e.stuffDef}]";
                string tags  = (e.inspiredCreativity ? " | Inspired" : string.Empty) +
                               (e.productionSpecialist ? " | ProdSpec" : string.Empty);

                // one-line, truncated
                DrawCellOneLine(r, $"{e.TimeAgoString} | {pawn} ({skill} {e.skillLevelAtFinish}) ➜ {e.quality} | {e.thingDef}{stuff}{tags}");
                y += rowH + 2f;
            }

            Widgets.EndScrollView();
        }

        // ----------------- TABLE VIEW -----------------
        private static readonly string[] ColHeaders = { "Time", "Pawn", "Skill", "Lvl", "Quality", "Item", "Stuff", "Tags" };
        private static readonly float[]  ColWidths  = { 0.12f, 0.16f, 0.13f, 0.06f, 0.12f, 0.22f, 0.12f, 0.07f };

        private void DrawTable(Rect outRect, List<QualityLogEntry> list)
        {
            list = SortForTable(list);

            var rowH = 28f;
            var headerH = 28f;
            var viewRect = new Rect(0, 0, outRect.width - 16f, headerH + list.Count * rowH + 8f);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            // Header (click to sort)
            float y = 0f, x = 0f;
            for (int i = 0; i < ColHeaders.Length; i++)
            {
                float w = viewRect.width * ColWidths[i];
                var hr = new Rect(x, y, w, headerH);
                if (Mouse.IsOver(hr)) Widgets.DrawHighlight(hr);
                var label = ColHeaders[i] + (s_sortCol == i ? (s_sortAsc ? " ▲" : " ▼") : string.Empty);
                if (Widgets.ButtonText(hr, label, false, false, false))
                {
                    if (s_sortCol == i) s_sortAsc = !s_sortAsc; else { s_sortCol = i; s_sortAsc = (i == 0); }
                }
                x += w;
            }
            y += headerH;
            Widgets.DrawLineHorizontal(0, y - 1f, viewRect.width);

            // Rows
            foreach (var e in list)
            {
                x = 0f;
                var row = new Rect(0, y, viewRect.width, rowH);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[0], rowH), e.TimeAgoString); x += viewRect.width * ColWidths[0];
                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[1], rowH), e.pawnName ?? "Unknown"); x += viewRect.width * ColWidths[1];
                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[2], rowH), e.skillDef ?? "Unknown"); x += viewRect.width * ColWidths[2];
                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[3], rowH), e.skillLevelAtFinish.ToString()); x += viewRect.width * ColWidths[3];

                var qRect = new Rect(x, y, viewRect.width * ColWidths[4], rowH);
                DrawQualityOneLine(qRect, e.quality);
                x += viewRect.width * ColWidths[4];

                var itemRect = new Rect(x, y, viewRect.width * ColWidths[5], rowH);
                DrawThingWithIconOneLine(itemRect, e.thingDef);
                x += viewRect.width * ColWidths[5];

                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[6], rowH), e.stuffDef ?? string.Empty);
                x += viewRect.width * ColWidths[6];

                string tags =
                    (e.inspiredCreativity ? "Inspired " : string.Empty) +
                    (e.productionSpecialist ? "ProdSpec" : string.Empty);
                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[7], rowH), tags);

                y += rowH;
            }

            Widgets.EndScrollView();
        }

        private List<QualityLogEntry> SortForTable(List<QualityLogEntry> list)
        {
            IOrderedEnumerable<QualityLogEntry> ordered = s_sortCol switch
            {
                0 => list.OrderBy(e => e.gameTicks),
                1 => list.OrderBy(e => e.pawnName),
                2 => list.OrderBy(e => e.skillDef),
                3 => list.OrderBy(e => e.skillLevelAtFinish),
                4 => list.OrderBy(e => e.quality),
                5 => list.OrderBy(e => e.thingDef),
                6 => list.OrderBy(e => e.stuffDef),
                7 => list.OrderBy(e => (e.inspiredCreativity ? 1 : 0) + (e.productionSpecialist ? 2 : 0)),
                _ => list.OrderBy(e => e.gameTicks)
            };
            return (s_sortAsc ? ordered : ordered.Reverse()).ToList();
        }

        // ===== one-line cell renderers (truncate + tooltip + centered vertically) =====
        private static void DrawCellOneLine(Rect r, string text)
        {
            r = r.ContractedBy(4f, 0f);
            var oldWrap = Text.WordWrap;
            var oldAnchor = Text.Anchor;
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;

            string t = text ?? string.Empty;
            string shown = t.Truncate(r.width, TruncCache);
            Widgets.Label(r, shown);
            if (!string.IsNullOrEmpty(t) && shown != t) TooltipHandler.TipRegion(r, t);

            Text.WordWrap = oldWrap;
            Text.Anchor = oldAnchor;
        }

        private static void DrawQualityOneLine(Rect r, QualityCategory q)
        {
            var old = GUI.color;
            GUI.color = q switch
            {
                QualityCategory.Awful      => new Color(0.6f, 0.6f, 0.6f),
                QualityCategory.Poor       => new Color(0.8f, 0.7f, 0.6f),
                QualityCategory.Normal     => Color.white,
                QualityCategory.Good       => new Color(0.7f, 1.0f, 0.7f),
                QualityCategory.Excellent  => new Color(0.6f, 0.9f, 1.0f),
                QualityCategory.Masterwork => new Color(0.9f, 0.8f, 1.0f),
                QualityCategory.Legendary  => new Color(1.0f, 0.9f, 0.5f),
                _ => Color.white
            };
            DrawCellOneLine(r, q.ToString());
            GUI.color = old;
        }

        private static void DrawThingWithIconOneLine(Rect r, string thingDefName)
        {
            r = r.ContractedBy(2f, 2f);
            var left  = r.LeftPartPixels(r.height);                  // square icon
            var right = new Rect(r.x + left.width + 4f, r.y, r.width - left.width - 4f, r.height);

            var def = DefDatabase<ThingDef>.GetNamedSilentFail(thingDefName);
            if (def?.uiIcon != null)
            {
                var old = GUI.color;
                GUI.color = def.uiIconColor;
                GUI.DrawTexture(left, def.uiIcon, ScaleMode.ScaleToFit);
                GUI.color = old;
            }

            DrawCellOneLine(right, thingDefName ?? string.Empty);
            if (!string.IsNullOrEmpty(thingDefName))
                TooltipHandler.TipRegion(r, thingDefName);
        }

        // ===== CSV export =====
        private static void ExportCSV(QualityLogComponent comp)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Ticks,TimeAgo,Pawn,Skill,Level,Quality,Thing,Stuff,Inspired,ProductionSpecialist");
            foreach (var e in comp.entries.OrderBy(x => x.gameTicks))
            {
                sb.AppendLine(string.Join(",",
                    e.gameTicks,
                    e.TimeAgoString,
                    Escape(e.pawnName),
                    Escape(e.skillDef),
                    e.skillLevelAtFinish,
                    e.quality,
                    Escape(e.thingDef),
                    Escape(e.stuffDef ?? string.Empty),
                    e.inspiredCreativity,
                    e.productionSpecialist));
            }
            var dir = GenFilePaths.SaveDataFolderPath;
            var path = Path.Combine(dir, "QualityInsights_Log.csv");
            File.WriteAllText(path, sb.ToString());
            Messages.Message($"Exported to {path}", MessageTypeDefOf.TaskCompletion, false);
        }

        private static string Escape(string s) => '"' + (s ?? string.Empty).Replace("\"", "\"\"") + '"';
    }
}
