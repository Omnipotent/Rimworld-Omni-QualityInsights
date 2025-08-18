using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using QualityInsights.Utils;

namespace QualityInsights.Prob
{
    public static class QualityEstimator
    {
        // ORIGINAL 4-arg API (keep this exact signature)
        public static Dictionary<QualityCategory, float> EstimateChances(
            Pawn pawn, SkillDef skill, ThingDef? productDef, int samples)
        {
            var counts = new Dictionary<QualityCategory, int>();
            foreach (QualityCategory qc in Enum.GetValues(typeof(QualityCategory)))
                counts[qc] = 0;

            // Guard rails
            if (pawn == null || skill == null)
                return ZeroResult();
            samples = Math.Max(100, samples);

            // Simulate vanilla quality generation + tier offsets
            for (int i = 0; i < samples; i++)
            {
                var rolled = QualityUtility.GenerateQualityCreatedByPawn(pawn, skill);
                var adj = AdjustForInspirationAndRoles(pawn, rolled);

                // Optional cap if your rules disallow Legendary
                if (adj == QualityCategory.Legendary && !QualityRules.LegendaryAllowedFor(pawn))
                    adj = QualityCategory.Masterwork;

                counts[adj] = counts[adj] + 1;
            }

            // Normalize
            var result = new Dictionary<QualityCategory, float>();
            foreach (var kv in counts)
                result[kv.Key] = kv.Value / (float)samples;
            return result;
        }

        // NEW overload: when you don't have a product ThingDef
        public static Dictionary<QualityCategory, float> EstimateChances(
            Pawn pawn, SkillDef skill, int samples)
        {
            return EstimateChances(pawn, skill, (ThingDef?)null, samples);
        }

        // ---- Helpers --------------------------------------------------------

        private static QualityCategory AdjustForInspirationAndRoles(Pawn pawn, QualityCategory baseQ)
        {
            int tiers = 0;

            // Vanilla: Inspired Creativity adds +2 quality tiers
            if (pawn.InspirationDef == InspirationDefOf.Inspired_Creativity)
                tiers += 2;

            // Ideology: Production Specialist adds +1 quality tier
            if (QualityRules.IsProductionSpecialist(pawn))
                tiers += 1;

            if (tiers == 0) return baseQ;

            // Apply tier bumps with clamping to Legendary
            var elevated = baseQ;
            for (int i = 0; i < tiers; i++)
            {
                if (elevated < QualityCategory.Legendary)
                    elevated += 1;
            }
            return elevated;
        }

        private static Dictionary<QualityCategory, float> ZeroResult()
        {
            var res = new Dictionary<QualityCategory, float>();
            foreach (QualityCategory qc in Enum.GetValues(typeof(QualityCategory)))
                res[qc] = 0f;
            return res;
        }
    }
}
