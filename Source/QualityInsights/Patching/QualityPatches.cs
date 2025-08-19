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
        [ThreadStatic] private static List<string>? _currentMats;


        private static readonly ConditionalWeakTable<Thing, Pawn> _productToWorker = new();
        private static readonly Dictionary<int, (int tick, QualityCategory q)> _logGuard
            = new Dictionary<int, (int, QualityCategory)>();


        private const bool DebugLogs = true; // set to false when you're done debugging

        private static string P(Pawn? p) => p != null ? p.LabelShortCap : "null";
        private static string S(SkillDef? s) => s != null ? (s.skillLabel ?? s.label ?? s.defName) : "null";
        // Tracks construction in-flight between CompleteConstruction prefix/postfix
        private struct BuildCtx { public Map map; public IntVec3 cell; public ThingDef builtDef; public Pawn worker; }
        private static readonly Dictionary<int, BuildCtx> _buildCtx = new();


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

            // 2.6) Bind construction products to the worker (so we can log/enforce later)
            var complete = typeof(Frame)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "CompleteConstruction") return false;
                    var ps = m.GetParameters();
                    // allow any signature whose first param is Pawn
                    return ps.Length >= 1 && ps[0].ParameterType == typeof(Pawn);
                });

            if (complete != null)
            {
                harmony.Patch(
                    complete,
                    prefix:  new HarmonyMethod(typeof(QualityPatches), nameof(CompleteConstruction_Prefix)),
                    postfix: new HarmonyMethod(typeof(QualityPatches), nameof(CompleteConstruction_Postfix)));
            }
            else
            {
                Log.Warning("[QualityInsights] Could not find Frame.CompleteConstruction(Pawn). Construction logs may miss the worker.");
            }

            // 3) Add the Live Odds gizmo to work tables
            var getGizmos = AccessTools.Method(typeof(Building), nameof(Building.GetGizmos));
            if (getGizmos != null)
                harmony.Patch(getGizmos, postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterGetGizmos)));

            Log.Message("[QualityInsights] Patches applied.");
        }

        public static void MakeRecipeProducts_Prefix(
            [HarmonyArgument(0)] RecipeDef recipeDef,
            [HarmonyArgument(1)] Pawn worker,
            [HarmonyArgument(2)] List<Thing> ingredients)   // <— grab actual used ingredients
        {
            _currentWorkerFromRecipe = worker;
            _currentPawn  = worker;
            _currentSkill = ResolveSkillForRecipeOrProduct(recipeDef);

            // Collect distinct defNames of used ingredients (steel, component, neutroamine, etc.)
            try
            {
                _currentMats = ingredients?
                    .Select(t => t?.def?.defName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .ToList();
            }
            catch { _currentMats = null; }
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

        public static void CompleteConstruction_Prefix(Frame __instance, Pawn worker)
        {
            try
            {
                // >>> seed the roll context so AfterSetQuality sees Construction <<<
                _currentPawn  = worker;                  // who is building right now
                _currentSkill = SkillDefOf.Construction; // construction always uses Construction

                // What will this frame become?
                var built = __instance?.def?.entityDefToBuild as ThingDef;
                if (built == null || worker == null) return;

                _buildCtx[__instance.thingIDNumber] = new BuildCtx
                {
                    map      = __instance.Map,
                    cell     = __instance.Position,
                    builtDef = built,
                    worker   = worker
                };

                if (DebugLogs)
                    Log.Message($"[QI] Construction prefix: will build {built.defName} at {__instance.Position} by {P(worker)}");
            }
            catch { /* keep gameplay safe */ }
        }

        public static void CompleteConstruction_Postfix(Frame __instance)
        {
            try
            {
                if (!_buildCtx.TryGetValue(__instance.thingIDNumber, out var ctx)) return;
                _buildCtx.Remove(__instance.thingIDNumber);

                // The frame is gone now; find the new building at the same cell
                var list = ctx.cell.GetThingList(ctx.map);
                Thing builtThing = null;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t?.def == null) continue;
                    if (t.def == ctx.builtDef || t.def.defName == ctx.builtDef.defName)
                    {
                        builtThing = t;
                        break;
                    }
                }
                if (builtThing == null) return;

                // Cache for AfterSetQuality
                var target = builtThing;
                if (target is MinifiedThing m && m.InnerThing != null)
                    target = m.InnerThing;

                try { _productToWorker.Remove(target); } catch { /* ignore */ }
                _productToWorker.Add(target, ctx.worker);

                if (DebugLogs)
                    Log.Message($"[QI] Construction bound: {P(ctx.worker)} -> {target.LabelCap}");
            }
            catch { /* keep gameplay safe */ }
        }


        // Cheat short-circuit before vanilla roll
        public static bool GenerateQuality_Prefix(Pawn pawn, SkillDef relevantSkill, ref QualityCategory __result)
        {
            _currentSkill = null; // ensure we never carry a stale skill into this roll
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

            return true; // let vanilla compute
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

        // Replace your current ResolveSkillForThing with this one
        private static SkillDef ResolveSkillForThing(Thing thing, SkillDef? fromRoll)
        {
            // Prefer what was captured in the roll (most accurate)
            if (fromRoll != null) return fromRoll;

            // Buildings are always Construction, even if they also have CompArt
            if (thing?.def?.IsBuildingArtificial == true) return SkillDefOf.Construction;

            // Non-buildings that have CompArt are Artistic (e.g., sculptures)
            if (thing?.TryGetComp<CompArt>() != null) return SkillDefOf.Artistic;

            // Everything else (weapons, apparel, guns, etc.) is Crafting
            return SkillDefOf.Crafting;
        }

        private static SkillDef ResolveSkillForRecipeOrProduct(RecipeDef recipe)
        {
            // 1) Primary: modders should set this; works for vanilla & most mods
            if (recipe?.workSkill != null) return recipe.workSkill;

            // 2) If any product is an art piece, treat as Artistic
            try
            {
                if (recipe?.products != null && recipe.products.Any(p => p?.thingDef?.HasComp(typeof(CompArt)) == true))
                    return SkillDefOf.Artistic;
            }
            catch { /* ignore */ }

            // 3) Soft fallback heuristics (covers odd mods with no workSkill set)
            var label = recipe?.label ?? recipe?.defName ?? string.Empty;
            var l = label.ToLowerInvariant();
            if (l.Contains("sculpt") || l.Contains("art")) return SkillDefOf.Artistic;
            if (l.Contains("build")  || l.Contains("construct")) return SkillDefOf.Construction;

            // 4) Last resort: Crafting (covers weapons, apparel, guns, etc.)
            return SkillDefOf.Crafting;
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

            // --- duplicate suppression ---
            int now = Find.TickManager.TicksGame;
            int id = thing.thingIDNumber;

            if (_logGuard.TryGetValue(id, out var g) && g.q == q && now - g.tick < 120)
            {
                if (DebugLogs)
                    Log.Message($"[QI] Suppressed duplicate log for {thing.LabelCap} (q={q}) within {now - g.tick} ticks.");
                goto ClearAndReturn;  // jump to the cleanup at the end of AfterSetQuality
            }
            _logGuard[id] = (now, q);

            // Optional light pruning to avoid unbounded growth.
            if ((now % 5000) == 0 && _logGuard.Count > 512)
            {
                // drop records older than ~10000 ticks (~2.8 in-game hours)
                var cutoff = now - 10000;
                foreach (var key in _logGuard.Where(kv => kv.Value.tick < cutoff).Select(kv => kv.Key).ToList())
                    _logGuard.Remove(key);
            }

            if (DebugLogs)
                Log.Message($"[QI] SetQuality: thing={thing.LabelCap} result={q} worker={P(worker)} skill={S(skill)}");

            if (QualityInsightsMod.Settings.enableLogging)
            {
                try
                {
                    var comp = QualityInsights.Logging.QualityLogComponent.Ensure(Current.Game);
                    var mats = new List<string>();
                    try
                    {
                        var compIng = thing?.TryGetComp<CompIngredients>();
                        if (compIng?.ingredients != null)
                        {
                            foreach (var d in compIng.ingredients)
                                if (d != null) mats.Add(d.defName);
                        }
                    }
                    catch { /* ignore; keep gameplay safe */ }
                    comp.Add(new QualityLogEntry
                    {
                        thingDef = thing.def?.defName ?? "Unknown",
                        stuffDef = thing.Stuff?.defName,
                        quality = q,
                        pawnName = worker?.Name?.ToStringShort ?? worker?.LabelShort ?? "Unknown",
                        skillDef = skill?.defName ?? "Unknown",
                        skillLevelAtFinish = worker?.skills?.GetSkill(skill)?.Level ?? -1,
                        inspiredCreativity = worker?.InspirationDef == InspirationDefOf.Inspired_Creativity,
                        productionSpecialist = worker != null && QualityInsights.Utils.QualityRules.IsProductionSpecialist(worker),
                        gameTicks = Find.TickManager.TicksGame,
                        mats = _currentMats != null ? new List<string>(_currentMats) : null,   // NEW
                    });
                }
                catch { /* never break gameplay */ }
            }

        // Clear after consumption
        ClearAndReturn:
            _currentPawn = null;
            _currentSkill = null;
            _forcedQuality = null;
            _currentWorkerFromRecipe = null;
            _currentMats = null;
        }
    }
}
