// Source\QualityInsights\ModInit.cs
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using QualityInsights.UI;

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

        // (optional cosmetic) This is the label that appears on the Mods screen.
        public override string SettingsCategory() => "Quality Insights";

        // ⇣ NEW: tiny helper so UI code can open the settings dialog cleanly
        public static void OpenSettings()
        {
            if (Instance != null)
                Find.WindowStack.Add(new Dialog_ModSettings(Instance));
        }

        public static void SaveSettingsNow()
        {
            // This writes the settings file immediately.
            Instance?.WriteSettings();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard { ColumnWidth = inRect.width };
            ls.Begin(inRect);

            ls.Label("QI_Settings_Title".Translate());
            ls.GapLine();

            // Existing toggles
            ls.CheckboxLabeled("QI_Settings_EnableLogging".Translate(), ref Settings.enableLogging);
            ls.CheckboxLabeled("QI_Settings_EnableLiveChances".Translate(), ref Settings.enableLiveChances);
            ls.CheckboxLabeled("QI_Settings_EnableCheat".Translate(), ref Settings.enableCheat);

            // Notifications
            ls.GapLine();
            ls.Label("QI_Settings_Notifications".Translate());
            ls.CheckboxLabeled(
                "QI_Settings_SilenceMasterwork".Translate(),
                ref Settings.silenceMasterworkNotifs,
                "QI_Settings_SilenceMasterwork_Tip".Translate()
            );
            ls.CheckboxLabeled(
                "QI_Settings_SilenceLegendary".Translate(),
                ref Settings.silenceLegendaryNotifs,
                "QI_Settings_SilenceLegendary_Tip".Translate()
            );

            // Diagnostics
            ls.GapLine();
            ls.Label("QI_Settings_Diagnostics".Translate());
            ls.CheckboxLabeled(
                "QI_Settings_EnableDebugLogs".Translate(),
                ref Settings.enableDebugLogs,
                "QI_Settings_EnableDebugLogs_Tip".Translate()
            );

            Settings.minCheatChance = Mathf.Clamp(ls.SliderLabeled(
                "QI_Settings_MinCheatChance".Translate() + $": {Settings.minCheatChance:P2}",
                Settings.minCheatChance, 0.0f, 0.2f), 0f, 1f);

            Settings.estimationSamples = Mathf.RoundToInt(ls.SliderLabeled(
                "QI_Settings_Samples".Translate() + $": {Settings.estimationSamples}",
                Settings.estimationSamples, 500, 20000));

            ls.GapLine();
            ls.Label("QI_Settings_QualityLogUI".Translate());

            // Font selection
            var font = Settings.logFont;
            if (ls.ButtonText("QI_Settings_Font".Translate() + $" {font}"))
            {
                var opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption("QI_Font_Tiny".Translate(),   () => Settings.logFont = QualityInsightsSettings.UIFont.Tiny),
                    new FloatMenuOption("QI_Font_Small".Translate(),  () => Settings.logFont = QualityInsightsSettings.UIFont.Small),
                    new FloatMenuOption("QI_Font_Medium".Translate(), () => Settings.logFont = QualityInsightsSettings.UIFont.Medium),
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            // Row height scale
            Settings.tableRowScale = Mathf.Round(
                ls.SliderLabeled(
                    "QI_Settings_RowHeight".Translate() + $" {Settings.tableRowScale:0.00}",
                    Settings.tableRowScale, 0.80f, 1.50f
                ) * 100f
            ) / 100f;

            // Reset columns button
            if (ls.ButtonText("QI_Settings_ResetColumns".Translate()))
            {
                MainTabWindow_QualityLog.ResetColumnsToDefaults();
                Messages.Message("QI_ResetColsMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            // --- NEW: Reset ALL mod settings button ---
            if (ls.ButtonText("QI_Settings_ResetAll".Translate()))
            {
                Find.WindowStack.Add(
                    Dialog_MessageBox.CreateConfirmation(
                        "QI_ResetAllConfirm".Translate(),
                        () =>
                        {
                            Settings.ResetAllToDefaults();

                            // Make sure any open table windows rebuild their layout immediately.
                            MainTabWindow_QualityLog.ResetColumnsToDefaults();

                            // Optional: toast
                            Messages.Message("QI_ResetAllMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
                        }
                    )
                );
            }

            if (ls.ButtonText("QI_Settings_ClearLog".Translate()))
            {
                var comp = Logging.QualityLogComponent.Ensure(Current.Game);
                comp.entries.Clear();
                Messages.Message("QI_ClearLogMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            ls.Gap();
            if (ls.ButtonText("QI_Settings_OpenLog".Translate()))
                Find.WindowStack.Add(new UI.QualityLogWindow());

            ls.End();
        }
    }
}
