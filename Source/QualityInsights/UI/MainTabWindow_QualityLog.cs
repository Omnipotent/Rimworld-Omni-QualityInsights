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

        // --- layout constants ---
        private const float HeaderH   = 32f;
        private const float FooterH   = 34f;
        private const float RowH      = 28f;
        private const float ColHeaderH= 28f;
        private const float Pad       = 8f;

        // fixed widths so buttons don’t jump around as labels change
        private const float QualBtnW  = 140f;     // wide enough for “Legendary”
        private const float SkillBtnW = 160f;     // most skills fit comfortably

        public override Vector2 RequestedTabSize => new(980f, 640f);

        public override void DoWindowContents(Rect rect)
        {
            var comp = Current.Game.GetComponent<QualityLogComponent>();
            var rows = comp.entries.AsEnumerable();

            // ===== Header (filters + export/reload) =====
            var header = new Rect(0, 0, rect.width, HeaderH);

            // Search label + box with explicit spacing
            var searchLabel = new Rect(0, header.y, 70f, HeaderH);
            Widgets.Label(searchLabel, "Search:");
            var searchBox = new Rect(searchLabel.xMax + 6f, header.y, rect.width * 0.28f, HeaderH);
            search = Widgets.TextField(searchBox, search);

            // Quality dropdown (fixed width)
            float x = searchBox.xMax + Pad;
            if (Widgets.ButtonText(new Rect(x, header.y, QualBtnW, HeaderH), filterQuality?.ToString() ?? "All qualities"))
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
            x += QualBtnW + Pad;

            // Skill dropdown (fixed width; list built from entries so it’s mod-safe)
            var skillsPresent = comp.entries
                .Select(e => e.skillDef ?? "Unknown")
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (Widgets.ButtonText(new Rect(x, header.y, SkillBtnW, HeaderH), filterSkill ?? "All skills"))
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

            // Export / Reload on the far right
            if (Widgets.ButtonText(new Rect(rect.width - 220f, header.y, 100f, HeaderH), "QI_ExportCSV".Translate()))
                ExportCSV(comp);
            if (Widgets.ButtonText(new Rect(rect.width - 110f, header.y, 110f, HeaderH), "Reload")) { /* live list */ }

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
            var body = new Rect(0, HeaderH + 8f, rect.width, rect.height - HeaderH - 8f - FooterH);
            if (s_viewMode == ViewMode.Table) DrawTable(body, list);
            else                              DrawLog(body, list);

            // ===== Footer: compact view toggle =====
            var footer = new Rect(0, rect.height - FooterH, rect.width, FooterH);
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
            int idx = 0;
            foreach (var e in list)
            {
                var r = new Rect(0, y, viewRect.width, rowH);
                if ((idx & 1) == 1) DrawZebra(r);         // subtle zebra striping
                if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);

                string pawn  = e.pawnName ?? "Unknown";
                string skill = e.skillDef ?? "Unknown";
                string stuff = string.IsNullOrEmpty(e.stuffDef) ? string.Empty : $"[{e.stuffDef}]";
                string tags  = (e.inspiredCreativity ? " | Inspired" : string.Empty) +
                               (e.productionSpecialist ? " | ProdSpec" : string.Empty);

                DrawCellOneLine(r, $"{e.TimeAgoString} | {pawn} ({skill} {e.skillLevelAtFinish}) ➜ {e.quality} | {e.thingDef}{stuff}{tags}");
                y += rowH + 2f;
                idx++;
            }

            Widgets.EndScrollView();
        }

        // ----------------- TABLE VIEW -----------------
        private static readonly string[] ColHeaders = { "Time", "Pawn", "Skill", "Lvl", "Quality", "Item", "Stuff", "Tags" };
        private static readonly float[]  ColWidths  = { 0.12f, 0.16f, 0.13f, 0.06f, 0.12f, 0.22f, 0.12f, 0.07f };

        private void DrawTable(Rect outRect, List<QualityLogEntry> list)
        {
            list = SortForTable(list);

            // 1) Sticky header (not inside scroll view)
            float x = 0f;
            for (int i = 0; i < ColHeaders.Length; i++)
            {
                float w = outRect.width * ColWidths[i];
                var hr = new Rect(outRect.x + x, outRect.y, w, ColHeaderH);
                if (Mouse.IsOver(hr)) Widgets.DrawHighlight(hr);
                var label = ColHeaders[i] + (s_sortCol == i ? (s_sortAsc ? " ▲" : " ▼") : string.Empty);
                if (Widgets.ButtonText(hr, label, false, false, false))
                {
                    if (s_sortCol == i) s_sortAsc = !s_sortAsc; else { s_sortCol = i; s_sortAsc = (i == 0); }
                }
                x += w;
            }
            Widgets.DrawLineHorizontal(outRect.x, outRect.y + ColHeaderH - 1f, outRect.width);

            // 2) Scroll area with rows
            var rowsOut = new Rect(outRect.x, outRect.y + ColHeaderH, outRect.width, outRect.height - ColHeaderH);
            var viewRect = new Rect(0, 0, rowsOut.width - 16f, list.Count * RowH + 8f);
            Widgets.BeginScrollView(rowsOut, ref scroll, viewRect);

            float y = 0f;
            int idx = 0;
            foreach (var e in list)
            {
                x = 0f;
                var row = new Rect(0, y, viewRect.width, RowH);
                if ((idx & 1) == 1) DrawZebra(row);     // zebra striping
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[0], RowH), e.TimeAgoString); x += viewRect.width * ColWidths[0];
                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[1], RowH), e.pawnName ?? "Unknown"); x += viewRect.width * ColWidths[1];
                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[2], RowH), e.skillDef ?? "Unknown"); x += viewRect.width * ColWidths[2];

                // right-align numeric level
                DrawCellRightOneLine(new Rect(x, y, viewRect.width * ColWidths[3], RowH), e.skillLevelAtFinish.ToString());
                x += viewRect.width * ColWidths[3];

                var qRect = new Rect(x, y, viewRect.width * ColWidths[4], RowH);
                DrawQualityOneLine(qRect, e.quality);
                x += viewRect.width * ColWidths[4];

                var itemRect = new Rect(x, y, viewRect.width * ColWidths[5], RowH);
                DrawThingWithIconOneLine(itemRect, e.thingDef);
                x += viewRect.width * ColWidths[5];

                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[6], RowH), e.stuffDef ?? string.Empty);
                x += viewRect.width * ColWidths[6];

                string tags =
                    (e.inspiredCreativity ? "Inspired " : string.Empty) +
                    (e.productionSpecialist ? "ProdSpec" : string.Empty);
                DrawCellOneLine(new Rect(x, y, viewRect.width * ColWidths[7], RowH), tags);

                y += RowH;
                idx++;
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

        // ===== one-line cell renderers (truncate + tooltip) =====
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

        private static void DrawCellRightOneLine(Rect r, string text)
        {
            r = r.ContractedBy(4f, 0f);
            var oldWrap = Text.WordWrap;
            var oldAnchor = Text.Anchor;
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleRight;

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

        private static void DrawZebra(Rect r)
        {
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.04f);
            GUI.DrawTexture(r, BaseContent.WhiteTex);
            GUI.color = old;
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
