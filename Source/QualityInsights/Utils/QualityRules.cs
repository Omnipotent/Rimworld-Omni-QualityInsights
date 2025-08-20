using System;
using System.Reflection;
using HarmonyLib;
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

        // Explicit, skill-aware check used by UI and patches.
        // Keep this PURE — do not read any thread-static roll context.
        public static bool IsProductionSpecialistFor(Pawn pawn, SkillDef skill)
        {
            if (pawn == null) return false;
            // If you ever want to gate by skills, uncomment this:
            // if (skill != SkillDefOf.Crafting && skill != SkillDefOf.Artistic && skill != SkillDefOf.Construction) return false;
            return IsProductionSpecialist(pawn);
        }

        public static bool IsProductionSpecialist(Pawn pawn)
        {
            try
            {
                if (pawn == null) return false;

                // If Ideology isn't active, the role cannot exist.
                try { if (!ModsConfig.IdeologyActive) return false; } catch { /* pre-Ideo */ }

                // --- Get the pawn's current role object (Precept_Role), using tolerant reflection ---
                object roleObj = null;

                var ideoTracker = pawn.GetType()
                    .GetField("ideo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(pawn);
                if (ideoTracker == null) return false;

                // Common property names on Pawn_IdeoTracker: Role / PrimaryRole / role
                var roleProp =
                    ideoTracker.GetType().GetProperty("Role",         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                    ideoTracker.GetType().GetProperty("PrimaryRole",  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                    ideoTracker.GetType().GetProperty("role",         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (roleProp != null)
                    roleObj = roleProp.GetValue(ideoTracker);

                // Fallback via Ideo.GetRole(pawn)
                if (roleObj == null)
                {
                    var ideoProp =
                        ideoTracker.GetType().GetProperty("Ideo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                        ideoTracker.GetType().GetProperty("ideo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var ideo = ideoProp?.GetValue(ideoTracker);

                    var getRole = ideo?.GetType().GetMethod("GetRole",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null, types: new[] { typeof(Pawn) }, modifiers: null);

                    if (getRole != null)
                        roleObj = getRole.Invoke(ideo, new object[] { pawn });
                }

                if (roleObj == null) return false;

                // --- Read role.def (RoleDef) ---
                var defObj =
                    roleObj.GetType().GetField("def", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleObj) ??
                    roleObj.GetType().GetProperty("def", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleObj);
                if (defObj == null) return false;

                // Fast path if it's a Verse.Def
                if (defObj is Def d)
                {
                    var name = d.defName ?? string.Empty;
                    if (name.IndexOf("Production", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }

                // Fallback: reflect defName
                var defName =
                    defObj.GetType().GetField("defName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(defObj) as string
                    ?? defObj.GetType().GetProperty("defName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(defObj) as string
                    ?? string.Empty;

                if (defName.IndexOf("Production", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // Optional fallback: roleTags that contain something with "Production"
                var tagsObj =
                    defObj.GetType().GetField("roleTags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(defObj)
                    ?? defObj.GetType().GetProperty("roleTags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(defObj);

                if (tagsObj is System.Collections.IEnumerable tagsEnum)
                {
                    foreach (var t in tagsEnum)
                    {
                        var s = t?.ToString();
                        if (!string.IsNullOrEmpty(s) &&
                            s.IndexOf("Production", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }

                return false;
            }
            catch
            {
                // Absolutely never break gameplay or the UI
                return false;
            }
        }
    }
}
