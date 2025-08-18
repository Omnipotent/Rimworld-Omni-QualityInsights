using System;
using System.Linq;
using System.Text;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using QualityInsights.Logging;

namespace QualityInsights.UI
{
    public class MainTabWindow_QualityLog : MainTabWindow
    {
        private Vector2 scroll;
        private string search = string.Empty;
        private QualityCategory? filterQuality = null;

        // NEW: skill filter (string because QualityLogEntry.skillDef is a string defName)
        private string? filterSkill = null;

        public override Vector2 RequestedTabSize => new(980f, 640f);

        public override void DoWindowContents(Rect rect)
        {
            var comp = Current.Game.GetComponent<QualityLogComponent>();
            var rows = comp.entries.AsEnumerable();

            // ========= Filters header =========
            // Layout:
            // [ Search: ________ ] [ Qualities ▼ ] [ Skills ▼ ]                [Export] [Reload]
            var headerRect = new Rect(0, 0, rect.width, 32f);

            // Search label + box
            Widgets.Label(headerRect.LeftPart(0.15f), "Search:");
            search = Widgets.TextField(headerRect.LeftPart(0.35f).RightPart(0.85f), search);

            // Dropdown widths
            const float dropW = 180f;
            float x = rect.width * 0.35f + 8f;

            // Quality dropdown
            var qualityBtn = new Rect(x, 0, dropW, 32f);
            if (Widgets.ButtonText(qualityBtn, filterQuality?.ToString() ?? "All qualities"))
            {
                var opts = new List<FloatMenuOption>();

                // concrete qualities
                foreach (QualityCategory q in Enum.GetValues(typeof(QualityCategory)))
                {
                    var localQ = q;
                    opts.Add(new FloatMenuOption(localQ.ToString(), () => filterQuality = localQ));
                }

                // reset
                opts.Add(new FloatMenuOption("All", () => filterQuality = null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            // Skill dropdown (build the set from current entries so it stays mod-friendly)
            x += dropW + 8f;
            var skillBtn = new Rect(x, 0, dropW, 32f);

            // Collect distinct skills present in the log
            var skillsPresent = comp.entries
                .Select(e => e.skillDef ?? "Unknown")
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (Widgets.ButtonText(skillBtn, filterSkill ?? "All skills"))
            {
                var opts = new List<FloatMenuOption>();

                foreach (var sd in skillsPresent)
                {
                    var local = sd;
                    opts.Add(new FloatMenuOption(local, () => filterSkill = local));
                }

                opts.Add(new FloatMenuOption("All", () => filterSkill = null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            // Export / Reload
            if (Widgets.ButtonText(new Rect(rect.width - 220f, 0, 100f, 32f), "QI_ExportCSV".Translate())) ExportCSV(comp);
            if (Widgets.ButtonText(new Rect(rect.width - 110f, 0, 110f, 32f), "Reload")) { /* No-op: relies on live list */ }

            // ========= Apply filters =========
            if (!string.IsNullOrWhiteSpace(search))
            {
                rows = rows.Where(e =>
                    (!string.IsNullOrEmpty(e.pawnName) && e.pawnName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(e.thingDef) && e.thingDef.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            if (filterQuality.HasValue)
                rows = rows.Where(e => e.quality == filterQuality);

            if (!string.IsNullOrEmpty(filterSkill))
                rows = rows.Where(e => string.Equals(e.skillDef, filterSkill, StringComparison.OrdinalIgnoreCase));

            var list = rows.OrderByDescending(e => e.gameTicks).ToList();

            // ========= Table =========
            var outRect  = new Rect(0, 40f, rect.width, rect.height - 40f);
            var viewRect = new Rect(0, 0, outRect.width - 16f, list.Count * 28f + 8f);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            float y = 0f;
            foreach (var e in list)
            {
                var r = new Rect(0, y, viewRect.width, 26f);
                if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);

                // Safeties: avoid nulls
                string pawn = e.pawnName ?? "Unknown";
                string skill = e.skillDef ?? "Unknown";
                int lvl = e.skillLevelAtFinish;
                string stuff = string.IsNullOrEmpty(e.stuffDef) ? string.Empty : $"[{e.stuffDef}]";
                string tags =
                    (e.inspiredCreativity ? " | Inspired" : string.Empty) +
                    (e.productionSpecialist ? " | ProdSpec" : string.Empty);

                Widgets.Label(r, $"{e.TimeAgoString} | {pawn} ({skill} {lvl}) ➜ {e.quality} | {e.thingDef}{stuff}{tags}");
                y += 28f;
            }

            Widgets.EndScrollView();
        }

        private static void ExportCSV(QualityLogComponent comp)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Ticks,TimeAgo,Pawn,Skill,Level,Quality,Thing,Stuff,Inspired,ProductionSpecialist");
            foreach (var e in comp.entries.OrderBy(x => x.gameTicks))
            {
                sb.AppendLine(string.Join(",",
                    e.gameTicks,
                    e.TimeAgoString,
                    Escape(e.pawnName),
                    Escape(e.skillDef),
                    e.skillLevelAtFinish,
                    e.quality,
                    Escape(e.thingDef),
                    Escape(e.stuffDef ?? string.Empty),
                    e.inspiredCreativity,
                    e.productionSpecialist));
            }

            var dir = GenFilePaths.SaveDataFolderPath;
            var path = Path.Combine(dir, "QualityInsights_Log.csv");
            File.WriteAllText(path, sb.ToString());
            Messages.Message($"Exported to {path}", MessageTypeDefOf.TaskCompletion, false);
        }

        private static string Escape(string s) => '"' + (s ?? string.Empty).Replace("\"", "\"\"") + '"';
    }
}
