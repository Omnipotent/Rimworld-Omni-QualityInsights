using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using RimWorld;
using QualityInsights.Prob;
using QualityInsights.Utils;
using QualityInsights.Patching;

namespace QualityInsights.UI
{
    public class ConstructionChancesWindow : Window
    {
        private static readonly QualityCategory[] TierOrder = new[]
        {
            QualityCategory.Awful, QualityCategory.Poor, QualityCategory.Normal,
            QualityCategory.Good, QualityCategory.Excellent, QualityCategory.Masterwork,
            QualityCategory.Legendary
        };

        private static Dictionary<QualityCategory, float> ShiftTiers(Dictionary<QualityCategory, float> src, int tiers)
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

        private readonly Map map;
        private readonly ThingDef builtDef;
        private readonly Frame frame; // may be null when launched from blueprint

        // Selection & caches
        private Pawn selectedPawn;
        private List<Pawn> cachedEligiblePawns;
        private Dictionary<QualityCategory, float> cachedChances;
        private Pawn cacheKeyPawn;
        private int  cacheKeyBoostMask;   // bit0: inspired, bit1: prodSpec
        private bool cacheKeyCheatFlag;

        // UI flags echoing the calc that produced cachedChances
        private bool uiLastInspired;
        private bool uiLastProdSpec;
        private int  uiLastTierBoost;

        private void UpdateUiFlagsFromMask(int mask)
        {
            uiLastInspired  = (mask & 1) != 0;
            uiLastProdSpec  = (mask & 2) != 0;
            uiLastTierBoost = (uiLastInspired ? 2 : 0) + (uiLastProdSpec ? 1 : 0);
        }

        public override Vector2 InitialSize => new(520f, 420f);

        // Frame-based ctor (preferred: lets us auto-pick the active worker, if any)
        public ConstructionChancesWindow(Frame frame)
        {
            this.frame = frame;
            this.map   = frame?.Map;
            this.builtDef = frame?.def?.entityDefToBuild as ThingDef;

            doCloseX = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            closeOnClickedOutside = true;
            draggable = true;

            BootstrapSelection();
        }

        // Blueprint-based ctor (no live frame yet)
        public ConstructionChancesWindow(Map map, ThingDef builtDef)
        {
            this.frame = null;
            this.map = map;
            this.builtDef = builtDef;

            doCloseX = true;
            absorbInputAroundWindow = false;
            forcePause = false;
            closeOnClickedOutside = true;
            draggable = true;

            BootstrapSelection();
        }

        private IEnumerable<Pawn> ColonyPawnsOnMap(Map m) =>
            m?.mapPawns?.FreeColonistsSpawned ?? Enumerable.Empty<Pawn>();

        private bool TryGetActiveConstructor(out Pawn worker)
        {
            worker = null;
            if (frame == null || map == null) return false;

            foreach (var p in map.mapPawns?.AllPawnsSpawned ?? Enumerable.Empty<Pawn>())
            {
                try
                {
                    if (p?.CurJobDef != JobDefOf.FinishFrame) continue;
                    var job = p.CurJob;
                    if (job == null) continue;
                    if (job.targetA.Thing == frame) { worker = p; return true; }
                }
                catch { }
            }
            return false;
        }

        private bool IsPawnEligible(Pawn p)
        {
            if (p == null) return false;
            var rec = p.skills?.GetSkill(SkillDefOf.Construction);
            return rec != null && !rec.TotallyDisabled;
        }

        private void RebuildEligiblePawns()
        {
            cachedEligiblePawns = new List<Pawn>();
            foreach (var p in ColonyPawnsOnMap(map))
            {
                var rec = p.skills?.GetSkill(SkillDefOf.Construction);
                if (rec != null && !rec.TotallyDisabled)
                    cachedEligiblePawns.Add(p);
            }
            cachedEligiblePawns.Sort((a, b) =>
                (b.skills?.GetSkill(SkillDefOf.Construction)?.Level ?? -1)
                .CompareTo(a.skills?.GetSkill(SkillDefOf.Construction)?.Level ?? -1));
        }

        private void BootstrapSelection()
        {
            RebuildEligiblePawns();

            // Prefer the live worker currently finishing this frame (if any)
            if (TryGetActiveConstructor(out var active) && IsPawnEligible(active))
                selectedPawn = active;
            else
                selectedPawn = cachedEligiblePawns?.FirstOrDefault()
                            ?? ColonyPawnsOnMap(map).FirstOrDefault();
        }

        private void OnPawnChanged(Pawn p)
        {
            if (p == selectedPawn) return;
            selectedPawn = p;
            cachedChances = null;
            cacheKeyPawn = null;
        }

        private Dictionary<QualityCategory, float> GetChances(Pawn pawn)
        {
            var skill = SkillDefOf.Construction;

            // dynamic mask
            int mask = 0;
            if (pawn?.InspirationDef == InspirationDefOf.Inspired_Creativity) mask |= 1;
            if (QualityRules.IsProductionSpecialistFor(pawn, skill)) mask |= 2;
            bool cheatNow = QualityInsightsMod.Settings.enableCheat;

            if (cachedChances != null
                && cacheKeyPawn == pawn
                && cacheKeyBoostMask == mask
                && cacheKeyCheatFlag == cheatNow)
            {
                UpdateUiFlagsFromMask(mask);
                return cachedChances;
            }

            var samples = Math.Max(100, QualityInsightsMod.Settings.estimationSamples);
            var seed = Gen.HashCombineInt(pawn?.thingIDNumber ?? 0, Gen.HashCombineInt(builtDef?.shortHash ?? 0, samples));

            bool cheatWas = QualityInsightsMod.Settings.enableCheat;
            Dictionary<QualityCategory, float> raw;

            QualityPatches._suppressInspirationSideEffects = true;
            QualityInsightsMod.Settings.enableCheat = false;
            QualityPatches._samplingNoInspiration = true;

            Rand.PushState(seed);
            try
            {
                raw = QualityEstimator.EstimateBaseline(pawn, skill, builtDef, samples);
            }
            finally
            {
                Rand.PopState();
                QualityPatches._samplingNoInspiration = false;
                QualityInsightsMod.Settings.enableCheat = cheatWas;
                QualityPatches._suppressInspirationSideEffects = false;
            }

            int tierBoost = 0;
            if ((mask & 1) != 0) tierBoost += 2;
            if ((mask & 2) != 0) tierBoost += 1;

            UpdateUiFlagsFromMask(mask);

            var final = tierBoost > 0 ? ShiftTiers(raw, tierBoost) : raw;

            // Cap Legendary if not allowed
            if (!QualityRules.LegendaryAllowedFor(pawn)
                && final.TryGetValue(QualityCategory.Legendary, out var l) && l > 0f)
            {
                final[QualityCategory.Masterwork] = (final.TryGetValue(QualityCategory.Masterwork, out var m) ? m : 0f) + l;
                final[QualityCategory.Legendary] = 0f;
            }

            cachedChances = final;
            cacheKeyPawn = pawn;
            cacheKeyBoostMask = mask;
            cacheKeyCheatFlag = cheatNow;

            return cachedChances;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);

            // Header
            var label = builtDef?.label?.CapitalizeFirst() ?? builtDef?.defName ?? "Building";
            ls.Label($"Building: {label}");
            ls.GapLine();

            // Pawn picker
            if (selectedPawn == null || !IsPawnEligible(selectedPawn))
                selectedPawn = cachedEligiblePawns?.FirstOrDefault() ?? ColonyPawnsOnMap(map).FirstOrDefault();

            var pawnLabel = selectedPawn?.LabelShortCap ?? "(best)";
            if (ls.ButtonTextLabeled("Pawn", pawnLabel))
            {
                var opts = new List<FloatMenuOption>();
                opts.Add(new FloatMenuOption("(best)", () =>
                {
                    OnPawnChanged(cachedEligiblePawns?.FirstOrDefault() ?? selectedPawn);
                }));
                if (cachedEligiblePawns != null)
                {
                    foreach (var p in cachedEligiblePawns)
                    {
                        var local = p;
                        opts.Add(new FloatMenuOption(local.LabelShortCap, () => OnPawnChanged(local)));
                    }
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            var pawn = selectedPawn;
            if (pawn == null)
            {
                ls.GapLine();
                ls.Label("No suitable pawn found.");
                ls.End(); return;
            }

            ls.GapLine();

            // Chances
            var chances = GetChances(pawn);
            float total = 0f;
            foreach (var qc in TierOrder)
            {
                var p = chances.TryGetValue(qc, out var v) ? v : 0f;
                total += p;
                DrawPercentRow(ls, qc.ToString(), p);
            }
            ls.Gap(2f);
            ls.Label($"Total: {total:P2}");

            ls.GapLine();
            var level = pawn.skills?.GetSkill(SkillDefOf.Construction)?.Level ?? 0;
            ls.Label($"Pawn: {pawn.LabelShortCap}  |  Skill: {SkillDefOf.Construction.skillLabel.CapitalizeFirst()} {level}");
            if (uiLastInspired)
                ls.Label("Inspiration: Inspired Creativity (+2 tiers; caps at Legendary)");
            if (uiLastProdSpec)
                ls.Label("Role: Production Specialist (+1 tier)");

            // Dev-only validation button, bottom-right
            if (Prefs.DevMode && selectedPawn != null)
            {
                const float bw = 132f;
                const float bh = 24f;
                const float pad = 6f;

                var br = new Rect(
                    inRect.width  - bw - pad,
                    inRect.height - bh - pad,
                    bw, bh);

                if (Widgets.ButtonText(br, "Validate 100k"))
                    DevValidateNow(selectedPawn);
            }

            ls.End();
        }

        private void DevValidateNow(Pawn pawn)
        {
            const int N = 100_000;

            var cheatWas = QualityInsightsMod.Settings.enableCheat;
            Dictionary<QualityCategory, float> big;

            QualityPatches._suppressInspirationSideEffects = true;
            QualityInsightsMod.Settings.enableCheat = false;
            QualityPatches._samplingNoInspiration = true;

            // Deterministic seed
            int seed = Gen.HashCombineInt(pawn.thingIDNumber, Gen.HashCombineInt(builtDef?.shortHash ?? 0, N));

            Rand.PushState(seed);
            try
            {
                big = QualityEstimator.EstimateBaseline(pawn, SkillDefOf.Construction, builtDef, N);
            }
            finally
            {
                Rand.PopState();
                QualityPatches._samplingNoInspiration = false;
                QualityInsightsMod.Settings.enableCheat = cheatWas;
                QualityPatches._suppressInspirationSideEffects = false;
            }

            int boost = (uiLastInspired ? 2 : 0) + (uiLastProdSpec ? 1 : 0);
            var bigShift = boost > 0 ? ShiftTiers(big, boost) : big;

            var ui = GetChances(pawn);

            float maxAbs = 0f;
            var sb = new System.Text.StringBuilder();
            foreach (var q in TierOrder)
            {
                ui.TryGetValue(q, out var pUI);
                bigShift.TryGetValue(q, out var pBig);
                float d = Mathf.Abs(pUI - pBig);
                maxAbs = Mathf.Max(maxAbs, d);
                sb.AppendLine($"{q,-10} UI={pUI:P2}  big@{N}={pBig:P2}  Δ={d:P3}");
            }

            string header = $"[QI] CONSTRUCTION VALIDATION ({N} samples)  Pawn={pawn.LabelShortCap}  BuiltDef={(builtDef?.defName ?? "<null>")}";
            Log.Message($"{header}\n{sb}\nMax |Δ| = {maxAbs:P3}");
            try { GUIUtility.systemCopyBuffer = $"{header}\n{sb}\nMax |Δ| = {maxAbs:P3}"; } catch { }
            Messages.Message("[QI] Validation complete (details copied to clipboard).", MessageTypeDefOf.TaskCompletion, false);
        }


        private static void DrawPercentRow(Listing_Standard ls, string label, float p01)
        {
            var r = ls.GetRect(24f);
            Widgets.Label(r.LeftPart(0.5f), label);
            Widgets.Label(r.RightPart(0.5f), p01.ToString("P2"));
        }
    }
}
