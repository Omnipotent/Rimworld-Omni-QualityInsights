using UnityEngine;
using Verse;
using QualityInsights.Logging;

namespace QualityInsights.UI
{
    public class QualityLogWindow : Window
    {
        public override Vector2 InitialSize => new(900f, 600f);
        private Vector2 scroll;

        public QualityLogWindow() { draggable = true; doCloseX = true; }

        public override void DoWindowContents(Rect inRect)
        {
            var comp = Current.Game.GetComponent<QualityLogComponent>();
            var rows = comp.entries;

            var outRect = new Rect(inRect.x, inRect.y + 10f, inRect.width, inRect.height - 20f);
            var viewRect = new Rect(0, 0, outRect.width - 16f, rows.Count * 26f + 10f);

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float y = 0;
            foreach (var e in rows)
            {
                var r = new Rect(0, y, viewRect.width, 24f);
                if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
                Widgets.Label(r, $"{e.TimeAgoString} | {e.pawnName} ({e.skillDef} {e.skillLevelAtFinish}) ➜ {e.quality} | {e.thingDef}{(string.IsNullOrEmpty(e.stuffDef) ? "" : $"[{e.stuffDef}]")} {(e.inspiredCreativity ? "| Inspired" : "")} {(e.productionSpecialist ? "| ProdSpec" : "")}");
                y += 26f;
            }
            Widgets.EndScrollView();
        }
    }
}
