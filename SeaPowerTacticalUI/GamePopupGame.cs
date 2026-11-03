using System;
using System.Collections;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>T-240: screen rectangles of the game's open popups (the context menu of the main view; the map hamburger is in MapMenuWindows) for UiWindowPrefs.Popup.</summary>
    internal sealed partial class GameApi
    {
        /// <summary>The Noesis element's rectangle {x, y, w, h} in screen pixels (y down), from PointToScreen(0,0) and PointToScreen(w,h) / NoesisPxScale. false when it is not a visual or a member is missing.</summary>
        internal bool PopupElementRect(object element, out double[] rect)
        {
            rect = null;
            try
            {
                var toScreen = Member(CoreSpecs.PointToScreen) as MethodInfo;
                if (toScreen == null || element == null || !toScreen.DeclaringType.IsInstanceOfType(element)) return false;
                if (!Present(MapMenuSpecs.ElementWidth) || !Present(MapMenuSpecs.ElementHeight)) return false;
                double w = D2(Get(element, MapMenuSpecs.ElementWidth)), h = D2(Get(element, MapMenuSpecs.ElementHeight));
                if (!(w > 0) || !(h > 0)) return false;
                var tPoint = toScreen.GetParameters()[0].ParameterType;
                double x0, y0, x1, y1;
                if (!PopupPoint(element, toScreen, tPoint, 0, 0, out x0, out y0) || !PopupPoint(element, toScreen, tPoint, w, h, out x1, out y1)) return false;
                rect = new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0) };
                return rect[2] > 1 && rect[3] > 1;
            }
            catch { rect = null; return false; }
        }

        private bool PopupPoint(object element, MethodInfo toScreen, Type tPoint, double x, double y, out double sx, out double sy)
        {
            var r = toScreen.Invoke(element, new[] { Activator.CreateInstance(tPoint, new object[] { (float)x, (float)y }) });
            double nx = D2(Get(r, RadarSpecs.PointX)), ny = D2(Get(r, RadarSpecs.PointY));
            RadialMath.MapPointFromNoesis(nx, ny, NoesisPxScale, out sx, out sy);
            return !double.IsNaN(sx) && !double.IsNaN(sy) && !double.IsInfinity(sx) && !double.IsInfinity(sy);
        }

        private double[] _ctxRect;
        private double _ctxRectAt = -1;

        /// <summary>T-240: the context menu (RMB) of the main game view: open flag and its screen rect, null rect if unknown. Without capability radial_lmb: false.</summary>
        internal bool ContextMenuOpen(out double[] rect, double now = 0)
        {
            rect = null;
            try
            {
                if (!Capabilities.Contains("radial_lmb")) return false;
                var mv = GetStatic(RadialSpecs.MainViewInstance);
                var cm = Get(Get(mv, RadialSpecs.ContextMenuCanvas), RadialSpecs.ContextMenu);
                if (cm == null || !BoolOf2(Get(cm, RadialSpecs.MenuIsOpen))) { _ctxRect = null; _ctxRectAt = -1; return false; }
                if (_ctxRectAt < 0 || now - _ctxRectAt >= 0.1)
                {
                    double[] r;
                    _ctxRect = PopupElementRect(cm, out r) ? r : null;
                    _ctxRectAt = now;
                }
                rect = _ctxRect;
                return true;
            }
            catch { rect = null; return false; }
        }
    }
}
