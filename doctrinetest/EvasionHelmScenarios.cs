using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-279 (ADR-0112): synthetic replay of N1 of run 0.30.20 (log_live.txt 09:12:27-09:14:03): the ship turned 117 -> 093, the game entered PerformingAirOps at 09:12:35 and held 093
    /// while the evasion put 020 / 010 / 000 / 350. The helm must take the airops mode from the 8th second and the divergence must be flagged within 29 s (09:12:56).
    /// The second part is a normal turn 117 -> 020 at 3 deg/s: no "stuck".
    /// </summary>
    internal static class EvasionHelmScenarios
    {
        private static double CmdAt(int t) { return t < 30 ? 20 : t < 55 ? 10 : t < 80 ? 0 : 350; }

        public static string SelfTest()
        {
            var w = new EvHelmWatch();
            int airopsAt = -1, stuckAt = -1;
            var wNoFlag = new EvHelmWatch();
            int stuckNoFlag = -1;
            for (int t = 0; t <= 96; t++)
            {
                double hdg = t <= 8 ? 117 - 3.0 * t : 93;
                bool air = EvasionHelmMath.AirOps(t >= 8 ? "PerformingAirOps" : "Default", null);
                string mode = EvasionHelmMath.Mode(air, air ? "air_ops" : "player", false, false);
                if (mode == "airops" && airopsAt < 0) airopsAt = t;
                if (EvasionHelmMath.Diverged(w, t, CmdAt(t), hdg) && stuckAt < 0) stuckAt = t;
                if (EvasionHelmMath.Diverged(wNoFlag, t, CmdAt(t), hdg) && stuckNoFlag < 0) stuckNoFlag = t;
            }
            if (airopsAt != 8) return "N1 airops_at_s=" + airopsAt;
            if (stuckAt < 0 || stuckAt > 29) return "N1 stuck_at_s=" + stuckAt;
            if (stuckNoFlag != stuckAt) return "N1 stuck depends on the Air Ops flag";
            Console.WriteLine("t279 scenario N1 airops_at_s=" + airopsAt + " stuck_at_s=" + stuckAt);

            var turn = new EvHelmWatch();
            for (int t = 0; t <= 90; t++)
            {
                double hdg = Math.Max(20, 117 - 3.0 * t);
                if (EvasionHelmMath.Diverged(turn, t, 20, hdg)) return "normal turn flagged stuck at " + t;
            }
            Console.WriteLine("t279 scenario turn no_stuck=1");
            return Telegraph283();
        }

        /// <summary>
        /// T-283: replay of N1 of run 0.30.21: evasion telegraph 5, Air Ops at t=10 (the game remembers 5), restore_control at t=20 puts the player's 3, Air Ops end at t=60,
        /// the game's onExit sets 5. The follower logic must put 3 back once; a change by the player during Air Ops (to 2) wins over the old 3.
        /// </summary>
        private static string Telegraph283()
        {
            foreach (int playerChange in new[] { -1, 2 })
            {
                int? player = 3, seen = 3, tg = 3;   // after restore_control
                int? fixedTo = null;
                for (int t = 20; t <= 62; t++)
                {
                    bool air = t < 60;
                    if (air && playerChange >= 0 && t == 40) tg = playerChange;
                    if (!air && t == 60) tg = 5;   // PerformingAirOps.onExit: setTelegraph(_previousTelegraph)
                    if (air) player = EvasionHelmMath.TgObserve(player, ref seen, tg);
                    else if (fixedTo == null)
                    {
                        fixedTo = EvasionHelmMath.ExitFix(player, tg);
                        if (fixedTo.HasValue) tg = fixedTo;
                    }
                }
                int want = playerChange >= 0 ? playerChange : 3;
                if (tg != want) return "T283 telegraph after Air Ops exit " + tg + " want " + want;
            }
            Console.WriteLine("t283 scenario telegraph_after_exit=player");
            return Telegraph293();
        }

        /// <summary>
        /// T-293: run 0.30.23 №21. Player telegraph 2, Air Ops active: the evasion's writes (5, slow 1) are deferred, only noted; Air Ops end (onExit sets 2),
        /// the slow tick re-sets 1. With the helper (noting) restore gives 2 back and the second snapshot is 2; without noting it was player_changed (the bug).
        /// </summary>
        private static string Telegraph293()
        {
            foreach (bool noting in new[] { true, false })
            {
                var cm = new ControlMemory();
                var u = new object();
                int tg = 2;
                cm.RememberField(u, "telegraph", "2");
                cm.NoteFieldSet(u, "telegraph", tg.ToString());   // deferred 5 in Air Ops: live noted
                cm.NoteFieldSet(u, "telegraph", tg.ToString());   // deferred slow 1
                tg = 2;                                            // Air Ops end: onExit
                tg = 1;                                            // slow tick re-sets 1
                if (noting) cm.NoteFieldSet(u, "telegraph", tg.ToString());
                var e = cm.Peek(u);
                int? noted = int.Parse(e.Fields["telegraph"].SetByMod);
                if (noting && EvasionHelmMath.ExitInsideIsMod(true, false, tg, noted, 1)) return "T293 exit note after noted re-set";
                string d = ControlMemory.DecideField(e, "telegraph", tg.ToString());
                if (noting)
                {
                    if (d != "restore") return "T293 decide " + d;
                    tg = int.Parse(e.Fields["telegraph"].Saved);
                    cm.Take(u);
                    if (tg != 2) return "T293 telegraph after restore " + tg;
                    cm.RememberField(u, "telegraph", tg.ToString());
                    if (cm.Peek(u).Fields["telegraph"].Saved != "2") return "T293 second snapshot";
                }
                else if (d != "player_changed") return "T293 negative control " + d;
            }
            // race: Air Ops entered at 5, the game's onExit sets 5 on the last tick; noted stays 2 -> safety net notes it
            {
                var cm = new ControlMemory();
                var u = new object();
                cm.RememberField(u, "telegraph", "2");
                cm.NoteFieldSet(u, "telegraph", "2");
                int tg = 5;
                var e = cm.Peek(u);
                if (!EvasionHelmMath.ExitInsideIsMod(true, false, tg, int.Parse(e.Fields["telegraph"].SetByMod), null)) return "T293 race not detected";
                cm.NoteFieldSet(u, "telegraph", tg.ToString());
                if (ControlMemory.DecideField(e, "telegraph", tg.ToString()) != "restore") return "T293 race decide";
            }
            Console.WriteLine("t293 scenario telegraph_after_slow_airops=player second_snapshot=player");
            return null;
        }
    }
}
