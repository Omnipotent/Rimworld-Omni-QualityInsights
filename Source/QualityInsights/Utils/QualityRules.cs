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
                var ideoTracker = pawn?.ideo;
                if (ideoTracker == null) return false;

                // Try common ways to get the pawn's role without hard binding
                object roleObj = null;

                // 1) Properties on Pawn_IdeoTracker (mods/versions vary: Role / PrimaryRole / role)
                var roleProp =
                    ideoTracker.GetType().GetProperty("Role") ??
                    ideoTracker.GetType().GetProperty("PrimaryRole") ??
                    ideoTracker.GetType().GetProperty("role");
                if (roleProp != null)
                    roleObj = roleProp.GetValue(ideoTracker);

                // 2) Fallback: ideoTracker.Ideo?.GetRole(pawn)
                if (roleObj == null)
                {
                    var ideoProp =
                        ideoTracker.GetType().GetProperty("Ideo") ??
                        ideoTracker.GetType().GetProperty("ideo");
                    var ideo = ideoProp?.GetValue(ideoTracker);
                    var getRole = ideo?.GetType().GetMethod("GetRole", new[] { typeof(Pawn) });
                    if (getRole != null)
                        roleObj = getRole.Invoke(ideo, new object[] { pawn });
                }

                if (roleObj == null) return false;

                // Read role.def.defName (string) and role.def.roleTags (IEnumerable)
                var def = roleObj.GetType().GetProperty("def")?.GetValue(roleObj);
                if (def == null) return false;

                var defName = def.GetType().GetProperty("defName")?.GetValue(def) as string;
                if (string.Equals(defName, "ProductionSpecialist", StringComparison.OrdinalIgnoreCase))
                    return true;

                var tagsObj = def.GetType().GetProperty("roleTags")?.GetValue(def) as System.Collections.IEnumerable;
                if (tagsObj != null)
                {
                    foreach (var t in tagsObj)
                        if (string.Equals(t?.ToString(), "ProductionSpecialist", StringComparison.OrdinalIgnoreCase))
                            return true;
                }

                return false;
            }
            catch { return false; }
        }
    }
}
