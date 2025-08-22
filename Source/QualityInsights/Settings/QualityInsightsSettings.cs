using System.Collections.Generic;
using UnityEngine;
using Verse;
using QualityInsights.UI;

namespace QualityInsights
{
    public class QualityInsightsSettings : ModSettings
    {
        public bool  enableLogging      = true;
        public bool  enableLiveChances  = true;
        public bool  enableCheat        = false;
        public float minCheatChance     = 0.02f; // 2%
        public int   estimationSamples  = 5000;  // Monte Carlo per pawn/skill
        // Notifications
        public bool  silenceMasterworkNotifs  = false;
        public bool  silenceLegendaryNotifs   = false;

        // UI controls for the Quality Log
        public enum UIFont { Tiny, Small, Medium }
        public UIFont logFont = UIFont.Small;

        // --- Persisted Quality Log filters ---
        public string savedSearch = string.Empty;   // search box
        // -1 means "All qualities"
        public int    savedFilterQuality = -1;
        // empty means "All skills"; otherwise SkillDef.defName
        public string savedFilterSkill   = string.Empty;

        // multiplicative row/header height scale (applies to both views)
        public float tableRowScale = 1.00f;   // suggest good range 0.9..1.4

        // Retention
        public bool pruneByAge   = true;
        public int  keepDays     = 60;     // in-game days
        public bool pruneByCount = true;
        public int  maxEntries   = 20000;  // hard ceiling

        // Export rotation
        public int  maxExportFiles    = 20; // keep last N
        public int  maxExportFolderMB = 50; // or cap by size

        // persistent table column fractions (must sum ~1) — store as List<float> for scribing
        private static List<float> NewDefaultColFractions() =>
            // new() { 0.12f, 0.16f, 0.13f, 0.06f, 0.12f, 0.22f, 0.12f, 0.07f }; // old 7 column sizing
            // new() { 0.08f, 0.08f, 0.14f, 0.12f, 0.06f, 0.08f, 0.18f, 0.18f, 0.08f };
            // Time, RL, Pawn, Skill, Lvl, Quality, Item, ItemRaw, Stuff, StuffRaw, Tags
            new() { 0.07f, 0.07f, 0.12f, 0.10f, 0.06f, 0.08f, 0.16f, 0.05f, 0.16f, 0.05f, 0.08f };
        // 9 columns (Time, RL, Pawn, Skill, Lvl, Quality, Item, Stuff, Tags)
        public List<float> colFractions = MainTabWindow_QualityLog.DefaultColFractions();
        public List<string> hiddenCols = new();   // e.g., "Time","RL","Pawn","Skill","Lvl","Quality","Item","Stuff","Tags"

        public bool enableDebugLogs = false;
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enableDebugLogs, "QI_enableDebugLogs", false);
            Scribe_Values.Look(ref silenceMasterworkNotifs, nameof(silenceMasterworkNotifs), false);
            Scribe_Values.Look(ref silenceLegendaryNotifs,  nameof(silenceLegendaryNotifs),  false);

            Scribe_Values.Look(ref enableLogging, nameof(enableLogging), true);
            Scribe_Values.Look(ref enableLiveChances, nameof(enableLiveChances), true);
            Scribe_Values.Look(ref enableCheat, nameof(enableCheat), false);
            Scribe_Values.Look(ref minCheatChance, nameof(minCheatChance), 0.02f);
            Scribe_Values.Look(ref estimationSamples, nameof(estimationSamples), 5000);
            Scribe_Values.Look(ref pruneByAge, nameof(pruneByAge), true);
            Scribe_Values.Look(ref keepDays, nameof(keepDays), 60);
            Scribe_Values.Look(ref pruneByCount, nameof(pruneByCount), true);
            Scribe_Values.Look(ref maxEntries, nameof(maxEntries), 20000);
            Scribe_Values.Look(ref maxExportFiles, nameof(maxExportFiles), 20);
            Scribe_Values.Look(ref maxExportFolderMB, nameof(maxExportFolderMB), 50);

            Scribe_Values.Look(ref logFont, nameof(logFont), UIFont.Small);
            Scribe_Values.Look(ref tableRowScale, nameof(tableRowScale), 1.00f);

            Scribe_Values.Look(ref savedSearch,        "QI_savedSearch",        string.Empty);
            Scribe_Values.Look(ref savedFilterQuality, "QI_savedFilterQuality", -1);
            Scribe_Values.Look(ref savedFilterSkill,   "QI_savedFilterSkill",   string.Empty);

            Scribe_Collections.Look(ref colFractions, nameof(colFractions), LookMode.Value);
            // If loading an older config (wrong count), migrate to current defaults (11 cols).
            var defFracs = MainTabWindow_QualityLog.DefaultColFractions();
            if (colFractions == null || colFractions.Count != defFracs.Count)
                colFractions = defFracs;

            Scribe_Collections.Look(ref hiddenCols, "QI_hiddenCols", LookMode.Value);
            hiddenCols ??= new List<string>();

            // ✅ Only default-hide the new raw columns on LOAD, not on SAVE.
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (!hiddenCols.Contains("ItemRaw"))  hiddenCols.Add("ItemRaw");
                if (!hiddenCols.Contains("StuffRaw")) hiddenCols.Add("StuffRaw");
            }
        }

        // convenience for UI code (unchanged)
        public GameFont GetLogGameFont() => logFont switch
        {
            UIFont.Tiny   => GameFont.Tiny,
            UIFont.Medium => GameFont.Medium,
            _             => GameFont.Small
        };

        // --- NEW: one-call reset for every mod setting we own ---
        public void ResetAllToDefaults()
        {
            enableLogging      = true;
            enableLiveChances  = true;
            enableCheat        = false;
            minCheatChance     = 0.02f;
            estimationSamples  = 5000;
            silenceMasterworkNotifs = false;
            silenceLegendaryNotifs  = false;

            savedSearch        = string.Empty;
            savedFilterQuality = -1;
            savedFilterSkill   = string.Empty;

            logFont            = UIFont.Small;
            tableRowScale      = 1.00f;

            pruneByAge         = true;
            keepDays           = 60;
            pruneByCount       = true;
            maxEntries         = 20000;

            maxExportFiles     = 20;
            maxExportFolderMB  = 50;

            colFractions       = NewDefaultColFractions();
            hiddenCols.Clear();

            hiddenCols.Add("ItemRaw");
            hiddenCols.Add("StuffRaw");

            enableDebugLogs    = false;
        }
    }
}
