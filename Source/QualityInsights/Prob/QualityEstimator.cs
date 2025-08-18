using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using QualityInsights.Utils;

namespace QualityInsights.Prob
{
    public static class QualityEstimator
    {
        public static Dictionary<QualityCategory, float> EstimateChances(
            Pawn pawn, SkillDef skill, ThingDef? thingDef, int samples)
        {
            var counts = new Dictionary<QualityCategory, int>();
            foreach (QualityCategory qc in Enum.GetValues(typeof(QualityCategory))) counts[qc] = 0;

            samples = Math.Max(100, samples);
            for (int i = 0; i < samples; i++)
            {
                var qc = QualityRules.RollQualityForSimulation(pawn, skill, thingDef);
                counts[qc]++;
            }

            var result = new Dictionary<QualityCategory, float>();
            foreach (var kv in counts) result[kv.Key] = kv.Value / (float)samples;
            return result;
        }
    }
}
