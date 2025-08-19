using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace QualityInsights
{
    public class QualityInsightsMod : Mod
    {
        public static QualityInsightsMod Instance { get; private set; } = null!;
        public static QualityInsightsSettings Settings { get; private set; } = null!;

        public QualityInsightsMod(ModContentPack content) : base(content)
        {
            Instance  = this;
            Settings  = GetSettings<QualityInsightsSettings>();
        }

        public override string SettingsCategory() => "QualityInsights";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard { ColumnWidth = inRect.width };
            ls.Begin(inRect);

            ls.Label("QI_Settings_Title".Translate());
            ls.GapLine();

            // Existing toggles
            ls.CheckboxLabeled("QI_Settings_EnableLogging".Translate(),     ref Settings.enableLogging);
            ls.CheckboxLabeled("QI_Settings_EnableLiveChances".Translate(), ref Settings.enableLiveChances);
            ls.CheckboxLabeled("QI_Settings_EnableCheat".Translate(),       ref Settings.enableCheat);

            Settings.minCheatChance = Mathf.Clamp(ls.SliderLabeled(
                "QI_Settings_MinCheatChance".Translate() + $": {Settings.minCheatChance:P2}",
                Settings.minCheatChance, 0.0f, 0.2f), 0f, 1f);

            Settings.estimationSamples = Mathf.RoundToInt(ls.SliderLabeled(
                "QI_Settings_Samples".Translate() + $": {Settings.estimationSamples}",
                Settings.estimationSamples, 500, 20000));

            ls.GapLine();
            ls.Label("Quality Log UI");

            // Font selection
            var font = Settings.logFont;
            if (ls.ButtonText($"Font: {font}"))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption("Tiny",   () => Settings.logFont = QualityInsightsSettings.UIFont.Tiny),
                    new FloatMenuOption("Small",  () => Settings.logFont = QualityInsightsSettings.UIFont.Small),
                    new FloatMenuOption("Medium", () => Settings.logFont = QualityInsightsSettings.UIFont.Medium),
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            // Row height scale
            Settings.tableRowScale = Mathf.Round(ls.SliderLabeled(
                $"Row height scale: {Settings.tableRowScale:0.00}",
                Settings.tableRowScale, 0.80f, 1.50f) * 100f) / 100f;

            // Reset columns button (since widths are now resizable via dragging)
            if (ls.ButtonText("Reset column widths"))
            {
                Settings.colFractions = new List<float> { 0.12f, 0.16f, 0.13f, 0.06f, 0.12f, 0.22f, 0.12f, 0.07f };
                Instance.WriteSettings();
                Messages.Message("QualityInsights: Column widths reset.", MessageTypeDefOf.TaskCompletion, false);
            }

            ls.Gap();
            if (ls.ButtonText("QI_OpenLog".Translate()))
                Find.WindowStack.Add(new UI.QualityLogWindow());

            ls.End();
        }
    }
}
