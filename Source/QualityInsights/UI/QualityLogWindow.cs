// Source\QualityInsights\UI\QualityLogWindow.cs
using UnityEngine;
using Verse;

namespace QualityInsights.UI
{
    public class QualityLogWindow : Window
    {
        private readonly MainTabWindow_QualityLog _tab = new();  // reuse the tab UI

        public override Vector2 InitialSize => new(1100f, 720f);

        public QualityLogWindow()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;

            absorbInputAroundWindow = false; // <-- let the map receive input outside the window
            // optional:
            // closeOnClickedOutside = true; // click outside to close
        }

        public override void DoWindowContents(Rect inRect)
        {
            _tab.DoWindowContents(inRect); // same table UI, columns, filters, etc.
        }
    }
}
