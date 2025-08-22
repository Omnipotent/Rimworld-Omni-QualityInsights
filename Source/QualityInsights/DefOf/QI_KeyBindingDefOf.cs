using RimWorld;
using Verse;

namespace QualityInsights
{
    [DefOf]
    public static class QI_KeyBindingDefOf
    {
        public static KeyBindingDef QualityInsights_ShowWorktableOdds;
        public static KeyBindingDef QualityInsights_ShowConstructionOdds;
        public static KeyBindingDef QualityInsights_ToggleLog; // (already defined; handy if you ever want to use it)

        static QI_KeyBindingDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(QI_KeyBindingDefOf));
        }
    }
}
