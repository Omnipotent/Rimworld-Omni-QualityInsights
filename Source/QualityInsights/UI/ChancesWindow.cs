using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using QualityInsights.Prob;
using QualityInsights.Utils;

namespace QualityInsights.UI
{
    public class ChancesWindow : Window
    {
        private static readonly QualityCategory[] TierOrder = new[]
        {
            QualityCategory.Awful,
            QualityCategory.Poor,
            QualityCategory.Normal,
            QualityCategory.Good,
            QualityCategory.Excellent,
            QualityCategory.Masterwork,
            QualityCategory.Legendary
        };
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

        private static bool ThingDefHasCompQuality(ThingDef def)
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
            // Return cached if inputs unchanged
            if (cachedChances != null && cacheKeyPawn == pawn && cacheKeyRecipe == selectedRecipe)
                return cachedChances;

            var samples = QualityInsightsMod.Settings.estimationSamples;
            samples = Math.Max(100, samples);

            // Optional deterministic seed for stability across frames (not strictly necessary now)
            var seed = Gen.HashCombineInt(pawn.thingIDNumber,
                        Gen.HashCombineInt(selectedRecipe?.shortHash ?? 0, samples));

            Dictionary<QualityCategory, float> raw;
            Rand.PushState(seed);
            try
            {
                raw = product != null
                    ? QualityEstimator.EstimateChances(pawn, skill, product, samples)
                    : QualityEstimator.EstimateChances(pawn, skill, samples);
            }
            finally { Rand.PopState(); }

            // No per-frame allocations after this: keep normalized in 0..1 for display
            cachedChances = raw;
            cacheKeyPawn = pawn;
            cacheKeyRecipe = selectedRecipe;
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

            // Default recipe: first quality-capable recipe on this table
            var recipes = GetRecipesOnce();
            selectedRecipe = table.BillStack?.Bills?.OfType<Bill_Production>()?.FirstOrDefault()?.recipe
                             ?? recipes.FirstOrDefault();

            // Initialize derived caches from the starting recipe
            cachedProductDef = selectedRecipe?.products?.FirstOrDefault()?.thingDef;
            cachedSkill = ResolveSkill(selectedRecipe, cachedProductDef);
            RebuildEligiblePawns();

            // Default pawn: best eligible
            selectedPawn = cachedEligiblePawns?.FirstOrDefault()
                           ?? ColonyPawnsOnMap(table.Map).FirstOrDefault();
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
                ls.Label("Inspiration: Inspired Creativity (+2 tiers; Legendary guaranteed if any chance > 0)");
            if (QualityRules.IsProductionSpecialist(pawn))
                ls.Label("Role: Production Specialist (+1 tier)");

            ls.End();
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
