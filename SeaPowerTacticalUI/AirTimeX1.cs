using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-175 / ADR-0075 item 3: the time acceleration goes back to x1 on danger for the own aircraft, with the reason. A key (raid + stage, missile id, lost uid) works once;
    /// between two resets by a stage at least TcMinGapS of real time; a missile and a loss always reset; x0 (pause), the message pause and x1 are never touched;
    /// [AirGuard] TimeX1 = all | stage2 | off. Without the game member only the notice. Pure: the game is behind delegates.
    /// </summary>
    internal sealed class AirTimeX1 : IAirTimeX1
    {
        private readonly Func<double?> _tc;
        private readonly Func<bool> _pausedOrMessage;
        private readonly Func<string> _setX1;
        private readonly Func<double> _realNow;
        private readonly AirGuardParams _p;
        private readonly Action<string, string> _evt;
        private readonly Action<string> _log, _notice;
        private readonly HashSet<string> _keys = new HashSet<string>();
        private double _lastStageAt = double.NegativeInfinity;
        private double? _lastSeen; private double _ownAt = double.NegativeInfinity;   // T-207: Watch

        public AirTimeX1(Func<double?> tc, Func<bool> pausedOrMessage, Func<string> setX1, Func<double> realNow, AirGuardParams p, Action<string, string> evt, Action<string> log, Action<string> notice)
        {
            _tc = tc ?? (() => null); _pausedOrMessage = pausedOrMessage ?? (() => false); _setX1 = setX1; _realNow = realNow ?? (() => 0);
            _p = p ?? new AirGuardParams(); _evt = evt ?? ((a, b) => { }); _log = log ?? (s => { }); _notice = notice ?? (s => { });
        }

        /// <summary>"Time x14 -> x1: reason" of the last reset (for the card); null = none yet.</summary>
        public string LastLine { get; private set; }

        public void Reset() { _keys.Clear(); _lastStageAt = double.NegativeInfinity; LastLine = null; _lastSeen = null; _ownAt = double.NegativeInfinity; }

        public void Offer(string kind, string key, string reason)
        {
            if (_p.TimeX1 == "off") return;
            if ((kind == "raid1" || kind == "alert_scramble") && _p.TimeX1 != "all") return;   // T-179: the alert scramble is a weak reason like the first sight of a raid
            if (key == null || !_keys.Add(kind + "|" + key)) return;   // a key works once, whatever happened to it
            double? tc = null;
            try { tc = _tc(); } catch { }
            if (!tc.HasValue || tc.Value <= 1.0 + 1e-6) return;          // x0, x1 or unknown: nothing to reduce
            bool paused = false;
            try { paused = _pausedOrMessage(); } catch { }
            if (paused) return;
            double now = _realNow();
            bool stage = kind == "raid1" || kind == "stage2" || kind == "alert_scramble";   // T-179: the pause TcMinGapS holds for it too
            if (stage && now - _lastStageAt < _p.TcMinGapS) return;
            string err = null;
            try { err = _setX1 == null ? "unsupported" : _setX1(); } catch (Exception e) { err = e.GetType().Name; }
            if (stage) _lastStageAt = now;
            string from = tc.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (err != null)
            {
                _log("tc from=" + from + " to=- reason=" + reason + " -> " + err);
                try { _notice(UiText.Get("airguard.tc.notice")); } catch { }
                return;
            }
            _ownAt = now; _lastSeen = 1.0;
            LastLine = UiText.Fmt("airguard.tc.line", from, reason);
            _log("tc from=" + from + " to=1 reason=" + reason);
            try { _evt("air_tc", "{\"from\":" + from + ",\"to\":1,\"reason\":" + Json.Str(reason ?? "") + ",\"key\":" + Json.Str(kind + ":" + key) + "}"); } catch { }
        }

        /// <summary>T-207: once per service tick: a change of the time compression that the mod did not make is logged ("tc seen from=8 to=1 by=other paused=false").</summary>
        public void Watch()
        {
            double? tc = null;
            try { tc = _tc(); } catch { }
            if (!tc.HasValue) return;
            double prev;
            if (!_lastSeen.HasValue) { _lastSeen = tc; return; }
            prev = _lastSeen.Value;
            if (Math.Abs(prev - tc.Value) < 1e-6) return;
            _lastSeen = tc;
            if (Math.Abs(tc.Value - 1.0) < 1e-6 && _realNow() - _ownAt <= 2.0) return;   // our own reset to x1
            bool paused = false;
            try { paused = _pausedOrMessage(); } catch { }
            _log("tc seen from=" + prev.ToString("0.##", CultureInfo.InvariantCulture) + " to=" + tc.Value.ToString("0.##", CultureInfo.InvariantCulture) + " by=other paused=" + (paused ? "true" : "false"));
        }

        public static string SelfTest()
        {
            double? tc = 14; bool paused = false; int sets = 0; string setErr = null; double real = 1000;
            var ev = new List<string>(); var logs = new List<string>(); var notes = new List<string>();
            var p = new AirGuardParams();
            Func<AirTimeX1> mk = () => new AirTimeX1(() => tc, () => paused, () => { sets++; if (setErr == null) tc = 1; return setErr; }, () => real, p, (n, j) => ev.Add(n + " " + j), s => logs.Add(s), s => notes.Add(s));
            var x = mk();
            x.Offer("raid1", "r1", "raid");
            if (sets != 1 || tc != 1) return "the first offer resets";
            if (ev.Count != 1 || !ev[0].StartsWith("air_tc {\"from\":14,\"to\":1,") || !ev[0].Contains("\"key\":\"raid1:r1\"")) return "event: " + string.Join("|", ev);
            if (logs.Count != 1 || logs[0] != "tc from=14 to=1 reason=raid") return "journal: " + string.Join("|", logs);
            if (x.LastLine == null || !x.LastLine.Contains("14") || !x.LastLine.Contains("raid")) return "LastLine " + x.LastLine;
            // the same key: once, even when the player accelerates again
            tc = 8; real += 100; x.Offer("raid1", "r1", "raid");
            if (sets != 1 || tc != 8) return "the same key is not offered twice";
            // a stage within TcMinGapS of the last stage reset: held back; after it: yes
            real = 1010; x.Offer("stage2", "r1:ASW-2", "stage 2 early");
            if (sets != 1 || tc != 8) return "a stage within 30 s of the raid1 reset is held back";
            real = 1031; x.Offer("stage2", "r1:ASW-3", "stage 2");
            if (sets != 2 || tc != 1) return "stage2 after the gap";
            tc = 6; real = 1040; x.Offer("stage2", "r1:ASW-4", "stage 2 b");
            if (sets != 2) return "a second stage within 30 s is held back";
            x.Offer("missile", "77", "missile");
            if (sets != 3 || tc != 1) return "a missile always resets";
            tc = 4; x.Offer("loss", "loss:5", "loss");
            if (sets != 4 || tc != 1) return "a loss always resets";
            // x0 / x1 / pause: nothing
            sets = 0; tc = 0; x.Offer("missile", "78", "m"); tc = 1; x.Offer("missile", "79", "m"); tc = 8; paused = true; x.Offer("missile", "80", "m"); paused = false; tc = null; x.Offer("missile", "81", "m");
            if (sets != 0) return "x0, x1, pause and unknown are left alone";
            // TimeX1 = stage2 skips raid1; off does nothing
            p.TimeX1 = "stage2"; tc = 8; x.Offer("raid1", "r9", "r"); if (sets != 0) return "stage2 mode skips raid1";
            real = 2000; x.Offer("stage2", "r9:g", "s"); if (sets != 1) return "stage2 mode keeps stage2";
            p.TimeX1 = "off"; tc = 8; x.Offer("missile", "90", "m"); if (sets != 1) return "off does nothing";
            p.TimeX1 = "all";
            // T-179: the alert scramble works at TimeX1 = all only, once per key, with the stage pause
            var z = mk(); p.TimeX1 = "all"; tc = 8; real = 3000; sets = 0;
            z.Offer("alert_scramble", "r7|ПВО-2", "alert"); if (sets != 1 || tc != 1) return "alert_scramble resets at all";
            tc = 8; z.Offer("alert_scramble", "r7|ПВО-2", "alert"); if (sets != 1) return "alert_scramble: a key works once";
            real = 3005; z.Offer("alert_scramble", "r8|ПВО-2", "alert"); if (sets != 1) return "alert_scramble: held back by TcMinGapS";
            real = 3040; z.Offer("alert_scramble", "r8b|ПВО-2", "alert"); if (sets != 2) return "alert_scramble after the gap";
            p.TimeX1 = "stage2"; tc = 8; real = 3100; z.Offer("alert_scramble", "r9|ПВО-2", "alert"); if (sets != 2) return "alert_scramble is skipped at stage2";
            p.TimeX1 = "all";
            real = 1000; sets = 0;
            // without the member: only the notice
            var y = mk(); sets = 0; setErr = "unsupported"; tc = 10; notes.Clear(); logs.Clear();
            y.Offer("missile", "1", "m");
            if (notes.Count != 1 || y.LastLine != null) return "unsupported: only the notice";
            if (logs.Count != 1 || !logs[0].Contains("unsupported")) return "unsupported journal";
            y.Reset();
            setErr = null; y.Offer("missile", "1", "m");
            if (sets != 2) return "Reset forgets the keys";
            // T-207: Watch logs a change the mod did not make
            var w = mk(); logs.Clear(); tc = 8; sets = 0; real = 5000; paused = false;
            w.Watch(); if (logs.Count != 0) return "Watch: the first sight is only a baseline";
            w.Offer("missile", "w1", "m"); w.Watch();
            if (sets != 1 || logs.Count != 1 || logs[0].Contains("tc seen")) return "Watch: our own reset is not logged: " + string.Join("|", logs);
            logs.Clear(); tc = 8; w.Watch();
            if (logs.Count != 1 || logs[0] != "tc seen from=1 to=8 by=other paused=false") return "Watch: 1 -> 8: " + string.Join("|", logs);
            w.Watch(); if (logs.Count != 1) return "Watch: one line per change";
            real += 10; tc = 1; w.Watch();
            if (logs.Count != 2 || logs[1] != "tc seen from=8 to=1 by=other paused=false") return "Watch: 8 -> 1 by the game: " + string.Join("|", logs);
            tc = null; w.Watch(); if (logs.Count != 2) return "Watch: unknown is not logged";
            tc = 1; w.Watch(); if (logs.Count != 2) return "Watch: null -> same value is not a change";
            return null;
        }
    }
}
