using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using QualityInsights.Utils;

namespace QualityInsights.Prob
{
    public static class QualityEstimator
    {
        // WITH boosts (used by cheat logic etc.)
        public static Dictionary<QualityCategory, float> EstimateChances(
            Pawn pawn, SkillDef skill, ThingDef? productDef, int samples)
        {
            var counts = new Dictionary<QualityCategory, int>();
            foreach (QualityCategory qc in Enum.GetValues(typeof(QualityCategory))) counts[qc] = 0;
            if (pawn == null || skill == null) return ZeroResult();

            samples = Math.Max(100, samples);

            for (int i = 0; i < samples; i++)
            {
                var rolled = QualityUtility.GenerateQualityCreatedByPawn(pawn, skill);
                var adj = AdjustForInspirationAndRoles(pawn, skill, rolled);

                // Keep the demotion only for boosted/cheat view if your mod forbids Legendary
                if (adj == QualityCategory.Legendary && !QualityRules.LegendaryAllowedFor(pawn))
                    adj = QualityCategory.Masterwork;

                counts[adj] = counts[adj] + 1;
            }

            var result = new Dictionary<QualityCategory, float>();
            foreach (var kv in counts) result[kv.Key] = kv.Value / (float)samples;
            return result;
        }

        public static Dictionary<QualityCategory, float> EstimateChances(
            Pawn pawn, SkillDef skill, int samples)
            => EstimateChances(pawn, skill, (ThingDef?)null, samples);


        // BASELINE (no boosts; used by the UI before applying tier shifts)
        public static Dictionary<QualityCategory, float> EstimateBaseline(
            Pawn pawn, SkillDef skill, ThingDef? productDef, int samples)
        {
            var counts = new Dictionary<QualityCategory, int>();
            foreach (QualityCategory qc in Enum.GetValues(typeof(QualityCategory))) counts[qc] = 0;
            if (pawn == null || skill == null) return ZeroResult();

            samples = Math.Max(100, samples);

            for (int i = 0; i < samples; i++)
            {
                // vanilla roll only; do NOT adjust for inspiration/roles
                // and do NOT demote Legendary here — the UI will cap after shifting.
                var rolled = QualityUtility.GenerateQualityCreatedByPawn(pawn, skill);
                counts[rolled] = counts[rolled] + 1;
            }

            var result = new Dictionary<QualityCategory, float>();
            foreach (var kv in counts) result[kv.Key] = kv.Value / (float)samples;
            return result;
        }

        public static Dictionary<QualityCategory, float> EstimateBaseline(
            Pawn pawn, SkillDef skill, int samples)
            => EstimateBaseline(pawn, skill, (ThingDef?)null, samples);


        // ---- Helpers --------------------------------------------------------

        private static QualityCategory AdjustForInspirationAndRoles(Pawn pawn, SkillDef skill, QualityCategory baseQ)
        {
            int tiers = 0;
            if (pawn.InspirationDef == InspirationDefOf.Inspired_Creativity) tiers += 2;
            if (QualityInsights.Utils.QualityRules.IsProductionSpecialistFor(pawn, skill)) tiers += 1;
            if (tiers == 0) return baseQ;

            var elevated = baseQ;
            for (int i = 0; i < tiers && elevated < QualityCategory.Legendary; i++) elevated += 1;
            return elevated;
        }

        private static Dictionary<QualityCategory, float> ZeroResult()
        {
            var res = new Dictionary<QualityCategory, float>();
            foreach (QualityCategory qc in Enum.GetValues(typeof(QualityCategory))) res[qc] = 0f;
            return res;
        }
    }
}
