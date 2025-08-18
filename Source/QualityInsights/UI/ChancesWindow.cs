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

        public override Vector2 InitialSize => new(520f, 420f);

        public ChancesWindow(Building_WorkTable table)
        {
            this.table = table;
            doCloseX = true; draggable = true; absorbInputAroundWindow = true;
            selectedRecipe = table.BillStack?.Bills?.OfType<Bill_Production>()?.FirstOrDefault()?.recipe;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);

            // Recipe picker
            // var recipes = table.AllRecipes.ToList();
            var recipes = table.def.AllRecipes.ToList();
            if (recipes.Count == 0)
            {
                ls.Label("No recipes on this table.");
                ls.End(); return;
            }
            // if (ls.ButtonTextLabeled("Recipe", selectedRecipe?.LabelCap ?? "(select)"))
            if (ls.ButtonTextLabeled("Recipe", (selectedRecipe?.LabelCap.ToString()) ?? "(select)"))
            {
                // var fl = new FloatMenu(recipes.Select(r =>
                //     new FloatMenuOption(r.LabelCap ?? r.label ?? r.defName, () => selectedRecipe = r)).ToList());
                // Find.WindowStack.Add(fl);
                var fl = new FloatMenu(recipes.Select(r =>
                {
                    var label = (r?.LabelCap.ToString()) ?? r?.label ?? r?.defName;
                    return new FloatMenuOption(label, () => selectedRecipe = r);
                }).ToList());
            }

            // Pawn picker (colonists capable of the recipe's skill)
            var skill = selectedRecipe?.workSkill ?? SkillDefOf.Crafting;
            var pawns = PawnsFinder.AllMaps_FreeColonists
                .Where(p => p.workSettings?.WorkIsActive(WorkTypeDefOf.Crafting) ?? true)
                .Where(p => p.skills?.GetSkill(skill) != null).ToList();

            if (ls.ButtonTextLabeled("Pawn", selectedPawn?.LabelShortCap ?? "(best)"))
            {
                var opts = new List<FloatMenuOption>();
                var best = pawns.OrderByDescending(p => p.skills.GetSkill(skill).Level).FirstOrDefault();
                opts.Add(new FloatMenuOption("(best)", () => selectedPawn = null));
                opts.AddRange(pawns.Select(p => new FloatMenuOption(p.LabelShortCap, () => selectedPawn = p)));
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            var pawn = selectedPawn ?? pawns.OrderByDescending(p => p.skills.GetSkill(skill).Level).FirstOrDefault();
            if (pawn == null || skill == null)
            {
                ls.Label("No suitable pawn found.");
                ls.End(); return;
            }

            ls.GapLine();

            var samples = QualityInsightsMod.Settings.estimationSamples;
            var thing = selectedRecipe?.products?.FirstOrDefault().thingDef;
            var dist = QualityEstimator.EstimateChances(pawn, skill, thing, samples);

            // Show top tiers
            // DrawRow(ls, "Excellent", dist.GetValueOrDefault(QualityCategory.Excellent));
            // DrawRow(ls, "Masterwork", dist.GetValueOrDefault(QualityCategory.Masterwork));
            // DrawRow(ls, "Legendary", dist.GetValueOrDefault(QualityCategory.Legendary));
            DrawRow(ls, "Excellent", dist.GetOrDefault(QualityCategory.Excellent, 0f));
            DrawRow(ls, "Masterwork", dist.GetOrDefault(QualityCategory.Masterwork, 0f));
            DrawRow(ls, "Legendary", dist.GetOrDefault(QualityCategory.Legendary, 0f));

            ls.GapLine();
            // ls.Label($"Pawn: {pawn.LabelShortCap}  |  Skill: {skill.labelCap} {pawn.skills.GetSkill(skill).Level}");
            ls.Label($"Pawn: {pawn.LabelShortCap}  |  Skill: {(skill?.skillLabel ?? skill?.label ?? skill?.defName).CapitalizeFirst()} {pawn.skills.GetSkill(skill).Level}");
            if (pawn.InspirationDef == InspirationDefOf.Inspired_Creativity) ls.Label("Inspiration: Inspired Creativity (+2 quality tiers)");
            if (QualityRules.IsProductionSpecialist(pawn)) ls.Label("Role: Production Specialist (+1 tier)");

            ls.End();
        }

        private static void DrawRow(Listing_Standard ls, string label, float p)
        {
            var r = ls.GetRect(24f);
            Widgets.Label(r.LeftPart(0.5f), label);
            Widgets.Label(r.RightPart(0.5f), p.ToString("P2"));
        }
    }
}
