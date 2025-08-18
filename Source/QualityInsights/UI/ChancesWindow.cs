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
        private readonly Building_WorkTable table;
        private RecipeDef? selectedRecipe;
        private Pawn? selectedPawn;
        private Vector2 scroll;

        private static IEnumerable<Pawn> ColonyPawnsOnMap(Map map) =>
            map?.mapPawns?.FreeColonistsSpawned ?? Enumerable.Empty<Pawn>();

        private static SkillDef ResolveSkill(RecipeDef recipe)
        {
            // RimWorld usually sets workSkill; fall back to Artistic for sculptures,
            // Crafting otherwise
            if (recipe?.workSkill != null) return recipe.workSkill;
            if (recipe?.label?.ToLowerInvariant().Contains("sculpt") == true) return SkillDefOf.Artistic;
            return SkillDefOf.Crafting;
        }


        public override Vector2 InitialSize => new(520f, 420f);

        public ChancesWindow(Building_WorkTable table)
        {
            this.doCloseX = true;
            this.absorbInputAroundWindow = false;  // don’t block clicks to map/hotkeys
            this.forcePause = false;               // don’t pause the game
            // this.forcePause = true;                // pause the game
            this.closeOnClickedOutside = true;     // optional QoL

            this.draggable = true;                 // optional QoL
            this.table = table;
            selectedRecipe = table.BillStack?.Bills?.OfType<Bill_Production>()?.FirstOrDefault()?.recipe;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);

            // -------------------------------
            // Recipe picker
            // -------------------------------
            var recipes = table.def.AllRecipes
                .Where(r =>
                    r.products != null &&
                    r.products.Any(p =>
                    {
                        var td = p.thingDef;
                        // true if this product’s ThingDef has a CompQuality
                        return td != null && td.comps != null &&
                            td.comps.Any(cp => cp?.compClass == typeof(CompQuality));
                    }))
                .ToList();


            if (selectedRecipe == null && recipes.Count > 0)
                selectedRecipe = recipes[0];

            if (recipes.Count == 0)
            {
                ls.Label("No recipes on this table.");
                ls.End(); return;
            }

            if (ls.ButtonTextLabeled("Recipe", (selectedRecipe?.LabelCap.ToString()) ?? "(select)"))
            {
                var fl = new FloatMenu(recipes.Select(r =>
                {
                    var label = r.LabelCap.ToString();
                    return new FloatMenuOption(label, () => selectedRecipe = r);
                }).ToList());
                Find.WindowStack.Add(fl); // <— actually open the menu
            }

            // -------------------------------
            // Pawn picker (eligible for the recipe's skill)
            // -------------------------------
            var skill = ResolveSkill(selectedRecipe);
            var pawnsOnMap = ColonyPawnsOnMap(table.Map).ToList();
            var eligible = pawnsOnMap
                .Where(p => p.skills?.GetSkill(skill) != null && !p.skills.GetSkill(skill).TotallyDisabled)
                .OrderByDescending(p => p.skills.GetSkill(skill).Level)
                .ToList();

            if (selectedPawn == null)
                selectedPawn = eligible.FirstOrDefault() ?? pawnsOnMap.FirstOrDefault();

            if (ls.ButtonTextLabeled("Pawn", selectedPawn?.LabelShortCap ?? "(best)"))
            {
                var options = new List<FloatMenuOption>();
                options.Add(new FloatMenuOption("(best)", () =>
                {
                    selectedPawn = eligible.FirstOrDefault() ?? pawnsOnMap.FirstOrDefault();
                }));
                foreach (var p in eligible)
                    options.Add(new FloatMenuOption(p.LabelShortCap, () => selectedPawn = p));
                Find.WindowStack.Add(new FloatMenu(options));
            }

            var pawn = selectedPawn ?? eligible.FirstOrDefault() ?? pawnsOnMap.FirstOrDefault();
            if (pawn == null)
            {
                ls.GapLine();
                ls.Label("No suitable pawn found.");
                ls.End(); return;
            }

            ls.GapLine();

            // -------------------------------
            // Chance calculation
            // -------------------------------
            var productDef = selectedRecipe?.products?.FirstOrDefault()?.thingDef;

            Dictionary<QualityCategory,float> chances;
            var seed = Gen.HashCombineInt(pawn.thingIDNumber,
                        Gen.HashCombineInt(selectedRecipe?.shortHash ?? 0,
                        QualityInsightsMod.Settings.estimationSamples));

            Rand.PushState(seed);
            try
            {
                if (productDef != null)
                    chances = QualityEstimator.EstimateChances(pawn, skill, productDef, QualityInsightsMod.Settings.estimationSamples);
                else
                    chances = QualityEstimator.EstimateChances(pawn, skill, QualityInsightsMod.Settings.estimationSamples);
            }
            finally { Rand.PopState(); }

            DrawPercentRow(ls, "Excellent", chances.TryGetValue(QualityCategory.Excellent, out var ex) ? ex : 0f);
            DrawPercentRow(ls, "Masterwork", chances.TryGetValue(QualityCategory.Masterwork, out var mw) ? mw : 0f);
            DrawPercentRow(ls, "Legendary", chances.TryGetValue(QualityCategory.Legendary, out var lg) ? lg : 0f);

            ls.GapLine();
            ls.Label($"Pawn: {pawn.LabelShortCap}  |  Skill: {(skill?.skillLabel ?? skill?.label ?? skill?.defName).CapitalizeFirst()} {pawn.skills.GetSkill(skill).Level}");
            if (pawn.InspirationDef == InspirationDefOf.Inspired_Creativity) ls.Label("Inspiration: Inspired Creativity (+2 quality tiers)");
            if (QualityRules.IsProductionSpecialist(pawn)) ls.Label("Role: Production Specialist (+1 tier)");

            // after chances are computed and before ls.End()
            bool legendaryPossible = chances.TryGetValue(QualityCategory.Legendary, out var pLegend) && pLegend > 0f;
            if (pawn.InspirationDef == InspirationDefOf.Inspired_Creativity && legendaryPossible)
                ls.Label("Note: Legendary is guaranteed while Inspired Creativity is active for this recipe/pawn.");

            ls.End();
        }

        private static void DrawPercentRow(Listing_Standard ls, string label, float p)
        {
            var r = ls.GetRect(24f);
            Widgets.Label(r.LeftPart(0.5f), label);
            Widgets.Label(r.RightPart(0.5f), p.ToString("P2"));
        }

        private static void DrawRow(Listing_Standard ls, string label, float p)
        {
            var r = ls.GetRect(24f);
            Widgets.Label(r.LeftPart(0.5f), label);
            Widgets.Label(r.RightPart(0.5f), p.ToString("P2"));
        }
    }
}
