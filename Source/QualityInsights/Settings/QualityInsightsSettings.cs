using Verse;

namespace QualityInsights
{
    public class QualityInsightsSettings : ModSettings
    {
        public bool enableLogging = true;
        public bool enableLiveChances = true;
        public bool enableCheat = false;
        public float minCheatChance = 0.02f; // 2%
        public int estimationSamples = 5000; // Monte Carlo per pawn/skill

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enableLogging, nameof(enableLogging), true);
            Scribe_Values.Look(ref enableLiveChances, nameof(enableLiveChances), true);
            Scribe_Values.Look(ref enableCheat, nameof(enableCheat), false);
            Scribe_Values.Look(ref minCheatChance, nameof(minCheatChance), 0.02f);
            Scribe_Values.Look(ref estimationSamples, nameof(estimationSamples), 5000);
            base.ExposeData();
        }
    }
}
