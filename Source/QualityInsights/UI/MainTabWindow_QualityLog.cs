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

        // --- layout constants ---
        private const float HeaderH   = 32f;
        private const float FooterH   = 34f;
        private const float RowH      = 28f;
        private const float ColHeaderH= 28f;
        private const float Pad       = 8f;

        // fixed widths so buttons don’t jump around as labels change
        private const float QualBtnW  = 140f;     // wide enough for “Legendary”
        private const float SkillBtnW = 160f;     // most skills fit comfortably

        // --- per-instance drag state (no cross-window fighting) ---
        private int   _dragCol = -1;          // which splitter is being dragged (this instance only)
        private float _dragStartX;            // local X at mouse-down
        private const float SplitterW = 10f;
        private const float ColMinFrac = 0.06f;
        public  bool IsDraggingSplitter => _dragCol >= 0;

        // --- per-instance pixel cache ---
        private float[] _colPx = Array.Empty<float>();
        private float   _lastTableW = -1f;
        private const float ColMinPx = 80f;

        // shared "layout version" so settings resets invalidate all instances
        private static int s_layoutGen = 0;
        private int _seenLayoutGen = -1;

        // Column layout helpers
        // NEW: default fractions now include an extra "RL" column after "Time"
        internal static List<float> DefaultColFractions() =>
            new() { 0.10f, 0.10f, 0.16f, 0.12f, 0.06f, 0.12f, 0.20f, 0.10f, 0.04f };
        public static void InvalidateColumnLayout() { s_layoutGen++; }

        // --- CSV helpers ---
        private static string s_lastExportPath = string.Empty;

        // Cross-platform “reveal in Finder/Explorer/Files”
        private static void OpenInFileBrowser(string targetPath)
        {
            try
            {
                if (string.IsNullOrEmpty(targetPath))
                    targetPath = GenFilePaths.SaveDataFolderPath;

                targetPath = Path.GetFullPath(targetPath);
                string dir = Directory.Exists(targetPath)
                    ? targetPath
                    : (Path.GetDirectoryName(targetPath) ?? GenFilePaths.SaveDataFolderPath);

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var plat = Application.platform;

                if (plat == RuntimePlatform.WindowsPlayer || plat == RuntimePlatform.WindowsEditor)
                {
                    if (File.Exists(targetPath))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = "/select,\"" + targetPath + "\"",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true
                        });
                    }
                    return;
                }

                if (plat == RuntimePlatform.OSXPlayer || plat == RuntimePlatform.OSXEditor)
                {
                    if (File.Exists(targetPath))
                        System.Diagnostics.Process.Start("open", "-R \"" + targetPath + "\"");
                    else
                        System.Diagnostics.Process.Start("open", "\"" + dir + "\"");
                    return;
                }

                System.Diagnostics.Process.Start("xdg-open", dir);
            }
            catch (System.Exception ex)
            {
                Log.Warning($"[QualityInsights] OpenInFileBrowser failed: {ex}");
                try
                {
                    GUIUtility.systemCopyBuffer = targetPath ?? GenFilePaths.SaveDataFolderPath;
                    Messages.Message("Couldn’t open file browser. Path copied to clipboard.", MessageTypeDefOf.RejectInput, false);
                }
                catch { }
            }
        }

        public static void ResetColumnsToDefaults()
        {
            QualityInsightsMod.Settings.colFractions = DefaultColFractions();
            QualityInsightsMod.Instance.WriteSettings();
            InvalidateColumnLayout();
        }

        private void EnsureColPx(float tableW)
        {
            int n = ColHeaders.Length;

            var frac = QualityInsightsMod.Settings.colFractions;
            if (frac == null || frac.Count != n)
            {
                frac = QualityInsightsMod.Settings.colFractions = DefaultColFractions();
                InvalidateColumnLayout();
            }

            if (_seenLayoutGen != s_layoutGen)
            {
                _colPx = Array.Empty<float>();
                _lastTableW = -1f;
                _seenLayoutGen = s_layoutGen;
            }

            if (_colPx.Length != n - 1 || Mathf.Abs(_lastTableW - tableW) > 0.5f || _colPx.All(w => w <= 0f))
            {
                _colPx = new float[n - 1];
                for (int i = 0; i < n - 1; i++)
                    _colPx[i] = Mathf.Max(ColMinPx, tableW * frac[i]);
                _lastTableW = tableW;
            }
        }

        public override Vector2 RequestedTabSize
        {
            get
            {
                float w = Mathf.Min(Mathf.Max(980f, Verse.UI.screenWidth * 0.90f), 1700f);
                float h = Mathf.Min(Mathf.Max(640f, Verse.UI.screenHeight * 0.85f), 1000f);
                return new Vector2(w, h);
            }
        }

        // Format RL seconds as compact "1h 2m", "3m 5s", etc.
        private static string FormatPlayTime(double seconds)
        {
            if (seconds < 0) return "–";
            int s = Mathf.FloorToInt((float)seconds);
            int h = s / 3600; s %= 3600;
            int m = s / 60;   s %= 60;
            if (h > 0) return $"{h}h {m}m";
            if (m > 0) return $"{m}m {s}s";
            return $"{s}s";
        }

        public override void DoWindowContents(Rect rect)
        {
            var oldFont = Text.Font;
            Text.Font = QualityInsightsMod.Settings.GetLogGameFont();
            float rowH = 28f * QualityInsightsMod.Settings.tableRowScale;
            float headerH = 28f * QualityInsightsMod.Settings.tableRowScale;

            try
            {
                var comp = QualityLogComponent.Ensure(Current.Game);
                var rows = comp.entries.AsEnumerable();
                double nowPlay = comp.PlaySecondsAccum; // snapshot once per repaint

                // ===== Header (filters + export) =====
                var header = new Rect(0, 0, rect.width, headerH);

                var searchLabel = new Rect(0, header.y, 70f, headerH);
                Widgets.Label(searchLabel, "Search:");
                var searchBox = new Rect(searchLabel.xMax + 6f, header.y, rect.width * 0.28f, headerH);
                search = Widgets.TextField(searchBox, search);

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

                // Right-aligned header: Pop out
                float rx = rect.width - 110f;
                if (Widgets.ButtonText(new Rect(rx, header.y, 100f, headerH), "Pop out"))
                    Find.WindowStack.Add(new QualityLogWindow());

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

                // ===== Body =====
                var body = new Rect(0, headerH + 8f, rect.width, rect.height - headerH - 8f - FooterH);
                if (s_viewMode == ViewMode.Table) DrawTable(body, list, rowH, headerH, nowPlay);
                else DrawLog(body, list, rowH, nowPlay);

                // ===== Footer =====
                var footer = new Rect(0, rect.height - FooterH, rect.width, FooterH);

                float bw = 100f, gap = 8f;
                float xRight = footer.xMax - (bw * 2 + gap * 2 + 90f) - 6f;

                // Export
                if (Widgets.ButtonText(new Rect(xRight, footer.y + 3f, bw, 28f), "QI_ExportCSV".Translate()))
                    ExportCSV(comp);
                xRight += bw + gap;

                // Open folder
                if (Widgets.ButtonText(new Rect(xRight, footer.y + 3f, 90f, 28f), "Open folder"))
                {
                    string target = File.Exists(s_lastExportPath) ? s_lastExportPath : ExportDir;
                    OpenInFileBrowser(target);
                }
                xRight += 90f + gap;

                // Settings
                if (Widgets.ButtonText(new Rect(xRight, footer.y + 3f, bw, 28f), "Settings"))
                    QualityInsightsMod.OpenSettings();

                // Left side: view mode toggles
                float fx = 4f;
                if (Widgets.ButtonText(new Rect(fx, footer.y + 3f, 80f, 28f), s_viewMode == ViewMode.Table ? "Table ✓" : "Table"))
                    s_viewMode = ViewMode.Table;
                fx += 88f;
                if (Widgets.ButtonText(new Rect(fx, footer.y + 3f, 70f, 28f), s_viewMode == ViewMode.Log ? "Log ✓" : "Log"))
                    s_viewMode = ViewMode.Log;
            }
            finally
            {
                Text.Font = oldFont;
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

            int maxIcons = Mathf.Clamp(Mathf.FloorToInt(r.width / h), 0, 3);
            int painted = 0;

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

            var textRect = new Rect(x + 2f, r.y, r.xMax - (x + 2f), r.height);
            string label = string.Join(", ", matDefNames);
            DrawCellOneLine(textRect, label);
            TooltipHandler.TipRegion(r, label);
        }

        // ----------------- LOG VIEW (adds RL display) -----------------
        private void DrawLog(Rect outRect, List<QualityLogEntry> list, float rowH, double nowPlay)
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

                string rl = e.HasPlayStamp ? $" (RL: {FormatPlayTime(nowPlay - e.playSecondsAtLog)})" : string.Empty;
                DrawCellOneLine(r, $"{e.TimeAgoString}{rl} | {pawn} ({skill} {e.skillLevelAtFinish}) ➜ {e.quality} | {e.thingDef}{stuff}{tags}");
                y += rowH + 2f;
                idx++;
            }

            Widgets.EndScrollView();
        }

        // ----------------- TABLE VIEW (adds RL column) -----------------
        private static readonly string[] ColHeaders = { "Time", "RL", "Pawn", "Skill", "Lvl", "Quality", "Item", "Stuff", "Tags" };

        private void DrawTable(Rect outRect, List<QualityLogEntry> list, float rowH, float headerH, double nowPlay)
        {
            EnsureColPx(outRect.width);
            int n = ColHeaders.Length;
            float hx = 0f;

            // ----- Header -----
            for (int i = 0; i < n; i++)
            {
                float w = (i < n - 1)
                    ? _colPx[i]
                    : Mathf.Max(ColMinPx, outRect.width - hx);
                var hr = new Rect(outRect.x + hx, outRect.y, w, headerH);

                bool hasSplit = i < n - 1;
                var hrLabel   = hasSplit ? new Rect(hr.x, hr.y, hr.width - SplitterW, hr.height) : hr;

                if (Mouse.IsOver(hrLabel)) Widgets.DrawHighlight(hrLabel);
                string label = ColHeaders[i] + (s_sortCol == i ? (s_sortAsc ? " ▲" : " ▼") : string.Empty);

                var oldA = Text.Anchor;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(hrLabel.ContractedBy(6f, 0f), label);
                Text.Anchor = oldA;

                if (Widgets.ButtonInvisible(hrLabel, true))
                {
                    if (s_sortCol == i) s_sortAsc = !s_sortAsc; else { s_sortCol = i; s_sortAsc = (i == 0); }
                    Event.current.Use();
                }

                if (i < n - 1)
                {
                    var split = new Rect(hr.xMax - SplitterW, hr.y, SplitterW, hr.height);
                    MouseoverSounds.DoRegion(split);

                    var old = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.08f);
                    Widgets.DrawLineVertical(split.center.x, split.y + 4f, split.height - 8f);
                    GUI.color = old;

                    if (Event.current.type == EventType.MouseDown && split.Contains(Event.current.mousePosition))
                    {
                        _dragCol    = i;
                        _dragStartX = Event.current.mousePosition.x;
                        Event.current.Use();
                    }
                }

                hx += w;
            }
            Widgets.DrawLineHorizontal(outRect.x, outRect.y + headerH - 1f, outRect.width);

            // ----- Handle splitter drag -----
            if (_dragCol >= 0)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    float dx = Event.current.mousePosition.x - _dragStartX;
                    _dragStartX = Event.current.mousePosition.x;

                    int i = _dragCol;
                    float newW = Mathf.Max(ColMinPx, _colPx[i] + dx);

                    float used = 0f; for (int k = 0; k < _colPx.Length; k++) used += _colPx[k];
                    float lastW = Mathf.Max(ColMinPx, outRect.width - used);

                    float delta    = newW - _colPx[i];
                    float newLastW = lastW - delta;
                    if (newLastW < ColMinPx)
                    {
                        float allowed = lastW - ColMinPx;
                        newW = _colPx[i] + allowed;
                        newLastW = ColMinPx;
                    }

                    _colPx[i] = newW;
                    Event.current.Use();
                }
                else if (Event.current.type == EventType.MouseUp || Event.current.rawType == EventType.MouseUp)
                {
                    _dragCol = -1;

                    float used = 0f; for (int k = 0; k < _colPx.Length; k++) used += _colPx[k];
                    float lastW = Mathf.Max(ColMinPx, outRect.width - used);

                    var fracs = new List<float>(n);
                    for (int k = 0; k < _colPx.Length; k++) fracs.Add(Mathf.Max(ColMinPx, _colPx[k]) / outRect.width);
                    fracs.Add(lastW / outRect.width);

                    float sum = fracs.Sum();
                    if (sum > 0f) for (int k = 0; k < fracs.Count; k++) fracs[k] /= sum;

                    QualityInsightsMod.Settings.colFractions = fracs;
                    QualityInsightsMod.Instance.WriteSettings();
                    InvalidateColumnLayout();
                    Event.current.Use();
                }

            }

            // ----- Sort -----
            list = SortForTable(list);

            // ----- Rows -----
            var rowsOut  = new Rect(outRect.x, outRect.y + headerH, outRect.width, outRect.height - headerH);
            var viewRect = new Rect(0, 0, rowsOut.width - 16f, list.Count * rowH + 8f);
            Widgets.BeginScrollView(rowsOut, ref scroll, viewRect);

            float usedPx = 0f; for (int k = 0; k < _colPx.Length; k++) usedPx += _colPx[k];
            float lastPxRows = Mathf.Max(ColMinPx, viewRect.width - usedPx);

            float y = 0f;
            int idx = 0;
            foreach (var e in list)
            {
                float x = 0f;
                var row = new Rect(0, y, viewRect.width, rowH);
                if ((idx & 1) == 1) DrawZebra(row);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                for (int i = 0; i < ColHeaders.Length; i++)
                {
                    float w = (i < ColHeaders.Length - 1) ? _colPx[i] : lastPxRows;
                    var cell = new Rect(x, y, w, rowH);

                    switch (i)
                    {
                        case 0: // Time (in-game)
                            DrawCellOneLine(cell, e.TimeAgoString);
                            break;
                        case 1: // RL (real-life play time)
                            DrawCellOneLine(cell, e.HasPlayStamp ? FormatPlayTime(nowPlay - e.playSecondsAtLog) : "–");
                            break;
                        case 2: // Pawn
                            DrawPawnWithIconOneLine(cell, e.pawnName);
                            break;
                        case 3: // Skill
                            DrawCellOneLine(cell, e.skillDef ?? "Unknown");
                            break;
                        case 4: // Lvl
                            DrawCellRightOneLine(cell, e.skillLevelAtFinish.ToString());
                            break;
                        case 5: // Quality
                            DrawQualityOneLine(cell, e.quality);
                            break;
                        case 6: // Item
                            DrawThingWithIconOneLine(cell, e.thingDef);
                            break;
                        case 7: // Stuff / Materials
                            if (e.HasMats) DrawMatsListOneLine(cell, e.mats);
                            else           DrawDefWithIconOneLine(cell, e.stuffDef);
                            break;
                        case 8: // Tags
                            string tags = (e.inspiredCreativity ? "Inspired " : string.Empty) +
                                          (e.productionSpecialist ? "ProdSpec" : string.Empty);
                            DrawCellOneLine(cell, tags);
                            break;
                    }

                    x += w;
                }

                y += rowH;
                idx++;
            }

            Widgets.EndScrollView();
        }

        private List<QualityLogEntry> SortForTable(List<QualityLogEntry> list)
        {
            // NOTE: Sorting for RL column uses HasPlayStamp + playSecondsAtLog (unknown first)
            IOrderedEnumerable<QualityLogEntry> ordered = s_sortCol switch
            {
                0 => list.OrderBy(e => e.gameTicks), // Time asc = older first
                1 => list.OrderBy(e => e.HasPlayStamp ? 0 : 1).ThenBy(e => e.playSecondsAtLog),
                2 => list.OrderBy(e => e.pawnName),
                3 => list.OrderBy(e => e.skillDef),
                4 => list.OrderBy(e => e.skillLevelAtFinish),
                5 => list.OrderBy(e => e.quality),
                6 => list.OrderBy(e => e.thingDef),
                7 => list.OrderBy(e => e.stuffDef),
                8 => list.OrderBy(e => (e.inspiredCreativity ? 1 : 0) + (e.productionSpecialist ? 2 : 0)),
                _ => list.OrderBy(e => e.gameTicks)
            };
            return (s_sortAsc ? ordered : ordered.Reverse()).ToList();
        }

        // ===== one-line cell renderers =====
        private static void DrawCellOneLine(Rect r, string text)
        {
            r = r.ContractedBy(4f, 0f);
            var oldWrap = Text.WordWrap;
            var oldAnchor = Text.Anchor;
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;

            string t = text ?? string.Empty;
            string shown = t.Truncate(r.width);
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
            string shown = t.Truncate(r.width);
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
            var left  = r.LeftPartPixels(r.height);
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
            var left  = r.LeftPartPixels(r.height);
            var right = new Rect(r.x + left.width + 4f, r.y, r.width - left.width - 4f, r.height);

            Pawn p = TryFindPawnByShortName(label);
            if (p != null)
            {
                var tex = PortraitsCache.Get(p, new Vector2(left.width, left.height), Rot4.South, default, 1.0f);
                GUI.DrawTexture(left, tex);
            }
            else
            {
                GUI.DrawTexture(left, BaseContent.GreyTex);
            }

            DrawCellOneLine(right, label);
            TooltipHandler.TipRegion(r, label);
        }

        private static Pawn TryFindPawnByShortName(string labelShort)
        {
            if (string.IsNullOrEmpty(labelShort)) return null;

            var map = Find.CurrentMap;
            IEnumerable<Pawn> cands = map?.mapPawns?.AllPawnsSpawned ?? Enumerable.Empty<Pawn>();

            Pawn found = cands.FirstOrDefault(p =>
                string.Equals(p.LabelShortCap, labelShort, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.LabelShort,    labelShort, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p?.Name?.ToStringShort, labelShort, StringComparison.OrdinalIgnoreCase));

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

        private static string ExportDir =>
            Path.Combine(GenFilePaths.SaveDataFolderPath, "QualityInsights", "Exports");

        private static void EnsureExportDir()
        {
            if (!Directory.Exists(ExportDir))
                Directory.CreateDirectory(ExportDir);
        }

        private static void PruneExportFolder()
        {
            var s  = QualityInsightsMod.Settings;
            var di = new DirectoryInfo(ExportDir);
            if (!di.Exists) return;

            var files = di.GetFiles("QualityInsights_*.csv")
                        .OrderBy(f => f.CreationTimeUtc).ToList();

            while (s.maxExportFiles > 0 && files.Count > s.maxExportFiles)
            {
                files[0].Delete();
                files.RemoveAt(0);
            }

            long maxBytes = (long)Math.Max(0, s.maxExportFolderMB) * 1024L * 1024L;
            if (maxBytes > 0)
            {
                long total = files.Sum(f => f.Length);
                int i = 0;
                while (total > maxBytes && i < files.Count)
                {
                    total -= files[i].Length;
                    files[i].Delete();
                    i++;
                }
            }
        }

        private static void DrawZebra(Rect r)
        {
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.04f);
            GUI.DrawTexture(r, BaseContent.WhiteTex);
            GUI.color = old;
        }

        private static void ExportCSV(QualityLogComponent comp)
        {
            EnsureExportDir();

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string path  = Path.Combine(ExportDir, $"QualityInsights_{stamp}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("Ticks,TimeAgo,PlayTime,Pawn,Skill,Level,Quality,Thing,Stuff,Materials,Inspired,ProductionSpecialist");
            foreach (var e in comp.entries.OrderBy(x => x.gameTicks))
            {
                string rl = e.HasPlayStamp ? FormatPlayTime(comp.PlaySecondsAccum - e.playSecondsAtLog) : "-";
                sb.AppendLine(string.Join(",",
                    e.gameTicks,
                    e.TimeAgoString,
                    rl,
                    Escape(e.pawnName),
                    Escape(e.skillDef),
                    e.skillLevelAtFinish,
                    e.quality,
                    Escape(e.thingDef),
                    Escape(e.stuffDef ?? string.Empty),
                    Escape(string.Join("+", e.mats ?? new List<string>())),
                    e.inspiredCreativity,
                    e.productionSpecialist));
            }

            File.WriteAllText(path, sb.ToString());
            s_lastExportPath = path;
            GUIUtility.systemCopyBuffer = path;

            PruneExportFolder();
            Messages.Message($"Exported to {path}", MessageTypeDefOf.TaskCompletion, false);
        }

        private static string Escape(string s) => '"' + (s ?? string.Empty).Replace("\"", "\"\"") + '"';
    }
}
