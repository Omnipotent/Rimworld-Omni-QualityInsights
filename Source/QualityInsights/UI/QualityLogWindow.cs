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
        private Vector2 _dragStartMouseScreen;
        private Vector2 _dragStartWinPos;

        // height of our internal draggable strip
        private static float DragBarH => Mathf.Ceil(Text.LineHeight) + 6f; // line height + padding
        public override Vector2 InitialSize => new(1100f, 720f);
        private int _lastW, _lastH;
        protected override float Margin => 6f;   // default is ~18

        public QualityLogWindow()
        {
            doCloseX = true;

            // IMPORTANT: keep RimWorld’s built-in dragging OFF so it won’t compete with the splitter.
            draggable = false;

            resizeable = true;
            absorbInputAroundWindow = _draggingWindow;
            preventCameraMotion = _draggingWindow;
        }

        // --- Register/unregister so the main tab knows whether a floating window is open ---
        public override void PreOpen()
        {
            base.PreOpen();
            MainTabWindow_QualityLog.RegisterFloating(this);

            var maxW = Verse.UI.screenWidth  * 0.95f;
            var maxH = Verse.UI.screenHeight * 0.95f;
            windowRect.width  = Mathf.Min(windowRect.width,  maxW);
            windowRect.height = Mathf.Min(windowRect.height, maxH);
            windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, Verse.UI.screenWidth  - windowRect.width));
            windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, Verse.UI.screenHeight - windowRect.height));
        }

        public override void PostClose()
        {
            base.PostClose();
            MainTabWindow_QualityLog.UnregisterFloating(this);
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (_lastW != Verse.UI.screenWidth || _lastH != Verse.UI.screenHeight)
            {
                _lastW = Verse.UI.screenWidth; _lastH = Verse.UI.screenHeight;
                windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, _lastW - windowRect.width));
                windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, _lastH - windowRect.height));
            }

            // calm down flicker by pausing passthrough & camera only while dragging
            absorbInputAroundWindow = _draggingWindow || _tab.IsDraggingSplitter;
            preventCameraMotion     = _draggingWindow || _tab.IsDraggingSplitter;

            // --- NEW: make the entire top margin + our bar draggable ---
            float topMargin = inRect.y; // Window’s built-in margin (usually ~18px)
            var visualBar   = new Rect(inRect.x, inRect.y, inRect.width, DragBarH);                    // what we draw
            var dragHit     = new Rect(0f, 0f, windowRect.width, topMargin + DragBarH);                // what we grab

            Widgets.DrawLightHighlight(visualBar);

            // A small “no-drag” zone where the close X sits, so we don’t steal its clicks
            var noDragTopRight = new Rect(windowRect.width - 40f, 0f, 40f, 40f);

            var ev = Event.current;

            if (!(_tab.IsDraggingSplitter || _tab.IsHoveringSplitter))
            {
                if (ev.type == EventType.MouseDown && ev.button == 0 && dragHit.Contains(ev.mousePosition) && !noDragTopRight.Contains(ev.mousePosition))
                {
                    _draggingWindow = true;
                    _dragStartMouseScreen = Verse.UI.MousePositionOnUIInverted;
                    _dragStartWinPos      = windowRect.position;
                    ev.Use();
                }
                else if (ev.type == EventType.MouseDrag && _draggingWindow && ev.button == 0)
                {
                    Vector2 curScreen = Verse.UI.MousePositionOnUIInverted;
                    Vector2 delta     = curScreen - _dragStartMouseScreen;
                    windowRect.position = _dragStartWinPos + delta;
                    ev.Use();
                }
                else if ((ev.type == EventType.MouseUp || ev.rawType == EventType.MouseUp) && _draggingWindow)
                {
                    _draggingWindow = false;
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
