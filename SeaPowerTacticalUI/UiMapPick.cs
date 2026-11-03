// T-178 Z3 (ADR-0077 §3): the phases of picking an own ship on the map (pattern SmartAttackPick). Start -> the cursor shows a hint, the nearest own ship within 24 px is
// ringed (Hover); a left click assigns it (OnClick returns the uid and the mode ends); the right button, Esc, "Cancel", closing the window or a change of the group / mode call Cancel
// (nothing changes). No timeout. The Unity side (cursor, ring, mouse block) is the window's; this class is the pure state.
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal sealed class UiMapPick
    {
        public bool Active { get; private set; }
        public string Window { get; private set; }
        /// <summary>Why the last pick ended without a ship ("rmb", "esc", "button", "closed", "group"); null while active or after a pick.</summary>
        public string LastReason { get; private set; }

        public void Start(string window) { Active = true; Window = window; LastReason = null; }

        public void Cancel(string reason)
        {
            if (!Active) return;
            Active = false; LastReason = string.IsNullOrEmpty(reason) ? "cancel" : reason;
        }

        /// <summary>A left click at (mx, my): the uid of the nearest own ship within PickRadiusPx (the mode ends), 0 = no own ship there (the mode goes on).</summary>
        public int OnClick(IList<ShipItem> items, IList<float[]> screenXY, float mx, float my)
        {
            if (!Active) return 0;
            int uid = ShipPickModel.Nearest(items, screenXY, mx, my, ShipPickModel.PickRadiusPx);
            if (uid != 0) { Active = false; LastReason = null; }
            return uid;
        }

        /// <summary>The ship to ring under the cursor, 0 = none (also while not active).</summary>
        public int Hover(IList<ShipItem> items, IList<float[]> screenXY, float mx, float my)
        {
            return Active ? ShipPickModel.Nearest(items, screenXY, mx, my, ShipPickModel.PickRadiusPx) : 0;
        }

        public static string SelfTest()
        {
            var items = new List<ShipItem>
            {
                new ShipItem { Uid = 1, Own = true, Name = "A" }, new ShipItem { Uid = 2, Own = true, Name = "B" }, new ShipItem { Uid = 7, Own = false, Name = "Foreign" },
            };
            var xy = new List<float[]> { new[] { 100f, 100f }, new[] { 300f, 100f }, new[] { 200f, 200f } };
            var p = new UiMapPick();
            if (p.Active || p.OnClick(items, xy, 100, 100) != 0 || p.Hover(items, xy, 100, 100) != 0) return "idle picks nothing";
            p.Start("asw");
            if (!p.Active || p.Window != "asw") return "start";
            if (p.Hover(items, xy, 110, 108) != 1) return "hover rings the nearest own ship";
            if (p.Hover(items, xy, 200, 205) != 0) return "a foreign ship is not ringed";
            if (p.OnClick(items, xy, 200, 200) != 0 || !p.Active) return "a click on a foreign ship / empty sea: no pick, the mode goes on";
            if (p.OnClick(items, xy, 600, 600) != 0 || !p.Active) return "a click far away: the mode goes on";
            if (p.OnClick(items, xy, 290, 110) != 2 || p.Active || p.LastReason != null) return "a click near an own ship assigns it and ends the mode";
            if (p.OnClick(items, xy, 100, 100) != 0) return "after the pick nothing more is picked";
            // cancel paths
            foreach (string why in new[] { "rmb", "esc", "button", "closed", "group" })
            {
                p.Start("cap");
                p.Cancel(why);
                if (p.Active || p.LastReason != why) return "cancel by " + why;
            }
            p.Start("asw"); p.Cancel(null);
            if (p.LastReason != "cancel") return "default reason";
            p.Cancel("again");
            if (p.LastReason != "cancel") return "a second cancel keeps the first reason";
            p.Start("asw");
            if (p.LastReason != null) return "start clears the reason";
            return null;
        }
    }
}
