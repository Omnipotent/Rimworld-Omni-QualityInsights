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
using System.Runtime.CompilerServices; // ConditionalWeakTable

namespace QualityInsights.Patching
{
    [StaticConstructorOnStartup]
    public static class QualityPatches
    {
        [ThreadStatic] private static Pawn? _currentPawn;
        [ThreadStatic] private static Pawn? _currentWorkerFromRecipe;
        [ThreadStatic] private static SkillDef? _currentSkill;
        [ThreadStatic] private static QualityCategory? _forcedQuality;

        private static readonly ConditionalWeakTable<Thing, Pawn> _productToWorker = new();


        private const bool DebugLogs = true; // set to false when you're done debugging

        private static string P(Pawn? p) => p != null ? p.LabelShortCap : "null";
        private static string S(SkillDef? s) => s != null ? (s.skillLabel ?? s.label ?? s.defName) : "null";

        static QualityPatches()
        {
            var harmony = new Harmony("omni.qualityinsights");

            // 1) Short-circuit quality rolls when cheat applies
            var genQual = AccessTools.Method(
                typeof(QualityUtility),
                "GenerateQualityCreatedByPawn",
                new Type[] { typeof(Pawn), typeof(SkillDef) });
            if (genQual != null)
            {
                harmony.Patch(
                    genQual,
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(GenerateQuality_Prefix)));
                QualityRules.Init(genQual);
            }

            var make = AccessTools.Method(typeof(GenRecipe), "MakeRecipeProducts");
            if (make != null)
            {
                harmony.Patch(
                    make,
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(MakeRecipeProducts_Prefix))
                );
            }


            // 2) Log final quality (and safety-bump if cheat says higher)
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
                harmony.Patch(setQ, postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterSetQuality)));
            else
                Log.Error("[QualityInsights] Could not locate CompQuality.SetQuality (signature changed?).");

            // 2.5) (optional) extra hook: log the crafted product & worker after vanilla post-processing
            var postProcess = AccessTools.Method(typeof(GenRecipe), "PostProcessProduct");
            if (postProcess != null)
            {
                harmony.Patch(
                    postProcess,
                    postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterPostProcessProduct)));
            }

            // 3) Add the Live Odds gizmo to work tables
            var getGizmos = AccessTools.Method(typeof(Building), nameof(Building.GetGizmos));
            if (getGizmos != null)
                harmony.Patch(getGizmos, postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterGetGizmos)));

            Log.Message("[QualityInsights] Patches applied.");
        }

        public static void MakeRecipeProducts_Prefix(Pawn worker)
        {
            _currentWorkerFromRecipe = worker;
            _currentPawn = worker;
            if (DebugLogs) Log.Message($"[QI] MakeRecipeProducts_Prefix: worker={P(worker)}");
        }

        // Gizmo injection on work tables
        public static void AfterGetGizmos(Building __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!QualityInsightsMod.Settings.enableLiveChances) return;
            if (__instance is not Building_WorkTable wt) return;

            var cmd = new Command_Action
            {
                defaultLabel = "QI_LiveOdds".Translate(),
                defaultDesc  = "Show estimated chances of Excellent/Masterwork/Legendary for a chosen pawn & recipe.",
                icon         = TexCommand.DesirePower,
                action       = () => Find.WindowStack.Add(new UI.ChancesWindow(wt))
            };
            __result = __result.Concat(new[] { cmd });
        }

        // Fires after a recipe product is finalized. Purely for diagnostics,
        // and useful on some mod stacks where SetQuality happens very late.
        public static void AfterPostProcessProduct(Thing product, RecipeDef recipeDef, Pawn worker)
        {
            try
            {
                if (product != null && worker != null)
                {
                    try { _productToWorker.Remove(product); } catch { /* ignore */ }
                    _productToWorker.Add(product, worker);
                    if (DebugLogs)
                        Log.Message($"[QI] Bound worker {worker.LabelShortCap} -> product {product.LabelCap}");
                }

                // (Optional debug) Quality is often not assigned yet here; q=n/a is expected.
                if (DebugLogs)
                {
                    var qComp = product?.TryGetComp<CompQuality>();
                    var qStr  = qComp != null ? qComp.Quality.ToString() : "n/a";
                    Log.Message($"[QI] PostProcessProduct: product={product?.LabelCap} recipe={recipeDef?.defName} worker={worker?.LabelShortCap ?? "null"} q={qStr}");
                }
            }
            catch { /* never break gameplay */ }
        }



        // Cheat short-circuit before vanilla roll
        public static bool GenerateQuality_Prefix(Pawn pawn, SkillDef relevantSkill, ref QualityCategory __result)
        {
            _currentPawn = pawn;
            _currentSkill = relevantSkill;

            if (DebugLogs)
                Log.Message($"[QI] Roll: pawn={P(pawn)} skill={S(relevantSkill)}");

            _forcedQuality = TryComputeCheatOverride(pawn, relevantSkill);

            if (DebugLogs)
                Log.Message($"[QI] Cheat? {(QualityInsightsMod.Settings.enableCheat ? "ON" : "OFF")}  forced={_forcedQuality?.ToString() ?? "null"}");

            if (QualityInsightsMod.Settings.enableCheat && _forcedQuality.HasValue)
            {
                __result = _forcedQuality.Value;
                if (DebugLogs) Log.Message($"[QI] Short-circuit: forcing quality to {__result}");
                return false;
            }

            return true;                              // let vanilla compute
        }


        private static QualityCategory? TryComputeCheatOverride(Pawn pawn, SkillDef skill)
        {
            var s = QualityInsightsMod.Settings;
            if (!s.enableCheat) return null;

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

        private static SkillDef ResolveSkillForThing(Thing thing, SkillDef? fromRoll)
        {
            // Prefer what we captured in the roll (most accurate)
            if (fromRoll != null) return fromRoll;

            // If the product has CompArt, it's an art piece => Artistic
            if (thing?.TryGetComp<CompArt>() != null) return SkillDefOf.Artistic;

            // True buildings use Construction; non-buildings (weapons, apparel, guns, etc.) use Crafting
            return thing?.def?.IsBuildingArtificial == true ? SkillDefOf.Construction : SkillDefOf.Crafting;
        }


        // Finalization: ensure cheat tier, then log with best-effort pawn resolution
        public static void AfterSetQuality(CompQuality __instance, QualityCategory q)
        {
            var thing = __instance?.parent;
            if (thing == null)
            {
                if (DebugLogs) Log.Message("[QI] AfterSetQuality: parent thing was null");
                _currentPawn = null; _currentSkill = null; _forcedQuality = null;
                return;
            }

            // Resolve skill first (prefer from roll)
            SkillDef skill = ResolveSkillForThing(thing, _currentSkill);

            // --- resolve the worker (roll → MakeRecipeProducts → cache)
            Pawn worker = _currentPawn ?? _currentWorkerFromRecipe;
            if (worker == null && _productToWorker.TryGetValue(thing, out var cached))
            {
                worker = cached;
                if (DebugLogs) Log.Message($"[QI] Resolved worker via cache: {worker.LabelShortCap} for {thing.LabelCap}");
                try { _productToWorker.Remove(thing); } catch { /* ignore */ }
            }

            // --- CHEAT ENFORCEMENT ---
            // If the prefix didn't run, _forcedQuality may be null. Recompute here.
            QualityCategory? forced = _forcedQuality;
            if (QualityInsightsMod.Settings.enableCheat && forced == null && worker != null)
            {
                forced = TryComputeCheatOverride(worker, skill);
                if (DebugLogs) Log.Message($"[QI] AfterSetQuality recompute forced={forced?.ToString() ?? "null"}");
            }

            // If a forced tier exists and it's higher than what vanilla set, bump it now.
            if (QualityInsightsMod.Settings.enableCheat && forced.HasValue && q < forced.Value)
            {
                try
                {
                    __instance.SetQuality(forced.Value, null);
                    if (DebugLogs) Log.Message($"[QI] Bumped {thing.LabelCap} from {q} -> {forced.Value}");
                    q = forced.Value;
                }
                catch { /* keep gameplay safe */ }
            }

            if (DebugLogs)
                Log.Message($"[QI] SetQuality: thing={thing.LabelCap} result={q} worker={P(worker)} skill={S(skill)}");

            if (QualityInsightsMod.Settings.enableLogging)
            {
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
                        // productionSpecialist = QualityInsights.Utils.QualityRules.IsProductionSpecialist(worker),
                        productionSpecialist = worker != null && QualityInsights.Utils.QualityRules.IsProductionSpecialist(worker),
                        gameTicks = Find.TickManager.TicksGame
                    });
                }
                catch { /* never break gameplay */ }
            }

            // Clear after consumption
            _currentPawn = null;
            _currentSkill = null;
            _forcedQuality = null;
            _currentWorkerFromRecipe = null;
        }
    }
}
