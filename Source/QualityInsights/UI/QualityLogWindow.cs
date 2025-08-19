// Source\QualityInsights\UI\QualityLogWindow.cs
using UnityEngine;
using Verse;

namespace QualityInsights.UI
{
    public class QualityLogWindow : Window
    {
        private readonly MainTabWindow_QualityLog _tab = new();
        public override Vector2 InitialSize => new(1100f, 720f);

        public QualityLogWindow()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;

            // Let the map handle input OUTSIDE the window.
            absorbInputAroundWindow = false;

            // Don’t freeze camera movement globally.
            preventCameraMotion = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            resizeable = true;
            _tab.DoWindowContents(inRect);
        }
    }
}
