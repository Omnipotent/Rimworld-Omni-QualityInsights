using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using QualityInsights.Logging;
using QualityInsights.Prob;
using QualityInsights.Utils;

namespace QualityInsights.Patching
{
    [StaticConstructorOnStartup]
    public static class QualityPatches
    {
        [ThreadStatic] private static Pawn? _currentPawn;
        [ThreadStatic] private static SkillDef? _currentSkill;
        [ThreadStatic] private static QualityCategory? _forcedQuality;

        static QualityPatches()
        {
            var harmony = new Harmony("omni.qualityinsights");

            // Patch the quality generator (used by crafting & construction).
            var target = AccessTools.Method(typeof(QualityUtility), "GenerateQualityCreatedByPawn",
                new Type[] { typeof(Pawn), typeof(SkillDef) });
            if (target != null)
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(BeforeRoll)),
                    postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterRoll)));
                QualityRules.Init(target);
            }

            // // Patch crafting outcome to log the concrete product.
            // harmony.Patch(
            //     AccessTools.Method(typeof(GenRecipe), nameof(GenRecipe.PostProcessProduct)),
            //     postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterPostProcessProduct)));

            // // Patch construction completion to log built structures.
            // harmony.Patch(
            //     AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction)),
            //     postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterCompleteConstruction)));

            // // in QualityPatches static ctor:
            // harmony.Patch(
            //     AccessTools.Method(typeof(CompQuality), nameof(CompQuality.SetQuality),
            //         new Type[] { typeof(QualityCategory), typeof(ArtGenerationContext) }),
            //     postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterSetQuality)));

            // Find CompQuality.SetQuality(QualityCategory, ArtGenerationContext?)
            // Works across game versions with/without nullable metadata
            var setQ = typeof(CompQuality)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "SetQuality") return false;
                    var ps = m.GetParameters();
                    if (ps.Length != 2) return false;
                    if (ps[0].ParameterType != typeof(QualityCategory)) return false;
                    var p2 = ps[1].ParameterType;
                    return p2 == typeof(ArtGenerationContext) || p2 == typeof(Nullable<ArtGenerationContext>);
                });

            if (setQ != null)
            {
                harmony.Patch(
                    setQ,
                    postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterSetQuality)));
            }
            else
            {
                Log.Error("[QualityInsights] Could not locate CompQuality.SetQuality (signature changed?).");
            }



            // // Add gizmo to work tables for live chances.
            // harmony.Patch(
            //     AccessTools.Method(typeof(Building_WorkTable), nameof(Building_WorkTable.GetGizmos)),
            //     postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterGetGizmos)));

            // Patch the declared virtual method on Building (WorkTable inherits it)
            harmony.Patch(
                AccessTools.Method(typeof(Building), nameof(Building.GetGizmos)),
                postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterGetGizmos)));


            Log.Message("[QualityInsights] Patches applied.");
        }

        public static void BeforeRoll(Pawn pawn, SkillDef relevantSkill)
        {
            _currentPawn = pawn;
            _currentSkill = relevantSkill;
            _forcedQuality = TryComputeCheatOverride(pawn, relevantSkill);
        }

        public static void AfterRoll(ref QualityCategory __result)
        {
            var settings = QualityInsightsMod.Settings;
            if (settings.enableCheat && _forcedQuality.HasValue)
                __result = _forcedQuality.Value;

            // Do NOT clear here; AfterSetQuality needs these.
            // They’ll naturally be overwritten by the next roll.
            // _currentPawn = null; _currentSkill = null; _forcedQuality = null;
        }

        private static QualityCategory? TryComputeCheatOverride(Pawn pawn, SkillDef skill)
        {
            var s = QualityInsightsMod.Settings; if (!s.enableCheat) return null;
            var chances = QualityEstimator.EstimateChances(pawn, skill, s.estimationSamples);
            foreach (var qc in Enum.GetValues(typeof(QualityCategory)).Cast<QualityCategory>().OrderByDescending(q => q))
            {
                if (chances.TryGetValue(qc, out var p) && p + 1e-6f >= s.minCheatChance)
                {
                    if (qc == QualityCategory.Legendary && !QualityRules.LegendaryAllowedFor(pawn)) continue;
                    return qc;
                }
            }
            return null;
        }

        // --- Logging hooks ---
        // public static void AfterPostProcessProduct(Thing product, RecipeDef recipeDef, Pawn worker)
        // {
        //     if (!QualityInsightsMod.Settings.enableLogging) return;
        //     var comp = product.TryGetComp<CompQuality>();
        //     if (comp == null) return;

        //     LogEntryFrom(worker, recipeDef.workSkill ?? SkillDefOf.Crafting, product.def, product.Stuff, comp.Quality);
        // }

        // public static void AfterCompleteConstruction(Frame __instance, Pawn worker)
        // {
        //     if (!QualityInsightsMod.Settings.enableLogging) return;
        //     var building = __instance.CompleteBuilding;
        //     if (building == null) return;
        //     var comp = building.TryGetComp<CompQuality>();
        //     if (comp == null) return;

        //     LogEntryFrom(worker, SkillDefOf.Construction, building.def, building.Stuff, comp.Quality);
        // }

        private static void LogEntryFrom(Pawn worker, SkillDef skill, ThingDef thingDef, ThingDef stuff, QualityCategory qc)
        {
            try
            {
                var comp = Current.Game.GetComponent<QualityLogComponent>();
                var entry = new QualityLogEntry
                {
                    thingDef = thingDef.defName,
                    stuffDef = stuff?.defName,
                    quality = qc,
                    pawnName = worker?.Name?.ToStringShort ?? worker?.LabelShort ?? "Unknown",
                    skillDef = skill?.defName ?? "Unknown",
                    skillLevelAtFinish = worker?.skills?.GetSkill(skill)?.Level ?? -1,
                    inspiredCreativity = worker?.InspirationDef == InspirationDefOf.Inspired_Creativity,
                    productionSpecialist = QualityRules.IsProductionSpecialist(worker),
                    gameTicks = Find.TickManager.TicksGame
                };
                comp.Add(entry);
            }
            catch { /* never break gameplay */ }
        }

        // --- UI gizmo injection ---
        public static void AfterGetGizmos(Building_WorkTable __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!QualityInsightsMod.Settings.enableLiveChances) return;

            var cmd = new Command_Action
            {
                defaultLabel = "QI_LiveOdds".Translate(),
                defaultDesc = "Show estimated chances of Excellent/Masterwork/Legendary for a chosen pawn & recipe.",
                icon = TexCommand.DesirePower, // reasonable stock icon
                action = () => Find.WindowStack.Add(new UI.ChancesWindow(__instance))
            };
            __result = __result.Concat(new[] { cmd });
        }

        public static void AfterSetQuality(CompQuality __instance, QualityCategory q)
        {
            if (!QualityInsightsMod.Settings.enableLogging) return;
            var thing = __instance?.parent; if (thing == null) return;

            // Prefer the worker from the current roll (thread-static), then try art author
            Pawn worker = _currentPawn;
            SkillDef skill = _currentSkill ?? (thing.def.IsBuildingArtificial ? SkillDefOf.Construction : SkillDefOf.Crafting);

            if (worker == null)
            {
                try
                {
                    var compArt = thing.TryGetComp<CompArt>();
                    if (compArt != null)
                    {
                        // Try several known shapes across versions/mods
                        Pawn? pick(object o)
                        {
                            switch (o)
                            {
                                case Pawn p: return p;
                                case Thing t when t is Pawn p2: return p2;
                                default: return null;
                            }
                        }

                        var t = compArt.GetType();

                        // Property "Author"
                        var propAuthor = t.GetProperty("Author", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        worker = propAuthor != null ? pick(propAuthor.GetValue(compArt)) : worker;

                        // Field "author"
                        if (worker == null)
                        {
                            var f = t.GetField("author", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (f != null) worker = pick(f.GetValue(compArt));
                        }

                        // Field "authorPawn"
                        if (worker == null)
                        {
                            var f = t.GetField("authorPawn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (f != null) worker = pick(f.GetValue(compArt));
                        }

                        // Property "CreatorPawn"
                        if (worker == null)
                        {
                            var p = t.GetProperty("CreatorPawn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (p != null) worker = pick(p.GetValue(compArt));
                        }
                    }
                }
                catch { /* ignore */ }
            }


            try
            {
                var comp = QualityInsights.Logging.QualityLogComponent.Ensure(Current.Game);
                comp.Add(new QualityInsights.Logging.QualityLogEntry
                {
                    thingDef = thing.def?.defName ?? "Unknown",
                    stuffDef = thing.Stuff?.defName,
                    quality = q,
                    pawnName = worker?.Name?.ToStringShort ?? worker?.LabelShort ?? "Unknown",
                    skillDef = skill?.defName ?? "Unknown",
                    skillLevelAtFinish = worker?.skills?.GetSkill(skill)?.Level ?? -1,
                    inspiredCreativity = worker?.InspirationDef == InspirationDefOf.Inspired_Creativity,
                    productionSpecialist = QualityInsights.Utils.QualityRules.IsProductionSpecialist(worker),
                    gameTicks = Find.TickManager.TicksGame
                });
                _currentPawn = null;
                _currentSkill = null;
                _forcedQuality = null;
            }
            catch { /* never break gameplay */ }
        }
    }
}
