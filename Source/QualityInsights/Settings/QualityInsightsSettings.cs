using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace QualityInsights
{
    public class QualityInsightsSettings : ModSettings
    {
        // existing
        public bool  enableLogging      = true;
        public bool  enableLiveChances  = true;
        public bool  enableCheat        = false;
        public float minCheatChance     = 0.02f; // 2%
        public int   estimationSamples  = 5000;  // Monte Carlo per pawn/skill

        // NEW: UI controls for the Quality Log
        public enum UIFont { Tiny, Small, Medium }
        public UIFont logFont = UIFont.Small;

        // multiplicative row/header height scale (applies to both views)
        public float tableRowScale = 1.00f;   // suggest good range 0.9..1.4

        // persistent table column fractions (must sum ~1) — store as List<float> for scribing
        // public List<float> colFractions = new List<float> { 0.12f, 0.16f, 0.13f, 0.06f, 0.12f, 0.22f, 0.12f, 0.07f };
        public List<float> colFractions = new() { 0.12f, 0.16f, 0.13f, 0.06f, 0.12f, 0.22f, 0.12f, 0.07f };

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enableLogging, nameof(enableLogging), true);
            Scribe_Values.Look(ref enableLiveChances, nameof(enableLiveChances), true);
            Scribe_Values.Look(ref enableCheat, nameof(enableCheat), false);
            Scribe_Values.Look(ref minCheatChance, nameof(minCheatChance), 0.02f);
            Scribe_Values.Look(ref estimationSamples, nameof(estimationSamples), 5000);

            // NEW
            Scribe_Values.Look(ref logFont, nameof(logFont), UIFont.Small);
            Scribe_Values.Look(ref tableRowScale, nameof(tableRowScale), 1.00f);

            // List<T> is required here (not arrays)
            Scribe_Collections.Look(ref colFractions, nameof(colFractions), LookMode.Value);

            // safety: if list wasn't saved or size changed, restore defaults
            // if (colFractions == null || colFractions.Count != 8)
            //     colFractions = new List<float> { 0.12f, 0.16f, 0.13f, 0.06f, 0.12f, 0.22f, 0.12f, 0.07f };
            if (colFractions == null || colFractions.Count != 8)
                colFractions = new() { 0.12f, 0.16f, 0.13f, 0.06f, 0.12f, 0.22f, 0.12f, 0.07f };

            base.ExposeData();
        }

        // convenience for UI code
        public GameFont GetLogGameFont() =>
            logFont switch
            {
                UIFont.Tiny   => GameFont.Tiny,
                UIFont.Medium => GameFont.Medium,
                _             => GameFont.Small
            };
    }
}
