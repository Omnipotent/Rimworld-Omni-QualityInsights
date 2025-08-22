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
        // ===== POP/DOCK TOGGLE STATE =====
        private static QualityLogWindow? s_floatingWin; // currently popped-out window (if any)

        private static bool FloatingOpen =>
            s_floatingWin != null && Find.WindowStack != null && Find.WindowStack.IsOpen(s_floatingWin);

        // Open a floating window and close the docked tab (this instance).
        private void PopOutAndCloseDock()
        {
            if (FloatingOpen) return;
            s_floatingWin = new QualityLogWindow();
            Find.WindowStack.Add(s_floatingWin);
            // Close this docked tab window so we never have both at once.
            Close(doCloseSound: false);
        }

        // Close the floating window (if any) and open the docked tab.
        private static void DockAndClosePopOut()
        {
            if (s_floatingWin != null)
            {
                try { s_floatingWin.Close(doCloseSound: false); } catch { }
                s_floatingWin = null;
            }
            // Find our MainButtonDef by tabWindowClass so we don't rely on a defName.
            var def = DefDatabase<MainButtonDef>.AllDefsListForReading
                .FirstOrDefault(d => d?.tabWindowClass == typeof(MainTabWindow_QualityLog));
            if (def != null)
                Find.MainTabsRoot.SetCurrentTab(def, playSound: true);
        }

        // Called by QualityLogWindow when the floating window opens/closes.
        internal static void RegisterFloating(QualityLogWindow w)   => s_floatingWin = w;
        internal static void UnregisterFloating(QualityLogWindow w) { if (s_floatingWin == w) s_floatingWin = null; }
        internal static void DockFromFloating() => DockAndClosePopOut();

        private enum ViewMode { Log, Table }
        private static ViewMode s_viewMode = ViewMode.Table;   // remembers last used while game runs
        private static Col  s_sortCol = Col.Time;                     // table sort column
        private static bool s_sortAsc = false;                 // sort direction

        private Vector2 scroll;
        private string search = string.Empty;
        private const string SearchCtrlName = "QI_SearchBox";   // NEW: for focusing the search box
        private QualityCategory? filterQuality = null;
        private string? filterSkill = null;  // defName from entries

        // --- layout constants ---
        private const float HeaderH   = 32f;
        private const float FooterH   = 34f;
        private const float RowH      = 28f;
        // private const float ColHeaderH= 28f;
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
        public bool IsHoveringSplitter { get; private set; }

        // --- per-instance pixel cache ---
        private float[] _colPx = Array.Empty<float>();
        private float   _lastTableW = -1f;
        private const float ColMinPx = 80f;

        // shared "layout version" so settings resets invalidate all instances
        private static int s_layoutGen = 0;
        private int _seenLayoutGen = -1;

        // Column layout helpers
        // default fractions include an extra "RL" column after "Time"
        internal static List<float> DefaultColFractions() =>
            // new() { 0.10f, 0.10f, 0.16f, 0.12f, 0.06f, 0.12f, 0.20f, 0.10f, 0.04f };
            // new() { 0.08f, 0.08f, 0.14f, 0.12f, 0.06f, 0.08f, 0.18f, 0.18f, 0.08f }; // 8 columns
            new() { 0.07f, 0.07f, 0.12f, 0.10f, 0.06f, 0.08f, 0.16f, 0.05f, 0.16f, 0.05f, 0.08f };

        public static void InvalidateColumnLayout() { s_layoutGen++; }

        // Fractions that produced the current _colPx cache.
        // Lets us auto-refresh if Settings.colFractions changes externally (e.g. via Settings UI).
        private float[] _appliedFracs = Array.Empty<float>();

        // --- CSV helpers ---
        private static string s_lastExportPath = string.Empty;

        // Stable column IDs so sort & persistence don't break when hidden
        private enum Col { Time, RL, Pawn, Skill, Lvl, Quality, Item, ItemRaw, Stuff, StuffRaw, Tags }

        private static readonly (Col id, string header, string key)[] AllCols =
        {
            (Col.Time,    "Time",     "Time"),
            (Col.RL,      "RL",       "RL"),
            (Col.Pawn,    "Pawn",     "Pawn"),
            (Col.Skill,   "Skill",    "Skill"),
            (Col.Lvl,     "Lvl",      "Lvl"),
            (Col.Quality, "Quality",  "Quality"),
            (Col.Item,    "Item",     "Item"),
            (Col.ItemRaw, "Item ID",  "ItemRaw"),
            (Col.Stuff,   "Stuff",    "Stuff"),
            (Col.StuffRaw,"Stuff ID", "StuffRaw"),
            (Col.Tags,    "Tags",     "Tags"),
        };


        // Helpers
        private static string HeaderFor(Col c) => AllCols.First(t => t.id == c).header;
        private static string KeyFor(Col c)    => AllCols.First(t => t.id == c).key;
        private static int IndexOf(Col c)      => Array.FindIndex(AllCols, t => t.id == c);

        // --- helpers for case-insensitive contains ---
        private static bool ContainsCI(string s, string needle)
            => !string.IsNullOrEmpty(s) && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool AnyCI(IEnumerable<string> seq, string needle)
            => seq != null && seq.Any(s => ContainsCI(s, needle));

        // one-time init of saved filters so the view opens "as you left it"
        private bool _filtersInitialized = false;

        private static string FriendlyThingLabel(string defNameOrNull)
        {
            if (string.IsNullOrEmpty(defNameOrNull)) return string.Empty;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defNameOrNull);
            return def?.LabelCap ?? defNameOrNull;
        }

        private static string FriendlyDefLabel(string defNameOrNull)
        {
            if (string.IsNullOrEmpty(defNameOrNull)) return string.Empty;
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defNameOrNull);
            return def?.LabelCap ?? defNameOrNull;
        }


        // Compute current visible list (never empty)
        private static List<Col> VisibleCols()
        {
            var hidden = QualityInsightsMod.Settings.hiddenCols ??= new List<string>();
            var vis = new List<Col>(AllCols.Length);
            foreach (var t in AllCols)
                if (!hidden.Contains(t.key)) vis.Add(t.id);
            if (vis.Count == 0) vis.Add(Col.Time); // safety
            return vis;
        }

        // NEW: right-click copy menu
        private void MaybeContextCopy(Rect rowRect, QualityLogEntry e)
        {
            var ev = Event.current;
            if (ev.type == EventType.MouseDown && ev.button == 1 && rowRect.Contains(ev.mousePosition))
            {
                string BuildFriendly()
                {
                    string pawn  = e.pawnName ?? "Unknown";
                    string skill = e.skillDef ?? "Unknown";
                    string item  = FriendlyThingLabel(e.thingDef);
                    string stuffFriendly = e.HasMats
                        ? string.Join("+", (e.mats ?? new List<string>()).Select(FriendlyDefLabel))
                        : FriendlyDefLabel(e.stuffDef);
                    string tags = (e.inspiredCreativity ? "Inspired " : string.Empty) +
                                (e.productionSpecialist ? "ProdSpec" : string.Empty);
                    return $"{e.TimeAgoString} | {pawn} ({skill} {e.skillLevelAtFinish}) ➜ {e.quality} | {item} | {stuffFriendly} | {tags}".TrimEnd(' ', '|');
                }

                string BuildRaw()
                {
                    string stuffRaw = e.HasMats
                        ? string.Join("+", e.mats ?? new List<string>())
                        : (e.stuffDef ?? string.Empty);
                    string tags = (e.inspiredCreativity ? "Inspired " : string.Empty) +
                                (e.productionSpecialist ? "ProdSpec" : string.Empty);
                    return $"{e.gameTicks},{e.pawnName},{e.skillDef},{e.skillLevelAtFinish},{e.quality},{e.thingDef},{stuffRaw},{tags}";
                }

                void Copy(string s)
                {
                    GUIUtility.systemCopyBuffer = s ?? string.Empty;
                    Messages.Message("Copied to clipboard", MessageTypeDefOf.TaskCompletion, false);
                }

                string stuffOnly = e.HasMats
                    ? string.Join("+", e.mats ?? new List<string>())
                    : (e.stuffDef ?? string.Empty);

                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption("Copy row (friendly)", () => Copy(BuildFriendly())),
                    new FloatMenuOption("Copy row (raw)",      () => Copy(BuildRaw())),
                    new FloatMenuOption("Copy Item defName",   () => Copy(e.thingDef ?? string.Empty)),
                    new FloatMenuOption("Copy Stuff defName(s)", () => Copy(stuffOnly)),
                };

                Find.WindowStack.Add(new FloatMenu(opts));
                ev.Use();
            }
        }

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
                // --- macOS ---
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
            var vis = VisibleCols();
            int nVis = vis.Count;
            int nAll = AllCols.Length;

            var frac = QualityInsightsMod.Settings.colFractions;
            if (frac == null || frac.Count != nAll)
            {
                frac = QualityInsightsMod.Settings.colFractions = DefaultColFractions();
                InvalidateColumnLayout();
            }

            // If settings changed elsewhere, rebuild our cache
            bool layoutBumped = (_seenLayoutGen != s_layoutGen);
            if (layoutBumped)
            {
                _colPx = Array.Empty<float>();
                _lastTableW = -1f;
                _seenLayoutGen = s_layoutGen;
                _appliedFracs = Array.Empty<float>();
            }

            // Build the visible-only normalized fractions
            var visFracs = new float[nVis];
            float sumVis = 0f;
            for (int i = 0; i < nVis; i++)
            {
                sumVis += Mathf.Max(0f, frac[IndexOf(vis[i])]);
            }
            if (sumVis <= 0f)
            {
                // fall back to defaults if all-zero
                var def = DefaultColFractions();
                sumVis = 0f;
                for (int i = 0; i < nVis; i++) { var f = def[IndexOf(vis[i])]; visFracs[i] = f; sumVis += f; }
            }
            else
            {
                for (int i = 0; i < nVis; i++) visFracs[i] = frac[IndexOf(vis[i])];
            }
            for (int i = 0; i < nVis; i++) visFracs[i] = visFracs[i] / sumVis;

            // Detect fraction changes even without a layout-gen bump
            bool fracsChanged = _appliedFracs.Length != nVis;
            if (!fracsChanged)
            {
                for (int i = 0; i < nVis; i++)
                    if (Mathf.Abs(visFracs[i] - _appliedFracs[i]) > 0.0001f) { fracsChanged = true; break; }
            }

            // Recompute px cache if size changed, cache missing, or fracs changed
            if (fracsChanged || _colPx.Length != Mathf.Max(0, nVis - 1) || Mathf.Abs(_lastTableW - tableW) > 0.5f || _colPx.All(w => w <= 0f))
            {
                _colPx = new float[Mathf.Max(0, nVis - 1)];
                for (int i = 0; i < nVis - 1; i++)
                    _colPx[i] = Mathf.Max(ColMinPx, tableW * visFracs[i]);

                _lastTableW = tableW;
                _appliedFracs = visFracs; // remember the visible set we applied
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

        public override void PostOpen()
        {
            base.PostOpen();
            ApplyPersistedFilters();
        }

        public override void PreClose()
        {
            base.PreClose();
            // Flush to disk once on close (keystroke-by-keystroke writes aren’t necessary)
            var s = QualityInsightsMod.Settings;
            s.savedSearch        = search ?? string.Empty;
            s.savedFilterQuality = filterQuality.HasValue ? (int)filterQuality.Value : -1;
            s.savedFilterSkill   = filterSkill ?? string.Empty;
            QualityInsightsMod.Instance?.WriteSettings();
        }

        private void ApplyPersistedFilters()
        {
            var s = QualityInsightsMod.Settings;

            // search box
            search = s.savedSearch ?? string.Empty;

            // quality (-1 means “All”)
            filterQuality = (s.savedFilterQuality >= 0)
                ? (QualityCategory?) (QualityCategory) s.savedFilterQuality
                : null;

            // skill (empty means “All”)
            filterSkill = string.IsNullOrEmpty(s.savedFilterSkill) ? null : s.savedFilterSkill;
        }

        // Format RL seconds as compact "1h 2m", "3m 5s", etc.
        private static string FormatPlayTime(double seconds)
        {
            if (seconds < 0) return "–";
            int s = Mathf.FloorToInt((float)seconds);
            int h = s / 3600; s %= 3600;
            int m = s / 60; s %= 60;
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

            if (!_filtersInitialized)
            {
                var s = QualityInsightsMod.Settings;
                search = s.savedSearch ?? string.Empty;
                filterQuality = s.savedFilterQuality >= 0
                    ? (QualityCategory?) (QualityCategory) s.savedFilterQuality
                    : null;
                filterSkill = string.IsNullOrEmpty(s.savedFilterSkill) ? null : s.savedFilterSkill;
                _filtersInitialized = true;
            }

            try
            {
                var comp = QualityLogComponent.Ensure(Current.Game);
                var rows = comp.entries.AsEnumerable();
                double nowPlay = comp.PlaySecondsAccum; // snapshot once per repaint

                // ===== Header (filters + toggle/export) =====
                var header = new Rect(0, 0, rect.width, headerH);

                var ev = Event.current; // reuse

                var searchLabel = new Rect(0, header.y, 70f, headerH);
                Widgets.Label(searchLabel, "Search:");

                // Keep a fixed overall width for the search area (textbox + clear button)
                float searchAreaW = rect.width * 0.28f;
                float searchX = searchLabel.xMax + 6f;

                // Ctrl/Cmd+F focuses the search box
                if (ev.type == EventType.KeyDown && (ev.control || ev.command) && ev.keyCode == KeyCode.F)
                {
                    GUI.FocusControl(SearchCtrlName);
                    ev.Use();
                }

                // If there’s text, reserve room for the clear button; otherwise 0
                bool showClear = !string.IsNullOrEmpty(search);
                float clearSz = Mathf.Min(22f, headerH - 6f);
                float clearPad = 4f;
                float reserved = showClear ? (clearSz + clearPad) : 0f;

                // Text field uses the left portion of the search area
                var searchBox = new Rect(searchX, header.y, searchAreaW - reserved, headerH);

                // Name the control so we can focus it
                GUI.SetNextControlName(SearchCtrlName);
                var prevSearch = search;
                search = Widgets.TextField(searchBox, search);
                if (!string.Equals(prevSearch, search))
                {
                    QualityInsightsMod.Settings.savedSearch = search ?? string.Empty; // in-memory persist
                }

                // Draw the clear button in the reserved strip (not overlapping the text field)
                if (showClear)
                {
                    // position the “×” inside the right-hand reserved strip
                    var clearRect = new Rect(searchX + searchAreaW - clearSz,
                                            header.y + (headerH - clearSz) * 0.5f,
                                            clearSz, clearSz);

                    // Use the image button if available, else a text fallback
                    bool clicked =
                        (TexButton.CloseXSmall != null && Widgets.ButtonImage(clearRect, TexButton.CloseXSmall))
                        || (TexButton.CloseXSmall == null && Widgets.ButtonText(clearRect, "×"));

                    if (clicked)
                    {
                        search = string.Empty;
                        QualityInsightsMod.Settings.savedSearch = string.Empty;
                        GUI.FocusControl(SearchCtrlName);
                    }

                    TooltipHandler.TipRegion(clearRect, "Clear");
                }

                // Continue layout to the right of the whole search area
                float x = searchX + searchAreaW + Pad;

                // Quality dropdown (goes before the Skill dropdown)
                if (Widgets.ButtonText(new Rect(x, header.y, QualBtnW, headerH), filterQuality?.ToString() ?? "All qualities"))
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (QualityCategory q in Enum.GetValues(typeof(QualityCategory)))
                    {
                        var localQ = q;
                        opts.Add(new FloatMenuOption(localQ.ToString(), () =>
                        {
                            filterQuality = localQ;
                            QualityInsightsMod.Settings.savedFilterQuality = (int)localQ;
                        }));
                    }
                    opts.Add(new FloatMenuOption("All", () =>
                    {
                        filterQuality = null;
                        QualityInsightsMod.Settings.savedFilterQuality = -1;
                    }));

                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                x += QualBtnW + Pad;

                // Build the list of skills present for the dropdown
                var skillsPresent = comp.entries
                    .Select(e => e.skillDef ?? "Unknown")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (Widgets.ButtonText(new Rect(x, header.y, SkillBtnW, headerH), filterSkill ?? "All skills"))
                {
                    var opts = new List<FloatMenuOption>();

                    foreach (var sd in skillsPresent)
                    {
                        var local = sd;
                        opts.Add(new FloatMenuOption(local, () =>
                        {
                            filterSkill = local;
                            QualityInsightsMod.Settings.savedFilterSkill = local ?? string.Empty;
                        }));
                    }
                    opts.Add(new FloatMenuOption("All", () =>
                    {
                        filterSkill = null;
                        QualityInsightsMod.Settings.savedFilterSkill = string.Empty;
                    }));

                    Find.WindowStack.Add(new FloatMenu(opts));
                }

                // advance past the Skill dropdown
                x += SkillBtnW + Pad;

                // Reset filters button (resets search + quality + skill)
                const float ResetBtnW = 110f;
                if (Widgets.ButtonText(new Rect(x, header.y, ResetBtnW, headerH), "Reset filters"))
                {
                    // Clear UI state
                    search = string.Empty;
                    filterQuality = null;
                    filterSkill = null;

                    // Persist in-memory and to disk
                    QualityInsightsMod.Settings.savedSearch = string.Empty;
                    QualityInsightsMod.Settings.savedFilterQuality = -1;
                    QualityInsightsMod.Settings.savedFilterSkill = string.Empty;
                    QualityInsightsMod.SaveSettingsNow();

                    GUI.FocusControl(SearchCtrlName);
                }
                x += ResetBtnW + Pad;

                // Right-aligned header buttons
                float rx = rect.width - 110f;
                string toggleLabel = FloatingOpen ? "Dock" : "Pop out";
                if (Widgets.ButtonText(new Rect(rx, header.y, 100f, headerH), toggleLabel))
                {
                    if (FloatingOpen) DockAndClosePopOut();
                    else PopOutAndCloseDock();
                }
                rx -= 110f;

                // NEW: Columns menu
                if (Widgets.ButtonText(new Rect(rx, header.y, 100f, headerH), "Columns"))
                {
                    var hidden = QualityInsightsMod.Settings.hiddenCols ??= new List<string>();
                    var opts = new List<FloatMenuOption>();

                    foreach (var t in AllCols)
                    {
                        bool isHidden = hidden.Contains(t.key);
                        string label = (isHidden ? "   " : "✓ ") + t.header;  // faux checkbox
                        opts.Add(new FloatMenuOption(label, () =>
                        {
                            // Never allow hiding all columns
                            if (!isHidden)
                            {
                                // attempting to hide this; ensure at least one other remains visible
                                int visibleCount = AllCols.Count(c => !hidden.Contains(c.key));
                                if (visibleCount <= 1) { SoundDefOf.ClickReject.PlayOneShotOnCamera(); return; }
                                hidden.Add(t.key);
                                // If we just hid the sorted column, switch to first visible
                                if (s_sortCol == t.id) s_sortCol = VisibleCols().First();
                            }
                            else
                            {
                                hidden.Remove(t.key);
                            }

                            QualityInsightsMod.Instance.WriteSettings();
                            InvalidateColumnLayout();    // force all open instances to rebuild layout
                        }));
                    }

                    Find.WindowStack.Add(new FloatMenu(opts));
                }

                // ===== Apply filters =====
                string term = (search ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(term))
                {
                    rows = rows.Where(e =>
                    {
                        // raw basics
                        bool hitPawn   = ContainsCI(e.pawnName, term);
                        bool hitSkill  = ContainsCI(e.skillDef, term);
                        bool hitItemID = ContainsCI(e.thingDef, term);
                        bool hitItemFriendly = ContainsCI(FriendlyThingLabel(e.thingDef), term);

                        // stuff (raw + friendly)
                        bool hitStuffRaw      = ContainsCI(e.stuffDef, term);
                        bool hitStuffFriendly = ContainsCI(FriendlyDefLabel(e.stuffDef), term);

                        // materials (joined) raw + friendly
                        var mats = e.mats ?? new List<string>();
                        bool hitMatsRaw      = AnyCI(mats, term);
                        bool hitMatsFriendly = AnyCI(mats.Select(FriendlyDefLabel), term);

                        // tags
                        string tags = (e.inspiredCreativity ? "Inspired " : string.Empty)
                                    + (e.productionSpecialist ? "ProdSpec" : string.Empty);
                        bool hitTags = ContainsCI(tags, term);

                        return hitPawn || hitSkill || hitItemID || hitItemFriendly
                            || hitStuffRaw || hitStuffFriendly
                            || hitMatsRaw || hitMatsFriendly
                            || hitTags;
                    });
                }

                if (filterQuality.HasValue)
                    rows = rows.Where(e => e.quality == filterQuality);

                if (!string.IsNullOrEmpty(filterSkill))
                    rows = rows.Where(e => string.Equals(e.skillDef, filterSkill, StringComparison.OrdinalIgnoreCase));

                var list = rows.ToList();
                int totalCount = comp.entries.Count;
                int shownCount = list.Count;

                // ===== Body =====
                var body = new Rect(0, headerH + 8f, rect.width, rect.height - headerH - 8f - FooterH);
                if (s_viewMode == ViewMode.Table) DrawTable(body, list, rowH, headerH, nowPlay);
                else DrawLog(body, list, rowH, nowPlay);

                // ===== Footer =====
                var footer = new Rect(0, rect.height - FooterH, rect.width, FooterH);

                float bw = 100f, gap = 8f;
                float resetW = 120f;

                // Right cluster: [Reset widths] [Export CSV] [Open folder] [Settings]
                float clusterW = resetW + gap + bw + gap + 90f + gap + bw;
                float xRight = footer.xMax - clusterW - 6f;

                // Reset widths (only meaningful in Table view, but harmless otherwise)
                if (Widgets.ButtonText(new Rect(xRight, footer.y + 3f, resetW, 28f), "Reset widths"))
                {
                    ResetColumnsToDefaults();
                    Messages.Message("Column widths reset.", MessageTypeDefOf.TaskCompletion, false);
                }
                xRight += resetW + gap;

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

                // NEW: “X of Y shown” counter (bottom-left)
                float afterButtonsX = 4f + 80f + 8f + 70f + 8f;
                Widgets.Label(new Rect(afterButtonsX, footer.y + 8f, 260f, 20f), $"{shownCount:n0} of {totalCount:n0} shown");
            }
            finally
            {
                Text.Font = oldFont;
            }
        }

        private static void DrawMatsListOneLine(Rect r, List<string> matDefNames, bool friendly = true)
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
            string label = string.Join(", ", matDefNames.Select(n => friendly ? FriendlyDefLabel(n) : n));
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
                MaybeContextCopy(r, e);
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
            IsHoveringSplitter = false;
            EnsureColPx(outRect.width);
            int n = ColHeaders.Length;
            var vis = VisibleCols();
            int nVis = vis.Count;

            float hx = 0f;
            for (int vi = 0; vi < nVis; vi++)
            {
                float w = (vi < nVis - 1)
                    ? _colPx[vi]
                    : Mathf.Max(ColMinPx, outRect.width - hx);

                var hr = new Rect(outRect.x + hx, outRect.y, w, headerH);

                bool hasSplit = vi < nVis - 1;
                var hrLabel   = hasSplit ? new Rect(hr.x, hr.y, hr.width - SplitterW, hr.height) : hr;

                if (Mouse.IsOver(hrLabel)) Widgets.DrawHighlight(hrLabel);
                var col = vis[vi];
                string label = HeaderFor(col) + (s_sortCol == col ? (s_sortAsc ? " ▲" : " ▼") : "");

                var oldA = Text.Anchor;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(hrLabel.ContractedBy(6f, 0f), label);
                Text.Anchor = oldA;

                if (Widgets.ButtonInvisible(hrLabel, true))
                {
                    if (s_sortCol == col) s_sortAsc = !s_sortAsc; else { s_sortCol = col; s_sortAsc = (col == Col.Time); }
                    Event.current.Use();
                }

                if (hasSplit)
                {
                    const float SplitterGrab = 14f;
                    var split = new Rect(hr.xMax - SplitterW, hr.y, SplitterW, hr.height);
                    var hit   = new Rect(split.x - (SplitterGrab - SplitterW) * 0.5f, split.y, SplitterGrab, split.height);

                    if (Mouse.IsOver(hit)) { IsHoveringSplitter = true; MouseoverSounds.DoRegion(hit); }

                    if (Event.current.type == EventType.MouseDown && hit.Contains(Event.current.mousePosition))
                    {
                        _dragCol    = vi; // visible index
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

                    // Compute normalized visible fractions from current pixel widths
                    float used = 0f; for (int k = 0; k < _colPx.Length; k++) used += _colPx[k];
                    float lastW = Mathf.Max(ColMinPx, outRect.width - used);

                    var visFracs = new List<float>(nVis);
                    for (int k = 0; k < _colPx.Length; k++) visFracs.Add(Mathf.Max(ColMinPx, _colPx[k]) / outRect.width);
                    visFracs.Add(lastW / outRect.width);

                    float sum = visFracs.Sum();
                    if (sum > 0f) for (int k = 0; k < visFracs.Count; k++) visFracs[k] /= sum;

                    // Map back into full fraction list (preserve hidden ratios)
                    var full = new List<float>(QualityInsightsMod.Settings.colFractions);
                    if (full.Count != AllCols.Length) full = DefaultColFractions();

                    // How much of the 1.0 total is currently allocated to hidden cols?
                    float hiddenSum = 0f;
                    foreach (var t in AllCols)
                        if (QualityInsightsMod.Settings.hiddenCols.Contains(t.key))
                            hiddenSum += Mathf.Max(0f, full[IndexOf(t.id)]);

                    float targetVisTotal = Mathf.Clamp01(1f - hiddenSum);
                    if (targetVisTotal <= 0f) targetVisTotal = 1f;

                    for (int k = 0; k < nVis; k++)
                        full[IndexOf(vis[k])] = visFracs[k] * targetVisTotal;

                    // Normalize to 1.0
                    float tot = full.Sum();
                    if (tot > 0f) for (int k = 0; k < full.Count; k++) full[k] /= tot;

                    QualityInsightsMod.Settings.colFractions = full;
                    QualityInsightsMod.Instance.WriteSettings();

                    // remember what we applied (visible-only)
                    _appliedFracs = visFracs.ToArray();

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
                MaybeContextCopy(row, e); // NEW
                if ((idx & 1) == 1) DrawZebra(row);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);

                for (int vi = 0; vi < nVis; vi++)
                {
                    float w = (vi < nVis - 1) ? _colPx[vi] : lastPxRows;
                    var cell = new Rect(x, y, w, rowH);

                    switch (vis[vi])
                    {
                        case Col.Time:    DrawCellOneLine(cell, e.TimeAgoString); break;
                        case Col.RL:      DrawCellOneLine(cell, e.HasPlayStamp ? FormatPlayTime(nowPlay - e.playSecondsAtLog) : "–"); break;
                        case Col.Pawn:    DrawPawnWithIconOneLine(cell, e.pawnName); break;
                        case Col.Skill:   DrawCellOneLine(cell, e.skillDef ?? "Unknown"); break;
                        case Col.Lvl:     DrawCellRightOneLine(cell, e.skillLevelAtFinish.ToString()); break;
                        case Col.Quality: DrawQualityOneLine(cell, e.quality); break;
                        case Col.Item:
                            DrawThingWithIconOneLine(cell, e.thingDef, friendly: true);
                            break;
                        case Col.ItemRaw:
                            DrawThingWithIconOneLine(cell, e.thingDef, friendly: false);
                            break;
                        case Col.Stuff:
                            if (e.HasMats) DrawMatsListOneLine(cell, e.mats, friendly: true);
                            else           DrawDefWithIconOneLine(cell, e.stuffDef, friendly: true);
                            break;
                        case Col.StuffRaw:
                            if (e.HasMats) DrawMatsListOneLine(cell, e.mats, friendly: false);
                            else           DrawDefWithIconOneLine(cell, e.stuffDef, friendly: false);
                            break;
                        case Col.Tags:
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
            IOrderedEnumerable<QualityLogEntry> ordered = s_sortCol switch
            {
                Col.Time     => list.OrderBy(e => e.gameTicks),
                Col.RL       => list.OrderBy(e => e.HasPlayStamp ? 0 : 1).ThenBy(e => e.playSecondsAtLog),
                Col.Pawn     => list.OrderBy(e => e.pawnName),
                Col.Skill    => list.OrderBy(e => e.skillDef),
                Col.Lvl      => list.OrderBy(e => e.skillLevelAtFinish),
                Col.Quality  => list.OrderBy(e => e.quality),

                // Friendly label sorts
                Col.Item     => list.OrderBy(e => FriendlyThingLabel(e.thingDef)),
                Col.Stuff    => list.OrderBy(e =>
                    e.HasMats
                        ? string.Join("+", e.mats?.Select(FriendlyDefLabel) ?? Enumerable.Empty<string>())
                        : FriendlyDefLabel(e.stuffDef)),

                // Raw defs
                Col.ItemRaw  => list.OrderBy(e => e.thingDef),
                Col.StuffRaw => list.OrderBy(e =>
                    e.HasMats
                        ? string.Join("+", e.mats ?? new List<string>())
                        : (e.stuffDef ?? string.Empty)),

                Col.Tags     => list.OrderBy(e => (e.inspiredCreativity ? 1 : 0) + (e.productionSpecialist ? 2 : 0)),
                _            => list.OrderBy(e => e.gameTicks),
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

        private static void DrawThingWithIconOneLine(Rect r, string thingDefName, bool friendly = true)
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

            string label = friendly ? (def?.LabelCap ?? thingDefName ?? string.Empty) : (thingDefName ?? string.Empty);
            DrawCellOneLine(right, label);

            if (!string.IsNullOrEmpty(thingDefName))
                TooltipHandler.TipRegion(r, friendly ? $"{label} ({thingDefName})" : thingDefName);
        }

        private static void DrawDefWithIconOneLine(Rect r, string defNameOrNull, bool friendly = true)
        {
            string raw = defNameOrNull ?? string.Empty;
            if (string.IsNullOrEmpty(raw))
            {
                DrawCellOneLine(r, string.Empty);
                return;
            }

            r = r.ContractedBy(2f, 2f);
            var left  = r.LeftPartPixels(r.height);
            var right = new Rect(r.x + left.width + 4f, r.y, r.width - left.width - 4f, r.height);

            var def = DefDatabase<ThingDef>.GetNamedSilentFail(raw);
            if (def?.uiIcon != null)
            {
                var old = GUI.color;
                GUI.color = def.uiIconColor;
                GUI.DrawTexture(left, def.uiIcon, ScaleMode.ScaleToFit);
                GUI.color = old;
            }

            string label = friendly ? (def?.LabelCap ?? raw) : raw;
            DrawCellOneLine(right, label);
            TooltipHandler.TipRegion(r, friendly ? $"{label} ({raw})" : raw);
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
