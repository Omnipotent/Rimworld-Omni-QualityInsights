// Source\QualityInsights\UI\QualityLogWindow.cs
using UnityEngine;
using Verse;

namespace QualityInsights.UI
{
    public class QualityLogWindow : Window
    {
        private readonly MainTabWindow_QualityLog _tab = new();

        // custom window-drag state (we’ll manage movement ourselves)
        private bool _draggingWindow;
        private Vector2 _dragStartMouse;
        private Vector2 _dragStartWinPos;

        // height of our internal draggable strip
        private const float DragBarH = 22f;

        public override Vector2 InitialSize => new(1100f, 720f);

        public QualityLogWindow()
        {
            doCloseX = true;

            // IMPORTANT: keep RimWorld’s built-in dragging OFF so it won’t compete with the splitter.
            draggable = false;

            resizeable = true;
            absorbInputAroundWindow = _draggingWindow;
            preventCameraMotion     = _draggingWindow;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // calm down flicker by pausing passthrough & camera only while dragging
            absorbInputAroundWindow = _draggingWindow;
            preventCameraMotion     = _draggingWindow;

            var dragBar = new Rect(inRect.x, inRect.y, inRect.width, DragBarH);
            Widgets.DrawLightHighlight(dragBar);

            if (!_tab.IsDraggingSplitter)
            {
                var ev = Event.current;
                if (ev.type == EventType.MouseDown && ev.button == 0 && dragBar.Contains(ev.mousePosition))
                {
                    _draggingWindow = true;
                    _dragStartMouse = ev.mousePosition;       // local coords
                    _dragStartWinPos = windowRect.position;   // absolute window pos
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDrag && _draggingWindow && ev.button == 0)
                {
                    Vector2 delta = ev.mousePosition - _dragStartMouse; // local delta
                    windowRect.position = _dragStartWinPos + delta;      // no clamp here
                    ev.Use();
                }
                else if ((ev.type == EventType.MouseUp || ev.rawType == EventType.MouseUp) && _draggingWindow)
                {
                    _draggingWindow = false;

                    // Clamp once at the end to keep it on-screen
                    float maxX = Verse.UI.screenWidth  - windowRect.width;
                    float maxY = Verse.UI.screenHeight - windowRect.height;
                    windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, maxX));
                    windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, maxY));

                    ev.Use();
                }
            }

            // content group unchanged...
            var content = new Rect(inRect.x, inRect.y + DragBarH, inRect.width, inRect.height - DragBarH);
            GUI.BeginGroup(content);
            try { _tab.DoWindowContents(new Rect(0f, 0f, content.width, content.height)); }
            finally { GUI.EndGroup(); }
        }
    }
}
