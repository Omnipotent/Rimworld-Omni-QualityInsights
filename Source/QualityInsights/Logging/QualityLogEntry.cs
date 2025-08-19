// Source\QualityInsights\Logging\QualityLogEntry.cs
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace QualityInsights.Logging
{
    public class QualityLogEntry : IExposable
    {
        public string thingDef = string.Empty;
        public string? stuffDef;
        public QualityCategory quality;
        public string pawnName = string.Empty;
        public string skillDef = string.Empty;
        public int    skillLevelAtFinish;
        public bool   inspiredCreativity;
        public bool   productionSpecialist;
        public int    gameTicks;

        // Actual ingredients used (distinct by def)
        public List<string>? mats;

        public bool HasMats => mats != null && mats.Count > 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref thingDef, nameof(thingDef));
            Scribe_Values.Look(ref stuffDef, nameof(stuffDef));
            Scribe_Values.Look(ref quality,  nameof(quality));
            Scribe_Values.Look(ref pawnName, nameof(pawnName));
            Scribe_Values.Look(ref skillDef, nameof(skillDef));
            Scribe_Values.Look(ref skillLevelAtFinish, nameof(skillLevelAtFinish));
            Scribe_Values.Look(ref inspiredCreativity, nameof(inspiredCreativity));
            Scribe_Values.Look(ref productionSpecialist, nameof(productionSpecialist));
            Scribe_Values.Look(ref gameTicks, nameof(gameTicks));

            // Persist materials list (ok if absent in older saves)
            Scribe_Collections.Look(ref mats, nameof(mats), LookMode.Value);
            mats ??= new List<string>(); // keep non-null after load
        }

        public string TimeAgoString =>
            GenDate.ToStringTicksToPeriod(Find.TickManager.TicksGame - gameTicks);
    }
}
