using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.Sound;
using RimWorld;
using QualityInsights.Prob;
using QualityInsights.Utils;
using QualityInsights.Patching;

namespace QualityInsights.UI
{
    public class ChancesWindow : Window
    {
        private static readonly QualityCategory[] TierOrder = new[]
        {
            QualityCategory.Awful, QualityCategory.Poor, QualityCategory.Normal,
            QualityCategory.Good, QualityCategory.Excellent, QualityCategory.Masterwork,
            QualityCategory.Legendary
        };
        private static Dictionary<QualityCategory, float> ShiftTiers(
            Dictionary<QualityCategory, float> src, int tiers)
        {
            var dst = TierOrder.ToDictionary(q => q, q => 0f);
            for (int i = 0; i < TierOrder.Length; i++)
            {
                var q = TierOrder[i];
                if (!src.TryGetValue(q, out var p) || p <= 0f) continue;
                int j = Math.Min(i + Math.Max(0, tiers), TierOrder.Length - 1);
                dst[TierOrder[j]] += p;
            }
            return dst;
        }
        private readonly Building_WorkTable table;

        // Current selections
        private RecipeDef? selectedRecipe;
        private Pawn? selectedPawn;

        // Cached for performance
        private List<RecipeDef>? cachedRecipes;                  // quality-capable recipes for this table
        private List<Pawn>? cachedEligiblePawns;                 // pawns eligible for the selected recipe's skill
        private SkillDef? cachedSkill;                           // resolved skill for current recipe/product
        private ThingDef? cachedProductDef;                      // first product def (if any)
        private Dictionary<QualityCategory, float>? cachedChances;
        private RecipeDef? cacheKeyRecipe;
        private Pawn? cacheKeyPawn;
        private Vector2 scroll;
        private int _nextLogTick;
        private int  cacheKeyBoostMask;   // bit0: inspired, bit1: prodSpec
        private bool cacheKeyCheatFlag;   // whether cheat was enabled when sampled


        // ===== Helpers ======================================================

        private static IEnumerable<Pawn> ColonyPawnsOnMap(Map map) =>
            map?.mapPawns?.FreeColonistsSpawned ?? Enumerable.Empty<Pawn>();

        private static SkillDef ResolveSkill(RecipeDef recipe, ThingDef? productDef)
        {
            if (recipe?.workSkill != null) return recipe.workSkill;

            // Buildings/furniture use Construction
            if (productDef != null && productDef.IsBuildingArtificial)
                return SkillDefOf.Construction;

            // Common vanilla/mod naming (fallback)
            var lbl = recipe?.label ?? recipe?.defName ?? string.Empty;
            var l = lbl.ToLowerInvariant();
            if (l.Contains("sculpt") || l.Contains("art")) return SkillDefOf.Artistic;

            // Default: Crafting
            return SkillDefOf.Crafting;
        }

        private bool TryGetActiveRecipe(out RecipeDef recipe, out Pawn worker)
        {
            recipe = null;
            worker = null;

            var map = table?.Map;
            if (map == null) return false;

            // Check all player pawns currently spawned on the map
            foreach (var p in map.mapPawns?.AllPawnsSpawned ?? Enumerable.Empty<Pawn>())
            {
                try
                {
                    // Only pawns currently doing a bill
                    if (p?.CurJobDef != JobDefOf.DoBill) continue;

                    var job = p.CurJob;
                    // Must be working on THIS table
                    if (job == null || job.targetA.Thing != table) continue;

                    // Job carries the bill (and thus the recipe)
                    var bill = job.bill;
                    var r = bill?.recipe;
                    if (r == null) continue;

                    recipe = r;
                    worker = p;
                    return true;
                }
                catch { /* safe */ }
            }
            return false;
        }

        private static bool ThingDefHasCompQuality(ThingDef? def)
        {
            if (def == null || def.comps == null) return false;
            for (int i = 0; i < def.comps.Count; i++)
                if (def.comps[i]?.compClass == typeof(CompQuality))
                    return true;
            return false;
        }

        private List<RecipeDef> GetRecipesOnce()
        {
            if (cachedRecipes != null) return cachedRecipes;

            var list = new List<RecipeDef>();
            var all = table?.def?.AllRecipes;
            if (all != null)
            {
                foreach (var r in all)
                {
                    var prods = r?.products;
                    if (prods == null) continue;
                    for (int i = 0; i < prods.Count; i++)
                    {
                        var td = prods[i]?.thingDef;
                        if (ThingDefHasCompQuality(td))
                        {
                            list.Add(r);
                            break;
                        }
                    }
                }
            }
            cachedRecipes = list;
            return cachedRecipes;
        }

        private void OnRecipeChanged(RecipeDef? newRecipe)
        {
            if (newRecipe == selectedRecipe) return;

            selectedRecipe = newRecipe;

            // Re-resolve product & skill for the new recipe
            cachedProductDef = selectedRecipe?.products?.FirstOrDefault()?.thingDef;
            cachedSkill = ResolveSkill(selectedRecipe, cachedProductDef);

            // Rebuild eligible pawns list (only when recipe/skill changes)
            RebuildEligiblePawns();

            // Reset selected pawn to "best" for this skill if current is not eligible
            if (selectedPawn == null || !IsPawnEligible(selectedPawn, cachedSkill))
                selectedPawn = cachedEligiblePawns?.FirstOrDefault() ?? ColonyPawnsOnMap(table.Map).FirstOrDefault();

            // Invalidate chance cache
            cachedChances = null;
            cacheKeyPawn = null;
            cacheKeyRecipe = null;
        }

        private void OnPawnChanged(Pawn? newPawn)
        {
            if (newPawn == selectedPawn) return;
            selectedPawn = newPawn;
            // Invalidate chance cache
            cachedChances = null;
            cacheKeyPawn = null;
            cacheKeyRecipe = null;
        }

        private bool IsPawnEligible(Pawn? p, SkillDef skill)
        {
            if (p == null || skill == null) return false;
            var rec = p.skills?.GetSkill(skill);
            return rec != null && !rec.TotallyDisabled;
        }

        private void RebuildEligiblePawns()
        {
            cachedEligiblePawns = new List<Pawn>();
            var skill = cachedSkill;
            if (skill == null) return;

            foreach (var p in ColonyPawnsOnMap(table.Map))
            {
                var rec = p.skills?.GetSkill(skill);
                if (rec != null && !rec.TotallyDisabled)
                    cachedEligiblePawns.Add(p);
            }

            // Highest skill first
            cachedEligiblePawns.Sort((a, b) =>
                (b.skills?.GetSkill(skill)?.Level ?? -1).CompareTo(a.skills?.GetSkill(skill)?.Level ?? -1));
        }

        private Dictionary<QualityCategory, float> GetChances(Pawn pawn, SkillDef skill, ThingDef? product)
        {
            // compute dynamic bits that affect probabilities
            int boostMaskNow = 0;
            if (pawn.InspirationDef == InspirationDefOf.Inspired_Creativity) boostMaskNow |= 1;
            if (QualityRules.IsProductionSpecialist(pawn)) boostMaskNow |= 2;
            bool cheatNow = QualityInsightsMod.Settings.enableCheat;

            // only reuse cache if EVERYTHING matches
            if (cachedChances != null
                && cacheKeyPawn == pawn
                && cacheKeyRecipe == selectedRecipe
                && cacheKeyBoostMask == boostMaskNow
                && cacheKeyCheatFlag == cheatNow)
            {
                return cachedChances;
            }

            var samples = Math.Max(100, QualityInsightsMod.Settings.estimationSamples);
            var seed = Gen.HashCombineInt(pawn.thingIDNumber,
                        Gen.HashCombineInt(selectedRecipe?.shortHash ?? 0, samples));

            bool cheatWasEnabled = QualityInsightsMod.Settings.enableCheat;
            Dictionary<QualityCategory, float> raw;

            QualityPatches._suppressInspirationSideEffects = true; // keep vanilla messages off
            QualityInsightsMod.Settings.enableCheat = false;       // never let cheat bias sampling
            QualityPatches._samplingNoInspiration = true;          // <-- NEW: per-roll strip

            Rand.PushState(seed);
            try
            {
                // (no need for InspirationScope now)
                raw = (product != null)
                    ? QualityEstimator.EstimateBaseline(pawn, skill, product, samples)
                    : QualityEstimator.EstimateBaseline(pawn, skill, samples);
            }
            finally
            {
                Rand.PopState();
                QualityPatches._samplingNoInspiration = false;      // <-- restore
                QualityInsightsMod.Settings.enableCheat = cheatWasEnabled;
                QualityPatches._suppressInspirationSideEffects = false;
            }

            // apply post-sample tier boost
            int tierBoost = 0;
            if ((boostMaskNow & 1) != 0) tierBoost += 2;
            if ((boostMaskNow & 2) != 0) tierBoost += 1;

            // --- DIAGNOSTIC START ---
                        bool inspiredProp =
                pawn?.InspirationDef == InspirationDefOf.Inspired_Creativity;

            var ih = pawn?.mindState?.inspirationHandler;
            bool inspiredHandler =
                ih != null &&
                (typeof(InspirationHandler)
                    .GetProperty("CurInspiration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(ih, null)) is Inspiration cur &&
                cur.def == InspirationDefOf.Inspired_Creativity;

            bool prodSpecNow = QualityRules.IsProductionSpecialist(pawn);

            if (Prefs.DevMode)
            {
                Log.Message($"[QI] Flags | InspiredProp={inspiredProp} | InspiredHandler={inspiredHandler} | ProdSpec={prodSpecNow} | tierBoost={tierBoost} | mask={boostMaskNow}");
            }
            // --- DIAGNOSTIC END ---

            // shift once according to the *current* state
            var finalChances = tierBoost > 0 ? ShiftTiers(raw, tierBoost) : raw;

            // Cap legendary if not allowed: move its mass into Masterwork
            if (!QualityRules.LegendaryAllowedFor(pawn)
                && finalChances.TryGetValue(QualityCategory.Legendary, out var l)
                && l > 0f)
            {
                if (finalChances.TryGetValue(QualityCategory.Masterwork, out var m))
                    finalChances[QualityCategory.Masterwork] = m + l;
                else
                    finalChances[QualityCategory.Masterwork] = l;

                finalChances[QualityCategory.Legendary] = 0f;
            }

            // (optional) dev log: show both baseline and final so you can eyeball they match the UI
            if (Prefs.DevMode)
            {
                float sumRaw = 0f;
                string rawDump = string.Join(", ", TierOrder.Select(q => { raw.TryGetValue(q, out var p); sumRaw += p; return $"{q}:{p:P2}"; }));

                float sumFinal = 0f;
                string finalDump = string.Join(", ", TierOrder.Select(q => { finalChances.TryGetValue(q, out var p); sumFinal += p; return $"{q}:{p:P2}"; }));

                Log.Message($"[QI] Raw (no boosts)   | {rawDump}   | Sum={sumRaw:F3} | TierBoost={tierBoost}");
                Log.Message($"[QI] Final (with boost)| {finalDump} | Sum={sumFinal:F3}");
            }

            // update cache keys
            cachedChances = finalChances;
            cacheKeyPawn = pawn;
            cacheKeyRecipe = selectedRecipe;
            cacheKeyBoostMask = boostMaskNow;
            cacheKeyCheatFlag = cheatNow;

            return cachedChances!;
        }

        // ===== Window plumbing ==============================================

        public override Vector2 InitialSize => new(520f, 420f);

        public ChancesWindow(Building_WorkTable table)
        {
            this.table = table;

            doCloseX = true;
            absorbInputAroundWindow = false;  // allow game interaction
            forcePause = false;
            closeOnClickedOutside = true;
            draggable = true;

            // Build the cached recipe list once
            var recipes = GetRecipesOnce();

            // Prefer the currently active bill on THIS table (if any)
            if (TryGetActiveRecipe(out var activeRecipe, out var activeWorker))
            {
                // Set recipe and pawn from live job
                selectedRecipe  = activeRecipe;
                cachedProductDef = selectedRecipe?.products?.FirstOrDefault()?.thingDef;
                cachedSkill     = ResolveSkill(selectedRecipe, cachedProductDef);
                RebuildEligiblePawns();

                // If the live worker is eligible for the resolved skill, select them; otherwise pick best
                selectedPawn = (activeWorker != null && IsPawnEligible(activeWorker, cachedSkill))
                    ? activeWorker
                    : (cachedEligiblePawns?.FirstOrDefault() ?? ColonyPawnsOnMap(table.Map).FirstOrDefault());
            }
            else
            {
                // Fallback: first bill on this table or first quality-capable recipe
                selectedRecipe = table.BillStack?.Bills?.OfType<Bill_Production>()?.FirstOrDefault()?.recipe
                                ?? recipes.FirstOrDefault();

                cachedProductDef = selectedRecipe?.products?.FirstOrDefault()?.thingDef;
                cachedSkill      = ResolveSkill(selectedRecipe, cachedProductDef);
                RebuildEligiblePawns();

                // Default pawn: best eligible
                selectedPawn = cachedEligiblePawns?.FirstOrDefault()
                            ?? ColonyPawnsOnMap(table.Map).FirstOrDefault();
            }

        }

        public override void DoWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);

            // -------------------------------
            // Recipe picker (cached list)
            // -------------------------------
            var recipes = GetRecipesOnce();
            if (recipes.Count == 0)
            {
                ls.Label("No quality-capable recipes on this table.");
                ls.End(); return;
            }

            var recipeLabel = selectedRecipe?.LabelCap.ToString() ?? "(select)";
            if (ls.ButtonTextLabeled("Recipe", recipeLabel))
            {
                var opts = new List<FloatMenuOption>(recipes.Count);
                foreach (var r in recipes)
                {
                    var local = r; // capture
                    opts.Add(new FloatMenuOption(r.LabelCap.ToString(), () => OnRecipeChanged(local)));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            // -------------------------------
            // Pawn picker (recomputed only on recipe change)
            // -------------------------------
            var skill = cachedSkill ?? SkillDefOf.Crafting; // ultra-safe fallback
            if (selectedPawn == null || !IsPawnEligible(selectedPawn, skill))
                selectedPawn = cachedEligiblePawns?.FirstOrDefault() ?? ColonyPawnsOnMap(table.Map).FirstOrDefault();

            var pawnLabel = selectedPawn?.LabelShortCap ?? "(best)";
            if (ls.ButtonTextLabeled("Pawn", pawnLabel))
            {
                var options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("(best)", () =>
                {
                    OnPawnChanged(cachedEligiblePawns?.FirstOrDefault() ?? selectedPawn);
                }));

                if (cachedEligiblePawns != null)
                {
                    foreach (var p in cachedEligiblePawns)
                    {
                        var local = p; // capture
                        options.Add(new FloatMenuOption(local.LabelShortCap, () => OnPawnChanged(local)));
                    }
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            var pawn = selectedPawn;
            if (pawn == null)
            {
                ls.GapLine();
                ls.Label("No suitable pawn found.");
                ls.End(); return;
            }

            ls.GapLine();

            // -------------------------------
            // Chance calculation (cached)
            // -------------------------------
            var chances = GetChances(pawn, skill, cachedProductDef);
            if (Prefs.DevMode)
            {
                float sumUI = 0f;
                var uiDump = string.Join(", ", TierOrder.Select(q =>
                {
                    var v = GetPct(chances, q); sumUI += v; return $"{q}:{v:P2}";
                }));
                Log.Message($"[QI] UI WillShow       | {uiDump} | Sum={sumUI:F3}");
            }
            // if (Find.TickManager.TicksGame >= _nextLogTick)
            // {
            //     Log.Message($"[QI] Debug | Pawn={pawn?.LabelShortCap} | Inspired={(pawn?.InspirationDef == InspirationDefOf.Inspired_Creativity)} | ProdSpec={QualityRules.IsProductionSpecialist(pawn)} | Skill={cachedSkill?.defName} | Recipe={selectedRecipe?.defName}");
            //     _nextLogTick = Find.TickManager.TicksGame + 120; // every 120 ticks (~2s)
            // }
            Log.Message($"[QI] Context | Pawn={pawn?.LabelShortCap} | InspiredProp={(pawn?.InspirationDef == InspirationDefOf.Inspired_Creativity)} | ProdSpec={QualityRules.IsProductionSpecialist(pawn)} | Skill={cachedSkill?.defName} | Recipe={selectedRecipe?.defName}");

            // Show all tiers so the rows add up to 100%
            float total = 0f;
            foreach (var qc in TierOrder)
            {
                var p = GetPct(chances, qc);
                total += p;
                DrawPercentRow(ls, qc.ToString(), p);
            }

            // Optional tiny footer to confirm totals (rounding may show 99.99% or 100.01%)
            ls.Gap(2f);
            ls.Label($"Total: {total.ToString("P2")}");

            ls.GapLine();
            var level = pawn.skills?.GetSkill(skill)?.Level ?? 0;
            ls.Label($"Pawn: {pawn.LabelShortCap}  |  Skill: {(skill?.skillLabel ?? skill?.label ?? skill?.defName).CapitalizeFirst()} {level}");
            if (pawn.InspirationDef == InspirationDefOf.Inspired_Creativity)
                ls.Label("Inspiration: Inspired Creativity (+2 tiers; caps at Legendary)");
            if (QualityRules.IsProductionSpecialist(pawn))
                ls.Label("Role: Production Specialist (+1 tier)");

            ls.End();

            // --- Resize Grip Overlay ---
            if (resizeable)
            {
                const float gripSize = 16f; // tweak as you like
                var gripRect = new Rect(inRect.width - gripSize, inRect.height - gripSize, gripSize, gripSize);

                // Draw a faint triangle or diagonal lines so player knows it’s draggable
                Widgets.DrawLine(gripRect.position, gripRect.position + new Vector2(gripSize, gripSize), Color.gray, 2f);

                // Ensure input here is passed to base resizing
                if (Mouse.IsOver(gripRect))
                    MouseoverSounds.DoRegion(gripRect); // optional hover sound/feedback
            }
        }

        private static float GetPct(Dictionary<QualityCategory, float> chances, QualityCategory qc)
            => chances.TryGetValue(qc, out var p) ? p : 0f;

        private static void DrawPercentRow(Listing_Standard ls, string label, float p01)
        {
            var r = ls.GetRect(24f);
            Widgets.Label(r.LeftPart(0.5f), label);
            Widgets.Label(r.RightPart(0.5f), p01.ToString("P2"));
        }
    }
}
