using System;
using System.Linq;
using System.Text;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using QualityInsights.Logging;
using Verse.Sound;

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

        // drag state
        private static int   s_dragCol = -1;    // index of the column being dragged (splitter is on its right edge)
        private static float s_dragStartX;      // screen X at mouse-down
        private static float[] s_col = Array.Empty<float>();   // not nullable, init empty
        private const float SplitterW = 10f;                   // easier to grab
        private const float ColMinFrac = 0.06f;


        public override Vector2 RequestedTabSize
        {
            get
            {
                // Use Verse.UI.*, not our QualityInsights.UI namespace
                float w = Mathf.Min(Mathf.Max(980f, Verse.UI.screenWidth  * 0.90f), 1700f);
                float h = Mathf.Min(Mathf.Max(640f,  Verse.UI.screenHeight * 0.85f), 1000f);
                return new Vector2(w, h);
            }
        }


        public override void DoWindowContents(Rect rect)
        {
            // // init working array for column widths from settings
            if (s_dragCol < 0)
            {
                s_col = QualityInsightsMod.Settings.colFractions.ToArray();
                NormalizeCols(s_col);
            }

            // apply user-selected font + row/header sizes
            var oldFont = Text.Font;
            Text.Font = QualityInsightsMod.Settings.GetLogGameFont();
            float rowH = 28f * QualityInsightsMod.Settings.tableRowScale;
            float headerH = 28f * QualityInsightsMod.Settings.tableRowScale;

            try
            {
                var comp = Current.Game.GetComponent<QualityLogComponent>();
                var rows = comp.entries.AsEnumerable();

                // ===== Header (filters + export/reload) =====
                var header = new Rect(0, 0, rect.width, headerH);

                // Search label + box with explicit spacing
                var searchLabel = new Rect(0, header.y, 70f, headerH);
                Widgets.Label(searchLabel, "Search:");
                var searchBox = new Rect(searchLabel.xMax + 6f, header.y, rect.width * 0.28f, headerH);
                search = Widgets.TextField(searchBox, search);

                // Quality dropdown (fixed width)
                float x = searchBox.xMax + Pad;
                if (Widgets.ButtonText(new Rect(x, header.y, QualBtnW, headerH), filterQuality?.ToString() ?? "All qualities"))
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

                if (Widgets.ButtonText(new Rect(x, header.y, SkillBtnW, headerH), filterSkill ?? "All skills"))
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

                // Export / Reload on the far right  // right-aligned header buttons
                float rx = rect.width - 330f;
                if (Widgets.ButtonText(new Rect(rx, header.y, 100f, headerH), "Pop out"))
                    Find.WindowStack.Add(new QualityLogWindow());
                rx += 110f;

                if (Widgets.ButtonText(new Rect(rx, header.y, 100f, headerH), "QI_ExportCSV".Translate()))
                    ExportCSV(comp);
                rx += 110f;

                if (Widgets.ButtonText(new Rect(rx, header.y, 110f, headerH), "Reload")) { /* live list */ }


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
                var body = new Rect(0, headerH + 8f, rect.width, rect.height - headerH - 8f - FooterH);
                if (s_viewMode == ViewMode.Table) DrawTable(body, list, rowH, headerH);
                else DrawLog(body, list, rowH);

                // ===== Footer: compact view toggle =====
                var footer = new Rect(0, rect.height - FooterH, rect.width, FooterH);
                float fx = 4f;
                if (Widgets.ButtonText(new Rect(fx, footer.y + 3f, 80f, 28f), s_viewMode == ViewMode.Table ? "Table ✓" : "Table"))
                    s_viewMode = ViewMode.Table;
                fx += 88f;
                if (Widgets.ButtonText(new Rect(fx, footer.y + 3f, 70f, 28f), s_viewMode == ViewMode.Log ? "Log ✓" : "Log"))
                    s_viewMode = ViewMode.Log;
            }
            finally
            {
                Text.Font = oldFont; // always restore
            }
        }

        private static void DrawMatsListOneLine(Rect r, List<string> matDefNames)
        {
            if (matDefNames == null || matDefNames.Count == 0)
            {
                DrawCellOneLine(r, string.Empty);
                return;
            }

            r = r.ContractedBy(2f, 2f);
            float h = r.height;
            float x = r.x;

            // how many square icons fit; cap to keep it tidy
            int maxIcons = Mathf.Clamp(Mathf.FloorToInt(r.width / h), 0, 3);
            int painted = 0;

            // icons
            for (int i = 0; i < matDefNames.Count && painted < maxIcons; i++)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(matDefNames[i]);
                if (def?.uiIcon == null) continue;

                var cell = new Rect(x, r.y, h, h);
                var old = GUI.color;
                GUI.color = def.uiIconColor;
                GUI.DrawTexture(cell, def.uiIcon, ScaleMode.ScaleToFit);
                GUI.color = old;

                x += h + 2f;
                painted++;
            }

            // text after icons (truncates nicely; full list in tooltip)
            var textRect = new Rect(x + 2f, r.y, r.xMax - (x + 2f), r.height);
            string label = string.Join(", ", matDefNames);
            DrawCellOneLine(textRect, label);
            TooltipHandler.TipRegion(r, label);
        }


        private static void NormalizeCols(float[] a)
        {
            // clamp to a minimum and renormalize to sum == 1
            float sum = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = Mathf.Max(a[i], ColMinFrac);
                sum += a[i];
            }
            if (sum <= 0f) sum = 1f;
            for (int i = 0; i < a.Length; i++) a[i] /= sum;
        }

        // ----------------- LOG VIEW (scaled) -----------------
        private void DrawLog(Rect outRect, List<QualityLogEntry> list, float rowH)
        {
            list = list.OrderByDescending(e => e.gameTicks).ToList();

            var viewRect = new Rect(0, 0, outRect.width - 16f, list.Count * (rowH + 2f) + 6f);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            float y = 0f;
            int idx = 0;
            foreach (var e in list)
            {
                var r = new Rect(0, y, viewRect.width, rowH);
                if ((idx & 1) == 1) DrawZebra(r);
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

        // ----------------- TABLE VIEW (scaled + resizable columns) -----------------
        private static readonly string[] ColHeaders = { "Time", "Pawn", "Skill", "Lvl", "Quality", "Item", "Stuff", "Tags" };
        // (Remove/ignore ColWidths; we now use s_col[] which is loaded from settings)

        private void DrawTable(Rect outRect, List<QualityLogEntry> list, float rowH, float headerH)
        {
            float hx = 0f;
            for (int i = 0; i < ColHeaders.Length; i++)
            {
                float w  = outRect.width * s_col[i];
                var hr   = new Rect(outRect.x + hx, outRect.y, w, headerH);

                bool hasSplit = i < s_col.Length - 1;
                var hrLabel   = hasSplit ? new Rect(hr.x, hr.y, hr.width - SplitterW, hr.height) : hr;

                if (Mouse.IsOver(hrLabel)) Widgets.DrawHighlight(hrLabel);
                string label = ColHeaders[i] + (s_sortCol == i ? (s_sortAsc ? " ▲" : " ▼") : string.Empty);

                var oldA = Text.Anchor;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(hrLabel.ContractedBy(6f, 0f), label);
                Text.Anchor = oldA;

                // sort when the *label* is clicked (not the splitter)
                if (Widgets.ButtonInvisible(hrLabel, true))
                {
                    if (s_sortCol == i) s_sortAsc = !s_sortAsc; else { s_sortCol = i; s_sortAsc = (i == 0); }
                    Event.current.Use();
                }

                // splitter at the right edge of the column (except the last column)
                if (hasSplit)
                {
                    var split = new Rect(hr.xMax - SplitterW, hr.y, SplitterW, hr.height);
                    MouseoverSounds.DoRegion(split);

                    // small visual cue to grab
                    var old = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, 0.08f);
                    Widgets.DrawLineVertical(split.center.x, split.y + 4f, split.height - 8f);
                    GUI.color = old;

                    if (Event.current.type == EventType.MouseDown && split.Contains(Event.current.mousePosition))
                    {
                        s_dragCol    = i;
                        s_dragStartX = Event.current.mousePosition.x;
                        Event.current.Use();
                    }
                }

                hx += w;
            }
            Widgets.DrawLineHorizontal(outRect.x, outRect.y + headerH - 1f, outRect.width);


            // handle splitter drag (if any)
            if (s_dragCol >= 0)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    float totalW = outRect.width;
                    float dx = Event.current.mousePosition.x - s_dragStartX;
                    s_dragStartX = Event.current.mousePosition.x;

                    float df = dx / totalW;
                    int a = s_dragCol, b = s_dragCol + 1;

                    // try to add to left (a) and subtract from right (b); clamp
                    float newA = Mathf.Clamp(s_col[a] + df, ColMinFrac, 1f);
                    float delta = newA - s_col[a];
                    float newB = Mathf.Clamp(s_col[b] - delta, ColMinFrac, 1f);

                    // if b clamped, adjust a so total stays 1
                    float actuallyTaken = s_col[b] - newB;
                    s_col[a] += (delta - actuallyTaken);
                    s_col[b] = newB;

                    NormalizeCols(s_col);

                    Event.current.Use();
                }
                else if (Event.current.type == EventType.MouseUp || Event.current.rawType == EventType.MouseUp)
                {
                    s_dragCol = -1;
                    // persist to settings
                    QualityInsightsMod.Settings.colFractions = new List<float>(s_col);
                    QualityInsightsMod.Instance.WriteSettings();
                    Event.current.Use();
                }
            }

            // 2) Sort after header interactions
                list = SortForTable(list);

            // 3) Scroll area with rows
            var rowsOut  = new Rect(outRect.x, outRect.y + headerH, outRect.width, outRect.height - headerH);
            var viewRect = new Rect(0, 0, rowsOut.width - 16f, list.Count * rowH + 8f);
            Widgets.BeginScrollView(rowsOut, ref scroll, viewRect);

            float y = 0f;
            int idx = 0;
            foreach (var e in list)
            {
                float x = 0f;
                var row = new Rect(0, y, viewRect.width, rowH);
                if ((idx & 1) == 1) DrawZebra(row);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                // Time
                DrawCellOneLine(new Rect(x, y, viewRect.width * s_col[0], rowH), e.TimeAgoString);
                x += viewRect.width * s_col[0];

                // Pawn (with portrait)
                DrawPawnWithIconOneLine(new Rect(x, y, viewRect.width * s_col[1], rowH), e.pawnName);
                x += viewRect.width * s_col[1];

                // Skill
                DrawCellOneLine(new Rect(x, y, viewRect.width * s_col[2], rowH), e.skillDef ?? "Unknown");
                x += viewRect.width * s_col[2];

                // Lvl (right-aligned)
                DrawCellRightOneLine(new Rect(x, y, viewRect.width * s_col[3], rowH), e.skillLevelAtFinish.ToString());
                x += viewRect.width * s_col[3];

                // Quality
                var qRect = new Rect(x, y, viewRect.width * s_col[4], rowH);
                DrawQualityOneLine(qRect, e.quality);
                x += viewRect.width * s_col[4];

                // Item (def icon)
                var itemRect = new Rect(x, y, viewRect.width * s_col[5], rowH);
                DrawThingWithIconOneLine(itemRect, e.thingDef);
                x += viewRect.width * s_col[5];

                // Stuff (def icon if available)
                var stuffRect = new Rect(x, y, viewRect.width * s_col[6], rowH);
                if (e.HasMats)
                    DrawMatsListOneLine(stuffRect, e.mats);
                else
                    DrawDefWithIconOneLine(stuffRect, e.stuffDef);
                x += viewRect.width * s_col[6];


                // Tags
                string tags = (e.inspiredCreativity ? "Inspired " : string.Empty) +
                            (e.productionSpecialist ? "ProdSpec" : string.Empty);
                DrawCellOneLine(new Rect(x, y, viewRect.width * s_col[7], rowH), tags);

                y += rowH;
                idx++;
            }

            Widgets.EndScrollView();
        }


        private List<QualityLogEntry> SortForTable(List<QualityLogEntry> list)
        {
            // stable, single-column sort with toggleable direction
            IOrderedEnumerable<QualityLogEntry> ordered = s_sortCol switch
            {
                0 => list.OrderBy(e => e.gameTicks), // Time asc = older first
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

        private static void DrawDefWithIconOneLine(Rect r, string defNameOrNull)
        {
            string name = defNameOrNull ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                DrawCellOneLine(r, string.Empty);
                return;
            }

            r = r.ContractedBy(2f, 2f);
            var left  = r.LeftPartPixels(r.height);
            var right = new Rect(r.x + left.width + 4f, r.y, r.width - left.width - 4f, r.height);

            var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def?.uiIcon != null)
            {
                var old = GUI.color;
                GUI.color = def.uiIconColor;
                GUI.DrawTexture(left, def.uiIcon, ScaleMode.ScaleToFit);
                GUI.color = old;
            }

            DrawCellOneLine(right, name);
            TooltipHandler.TipRegion(r, name);
        }

        private static void DrawPawnWithIconOneLine(Rect r, string pawnNameOrNull)
        {
            string label = pawnNameOrNull ?? "Unknown";
            r = r.ContractedBy(2f, 2f);
            var left  = r.LeftPartPixels(r.height); // portrait square
            var right = new Rect(r.x + left.width + 4f, r.y, r.width - left.width - 4f, r.height);

            Pawn p = TryFindPawnByShortName(label);
            if (p != null)
            {
                // cached portrait
                var tex = PortraitsCache.Get(p, new Vector2(left.width, left.height), Rot4.South, default, 1.0f);
                GUI.DrawTexture(left, tex);
            }
            else
            {
                // fallback: skill/pawn silhouette from Thing icon set would be ideal; else small white box
                GUI.DrawTexture(left, BaseContent.GreyTex);
            }

            DrawCellOneLine(right, label);
            TooltipHandler.TipRegion(r, label);
        }

        private static Pawn TryFindPawnByShortName(string labelShort)
        {
            if (string.IsNullOrEmpty(labelShort)) return null;

            // Current map first
            var map = Find.CurrentMap;
            IEnumerable<Pawn> cands = map?.mapPawns?.AllPawnsSpawned ?? Enumerable.Empty<Pawn>();

            Pawn found = cands.FirstOrDefault(p =>
                string.Equals(p.LabelShortCap, labelShort, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.LabelShort,    labelShort, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p?.Name?.ToStringShort, labelShort, StringComparison.OrdinalIgnoreCase));

            // If not on map, try world pawns/colonists in caravan etc.
            if (found == null)
            {
                var world = Find.WorldPawns?.AllPawnsAliveOrDead ?? Enumerable.Empty<Pawn>();
                found = world.FirstOrDefault(p =>
                    string.Equals(p.LabelShortCap, labelShort, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.LabelShort,    labelShort, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p?.Name?.ToStringShort, labelShort, StringComparison.OrdinalIgnoreCase));
            }
            return found;
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
            // sb.AppendLine("Ticks,TimeAgo,Pawn,Skill,Level,Quality,Thing,Stuff,Inspired,ProductionSpecialist");
            sb.AppendLine("Ticks,TimeAgo,Pawn,Skill,Level,Quality,Thing,Stuff,Materials,Inspired,ProductionSpecialist");
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
                    Escape(string.Join("+", e.mats ?? new List<string>())),   // Materials column
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
