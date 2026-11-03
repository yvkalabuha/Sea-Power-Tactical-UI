using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-356 (ADR-0135): the world of the mission is decided (lifecycle order 0) before the order restores (sar 20 / asw 30 / cap 40 / strike 42) run. `puretest t356world`.</summary>
    internal static class WorldOrderTests
    {
        public static string Run()
        {
            var log = new List<string>();
            var m = new MissionLifecycle(log.Add);
            bool known = false;
            var seen = new List<string>();
            // subscribed in the order of the code base: the restorers first, "world" last; the order number decides
            m.Subscribe(40, "cap", e => seen.Add("cap:" + known), why => seen.Add("capL"));
            m.Subscribe(20, "sar", e => seen.Add("sar:" + known), why => seen.Add("sarL"));
            m.Subscribe(42, "strike", e => seen.Add("strike:" + known), why => seen.Add("strikeL"));
            m.Subscribe(30, "asw", e => seen.Add("asw:" + known), why => seen.Add("aswL"));
            m.Subscribe(0, "world", e => { known = true; seen.Add("world"); }, why => { known = false; seen.Add("worldL"); });
            var missionA = new object();
            m.Tick(missionA, () => "save.json", () => 5.0, 1.0);
            if (string.Join(",", seen) != "world,sar:True,asw:True,cap:True,strike:True") return "enter order: " + string.Join(",", seen);
            seen.Clear();
            var missionB = new object();   // a switch: Left descending ends with world, Entered starts with world
            m.Tick(missionB, () => null, () => 0.0, 2.0);
            if (string.Join(",", seen) != "strikeL,capL,aswL,sarL,worldL,world,sar:True,asw:True,cap:True,strike:True") return "switch order: " + string.Join(",", seen);
            return null;
        }
    }
}
