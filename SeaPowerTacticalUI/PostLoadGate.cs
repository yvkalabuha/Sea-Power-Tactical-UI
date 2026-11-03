// T-211 (ADR-0088): "the world after a save load has settled". Pure (no Unity).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// The load screen is gone, own units are listed and their signature stayed the same for SettleGameS of game time over at least SettleSamples samples.
    /// Until then the air tick does not run and the deferred restore of the CAP/ASW groups waits (the game lists are empty in the load frame).
    /// </summary>
    internal sealed class PostLoadGate
    {
        public const double SettleGameS = 2.0;    // the signature is unchanged this long (game time)
        public const int SettleSamples = 3;        // ... and over at least this many samples
        public const double SampleRealS = 0.25;    // sampling cadence of the caller (real time)
        public const double MaxGameS = 30.0;       // game time after the load screen: open anyway (State "timeout")
        public const double MaxRealS = 300.0;      // real time since Arm: open anyway ("timeout_real"; a broken load-screen sign)
        public const double PausedSettleRealS = 3.0;   // T-217 (ADR-0089): on pause the signature stayed this long (real time): "open_paused_settled"
        public const int PausedFoundSamples = 2;       // T-217: on pause all expected members listed on this many samples in a row: "open_paused"
        public const int PausedFoundSamplesLoadScreen = 8;   // T-232: the same under a load camera that is still on (2 s at SampleRealS): "open_paused_ls"
        // T-248: wait_s counts from Arm, i.e. includes the game's own load (members appeared at 10.1-10.3 s in run 10); the gate itself adds PausedFoundSamplesLoadScreen * SampleRealS = 2 s.
        // So the checklist limit for open_paused_ls is 15 s from Arm, not 10 s.
        public const double StatusEveryRealS = 5.0;    // T-232: period of the "post-load gate wait" status line

        private bool _armed;
        private double _armRealT, _firstOffGameT = double.NaN, _baseGameT = double.NaN, _lastGameT = double.NaN;
        private int _samples;
        private string _sig;
        private double _baseRealT = double.NaN, _standSinceRealT = double.NaN, _lastObsGameT = double.NaN;
        private int _foundStreak, _lsFoundStreak;
        private double _lastStatusRealT = double.NaN;
        private int _lastUnits;
        private string _lastPaused = "?";

        public bool Armed { get { return _armed; } }
        public bool Open { get { return !_armed; } }
        public string State { get; private set; } = "idle";
        public double WaitedRealS { get; private set; }
        /// <summary>The last sample was taken on pause (diagnostics).</summary>
        public bool Paused { get; private set; }
        public int MembersWanted { get; private set; }
        public int MembersFound { get; private set; }
        /// <summary>T-232: the loadScreen flag of the last sample (for the status line).</summary>
        public bool LastLoadScreen { get; private set; }

        public void Arm(double realT)
        {
            _armed = true; _armRealT = realT; WaitedRealS = 0; State = "load_screen"; _lastStatusRealT = double.NaN; _lsFoundStreak = 0; LastLoadScreen = false; _lastUnits = 0; _lastPaused = "?";
            _firstOffGameT = double.NaN; ResetBase();
        }

        public void Disarm()
        {
            _armed = false; State = "idle"; _firstOffGameT = double.NaN; ResetBase();
        }

        private void ResetBase()
        {
            _baseGameT = double.NaN; _lastGameT = double.NaN; _samples = 0; _sig = null;
            _baseRealT = double.NaN; _standSinceRealT = double.NaN; _lastObsGameT = double.NaN; _foundStreak = 0; _lsFoundStreak = 0; Paused = false;
        }

        /// <summary>Counts distinct positive uids of wanted and how many of them are in live.</summary>
        public static void CountFound(IEnumerable<int> wanted, ICollection<int> live, out int w, out int f)
        {
            w = 0; f = 0;
            if (wanted == null) return;
            var seen = new HashSet<int>();
            foreach (int u in wanted)
            {
                if (u <= 0 || !seen.Add(u)) continue;
                w++;
                if (live != null && live.Contains(u)) f++;
            }
        }

        /// <summary>One sample without the pause data (the game time decides). true exactly once: the sample that opens the gate.</summary>
        public bool Step(bool loadScreen, int ownUnits, string sig, double gameT, double realT)
        {
            return Step(loadScreen, ownUnits, sig, gameT, realT, false, -1, 0);
        }

        /// <summary>
        /// T-217 (ADR-0089). paused: true/false = the game flag; null = unknown, then paused = the game time stood for PausedSettleRealS of real time.
        /// membersWanted &lt; 0 = unknown (as 0). true exactly once: the sample that opens the gate.
        /// </summary>
        public bool Step(bool loadScreen, int ownUnits, string sig, double gameT, double realT, bool? paused, int membersWanted, int membersFound)
        {
            if (!_armed) return false;
            MembersWanted = membersWanted; MembersFound = membersFound;
            WaitedRealS = realT - _armRealT;
            if (WaitedRealS >= MaxRealS) return Finish("timeout_real");
            LastLoadScreen = loadScreen; _lastUnits = ownUnits; _lastPaused = paused.HasValue ? (paused.Value ? "1" : "0") : "?";
            if (loadScreen)
            {
                // T-232: the load camera can stay on although the world is there; on pause with every expected member listed for 2 s the gate opens anyway
                int streak = _lsFoundStreak;
                _firstOffGameT = double.NaN; ResetBase();
                if (paused == true && ownUnits > 0 && membersWanted > 0 && membersFound >= membersWanted) _lsFoundStreak = streak + 1; else _lsFoundStreak = 0;
                if (_lsFoundStreak >= PausedFoundSamplesLoadScreen) return Finish("open_paused_ls");
                State = "load_screen";
                return false;
            }
            _lsFoundStreak = 0;
            if (double.IsNaN(_firstOffGameT) || gameT < _firstOffGameT) _firstOffGameT = gameT;
            if (gameT - _firstOffGameT >= MaxGameS) return Finish("timeout");
            if (ownUnits <= 0) { State = "empty"; ResetBase(); return false; }
            bool changed = _samples == 0 || sig != _sig || gameT < _lastGameT;
            _lastGameT = gameT;
            bool onPause;
            if (paused.HasValue) onPause = paused.Value;
            else
            {
                if (double.IsNaN(_standSinceRealT) || gameT != _lastObsGameT) _standSinceRealT = realT;
                onPause = realT - _standSinceRealT >= PausedSettleRealS;
            }
            _lastObsGameT = gameT;
            Paused = onPause;
            if (changed) { _sig = sig; _baseGameT = gameT; _baseRealT = realT; _samples = 1; State = onPause ? "paused" : "settling"; if (!onPause) return false; }
            else _samples++;
            if (onPause)
            {
                if (membersWanted > 0 && membersFound >= membersWanted) _foundStreak++; else _foundStreak = 0;
                if (_foundStreak >= PausedFoundSamples) return Finish("open_paused");
                if (!changed && realT - _baseRealT >= PausedSettleRealS && _samples >= SettleSamples) return Finish("open_paused_settled");
                State = "paused";
                return false;
            }
            _foundStreak = 0;
            if (gameT - _baseGameT >= SettleGameS && _samples >= SettleSamples) return Finish("open");
            State = "settling";
            return false;
        }

        /// <summary>T-232: one status line per StatusEveryRealS while the gate is closed (null: too early, or open). The first line comes StatusEveryRealS after Arm.</summary>
        public string StatusLine(double realT)
        {
            if (!_armed) return null;
            if (double.IsNaN(_lastStatusRealT)) _lastStatusRealT = _armRealT;
            if (realT - _lastStatusRealT < StatusEveryRealS) return null;
            _lastStatusRealT = realT;
            return "post-load gate wait state=" + State + " waited_s=" + WaitedRealS.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " load_screen=" + (LastLoadScreen ? "1" : "0") + " units=" + _lastUnits + " members=" + MembersFound + "/" + MembersWanted + " paused=" + _lastPaused;
        }

        private bool Finish(string state) { _armed = false; State = state; return true; }

        private static string T232SelfTest()
        {
            // status line: not in the first 5 s, then once per 5 s, none when open
            var g = new PostLoadGate(); g.Arm(0);
            g.Step(true, 20, "a", 50, 1, true, 14, 5);
            if (g.StatusLine(1) != null) return "t232 status: first 5 s";
            g.Step(true, 20, "a", 50, 5.25, true, 14, 5);
            string l = g.StatusLine(5.25);
            if (l == null || !l.StartsWith("post-load gate wait state=load_screen") || !l.Contains("load_screen=1 units=20 members=5/14") || !l.EndsWith("paused=1")) return "t232 status line: " + l;
            g.Step(true, 20, "a", 50, 6, true, 14, 5);
            if (g.StatusLine(6.0) != null) return "t232 status: again after 0.75 s";
            g.Step(true, 20, "a", 50, 10.5, true, 14, 5);
            if (g.StatusLine(10.5) == null) return "t232 status: 5 s later";
            // paused under a load camera that stays on
            g = new PostLoadGate(); g.Arm(0);
            int openAt = -1;
            for (int i = 0; i < 10 && openAt < 0; i++) if (g.Step(true, 20, "a", 50, 6 + 0.25 * i, true, 14, 14)) openAt = i;
            if (openAt != PausedFoundSamplesLoadScreen - 1 || g.State != "open_paused_ls" || !g.Open) return "t232 open_paused_ls at " + openAt + " " + g.State;
            if (g.StatusLine(20) != null) return "t232 status after open";
            g = new PostLoadGate(); g.Arm(0);
            for (int i = 0; i < 12; i++) if (g.Step(true, 20, "a", 50, 6 + 0.25 * i, true, 14, i == 5 ? 13 : 14) && i < 13) { if (i < 5 + PausedFoundSamplesLoadScreen) return "t232 ls streak must be reset by found < wanted (opened at " + i + ")"; }
            g = new PostLoadGate(); g.Arm(0);
            for (int i = 0; i < 20; i++) if (g.Step(true, 20, "a", 50, 6 + 0.25 * i, false, 14, 14) || g.Step(true, 20, "a", 50, 6.1 + 0.25 * i, null, 14, 14)) return "t232 ls not paused must not open";
            g = new PostLoadGate(); g.Arm(0);
            for (int i = 0; i < 20; i++) if (g.Step(true, 0, "a", 50, 6 + 0.25 * i, true, 14, 14)) return "t232 ls no units must not open";
            g = new PostLoadGate(); g.Arm(0);
            for (int i = 0; i < 20; i++) if (g.Step(true, 20, "a", 50, 6 + 0.25 * i, true, 0, 0)) return "t232 ls wanted 0 must not open";
            return null;
        }

        private static string PausedSelfTest()
        {
            // 1. paused, game time stands, members appear (run 6 block 49.6): opens on the 2nd sample with all found
            var g = new PostLoadGate(); g.Arm(0);
            int[] u = { 0, 3, 11, 19 }, fo = { 0, 2, 5, 5 };
            int openAt = -1;
            for (int i = 0; i < u.Length; i++)
                if (g.Step(false, u[i], "s" + u[i], 50, 1 + i * 0.25, true, 5, fo[i])) { if (openAt >= 0) return "paused: opened twice"; openAt = i; }
            if (openAt != 3 || g.State != "open_paused" || !g.Open) return "paused: open_paused at sample 3, got " + openAt + " " + g.State;
            // 2. one sample with all found, then 4/5: no open by rule 1
            g = new PostLoadGate(); g.Arm(0);
            if (g.Step(false, 5, "a", 50, 1, true, 5, 5) || g.Step(false, 6, "b", 50, 1.25, true, 5, 4) || g.Step(false, 7, "c", 50, 1.5, true, 5, 5)) return "paused: found streak must be consecutive";
            // 3. wanted 0: settled after 3 s and 3 samples; a change restarts
            g = new PostLoadGate(); g.Arm(0);
            if (g.Step(false, 4, "a", 50, 1, true, 0, 0)) return "paused: wanted 0 opened at once";
            if (g.Step(false, 4, "a", 50, 2, true, 0, 0) || g.Step(false, 4, "a", 50, 3.75, true, 0, 0)) return "paused: opened before 3 s";
            if (g.State != "paused") return "paused: state paused, got " + g.State;
            if (g.Step(false, 5, "b", 50, 4, true, 0, 0) || g.Step(false, 5, "b", 50, 6, true, 0, 0)) return "paused: change must restart";
            if (!g.Step(false, 5, "b", 50, 7, true, 0, 0) || g.State != "open_paused_settled") return "paused: open_paused_settled, got " + g.State;
            // 4. partial 3/5: settled after 3 s
            g = new PostLoadGate(); g.Arm(0);
            g.Step(false, 4, "a", 50, 1, true, 5, 3); g.Step(false, 4, "a", 50, 2, true, 5, 3);
            if (!g.Step(false, 4, "a", 50, 4, true, 5, 3) || g.State != "open_paused_settled") return "paused: partial, got " + g.State;
            // unknown wanted (-1)
            g = new PostLoadGate(); g.Arm(0);
            g.Step(false, 4, "a", 50, 1, true, -1, 0); g.Step(false, 4, "a", 50, 2, true, -1, 0);
            if (!g.Step(false, 4, "a", 50, 4, true, -1, 0)) return "paused: wanted -1 settles";
            // 5. paused unknown: game time stands 3 s -> settled; running time -> the old way
            g = new PostLoadGate(); g.Arm(0);
            bool o = false;
            for (int i = 0; i <= 16 && !o; i++) o = g.Step(false, 4, "a", 50, 1 + i * 0.25, null, 0, 0);
            if (!o || g.State != "open_paused_settled") return "paused null: stands, got " + g.State;
            g = new PostLoadGate(); g.Arm(0);
            g.Step(false, 3, "a", 10, 1, null, 0, 0); g.Step(false, 3, "a", 11, 1.25, null, 0, 0);
            if (!g.Step(false, 3, "a", 12, 1.5, null, 0, 0) || g.State != "open") return "paused null: running time opens the old way, got " + g.State;
            // 6. pause lifted: old path, open after 2 s of game time
            g = new PostLoadGate(); g.Arm(0);
            g.Step(false, 3, "a", 50, 1, true, 5, 0); g.Step(false, 3, "a", 50, 1.25, true, 5, 0);
            if (g.Step(false, 3, "a", 50, 1.5, false, 5, 0)) return "unpaused: base";
            g.Step(false, 3, "a", 51, 2, false, 5, 0);
            if (g.Step(false, 3, "a", 51.5, 2.5, false, 5, 0)) return "unpaused: too early";
            if (!g.Step(false, 3, "a", 52, 3, false, 5, 0) || g.State != "open") return "unpaused: open, got " + g.State;
            // 7. load screen on pause
            g = new PostLoadGate(); g.Arm(0);
            g.Step(false, 3, "a", 50, 1, true, 5, 5);
            if (g.Step(true, 3, "a", 50, 1.25, true, 5, 5) || g.State != "load_screen") return "paused: load screen";
            if (g.Step(false, 3, "a", 50, 1.5, true, 5, 5)) return "paused: found streak must be reset by the load screen";
            // 8. not paused, time stands: not before timeout_real
            g = new PostLoadGate(); g.Arm(0);
            for (int i = 0; i < 40; i++) if (g.Step(false, 12, "s", 50, 1 + i * 0.25, false, 5, 5)) return "unpaused stand must not open";
            // 9. CountFound
            int w, f;
            CountFound(new[] { 5, 5, 0, -3, 7, 9 }, new HashSet<int> { 5, 9, 100 }, out w, out f);
            if (w != 3 || f != 2) return "CountFound " + w + "/" + f;
            CountFound(null, null, out w, out f);
            if (w != 0 || f != 0) return "CountFound null";
            return null;
        }

        public static string SelfTest()
        {
            // load screen: closed
            var g = new PostLoadGate();
            if (!g.Open) return "a fresh gate is open";
            g.Arm(0);
            if (!g.Armed || g.Open) return "Arm must close the gate";
            for (int i = 0; i < 10; i++) if (g.Step(true, 12, "a", 100, i * 0.25)) return "load screen must keep it closed";
            if (g.State != "load_screen") return "state load_screen, got " + g.State;
            // screen off, no units
            if (g.Step(false, 0, "x", 100, 3)) return "no units must not open";
            if (g.State != "empty") return "state empty, got " + g.State;
            // 0 -> 5 -> 12 -> 12 -> 12, 1 s of game time per sample: opens exactly at the 4th settled step
            g = new PostLoadGate(); g.Arm(0);
            int opened = 0; int openAt = -1;
            int[] units = { 0, 5, 12, 12, 12, 12 }; string[] sigs = { "0", "5", "12", "12", "12", "12" };
            for (int i = 0; i < units.Length; i++)
                if (g.Step(false, units[i], sigs[i], 10 + i, 1 + i * 0.25)) { opened++; openAt = i; }
            if (opened != 1 || openAt != 4) return "open once at sample 4, got " + opened + " at " + openAt;
            if (g.State != "open" || !g.Open) return "state open";
            if (g.Step(false, 12, "12", 20, 5)) return "Step after open must be false";
            // pause: game time stands
            g = new PostLoadGate(); g.Arm(0);
            for (int i = 0; i < 40; i++) if (g.Step(false, 12, "s", 50, 1 + i * 0.25)) return "paused world must not open";
            if (!g.Armed || g.State != "settling") return "paused: still settling, got " + g.State;
            // x16: samples 4 s of game time apart need 3 samples
            g = new PostLoadGate(); g.Arm(0);
            if (g.Step(false, 3, "q", 100, 1) || g.Step(false, 3, "q", 104, 2)) return "x16: two samples must not open";
            if (!g.Step(false, 3, "q", 108, 3)) return "x16: third sample opens";
            // signature change restarts the base
            g = new PostLoadGate(); g.Arm(0);
            g.Step(false, 3, "a", 0, 1); g.Step(false, 3, "a", 1, 2); g.Step(false, 3, "b", 2, 3);
            if (g.Step(false, 3, "b", 3, 4)) return "a changed signature restarts the wait";
            if (!g.Step(false, 3, "b", 4, 5)) return "settled again after the change";
            // timeout: empty for MaxGameS after the screen
            g = new PostLoadGate(); g.Arm(0);
            g.Step(true, 0, "", 5, 1);
            bool to = false;
            for (int i = 0; i <= 30 && !to; i++) to = g.Step(false, 0, "", 6 + i, 2 + i);
            if (!to || g.State != "timeout") return "timeout after MaxGameS, got " + g.State;
            // real-time timeout: the screen "hangs"
            g = new PostLoadGate(); g.Arm(10);
            if (g.Step(true, 0, "", 0, 309)) return "not yet";
            if (!g.Step(true, 0, "", 0, 310) || g.State != "timeout_real") return "timeout_real, got " + g.State;
            // time going back: new base
            g = new PostLoadGate(); g.Arm(0);
            g.Step(false, 3, "a", 100, 1); g.Step(false, 3, "a", 101, 2);
            g.Step(false, 3, "a", 50, 3);
            if (g.Step(false, 3, "a", 51, 4)) return "time back restarts the wait";
            if (!g.Step(false, 3, "a", 52, 5)) return "opens 2 s after the restart";
            string p = PausedSelfTest(); if (p != null) return p;
            p = T232SelfTest(); if (p != null) return p;
            // Disarm
            g = new PostLoadGate(); g.Arm(0); g.Disarm();
            if (!g.Open || g.Step(false, 3, "a", 1, 1)) return "Disarm opens and Step is inert";
            return null;
        }
    }
}
