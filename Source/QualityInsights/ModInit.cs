using UnityEngine;
using Verse;

namespace QualityInsights
{
    public class QualityInsightsMod : Mod
    {
        public static QualityInsightsSettings Settings { get; private set; } = null!;

        public QualityInsightsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<QualityInsightsSettings>();
        }

        public override string SettingsCategory() => "QualityInsights";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard { ColumnWidth = inRect.width };
            ls.Begin(inRect);
            ls.Label("QI_Settings_Title".Translate());
            ls.GapLine();

            ls.CheckboxLabeled("QI_Settings_EnableLogging".Translate(), ref Settings.enableLogging);
            ls.CheckboxLabeled("QI_Settings_EnableLiveChances".Translate(), ref Settings.enableLiveChances);
            ls.CheckboxLabeled("QI_Settings_EnableCheat".Translate(), ref Settings.enableCheat);

            Settings.minCheatChance = Mathf.Clamp(ls.SliderLabeled(
                label: "QI_Settings_MinCheatChance".Translate() + $": {Settings.minCheatChance:P2}",
                val: Settings.minCheatChance, min: 0.0f, max: 0.2f), 0f, 1f);

            Settings.estimationSamples = Mathf.RoundToInt(ls.SliderLabeled(
                label: "QI_Settings_Samples".Translate() + $": {Settings.estimationSamples}",
                val: Settings.estimationSamples, min: 500, max: 20000));

            if (ls.ButtonText("QI_OpenLog".Translate()))
                Find.WindowStack.Add(new UI.QualityLogWindow());

            ls.End();
        }
    }
}
