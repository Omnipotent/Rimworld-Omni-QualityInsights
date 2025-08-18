using System;
using System.Linq;
using System.Text;
using System.IO;
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

        public override Vector2 RequestedTabSize => new(980f, 640f);

        public override void DoWindowContents(Rect rect)
        {
            var comp = Current.Game.GetComponent<QualityLogComponent>();
            var rows = comp.entries.AsEnumerable();

            // Filters
            var he = new Rect(0, 0, rect.width, 32f);
            // Widgets.TextFieldNumericLabeled(he.LeftPart(0.35f), "Search:", ref search, ref _); // simple text box label
            Widgets.Label(he.LeftPart(0.15f), "Search:");
            search = Widgets.TextField(he.LeftPart(0.35f).RightPart(0.85f), search);
            if (Widgets.ButtonText(new Rect(he.x + rect.width * 0.35f + 8f, 0, 180f, 32f), filterQuality?.ToString() ?? "All qualities"))
            {
                var fl = new FloatMenu(
                    Enum.GetValues(typeof(QualityCategory))
                        .Cast<QualityCategory>()
                        .Select(q => new FloatMenuOption(q.ToString(), () => filterQuality = q))
                        .Concat(new[] { new FloatMenuOption("All", () => filterQuality = null) })
                        .ToList()
                );
                Find.WindowStack.Add(fl);
            }
            if (Widgets.ButtonText(new Rect(rect.width - 220f, 0, 100f, 32f), "QI_ExportCSV".Translate())) ExportCSV(comp);
            if (Widgets.ButtonText(new Rect(rect.width - 110f, 0, 110f, 32f), "Reload")) { /* No-op: relies on live list*/ }

            rows = rows.Where(e => string.IsNullOrEmpty(search) ||
                                   e.pawnName.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   e.thingDef.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (filterQuality.HasValue) rows = rows.Where(e => e.quality == filterQuality);

            var list = rows.OrderByDescending(e => e.gameTicks).ToList();

            var outRect = new Rect(0, 40f, rect.width, rect.height - 40f);
            var viewRect = new Rect(0, 0, outRect.width - 16f, list.Count * 28f + 8f);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);

            float y = 0f;
            foreach (var e in list)
            {
                var r = new Rect(0, y, viewRect.width, 26f);
                if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
                Widgets.Label(r, $"{e.TimeAgoString} | {e.pawnName} ({e.skillDef} {e.skillLevelAtFinish}) ➜ {e.quality} | {e.thingDef}{(string.IsNullOrEmpty(e.stuffDef) ? string.Empty : $"[{e.stuffDef}]")} {(e.inspiredCreativity ? "| Inspired" : string.Empty)}{(e.productionSpecialist ? "| ProdSpec" : string.Empty)}");
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

        private static string Escape(string s) => '"' + s.Replace("\"", "\"\"") + '"';
    }
}
