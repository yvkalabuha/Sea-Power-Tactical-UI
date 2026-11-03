using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 phase B: the pick of a hostile ship on the map (the mode of "On the map"). Pure: the screen positions come from the shell. Only valid targets (no exclusion reason) are picked.
    internal sealed class StrikeTargetPick
    {
        public const float PickRadiusPx = 24f;

        public bool Active { get; private set; }
        /// <summary>Why the last pick ended without a ship ("rmb", "esc", "button", "closed", "group"); null while active or after a pick.</summary>
        public string LastReason { get; private set; }

        public void Start() { Active = true; LastReason = null; }

        public void Cancel(string reason)
        {
            if (!Active) return;
            Active = false; LastReason = string.IsNullOrEmpty(reason) ? "cancel" : reason;
        }

        /// <summary>The nearest valid ship within the radius, 0 = none. screenXY[i] belongs to rows[i] (null = not on the screen).</summary>
        public static int Nearest(IList<StrikeShipRow> rows, IList<float[]> screenXY, float mx, float my)
        {
            int best = 0; float bd = PickRadiusPx * PickRadiusPx;
            if (rows == null || screenXY == null) return 0;
            for (int i = 0; i < rows.Count && i < screenXY.Count; i++)
            {
                if (rows[i] == null || rows[i].Excl != null || screenXY[i] == null) continue;
                float dx = screenXY[i][0] - mx, dy = screenXY[i][1] - my, d = dx * dx + dy * dy;
                if (d <= bd) { bd = d; best = rows[i].Id; }
            }
            return best;
        }

        /// <summary>A left click: the id of the ship (the mode ends), 0 = nothing there (the mode goes on).</summary>
        public int OnClick(IList<StrikeShipRow> rows, IList<float[]> screenXY, float mx, float my)
        {
            if (!Active) return 0;
            int id = Nearest(rows, screenXY, mx, my);
            if (id != 0) { Active = false; LastReason = null; }
            return id;
        }

        public int Hover(IList<StrikeShipRow> rows, IList<float[]> screenXY, float mx, float my) { return Active ? Nearest(rows, screenXY, mx, my) : 0; }

        public static string SelfTest()
        {
            var rows = new List<StrikeShipRow> { new StrikeShipRow { Id = 1 }, new StrikeShipRow { Id = 2 }, new StrikeShipRow { Id = 3, Excl = "neutral" }, new StrikeShipRow { Id = 4 } };
            var xy = new List<float[]> { new[] { 100f, 100f }, new[] { 300f, 100f }, new[] { 200f, 200f }, null };
            var p = new StrikeTargetPick();
            if (p.Active || p.OnClick(rows, xy, 100, 100) != 0 || p.Hover(rows, xy, 100, 100) != 0) return "idle picks nothing";
            p.Start();
            if (!p.Active || p.OnClick(rows, xy, 200, 200) != 0 || !p.Active) return "a click on an excluded ship or on nothing goes on";
            if (p.Hover(rows, xy, 110, 105) != 1) return "hover";
            if (p.OnClick(rows, xy, 123, 100) != 1 || p.Active || p.LastReason != null) return "within 24 px";
            p.Start();
            if (p.OnClick(rows, xy, 125, 100) != 0) return "outside 24 px";
            if (p.OnClick(rows, xy, 300, 124) != 2) return "second ship at the border";
            p.Start(); p.Cancel("rmb");
            if (p.Active || p.LastReason != "rmb") return "cancel reason";
            p.Cancel("esc");
            if (p.LastReason != "rmb") return "cancel when idle keeps the reason";
            if (Nearest(rows, xy, 0, 0) != 0 || Nearest(null, xy, 0, 0) != 0) return "far / null";
            return null;
        }
    }
}
