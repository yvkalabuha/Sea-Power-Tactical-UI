// T-154 A2: the phases of drawing on the map (a rectangle by dragging, a point by a click) as a pure per-frame model.
// The live bug (0.20.0): the game mouse block (setMouseIsOverUIWindow) went on AFTER the press, so the map had already started to pan and never
// got the release. Here the block is wanted from Arm() (cursor outside the window) through the drag until one frame after the button is up.
using System;

namespace VoiceBotBridge
{
    internal enum DrawPhase { Idle, Armed, Dragging, Releasing }

    internal sealed class UiMapDrawInput
    {
        private bool _inside;
        public DrawPhase Phase { get; private set; }
        public double StartX, StartY, EndX, EndY;

        /// <summary>The game mouse must be blocked now.</summary>
        public bool WantBlock
        {
            get
            {
                switch (Phase)
                {
                    case DrawPhase.Armed: return !_inside;
                    case DrawPhase.Dragging: return true;
                    case DrawPhase.Releasing: return true;
                    default: return false;
                }
            }
        }

        public bool Active { get { return Phase == DrawPhase.Armed || Phase == DrawPhase.Dragging; } }

        /// <summary>Hard reset (window closed / mission exit): the caller releases the block itself.</summary>
        public void Reset() { Phase = DrawPhase.Idle; _inside = false; }

        public void Arm() { Phase = DrawPhase.Armed; _inside = false; }

        /// <summary>Leaves the mode. During a drag the block is kept until the button is released (Releasing).</summary>
        public void Cancel()
        {
            Phase = Phase == DrawPhase.Dragging ? DrawPhase.Releasing : DrawPhase.Idle;
        }

        /// <summary>T-178 Z2 (ADR-0077 §4): the right mouse button in a drawing mode. true = consumed: Armed / Dragging end like Cancel (the mouse block is kept until the
        /// button is released during a drag) and the caller restores the previous draft zone; false = not in a drawing mode, the click is not ours.</summary>
        public bool RightClick()
        {
            if (!Active) return false;
            Cancel();
            return true;
        }

        /// <summary>Per frame (Update). inside = the cursor is over the mod window; down = LMB held; downEdge = pressed this frame; esc = Esc pressed this frame.
        /// Returns "start", "finish", "cancel" or null. On "finish" StartX/StartY/EndX/EndY hold the drag.</summary>
        public string Tick(bool inside, bool down, bool downEdge, bool esc, double mx, double my)
        {
            _inside = inside;
            switch (Phase)
            {
                case DrawPhase.Armed:
                    if (esc) { Phase = DrawPhase.Idle; return "cancel"; }
                    if (downEdge && !inside)
                    {
                        Phase = DrawPhase.Dragging; StartX = EndX = mx; StartY = EndY = my;
                        return "start";
                    }
                    return null;
                case DrawPhase.Dragging:
                    EndX = mx; EndY = my;
                    if (esc) { Phase = DrawPhase.Releasing; return "cancel"; }
                    if (!down) { Phase = DrawPhase.Releasing; return "finish"; }
                    return null;
                case DrawPhase.Releasing:
                    if (!down) Phase = DrawPhase.Idle;
                    return null;
                default:
                    return null;
            }
        }

        public static string SelfTest()
        {
            var d = new UiMapDrawInput();
            if (d.WantBlock || d.Phase != DrawPhase.Idle) return "idle";
            if (d.Tick(false, true, true, false, 5, 5) != null || d.Phase != DrawPhase.Idle) return "idle ignores input";
            d.Arm();
            if (!d.WantBlock) return "armed: block before the press";
            if (d.Tick(false, false, false, false, 100, 100) != null || !d.WantBlock) return "armed idle frame";
            if (d.Tick(true, false, false, false, 10, 10) != null || d.WantBlock) return "armed inside the window: no block";
            if (d.Tick(true, true, true, false, 10, 10) != null || d.Phase != DrawPhase.Armed) return "press inside the window must not start";
            if (d.Tick(false, false, false, false, 100, 100) != null || !d.WantBlock) return "armed again outside";
            if (d.Tick(false, true, true, false, 100, 110) != "start" || d.Phase != DrawPhase.Dragging || d.StartX != 100 || d.StartY != 110) return "start";
            if (d.Tick(false, true, false, false, 150, 160) != null || d.EndX != 150 || !d.WantBlock) return "drag";
            if (d.Tick(false, false, false, false, 200, 210) != "finish" || d.EndX != 200 || d.EndY != 210) return "finish";
            if (!d.WantBlock || d.Phase != DrawPhase.Releasing) return "block stays one frame after the release";
            if (d.Tick(false, false, false, false, 200, 210) != null || d.WantBlock || d.Phase != DrawPhase.Idle) return "idle after the release frame";
            // Esc while armed
            d.Arm();
            if (d.Tick(false, false, false, true, 0, 0) != "cancel" || d.WantBlock || d.Phase != DrawPhase.Idle) return "esc armed";
            // Esc during a drag: block stays until the button is up
            d.Arm(); d.Tick(false, true, true, false, 1, 1);
            if (d.Tick(false, true, false, true, 2, 2) != "cancel" || !d.WantBlock) return "esc dragging keeps the block";
            if (d.Tick(false, true, false, false, 3, 3) != null || !d.WantBlock) return "button still held after esc";
            if (d.Tick(false, false, false, false, 3, 3) != null || d.WantBlock) return "released after esc";
            // external Cancel
            d.Arm(); d.Cancel();
            if (d.WantBlock || d.Phase != DrawPhase.Idle) return "cancel armed";
            d.Arm(); d.Tick(false, true, true, false, 1, 1); d.Cancel();
            if (d.Phase != DrawPhase.Releasing || !d.WantBlock) return "cancel dragging";
            d.Tick(false, false, false, false, 1, 1);
            if (d.Phase != DrawPhase.Idle) return "cancel dragging release";
            // T-178 Z2: the right button
            var rc = new UiMapDrawInput();
            if (rc.RightClick() || rc.Phase != DrawPhase.Idle) return "RMB in Idle is not consumed";
            rc.Arm();
            if (!rc.RightClick() || rc.Phase != DrawPhase.Idle || rc.WantBlock) return "RMB cancels Armed";
            if (rc.RightClick()) return "RMB after the cancel is not ours";
            rc.Arm(); rc.Tick(false, true, true, false, 1, 1);
            if (!rc.RightClick() || rc.Phase != DrawPhase.Releasing || !rc.WantBlock) return "RMB cancels Dragging, the block stays until the release";
            if (rc.RightClick()) return "RMB during Releasing is not a new cancel";
            rc.Tick(false, true, false, false, 2, 2);
            if (!rc.WantBlock) return "block while LMB is still held";
            rc.Tick(false, false, false, false, 2, 2);
            if (rc.WantBlock || rc.Phase != DrawPhase.Idle) return "released after RMB";
            return null;
        }
    }
}
