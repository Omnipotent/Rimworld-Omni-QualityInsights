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
        [ThreadStatic] private static bool? _hadInspirationAtRoll;
        [ThreadStatic] private static bool? _wasProdSpecAtRoll;
        [ThreadStatic] internal static bool _suppressInspirationSideEffects;
        [ThreadStatic] private static bool _clearedInspThisRoll;
        [ThreadStatic] private static object? _savedInspObj;
        [ThreadStatic] private static FieldInfo? _curInspField;
        [ThreadStatic] private static PropertyInfo? _curInspProp;
        [ThreadStatic] private static bool _inConstructionRun;
        [ThreadStatic] private static List<string>? _constructionMats;
        // Add near other [ThreadStatic] fields
        [ThreadStatic] private static int _rollDepth;
        private static bool InVanillaRoll => _rollDepth > 0;

        // Cache worker per product (safe to hold weakly)
        private static readonly ConditionalWeakTable<Thing, Pawn> _productToWorker = new();
        private static readonly ConditionalWeakTable<Thing, SkillDef> _productToSkill = new();

        // Mats are stored by stable id so they survive wrapping/replacement/minification.
        private static readonly Dictionary<int, List<string>> _matsById = new();

        // Prevent duplicate logs for the same thing in a short window
        private static readonly Dictionary<int, (int tick, QualityCategory q)> _logGuard
            = new Dictionary<int, (int, QualityCategory)>();

        private static bool DebugLogs => Prefs.DevMode && QualityInsightsMod.Settings.enableDebugLogs;
        private const bool VerboseSamplingLogs = false;

        private static string P(Pawn? p) => p != null ? p.LabelShortCap : "null";
        private static string S(SkillDef? s) => s != null ? (s.skillLabel ?? s.label ?? s.defName) : "null";

        private struct BuildCtx
        {
            public Map map;
            public IntVec3 cell;
            public ThingDef builtDef;
            public Pawn worker;
            public List<string> mats; // NEW: Stuff + costList materials captured from the Frame
        }        private static readonly Dictionary<int, BuildCtx> _buildCtx = new();
        private static readonly HashSet<int> _bumping = new();
        // Toast suppression (same-tick) for masterwork/legendary messages
        private static int _suppressTick = -1;
        private static readonly HashSet<int> _suppressIdsThisTick = new();

        [ThreadStatic] internal static bool _samplingNoInspiration;
        [ThreadStatic] private static int _afterSetQDepth;
        [ThreadStatic] private static bool _inCheatEval;

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
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(MakeRecipeProducts_Prefix)),
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
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(GenerateQuality_Prefix)) { priority = Priority.High },
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

            // --- Suppress vanilla "Masterwork!" / "Legendary!" toasts ---
            // 1.5/1.6 (TaggedString), with & without the historical bool
            TryPatchMessage(harmony, new[] { typeof(TaggedString), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
            TryPatchMessage(harmony, new[] { typeof(TaggedString), typeof(LookTargets), typeof(MessageTypeDef) });

            // 1.4 (string), with & without the historical bool
            TryPatchMessage(harmony, new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
            TryPatchMessage(harmony, new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef) });

            // NEW: also suppress Letters (right-side blue mail)
            PatchReceiveLetters(harmony);

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
            {
                harmony.Patch(
                    setQ,
                    prefix:  new HarmonyMethod(typeof(QualityPatches), nameof(BeforeSetQuality)),
                    postfix: new HarmonyMethod(typeof(QualityPatches), nameof(AfterSetQuality))
                );
            }
            else
            {
                Log.Error("[QualityInsights] Could not locate CompQuality.SetQuality (signature changed?).");
            }

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
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(CompleteConstruction_Prefix)),
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

        // Works across overloads; __0=label, __1=text (string or TaggedString), __2=LetterDef, __3=LookTargets
        public static bool LetterStack_ReceiveLetter_Prefix(object __0, object __1, LetterDef __2, LookTargets __3)
        {
            try
            {
                // If user didn’t ask for silencing, do nothing
                if (!QualityInsightsMod.Settings.silenceMasterworkNotifs && !QualityInsightsMod.Settings.silenceLegendaryNotifs)
                    return true;

                if (_suppressIdsThisTick.Count == 0) return true;

                int now = Find.TickManager?.TicksGame ?? 0;
                if (now != _suppressTick) { _suppressIdsThisTick.Clear(); return true; }

                var lt = __3;
                if (!lt.IsValid) return true;

                var t = lt.PrimaryTarget.Thing;
                if (t == null) return true;

                bool suppress = _suppressIdsThisTick.Contains(t.thingIDNumber)
                    || (t is MinifiedThing mm && mm.InnerThing != null && _suppressIdsThisTick.Contains(mm.InnerThing.thingIDNumber));

                if (!suppress) return true;

                if (Prefs.DevMode && QualityInsightsMod.Settings.enableDebugLogs)
                {
                    string label = __0?.ToString() ?? "<null>";
                    string def   = __2?.defName ?? "<null>";
                    Log.Message($"[QI] Suppressed LETTER '{label}' (def={def}) for thing id={t.thingIDNumber} at tick={now}");
                }

                return false; // block vanilla letter
            }
            catch { return true; } // never break gameplay
        }

        private static void TryPatchMessage(Harmony h, Type[] sig)
        {
            var mi = AccessTools.Method(typeof(Messages), nameof(Messages.Message), sig);
            if (mi != null)
            {
                h.Patch(mi,
                    prefix: new HarmonyMethod(typeof(QualityPatches), nameof(Messages_Message_Prefix_LookTargets)));
            }
            else if (Prefs.DevMode)
            {
                // Optional: dev log to help verify which overloads exist at runtime
                try { Log.Message("[QI] Messages.Message overload not found for: " + string.Join(", ", sig.Select(t => t.Name))); } catch { }
            }
        }

        private static void PatchReceiveLetters(Harmony h)
        {
            // Patch every ReceiveLetter that has a LookTargets parameter
            var methods = typeof(LetterStack).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "ReceiveLetter" && m.GetParameters().Any(p => p.ParameterType == typeof(LookTargets)));

            int count = 0;
            foreach (var mi in methods)
            {
                h.Patch(mi, prefix: new HarmonyMethod(typeof(QualityPatches), nameof(LetterStack_ReceiveLetter_Prefix)));
                count++;
            }

            if (Prefs.DevMode && QualityInsightsMod.Settings.enableDebugLogs)
                Log.Message($"[QualityInsights] Patched LetterStack.ReceiveLetter overloads with LookTargets: {count}");
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
             return !_suppressInspirationSideEffects;  // false => Harmony blocks original
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

        private static void MarkSuppressToastFor(Thing t)
        {
            if (t == null) return;
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now != _suppressTick) { _suppressTick = now; _suppressIdsThisTick.Clear(); }
            _suppressIdsThisTick.Add(t.thingIDNumber);
            if (t is MinifiedThing m && m.InnerThing != null)
                _suppressIdsThisTick.Add(m.InnerThing.thingIDNumber);
        }

        // Works for both overloads we patched; Harmony injects arg #1 regardless of text type.
        // Return false => skip vanilla Messages.Message (no letter, no sound)
        public static bool Messages_Message_Prefix_LookTargets([HarmonyArgument(1)] LookTargets lookTargets)
        {
            try
            {
                if (_suppressIdsThisTick.Count == 0) return true;

                int now = Find.TickManager?.TicksGame ?? 0;
                if (now != _suppressTick) { _suppressIdsThisTick.Clear(); return true; }

                if (!lookTargets.IsValid) return true;

                var t = lookTargets.PrimaryTarget.Thing;
                if (t == null) return true;

                if (_suppressIdsThisTick.Contains(t.thingIDNumber) ||
                    (t is MinifiedThing m && m.InnerThing != null && _suppressIdsThisTick.Contains(m.InnerThing.thingIDNumber)))
                {
                    if (Prefs.DevMode && QualityInsightsMod.Settings.enableDebugLogs)
                        Log.Message($"[QI] Suppressed MESSAGE for thing id={t.thingIDNumber} tick={now}");
                    return false;
                }

                return true;
            }
            catch { return true; } // never break Messages
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

            // Safety net: while CompleteConstruction() is running, use thread-static capture
            if (_inConstructionRun && _constructionMats != null && _constructionMats.Count > 0)
                return new List<string>(_constructionMats);

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
                    action = () => Find.WindowStack.Add(new UI.ChancesWindow(wt)),
                    hotKey = QI_KeyBindingDefOf.QualityInsights_ShowWorktableOdds   // ← NEW
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
                        defaultLabel = "QI_LiveOdds".Translate(),
                        defaultDesc  = "Show estimated construction quality odds for this building.",
                        icon = TexCommand.DesirePower,
                        action = () => Find.WindowStack.Add(new UI.ConstructionChancesWindow(frame)),
                        hotKey = QI_KeyBindingDefOf.QualityInsights_ShowConstructionOdds   // ← NEW
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
                action = () => Find.WindowStack.Add(new UI.ConstructionChancesWindow(__instance.Map, builtDef)),
                hotKey = QI_KeyBindingDefOf.QualityInsights_ShowConstructionOdds   // ← NEW
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

                // Gather materials BEFORE the frame consumes them
                var mats = new List<string>();
                try
                {
                    if (__instance.Stuff != null)
                        mats.Add(__instance.Stuff.defName);

                    // Frame.resourceContainer is a private ThingOwner<Thing>
                    var rcField = AccessTools.Field(typeof(Frame), "resourceContainer");
                    var rc = rcField?.GetValue(__instance) as ThingOwner<Thing>;
                    if (rc != null)
                        foreach (var t in rc)
                            if (t?.def != null) mats.Add(t.def.defName);
                }
                catch { /* best-effort */ }
                mats = mats.Distinct(StringComparer.Ordinal).ToList();

                // Hand off mats for the duration of CompleteConstruction (covers SetQuality timing)
                _inConstructionRun = true;
                _constructionMats  = mats;

                _buildCtx[__instance.thingIDNumber] = new BuildCtx
                {
                    map = __instance.Map,
                    cell = __instance.Position,
                    builtDef = built,
                    worker = worker,
                    mats = mats
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

                // NEW: bind mats captured from the frame to the product id(s)
                if (ctx.mats != null && ctx.mats.Count > 0)
                {
                    _matsById[target.thingIDNumber] = new List<string>(ctx.mats);
                    if (builtThing is MinifiedThing mm && mm.InnerThing != null)
                        _matsById[mm.InnerThing.thingIDNumber] = new List<string>(ctx.mats);

                    if (DebugLogs)
                    {
                        string idText = (builtThing is MinifiedThing mm2 && mm2.InnerThing != null)
                            ? $"{target.thingIDNumber}/{mm2.InnerThing.thingIDNumber}"
                            : target.thingIDNumber.ToString();

                        Log.Message($"[QI] Construction mats bound: id={idText} mats=[{string.Join(",", ctx.mats)}]");
                    }
                }
                _inConstructionRun = false;
                _constructionMats  = null;
            }
            catch { }
        }

        // Cheat short-circuit before vanilla roll
        public static bool GenerateQuality_Prefix(Pawn pawn, SkillDef relevantSkill, ref QualityCategory __result)
        {
            // START roll scope
            _rollDepth++;

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
                _clearedInspThisRoll = false;

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
            }


            // Cheat short-circuit (now safe—won't recurse)
            if (QualityInsightsMod.Settings.enableCheat && !_samplingNoInspiration)
            {
                var forced = TryComputeCheatOverride(pawn, relevantSkill);
                if (forced.HasValue)
                {
                    _forcedQuality = forced;      // <— see #3 (perf tweak)
                    __result = forced.Value;

                    // IMPORTANT: we’re exiting the roll path here,
                    // so close the scope before returning false.
                    _rollDepth--;
                    return false; // skip vanilla
                }
            }

            return true; // vanilla roll
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

            // END roll scope
            if (_rollDepth > 0) _rollDepth--;

            // Do NOT clear here when we're in a creation pipeline that will immediately call SetQuality.
            // AfterSetQuality will always clear these at the end.
            if (!_inConstructionRun && _currentWorkerFromRecipe == null)
            {
                _currentPawn  = null;
                _currentSkill = null;
            }
        }

        private static QualityCategory? TryComputeCheatOverride(Pawn pawn, SkillDef skill)
        {
            var s = QualityInsightsMod.Settings;
            if (!s.enableCheat || pawn == null || skill == null) return null;

            // Prevent recursion if we re-enter through our own prefix
            if (_inCheatEval) return null;

            Dictionary<QualityCategory, float> chances;
            bool cheatWas = s.enableCheat;

            _inCheatEval = true;
            _suppressInspirationSideEffects = true; // no vanilla insp side-effects
            _samplingNoInspiration = true;          // compute true baseline
            try
            {
                s.enableCheat = false; // **critical**: never sample with cheat on
                chances = QualityEstimator.EstimateChances(pawn, skill, s.estimationSamples);
            }
            finally
            {
                s.enableCheat = cheatWas;
                _samplingNoInspiration = false;
                _suppressInspirationSideEffects = false;
                _inCheatEval = false;
            }

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

        public static void BeforeSetQuality(CompQuality __instance, QualityCategory q)
        {
            try
            {
                var thing = __instance?.parent;
                if (thing == null) return;

                if ((q == QualityCategory.Masterwork  && QualityInsightsMod.Settings.silenceMasterworkNotifs) ||
                    (q == QualityCategory.Legendary   && QualityInsightsMod.Settings.silenceLegendaryNotifs))
                {
                    MarkSuppressToastFor(thing);

                    if (Prefs.DevMode && QualityInsightsMod.Settings.enableDebugLogs)
                    {
                        int now = Find.TickManager?.TicksGame ?? 0;
                        Log.Message($"[QI] Marked for suppression id={thing.thingIDNumber} '{thing.LabelCap}' q={q} tick={now}");
                    }
                }
            }
            catch { /* never break gameplay */ }
        }

        public static void AfterSetQuality(CompQuality __instance, QualityCategory q)
        {
            // --- Reentrancy shield (covers other mods re-calling SetQuality) ---
            if (++_afterSetQDepth > 3)
            {
                try { if (DebugLogs) Log.Warning("[QI] AfterSetQuality: excessive re-entrancy; aborting."); } catch { }
                _afterSetQDepth--;
                return;
            }

            try
            {
                // 1) Get the thing first
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

                // 2) Bound worker availability
                Pawn boundWorker = null;
                bool hasBoundWorker = _productToWorker.TryGetValue(thing, out boundWorker);
                if (!hasBoundWorker && thing is MinifiedThing mx && mx.InnerThing != null)
                    hasBoundWorker = _productToWorker.TryGetValue(mx.InnerThing, out boundWorker);

                // 3) Creation-context gate (skip strip/spawn/retouch SetQuality calls)
                bool validContext =
                    InVanillaRoll           // real quality roll on the stack
                    || _inConstructionRun   // construction pipeline
                    || hasBoundWorker       // recipe product we bound in PostProcessProduct
                    || (_currentWorkerFromRecipe != null); // still enumerating a recipe

                if (!validContext)
                {
                    if (DebugLogs)
                        Log.Message($"[QI] Ignoring SetQuality for '{thing?.LabelCap}' (no creation context; likely strip/spawn/retouch).");
                    CleanupCaches(thing);
                    goto ClearAndReturn;
                }

                int id = thing.thingIDNumber;

                // 4) Resolve skill (avoid stale _currentSkill unless we trust the context)
                SkillDef skill = ResolveSkillForThing(
                    thing,
                    (InVanillaRoll || _inConstructionRun || _currentWorkerFromRecipe != null) ? _currentSkill : null
                );

                // 5) Prefer the bound worker, then fall back
                Pawn worker = _currentPawn ?? _currentWorkerFromRecipe ?? boundWorker;
                if (worker == null && _productToWorker.TryGetValue(thing, out var cached))
                {
                    worker = cached; // ultra fallback
                }

                // If we consumed a bound worker, clear the binding(s)
                if (worker != null && hasBoundWorker)
                {
                    try { _productToWorker.Remove(thing); } catch { }
                    if (thing is MinifiedThing m2 && m2.InnerThing != null)
                        try { _productToWorker.Remove(m2.InnerThing); } catch { }
                }

                // 6) Filter non-player / unknown workers (worldgen, traders, raids, etc.)
                if (worker == null || worker.Faction != Faction.OfPlayer)
                {
                    CleanupCaches(thing);
                    goto ClearAndReturn;
                }

                // If the prefix already forced a quality for this roll, reuse it; otherwise compute once.
                QualityCategory? forced = _forcedQuality;
                if (QualityInsightsMod.Settings.enableCheat && forced == null)
                    forced = TryComputeCheatOverride(worker, skill);

                // Clear the thread-static so we never carry it past this item
                _forcedQuality = null;

                // Safe "bump" (single hop). If something re-enters, we bail early via _bumping + depth guard.
                if (QualityInsightsMod.Settings.enableCheat && forced.HasValue && q < forced.Value)
                {
                    if (_bumping.Contains(id)) goto AfterBump;

                    try
                    {
                        _bumping.Add(id);

                        var art = thing.TryGetComp<CompArt>();
                        if (art != null && !art.Active)
                        {
                            try { art.InitializeArt(worker); } catch { /* never break */ }
                        }

                        var ctx = ArtGenerationContext.Colony;
                        __instance.SetQuality(forced.Value, ctx); // re-enters postfix once
                        if (DebugLogs) Log.Message($"[QI] Bumped {thing.LabelCap} from {q} -> {forced.Value} (ctx={ctx})");
                        q = forced.Value;
                    }
                    catch { /* never break */ }
                    finally { _bumping.Remove(id); }
                }
            AfterBump:

                int now = Find.TickManager?.TicksGame ?? 0;

                // Optional: silence toasts on Masterwork/Legendary, per settings
                if ((q == QualityCategory.Masterwork && QualityInsightsMod.Settings.silenceMasterworkNotifs) ||
                    (q == QualityCategory.Legendary && QualityInsightsMod.Settings.silenceLegendaryNotifs))
                {
                    MarkSuppressToastFor(thing);
                }

                if (_logGuard.TryGetValue(id, out var g) && g.q == q && now - g.tick < 120)
                {
                    if (DebugLogs)
                        Log.Message($"[QI] Suppressed duplicate log for {thing.LabelCap} (q={q}) within {now - g.tick} ticks.");
                    CleanupCaches(thing);
                    goto ClearAndReturn;
                }
                _logGuard[id] = (now, q);

                if (now != 0 && (now % 5000) == 0 && _logGuard.Count > 512)
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

                        // Ensure construction mats are available immediately during SetQuality
                        if (_inConstructionRun && _constructionMats != null && _constructionMats.Count > 0)
                        {
                            try
                            {
                                _matsById[thing.thingIDNumber] = new List<string>(_constructionMats);
                                if (thing is MinifiedThing mi && mi.InnerThing != null)
                                    _matsById[mi.InnerThing.thingIDNumber] = new List<string>(_constructionMats);
                            }
                            catch { /* non-fatal */ }
                        }

                        var matsForLog = GetMaterialsFor(thing);

                        if (DebugLogs)
                            Log.Message("[QI] MatsForLog=" + (matsForLog != null ? string.Join(",", matsForLog) : "<none>")
                                        + " stuff=" + (thing.Stuff?.defName ?? "<none>")
                                        + " for " + thing.LabelCap);

                        comp.Add(new QualityLogEntry
                        {
                            thingDef = thing.def?.defName ?? "Unknown",
                            stuffDef = thing.Stuff?.defName,
                            quality = q,
                            pawnName = worker?.Name?.ToStringShort ?? worker?.LabelShort ?? "Unknown",
                            skillDef = skill?.defName ?? "Unknown",
                            skillLevelAtFinish = worker?.skills?.GetSkill(skill)?.Level ?? -1,
                            inspiredCreativity = _hadInspirationAtRoll ?? (worker?.InspirationDef == InspirationDefOf.Inspired_Creativity),
                            productionSpecialist = _wasProdSpecAtRoll ?? (worker != null && QualityRules.IsProductionSpecialistFor(worker, skill)),
                            gameTicks = now,
                            mats = matsForLog
                        });
                    }
                    catch { /* never break gameplay */ }
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
            catch (Exception ex)
            {
                // Final safety net: never let SetQuality kill the game.
                try { Log.Warning("[QualityInsights] AfterSetQuality swallowed exception: " + ex); } catch { }
                _currentPawn = null; _currentSkill = null; _forcedQuality = null;
                _currentWorkerFromRecipe = null; _hadInspirationAtRoll = null; _wasProdSpecAtRoll = null;
            }
            finally
            {
                _afterSetQDepth = Math.Max(0, _afterSetQDepth - 1);
            }
        }
    }
}
