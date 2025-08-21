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
using System.Collections;

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
        [ThreadStatic] private static bool? _hadInspirationAtRoll;
        [ThreadStatic] private static bool? _wasProdSpecAtRoll;
        [ThreadStatic] internal static bool _suppressInspirationSideEffects;
        [ThreadStatic] private static bool _clearedInspThisRoll;
        [ThreadStatic] private static object? _savedInspObj;
        [ThreadStatic] private static FieldInfo? _curInspField;
        [ThreadStatic] private static PropertyInfo? _curInspProp;

        // Cache worker per product (safe to hold weakly)
        private static readonly ConditionalWeakTable<Thing, Pawn> _productToWorker = new();

        // Mats are stored by stable id so they survive wrapping/replacement/minification.
        private static readonly Dictionary<int, List<string>> _matsById = new();

        // Prevent duplicate logs for the same thing in a short window
        private static readonly Dictionary<int, (int tick, QualityCategory q)> _logGuard
            = new Dictionary<int, (int, QualityCategory)>();

        private static bool DebugLogs => Prefs.DevMode && QualityInsightsMod.Settings.enableDebugLogs;
        private const bool VerboseSamplingLogs = false;

        private static string P(Pawn? p) => p != null ? p.LabelShortCap : "null";
        private static string S(SkillDef? s) => s != null ? (s.skillLabel ?? s.label ?? s.defName) : "null";

        private struct BuildCtx { public Map map; public IntVec3 cell; public ThingDef builtDef; public Pawn worker; }
        private static readonly Dictionary<int, BuildCtx> _buildCtx = new();
        private static readonly HashSet<int> _bumping = new();
        [ThreadStatic] internal static bool _samplingNoInspiration;

        static QualityPatches()
        {
            var harmony = new Harmony("omni.qualityinsights");

            PatchAllOverloads(harmony, typeof(InspirationHandler), "EndInspiration",
                new HarmonyMethod(typeof(QualityPatches), nameof(InspirationGuardPrefix)));
            PatchAllOverloads(harmony, typeof(InspirationHandler), "TryStartInspiration",
                new HarmonyMethod(typeof(QualityPatches), nameof(InspirationGuardPrefix)));

            // Capture mats/worker at recipe time
            var make = AccessTools.Method(typeof(GenRecipe), "MakeRecipeProducts");
            if (make != null)
            {
                harmony.Patch(
                    make,
                    prefix:  new HarmonyMethod(typeof(QualityPatches), nameof(MakeRecipeProducts_Prefix)),
                    postfix: new HarmonyMethod(typeof(QualityPatches), nameof(MakeRecipeProducts_Postfix))
                );
            }

            // Patch GenerateQualityCreatedByPawn(Pawn, SkillDef, ...)
            bool IsPawnSkillFirst(MethodInfo m)
            {
                var ps = m.GetParameters();
                return ps.Length >= 2 && ps[0].ParameterType == typeof(Pawn) && ps[1].ParameterType == typeof(SkillDef);
            }

            var genQualsPawnSkill = typeof(QualityUtility)
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "GenerateQualityCreatedByPawn"
                         && m.ReturnType == typeof(QualityCategory)
                         && IsPawnSkillFirst(m))
                .ToArray();

            foreach (var mi in genQualsPawnSkill)
            {
                harmony.Patch(
                    mi,
                    prefix:  new HarmonyMethod(typeof(QualityPatches), nameof(GenerateQuality_Prefix)) { priority = Priority.High },
                    postfix: new HarmonyMethod(typeof(QualityPatches), nameof(GenerateQuality_Postfix)));
                if (DebugLogs)
                    Log.Message($"[QualityInsights] Patched {mi.DeclaringType?.Name}.{mi.Name} (Pawn,SkillDef,...)");
            }

            if (genQualsPawnSkill.Length > 0)
                QualityRules.Init(genQualsPawnSkill[0]);
            else
                Log.Warning("[QualityInsights] No (Pawn, SkillDef) GenerateQualityCreatedByPawn overloads found; sampling/cheat may misbehave.");

            // Also patch GenerateQualityCreatedByPawn(int level, bool inspired, ...)
            var genQualsLevelInspired = typeof(QualityUtility)
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "GenerateQualityCreatedByPawn" && m.ReturnType == typeof(QualityCategory))
                .Where(m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length >= 2 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(bool);
                })
                .ToArray();

            foreach (var mi in genQualsLevelInspired)
            {
                harmony.Patch(mi,
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(GenerateQuality_Prefix_LevelInspired))
                    { priority = Priority.High });
                if (DebugLogs)
                    Log.Message($"[QualityInsights] Patched {mi.DeclaringType?.Name}.{mi.Name} (int,bool,...)");
            }

            // Log final quality (and safety-bump if cheat says higher)
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

            // Capture product/pawn/materials right before quality is set
            var postProcess = AccessTools.Method(typeof(GenRecipe), "PostProcessProduct");
            if (postProcess != null)
                harmony.Patch(postProcess,
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(PostProcessProduct_Prefix)));

            // Bind construction products to worker
            var complete = typeof(Frame)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "CompleteConstruction") return false;
                    var ps = m.GetParameters();
                    return ps.Length >= 1 && ps[0].ParameterType == typeof(Pawn);
                });
            if (complete != null)
                harmony.Patch(complete,
                    prefix:  new HarmonyMethod(typeof(QualityPatches), nameof(CompleteConstruction_Prefix)),
                    postfix: new HarmonyMethod(typeof(QualityPatches), nameof(CompleteConstruction_Postfix)));
            else
                Log.Warning("[QualityInsights] Could not find Frame.CompleteConstruction(Pawn).");

            // Live Odds gizmo
            var getGizmos = AccessTools.Method(typeof(Building), nameof(Building.GetGizmos));
            if (getGizmos != null)
                harmony.Patch(getGizmos, postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterGetGizmos)));

            // Patch Blueprint_Build.GetGizmos so we can show odds before the frame exists
            var getGizmosBlueprint = AccessTools.Method(typeof(Blueprint_Build), nameof(Blueprint_Build.GetGizmos));
            if (getGizmosBlueprint != null)
                harmony.Patch(getGizmosBlueprint, postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterGetGizmos_Blueprint)));

            Log.Message("[QualityInsights] Patches applied.");
        }

        private static void PatchAllOverloads(Harmony harmony, Type type, string methodName, HarmonyMethod prefix)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              .Where(m => m.Name == methodName);
            foreach (var mi in methods) harmony.Patch(mi, prefix: prefix);
        }

        private static bool ThingDefHasCompQuality(ThingDef? def)
        {
            if (def == null || def.comps == null) return false;
            for (int i = 0; i < def.comps.Count; i++)
                if (def.comps[i]?.compClass == typeof(CompQuality))
                    return true;
            return false;
        }

        public static bool InspirationGuardPrefix()
        {
            // When we're sampling, skip vanilla inspiration start/end entirely
            return !_suppressInspirationSideEffects;  // false => Harmony prevents original method
        }

        public static void MakeRecipeProducts_Prefix(
            [HarmonyArgument(0)] RecipeDef recipeDef,
            [HarmonyArgument(1)] Pawn worker,
            [HarmonyArgument(2)] List<Thing> ingredients)
        {
            _currentWorkerFromRecipe = worker;
            _currentPawn = worker;
            _currentSkill = ResolveSkillForRecipeOrProduct(recipeDef);
            _hadInspirationAtRoll ??= worker?.InspirationDef == InspirationDefOf.Inspired_Creativity;
            _wasProdSpecAtRoll ??= QualityRules.IsProductionSpecialistFor(worker, _currentSkill ?? ResolveSkillForRecipeOrProduct(recipeDef));

            try
            {
                _currentMats = ingredients?
                    .Select(t => t?.def?.defName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .Distinct()
                    .ToList();
            }
            catch { _currentMats = null; }

            if (DebugLogs)
                Log.Message("[QI] MakeRecipeProducts captured mats=[" + string.Join(",", _currentMats ?? new List<string>()) + "]");
        }

        public static void MakeRecipeProducts_Postfix()
        {
            // IMPORTANT:
            // Do NOT clear _currentMats here. MakeRecipeProducts is an iterator,
            // and PostProcessProduct runs later during enumeration.
            // We clear transient state after logging in AfterSetQuality.
        }

        private static List<string>? GetMaterialsFor(Thing thing)
        {
            // Prefer live CompIngredients
            try
            {
                var compIng = thing?.TryGetComp<CompIngredients>();
                if (compIng?.ingredients != null && compIng.ingredients.Count > 0)
                    return compIng.ingredients.Where(d => d != null).Select(d => d.defName).Distinct().ToList();
            }
            catch { }

            if (thing == null) return null;

            // Cache (by id, for both normal and minified cases)
            if (_matsById.TryGetValue(thing.thingIDNumber, out var mats))
                return new List<string>(mats);

            if (thing is MinifiedThing m && m.InnerThing != null &&
                _matsById.TryGetValue(m.InnerThing.thingIDNumber, out var matsInner))
                return new List<string>(matsInner);

            // For construction we rely on Stuff; don't synthesize mats.
            return null;
        }

        public static void AfterGetGizmos(Building __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!QualityInsightsMod.Settings.enableLiveChances) return;

            // Existing: worktables
            if (__instance is Building_WorkTable wt)
            {
                var cmd = new Command_Action
                {
                    defaultLabel = "QI_LiveOdds".Translate(),
                    defaultDesc  = "Show estimated chances of Excellent/Masterwork/Legendary for a chosen pawn & recipe.",
                    icon = TexCommand.DesirePower,
                    action = () => Find.WindowStack.Add(new UI.ChancesWindow(wt))
                };
                __result = __result.Concat(new[] { cmd });
                return;
            }

            // NEW: construction frames
            if (__instance is Frame frame)
            {
                var builtDef = frame?.def?.entityDefToBuild as ThingDef;
                if (ThingDefHasCompQuality(builtDef))
                {
                    var cmd = new Command_Action
                    {
                        defaultLabel = "QI_LiveOdds".Translate(), // reuse key; or add a dedicated one later
                        defaultDesc  = "Show estimated construction quality odds for this building.",
                        icon = TexCommand.DesirePower,
                        action = () => Find.WindowStack.Add(new UI.ConstructionChancesWindow(frame))
                    };
                    __result = __result.Concat(new[] { cmd });
                }
            }
        }

        public static void AfterGetGizmos_Blueprint(Blueprint_Build __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!QualityInsightsMod.Settings.enableLiveChances) return;

            var builtDef = __instance?.def?.entityDefToBuild as ThingDef;
            if (!ThingDefHasCompQuality(builtDef)) return;

            var cmd = new Command_Action
            {
                defaultLabel = "QI_LiveOdds".Translate(),
                defaultDesc  = "Show estimated construction quality odds for this building.",
                icon = TexCommand.DesirePower,
                action = () => Find.WindowStack.Add(new UI.ConstructionChancesWindow(__instance.Map, builtDef))
            };
            __result = __result.Concat(new[] { cmd });
        }

        // Bind worker + materials just before product is finalized.
        public static void PostProcessProduct_Prefix(Thing product, RecipeDef recipeDef, Pawn worker)
        {
            try
            {
                if (product == null) return;

                void BindWorker(Thing t)
                {
                    if (t == null) return;
                    try { _productToWorker.Remove(t); } catch { }
                    _productToWorker.Add(t, worker);
                }

                void BindMatsById(Thing t)
                {
                    if (t == null || _currentMats == null || _currentMats.Count == 0) return;
                    _matsById[t.thingIDNumber] = new List<string>(_currentMats);
                }

                BindWorker(product);
                BindMatsById(product);

                if (product is MinifiedThing min && min.InnerThing != null)
                {
                    BindWorker(min.InnerThing);
                    BindMatsById(min.InnerThing);
                }

                if (DebugLogs)
                    Log.Message("[QI] Bound worker + mats for id(s): " +
                        (product is MinifiedThing mm && mm.InnerThing != null
                            ? $"{product.thingIDNumber}/{mm.InnerThing.thingIDNumber}"
                            : product.thingIDNumber.ToString()) +
                        " mats=[" + string.Join(",", _currentMats ?? new List<string>()) + "]");
            }
            catch { /* never break bills */ }
        }

        public static void CompleteConstruction_Prefix(Frame __instance, Pawn worker)
        {
            try
            {
                _currentPawn = worker;
                _currentSkill = SkillDefOf.Construction;
                _hadInspirationAtRoll = worker?.InspirationDef == InspirationDefOf.Inspired_Creativity;
                _wasProdSpecAtRoll = QualityRules.IsProductionSpecialistFor(worker, SkillDefOf.Construction);

                var built = __instance?.def?.entityDefToBuild as ThingDef;
                if (built == null || worker == null) return;

                _buildCtx[__instance.thingIDNumber] = new BuildCtx
                {
                    map = __instance.Map,
                    cell = __instance.Position,
                    builtDef = built,
                    worker = worker
                };

                if (DebugLogs)
                    Log.Message($"[QI] Construction prefix: will build {built.defName} at {__instance.Position} by {P(worker)}");
            }
            catch { }
        }

        public static void CompleteConstruction_Postfix(Frame __instance)
        {
            try
            {
                if (!_buildCtx.TryGetValue(__instance.thingIDNumber, out var ctx)) return;
                _buildCtx.Remove(__instance.thingIDNumber);

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

                var target = builtThing;
                if (target is MinifiedThing m && m.InnerThing != null)
                    target = m.InnerThing;

                try { _productToWorker.Remove(target); } catch { }
                _productToWorker.Add(target, ctx.worker);

                if (DebugLogs)
                    Log.Message($"[QI] Construction bound: {P(ctx.worker)} -> {target.LabelCap}");
            }
            catch { }
        }

        // Cheat short-circuit before vanilla roll
        public static bool GenerateQuality_Prefix(Pawn pawn, SkillDef relevantSkill, ref QualityCategory __result)
        {
            _currentPawn = pawn;
            _currentSkill = relevantSkill;
            _hadInspirationAtRoll = pawn?.InspirationDef == InspirationDefOf.Inspired_Creativity;
            _wasProdSpecAtRoll = QualityRules.IsProductionSpecialistFor(pawn, relevantSkill);

            _clearedInspThisRoll = false;

            // During sampling, strip inspiration so baseline is truly baseline
            if (_samplingNoInspiration && pawn != null)
            {
                var ih = pawn.mindState?.inspirationHandler;
                _savedInspObj = null; _curInspField = null; _curInspProp = null;

                if (ih != null)
                {
                    _curInspField = typeof(InspirationHandler)
                        .GetField("curInspiration", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_curInspField != null)
                    {
                        _savedInspObj = _curInspField.GetValue(ih);
                        _curInspField.SetValue(ih, null);
                        _clearedInspThisRoll = true;
                    }
                    else
                    {
                        _curInspProp = typeof(InspirationHandler)
                            .GetProperty("CurInspiration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (_curInspProp != null && _curInspProp.CanWrite)
                        {
                            _savedInspObj = _curInspProp.GetValue(ih, null);
                            _curInspProp.SetValue(ih, null, null);
                            _clearedInspThisRoll = true;
                        }
                    }
                }

                if (VerboseSamplingLogs && Prefs.DevMode)
                    Log.Message("[QI] StripInspiration: sampling baseline (cleared=" + _clearedInspThisRoll + ")");
            }

            // Cheat short-circuit (disabled during sampling, but guard anyway)
            if (QualityInsightsMod.Settings.enableCheat && !_samplingNoInspiration)
            {
                var forced = TryComputeCheatOverride(pawn, relevantSkill);
                if (forced.HasValue)
                {
                    __result = forced.Value;
                    return false; // skip vanilla
                }
            }

            return true; // let vanilla compute the roll
        }

        // Prefix for GenerateQualityCreatedByPawn(int relevantSkillLevel, bool inspired, ...)
        public static void GenerateQuality_Prefix_LevelInspired([HarmonyArgument(1)] ref bool inspired)
        {
            if (_samplingNoInspiration) inspired = false;
            if (VerboseSamplingLogs && Prefs.DevMode && _samplingNoInspiration)
                Log.Message("[QI] Force inspired=false on (int,bool) overload");
        }

        public static void GenerateQuality_Postfix(Pawn pawn)
        {
            if (_clearedInspThisRoll && pawn != null)
            {
                var ih = pawn.mindState?.inspirationHandler;
                try
                {
                    if (_curInspField != null) _curInspField.SetValue(ih, _savedInspObj);
                    else if (_curInspProp != null && _curInspProp.CanWrite) _curInspProp.SetValue(ih, _savedInspObj, null);
                }
                catch { }
            }
            _clearedInspThisRoll = false;
            _savedInspObj = null; _curInspField = null; _curInspProp = null;
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
            if (fromRoll != null) return fromRoll;
            if (thing?.def?.IsBuildingArtificial == true) return SkillDefOf.Construction;
            if (thing?.TryGetComp<CompArt>() != null) return SkillDefOf.Artistic;
            return SkillDefOf.Crafting;
        }

        private static SkillDef ResolveSkillForRecipeOrProduct(RecipeDef recipe)
        {
            if (recipe?.workSkill != null) return recipe.workSkill;

            try
            {
                if (recipe?.products != null && recipe.products.Any(p => p?.thingDef?.HasComp(typeof(CompArt)) == true))
                    return SkillDefOf.Artistic;
            }
            catch { }

            var label = recipe?.label ?? recipe?.defName ?? string.Empty;
            var l = label.ToLowerInvariant();
            if (l.Contains("sculpt") || l.Contains("art")) return SkillDefOf.Artistic;
            if (l.Contains("build") || l.Contains("construct")) return SkillDefOf.Construction;

            return SkillDefOf.Crafting;
        }

        public static void AfterSetQuality(CompQuality __instance, QualityCategory q)
        {
            var thing = __instance?.parent;
            if (thing == null)
            {
                if (DebugLogs) Log.Message("[QI] AfterSetQuality: parent thing was null");
                _currentPawn = null; _currentSkill = null; _forcedQuality = null;
                return;
            }

            // Local helper so we always clean caches even on early exits.
            void CleanupCaches(Thing t)
            {
                try { _productToWorker.Remove(t); } catch { }
                _matsById.Remove(t.thingIDNumber);

                if (t is MinifiedThing mm && mm.InnerThing != null)
                {
                    try { _productToWorker.Remove(mm.InnerThing); } catch { }
                    _matsById.Remove(mm.InnerThing.thingIDNumber);
                }
            }

            int id = thing.thingIDNumber;

            SkillDef skill = ResolveSkillForThing(thing, _currentSkill);

            Pawn worker = _currentPawn ?? _currentWorkerFromRecipe;
            if (worker == null && _productToWorker.TryGetValue(thing, out var cached))
            {
                worker = cached;
                if (DebugLogs) Log.Message($"[QI] Resolved worker via cache: {worker.LabelShortCap} for {thing.LabelCap}");
                try { _productToWorker.Remove(thing); } catch { }
            }

            // Ignore non-player / unknown workers (filters worldgen, traders, raids, etc.)
            if (worker == null || worker.Faction != Faction.OfPlayer)
            {
                CleanupCaches(thing);
                goto ClearAndReturn;
            }

            QualityCategory? forced = _forcedQuality;

            if (QualityInsightsMod.Settings.enableCheat && forced == null && worker != null)
                forced = TryComputeCheatOverride(worker, skill);

            if (QualityInsightsMod.Settings.enableCheat && forced.HasValue && q < forced.Value)
            {
                if (_bumping.Contains(id)) goto AfterBump;

                try
                {
                    _bumping.Add(id);

                    var art = thing.TryGetComp<CompArt>();
                    if (art != null && !art.Active && worker != null)
                    {
                        try { art.InitializeArt(worker); } catch { }
                    }

                    var ctx = ArtGenerationContext.Colony;
                    __instance.SetQuality(forced.Value, ctx);

                    if (DebugLogs) Log.Message($"[QI] Bumped {thing.LabelCap} from {q} -> {forced.Value} (ctx={ctx})");
                    q = forced.Value;
                }
                catch { }
                finally { _bumping.Remove(id); }
            }
        AfterBump:

            int now = Find.TickManager.TicksGame;

            if (_logGuard.TryGetValue(id, out var g) && g.q == q && now - g.tick < 120)
            {
                if (DebugLogs)
                    Log.Message($"[QI] Suppressed duplicate log for {thing.LabelCap} (q={q}) within {now - g.tick} ticks.");
                CleanupCaches(thing);
                goto ClearAndReturn;
            }
            _logGuard[id] = (now, q);

            if ((now % 5000) == 0 && _logGuard.Count > 512)
            {
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
                    var comp = QualityLogComponent.Ensure(Current.Game);

                    var matsForLog = GetMaterialsFor(thing);

                    if (DebugLogs)
                        Log.Message("[QI] MatsForLog=" + (matsForLog != null ? string.Join(",", matsForLog) : "<none>")
                                    + " stuff=" + (thing.Stuff?.defName ?? "<none>")
                                    + " for " + thing.LabelCap);

                    comp.Add(new QualityLogEntry
                    {
                        thingDef = thing.def?.defName ?? "Unknown",
                        stuffDef = thing.Stuff?.defName,
                        quality  = q,
                        pawnName = worker?.Name?.ToStringShort ?? worker?.LabelShort ?? "Unknown",
                        skillDef = skill?.defName ?? "Unknown",
                        skillLevelAtFinish   = worker?.skills?.GetSkill(skill)?.Level ?? -1,
                        inspiredCreativity   = _hadInspirationAtRoll ?? (worker?.InspirationDef == InspirationDefOf.Inspired_Creativity),
                        productionSpecialist = _wasProdSpecAtRoll ?? (worker != null && QualityRules.IsProductionSpecialistFor(worker, skill)),
                        gameTicks = Find.TickManager.TicksGame,
                        mats      = matsForLog
                    });
                }
                catch { }
            }

            // Always drop caches for this thing (and inner thing) after we’ve attempted to log it.
            CleanupCaches(thing);

        ClearAndReturn:
            _currentPawn = null;
            _currentSkill = null;
            _forcedQuality = null;
            _currentWorkerFromRecipe = null;
            // Do NOT clear _currentMats here; it's overwritten on the next recipe prefix.
            _hadInspirationAtRoll = null;
            _wasProdSpecAtRoll = null;
        }
    }
}
