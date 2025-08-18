using System;
using System.Reflection;
using RimWorld;
using Verse;

namespace QualityInsights.Utils
{
    public static class QualityRules
    {
        private static MethodInfo? _rollMethod;

        public static void Init(MethodInfo rollMethod) => _rollMethod = rollMethod;

        public static QualityCategory RollQualityForSimulation(Pawn pawn, SkillDef skill, ThingDef? thing)
        {
            if (_rollMethod == null) return QualityCategory.Normal;
            try
            {
                // Vanilla method is static QualityUtility.GenerateQualityCreatedByPawn(Pawn, SkillDef)
                return (QualityCategory)_rollMethod.Invoke(null, new object[] { pawn, skill });
            }
            catch
            {
                return QualityCategory.Normal;
            }
        }

        public static bool LegendaryAllowedFor(Pawn pawn)
        {
            try
            {
                if (pawn == null) return false;
                if (pawn.InspirationDef == InspirationDefOf.Inspired_Creativity) return true; // +2 tiers
                return IsProductionSpecialist(pawn); // +1 tier (Ideology)
            }
            catch { return false; }
        }

        public static bool IsProductionSpecialist(Pawn pawn)
        {
            try
            {
                if (pawn?.ideo == null) return false;
                var ideoTracker = pawn.ideo;

                // Try pawn.ideo.GetRole(pawn)
                var getRole = ideoTracker.GetType().GetMethod("GetRole", new[] { typeof(Pawn) });
                object roleObj = null;
                if (getRole != null)
                {
                    roleObj = getRole.Invoke(ideoTracker, new object[] { pawn });
                }
                else
                {
                    // Fallbacks: pawn.ideo.Role or pawn.Ideo?.GetRole(pawn)
                    var roleProp = ideoTracker.GetType().GetProperty("Role");
                    roleObj = roleProp?.GetValue(ideoTracker);
                    if (roleObj == null)
                    {
                        var ideoProp = ideoTracker.GetType().GetProperty("Ideo");
                        var ideo = ideoProp?.GetValue(ideoTracker);
                        var ideoGetRole = ideo?.GetType().GetMethod("GetRole", new[] { typeof(Pawn) });
                        roleObj = ideoGetRole?.Invoke(ideo, new object[] { pawn });
                    }
                }

                if (roleObj == null) return false;
                var defProp = roleObj.GetType().GetProperty("def");
                var def = defProp?.GetValue(roleObj);
                var defNameProp = def?.GetType().GetProperty("defName");
                var defName = defNameProp?.GetValue(def) as string;
                return !string.IsNullOrEmpty(defName) &&
                    defName.IndexOf("Production", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }
}
