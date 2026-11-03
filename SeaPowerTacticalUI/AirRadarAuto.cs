using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>T-241: radar list kinds of the ledger (kept out of the game layer's literals).</summary>
    internal static class AirRadarKind { public const string Air = "air", SearchSurface = "search_surface"; }

    // T-241 (ADR-0099): the search radar of an aircraft is on while the mod runs its task (CAP patrol / alert, ASW search) and goes back
    // to the previous state after it. Pure core: no game types, so the stands (captest, aswtest) compile it as is.

    /// <summary>One read of one radar list of a unit. List: "air" | "surface" - the list actually used (the ASW kind may fall back to "air").</summary>
    internal sealed class AirRadarRead { public bool Known, HasRadar, On, Emcon; public string List; }

    internal interface IAirRadarIo
    {
        /// <summary>Mission time, s (NaN outside a mission).</summary>
        double NowS { get; }
        /// <summary>want: "air" (the air list) | "search_surface" (surface list with an operable radar, else SurfaceAndAir radars of the air list).</summary>
        AirRadarRead Read(int uid, string want);
        /// <summary>null = ok; list = AirRadarRead.List.</summary>
        string Set(int uid, string list, bool on);
    }

    /// <summary>What the ledger remembers about one aircraft (save files: JSON key "radar").</summary>
    internal sealed class AirRadarDto { public string Want, List; public bool SavedOn, Confirmed, Player, Untouched; }

    internal static class AirRadarRules
    {
        public const double ConfirmS = 20;

        /// <summary>CAP wish: the radar is needed while the aircraft flies its task (not going home, not returning after an alert).</summary>
        public static bool CapWants(bool auto, bool airborne, string leaving, string alertState)
        {
            return auto && airborne && string.IsNullOrEmpty(leaving) && alertState != "return";
        }

        /// <summary>
        /// ASW wish (helicopters and planes alike, Q-T241-4). true = on, false = give back, null = keep as it is
        /// (submarine attack - Q-T241-1 "as is"; a missile evasion maneuver).
        /// </summary>
        public static bool? AswWants(bool auto, string where, string order, string assign,
                                     bool leaving, bool retreat, bool inAttack, bool evading)
        {
            if (!auto || where != "air") return false;
            if (order != "course" && order != "area") return false;
            if (retreat || leaving) return false;
            if (assign == "reserve") return false;
            if (inAttack || evading) return null;
            return true;
        }

        public static string ToJson(AirRadarDto d)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"want\":").Append(Json.Str(d.Want ?? "air")).Append(",\"list\":").Append(Json.Str(d.List ?? "air"));
            sb.Append(",\"saved_on\":").Append(Json.Bool(d.SavedOn)).Append(",\"confirmed\":").Append(Json.Bool(d.Confirmed));
            sb.Append(",\"player\":").Append(Json.Bool(d.Player)).Append(",\"untouched\":").Append(Json.Bool(d.Untouched));
            return sb.Append('}').ToString();
        }

        /// <summary>Null on a missing or malformed object.</summary>
        public static AirRadarDto FromJson(Dictionary<string, object> o)
        {
            if (o == null) return null;
            object v;
            string want = o.TryGetValue("want", out v) ? v as string : null;
            string list = o.TryGetValue("list", out v) ? v as string : null;
            if (want != "air" && want != "search_surface") return null;
            if (list != "air" && list != "surface") return null;
            return new AirRadarDto { Want = want, List = list, SavedOn = B(o, "saved_on"), Confirmed = B(o, "confirmed"), Player = B(o, "player"), Untouched = B(o, "untouched") };
        }

        private static bool B(Dictionary<string, object> o, string k) { object v; return o.TryGetValue(k, out v) && v is bool && (bool)v; }
    }

    /// <summary>
    /// The aircraft radar ledger (ADR-0099). Key - uid. A holder ("cap" / "asw") says per tick whether it needs the radar. Switched on once when
    /// off; a switch-off by somebody else (player, mission trigger) is respected until the task ends; at the end only the radar the mod
    /// itself switched on is switched off. EMCON is derived by the game and never touched here.
    /// </summary>
    internal sealed class AirRadarLedger
    {
        private sealed class Entry
        {
            public string Want, List;
            public bool Live;                 // the mod holds the radar (between the first "true" and the give-back)
            public bool Untouched;            // nothing to give back (already on / no radar / failed)
            public bool Confirmed, Player;
            public bool NoneLogged;
            public double AppliedAt;
            public readonly HashSet<string> Holders = new HashSet<string>();
            public readonly HashSet<string> Wanters = new HashSet<string>();
        }

        private readonly IAirRadarIo _io;
        private readonly Dictionary<int, Entry> _e = new Dictionary<int, Entry>();
        private readonly Dictionary<string, Action<string>> _log = new Dictionary<string, Action<string>>();

        /// <summary>true = also the "confirmed" line.</summary>
        public bool Debug;

        public AirRadarLedger(IAirRadarIo io) { _io = io; }

        public int Count { get { return _e.Count; } }
        public bool Has(int uid) { return _e.ContainsKey(uid); }

        /// <summary>log: journal sink of the holder ("cap" -> "[Cap] ...", "asw" -> "asw ..."); lines are "radar &lt;verb&gt; uid=&lt;u&gt; list=&lt;l&gt; ...".</summary>
        public void Sink(string holder, Action<string> log) { _log[holder] = log; }

        private void Log(string holder, string line) { Action<string> a; if (holder != null && _log.TryGetValue(holder, out a) && a != null) a(line); }

        private static string Inv(int uid) { return uid.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Per tick and member. want "air" | "search_surface"; wish null = keep. Returns on | none | untouched | confirmed | failed | player_off | restore | kept | wait | idle.</summary>
        public string Tick(int uid, string holder, string want, bool? wish)
        {
            Entry e; _e.TryGetValue(uid, out e);
            if (wish == null)
            {
                if (e == null) return "idle";
                e.Holders.Add(holder);
                return e.Live ? Monitor(uid, e, holder) : "idle";
            }
            if (wish == false)
            {
                if (e == null) return "idle";
                e.Wanters.Remove(holder); e.Holders.Add(holder);
                return GiveBackIfIdle(uid, e, holder, true);
            }
            // wish == true
            if (e == null) { e = new Entry { Want = want }; _e[uid] = e; }
            e.Holders.Add(holder); e.Wanters.Add(holder);
            if (e.Live) return Monitor(uid, e, holder);
            if (e.Player) { e.Live = true; return "player_off"; }     // the player had it off: leave it off for the whole task
            e.Want = want; e.Confirmed = false; e.Untouched = false; e.NoneLogged = false;
            return Start(uid, e, holder);
        }

        private string Start(int uid, Entry e, string holder)
        {
            AirRadarRead r = _io.Read(uid, e.Want);
            e.Live = true;
            if (r == null || !r.Known) return None(uid, e, holder, "unknown");
            e.List = r.List ?? "air";
            if (!r.HasRadar) return None(uid, e, holder, "no_radar");
            if (r.On) { e.Untouched = true; e.Confirmed = true; return "untouched"; }
            string err = _io.Set(uid, e.List, true);
            if (err != null) return None(uid, e, holder, err == "unsupported" ? "unsupported" : "error");
            e.AppliedAt = _io.NowS;
            Log(holder, "radar on uid=" + Inv(uid) + " list=" + e.List + " emcon=" + (r.Emcon ? "true" : "false") + " why=" + holder);
            return "on";
        }

        private string None(int uid, Entry e, string holder, string reason)
        {
            e.Untouched = true; e.Confirmed = true;
            if (!e.NoneLogged) { e.NoneLogged = true; Log(holder, "radar none uid=" + Inv(uid) + " reason=" + reason); }
            return "none";
        }

        /// <summary>Confirmation of our switch-on and recognition of a foreign switch-off.</summary>
        private string Monitor(int uid, Entry e, string holder)
        {
            if (e.Untouched || e.Player) return "idle";   // nothing to watch: no game read
            AirRadarRead r = _io.Read(uid, e.Want);
            if (r == null || !r.Known) return e.Confirmed ? "idle" : "wait";
            if (!e.Confirmed)
            {
                if (r.On) { e.Confirmed = true; if (Debug) Log(holder, "radar confirmed uid=" + Inv(uid) + " list=" + e.List); return "confirmed"; }
                double dt = _io.NowS - e.AppliedAt;
                if (dt >= AirRadarRules.ConfirmS)
                {
                    e.Untouched = true; e.Confirmed = true;
                    Log(holder, "radar failed uid=" + Inv(uid) + " list=" + e.List + " after=" + ((int)AirRadarRules.ConfirmS).ToString(CultureInfo.InvariantCulture) + "s");
                    return "failed";
                }
                return "wait";
            }
            if (!e.Player && !r.On)
            {
                e.Player = true;
                Log(holder, "radar player_off uid=" + Inv(uid) + " list=" + e.List + " - the mod keeps it off until the task ends");
                return "player_off";
            }
            return "idle";
        }

        /// <summary>Gives the radar back when no holder wants it any more. restore=false: no game call.</summary>
        private string GiveBackIfIdle(int uid, Entry e, string holder, bool restore)
        {
            if (e.Wanters.Count > 0 || !e.Live) return "idle";
            e.Live = false;
            string res = "kept";
            string why = null;
            if (e.Untouched) why = "untouched";
            else if (e.Player) why = "player";
            else if (!restore) why = null;
            else
            {
                // Not confirmed yet: the radar is still warming up after our command, so it is switched off anyway (it would stay on).
                AirRadarRead r = e.Confirmed ? _io.Read(uid, e.Want) : null;
                if (e.Confirmed && (r == null || !r.Known || !r.On)) why = "already_off";
                else
                {
                    string err = _io.Set(uid, e.List, false);
                    if (err == null) { Log(holder, "radar restore uid=" + Inv(uid) + " list=" + e.List + " off"); res = "restore"; }
                    else why = "error";
                }
            }
            if (res == "kept" && restore && why != null) Log(holder, "radar restore uid=" + Inv(uid) + " list=" + (e.List ?? "-") + " kept(" + why + ")");
            // The entry stays only to remember a foreign switch-off; Release drops it.
            if (!e.Player) _e.Remove(uid);
            return res;
        }

        /// <summary>The holder lets go of the unit. restore=false: no game call (landed, lost, mission left).</summary>
        public string Release(int uid, string holder, bool restore)
        {
            Entry e; if (!_e.TryGetValue(uid, out e)) return "idle";
            e.Holders.Remove(holder); e.Wanters.Remove(holder);
            string res = "idle";
            if (e.Live && e.Wanters.Count == 0)
            {
                if (restore) res = GiveBackIfIdle(uid, e, holder, true);
                else e.Live = false;
            }
            if (e.Holders.Count == 0) _e.Remove(uid);
            return res;
        }

        /// <summary>The holder lets go of every unit (its controller was cleared). No game calls.</summary>
        public void ReleaseAll(string holder)
        {
            var gone = new List<int>();
            foreach (var kv in _e)
            {
                if (!kv.Value.Holders.Contains(holder)) continue;   // T-246: the memory of another holder is not touched (CAP and ASW load their files separately)
                kv.Value.Holders.Remove(holder); kv.Value.Wanters.Remove(holder);
                if (kv.Value.Wanters.Count == 0) kv.Value.Live = false;
                if (kv.Value.Holders.Count == 0) gone.Add(kv.Key);
            }
            foreach (int u in gone) _e.Remove(u);
        }

        public AirRadarDto Export(int uid)
        {
            Entry e; if (!_e.TryGetValue(uid, out e)) return null;
            if (!e.Live && !e.Player) return null;
            return new AirRadarDto { Want = e.Want ?? "air", List = e.List ?? "air", SavedOn = !e.Untouched, Confirmed = e.Confirmed, Player = e.Player, Untouched = e.Untouched };
        }

        public void Import(int uid, string holder, AirRadarDto d)
        {
            if (d == null) return;
            var e = new Entry { Want = d.Want ?? "air", List = d.List ?? "air", Live = true, Confirmed = d.Confirmed, Player = d.Player, Untouched = d.Untouched || !d.SavedOn, AppliedAt = _io.NowS };
            e.Holders.Add(holder);
            if (!e.Untouched && !e.Player) e.Wanters.Add(holder);   // T-246: the saved switch-on is the holder's wish until its first tick says otherwise
            _e[uid] = e;
        }

        public void Remap(int fromUid, int toUid)
        {
            Entry e; if (fromUid == toUid || !_e.TryGetValue(fromUid, out e)) return;
            _e.Remove(fromUid); _e[toUid] = e;
        }

        /// <summary>null | on | player | untouched | wait (for cap_state / asw_state).</summary>
        public string StateOf(int uid)
        {
            Entry e; if (!_e.TryGetValue(uid, out e)) return null;
            if (e.Player) return "player";
            if (!e.Live) return null;
            if (e.Untouched) return "untouched";
            return e.Confirmed ? "on" : "wait";
        }

        public void Clear() { _e.Clear(); }

        // ---- self test --------------------------------------------------------------------------------------------------------------

        private sealed class Sim : IAirRadarIo
        {
            public double Now; public double WarmS = 3;
            public bool Known = true, HasAir = true, HasSurf, AirOn, SurfOn, Unsupported;
            public double AirAt = -1, SurfAt = -1;
            public readonly List<string> Calls = new List<string>();
            public double NowS { get { return Now; } }
            private void Warm() { if (AirAt >= 0 && Now >= AirAt) { AirOn = true; AirAt = -1; } if (SurfAt >= 0 && Now >= SurfAt) { SurfOn = true; SurfAt = -1; } }
            public AirRadarRead Read(int uid, string want)
            {
                Warm();
                if (!Known) return new AirRadarRead();
                bool surf = want == "search_surface" && HasSurf;
                return new AirRadarRead { Known = true, HasRadar = surf || HasAir, On = surf ? SurfOn : AirOn, Emcon = !AirOn && !SurfOn, List = surf ? "surface" : "air" };
            }
            public string Set(int uid, string list, bool on)
            {
                if (Unsupported) return "unsupported";
                Calls.Add("set " + uid + " " + list + " " + (on ? "on" : "off"));
                if (list == "surface") { if (on) SurfAt = Now + WarmS; else { SurfOn = false; SurfAt = -1; } }
                else { if (on) AirAt = Now + WarmS; else { AirOn = false; AirAt = -1; } }
                return null;
            }
        }

        private static string Fail(string s) { return s; }

        public static string SelfTest()
        {
            // rules
            if (!AirRadarRules.CapWants(true, true, null, "") || !AirRadarRules.CapWants(true, true, "", "scramble")) return "cap wants: patrol";
            if (AirRadarRules.CapWants(false, true, null, "") || AirRadarRules.CapWants(true, false, null, "") || AirRadarRules.CapWants(true, true, "fuel", "") || AirRadarRules.CapWants(true, true, null, "return")) return "cap wants: no";
            if (AirRadarRules.AswWants(true, "air", "course", "dip", false, false, false, false) != true) return "asw wants: search";
            if (AirRadarRules.AswWants(true, "air", "area", "contact", false, false, false, false) != true) return "asw wants: contact (plane or helicopter alike)";
            if (AirRadarRules.AswWants(true, "air", "course", "dip", false, false, true, false) != null) return "asw wants: attack keeps";
            if (AirRadarRules.AswWants(true, "air", "course", "dip", false, false, false, true) != null) return "asw wants: evading keeps";
            if (AirRadarRules.AswWants(true, "air", "course", "dip", false, true, true, false) != false) return "asw wants: retreat beats attack";
            if (AirRadarRules.AswWants(true, "air", "course", "dip", true, false, false, false) != false) return "asw wants: leaving";
            if (AirRadarRules.AswWants(true, "air", "course", "reserve", false, false, false, false) != false) return "asw wants: reserve";
            if (AirRadarRules.AswWants(true, "air", "sar", "dip", false, false, false, false) != false || AirRadarRules.AswWants(true, "air", "strike", "dip", false, false, false, false) != false || AirRadarRules.AswWants(true, "air", "none", "dip", false, false, false, false) != false) return "asw wants: sar/strike/none";
            if (AirRadarRules.AswWants(true, "deck", "course", "dip", false, false, false, false) != false || AirRadarRules.AswWants(false, "air", "course", "dip", false, false, false, false) != false) return "asw wants: deck / auto off";

            var lines = new List<string>();
            Action<string> log = s => lines.Add(s);
            // on -> confirm -> restore
            {
                var io = new Sim(); var l = new AirRadarLedger(io); l.Sink("cap", log);
                if (l.Tick(1, "cap", "air", true) != "on" || io.Calls.Count != 1 || io.Calls[0] != "set 1 air on") return "ledger: on";
                if (l.StateOf(1) != "wait") return "ledger: state wait";
                io.Now = 1; if (l.Tick(1, "cap", "air", true) != "wait" || io.Calls.Count != 1) return "ledger: wait";
                io.Now = 4; if (l.Tick(1, "cap", "air", true) != "confirmed" || l.StateOf(1) != "on") return "ledger: confirmed";
                if (l.Tick(1, "cap", "air", true) != "idle" || io.Calls.Count != 1) return "ledger: no repeated set";
                if (l.Tick(1, "cap", "air", null) != "idle") return "ledger: null keeps";
                if (l.Tick(1, "cap", "air", false) != "restore" || io.Calls.Count != 2 || io.Calls[1] != "set 1 air off" || io.AirOn) return "ledger: restore off";
                if (l.Has(1) || l.Count != 0) return "ledger: entry dropped after restore";
                if (l.Tick(1, "cap", "air", false) != "idle") return "ledger: false without entry";
                if (lines.Count != 2 || !lines[0].StartsWith("radar on uid=1 list=air emcon=true") || lines[1] != "radar restore uid=1 list=air off") return "ledger: lines " + string.Join("|", lines);
            }
            // untouched
            {
                var io = new Sim { AirOn = true }; var l = new AirRadarLedger(io);
                if (l.Tick(1, "cap", "air", true) != "untouched" || io.Calls.Count != 0 || l.StateOf(1) != "untouched") return "ledger: untouched";
                if (l.Tick(1, "cap", "air", false) != "kept" || io.Calls.Count != 0 || !io.AirOn) return "ledger: untouched keeps radar on";
            }
            // none: no radar / unknown / unsupported (one line per task)
            {
                lines.Clear();
                var io = new Sim { HasAir = false }; var l = new AirRadarLedger(io); l.Sink("cap", log);
                if (l.Tick(1, "cap", "air", true) != "none" || l.Tick(1, "cap", "air", true) != "idle" || lines.Count != 1 || lines[0] != "radar none uid=1 reason=no_radar") return "ledger: none no_radar";
                io = new Sim { Known = false }; l = new AirRadarLedger(io); l.Sink("cap", log); lines.Clear();
                if (l.Tick(2, "cap", "air", true) != "none" || lines[0] != "radar none uid=2 reason=unknown") return "ledger: none unknown";
                io = new Sim { Unsupported = true }; l = new AirRadarLedger(io); l.Sink("cap", log); lines.Clear();
                if (l.Tick(3, "cap", "air", true) != "none" || lines[0] != "radar none uid=3 reason=unsupported" || io.Calls.Count != 0) return "ledger: none unsupported";
            }
            // failed after ConfirmS
            {
                lines.Clear();
                var io = new Sim { WarmS = 1000 }; var l = new AirRadarLedger(io); l.Sink("cap", log);
                l.Tick(1, "cap", "air", true);
                io.Now = 19; if (l.Tick(1, "cap", "air", true) != "wait") return "ledger: still waiting at 19 s";
                io.Now = 20; if (l.Tick(1, "cap", "air", true) != "failed" || lines[lines.Count - 1] != "radar failed uid=1 list=air after=20s") return "ledger: failed";
                if (l.Tick(1, "cap", "air", false) != "kept" || io.Calls.Count != 1) return "ledger: failed gives nothing back";
            }
            // player switched it off: never switched on again, nothing given back
            {
                lines.Clear();
                var io = new Sim(); var l = new AirRadarLedger(io); l.Sink("cap", log);
                l.Tick(1, "cap", "air", true); io.Now = 5; l.Tick(1, "cap", "air", true);
                io.AirOn = false;
                io.Now = 6; if (l.Tick(1, "cap", "air", true) != "player_off" || l.StateOf(1) != "player") return "ledger: player_off";
                if (!lines[lines.Count - 1].StartsWith("radar player_off uid=1 list=air")) return "ledger: player_off line";
                if (l.Tick(1, "cap", "air", false) != "kept" || !l.Has(1)) return "ledger: player kept after false";
                io.Now = 7; if (l.Tick(1, "cap", "air", true) != "player_off" || io.Calls.Count != 1) return "ledger: no set after player_off";
                io.AirOn = true;   // the player switched it on again: still the player's radar
                if (l.Tick(1, "cap", "air", false) != "kept" || !io.AirOn || io.Calls.Count != 1) return "ledger: player radar kept on";
                if (l.Release(1, "cap", true) != "idle" || l.Has(1)) return "ledger: release drops the player entry";
            }
            // unconfirmed / already off at give-back
            {
                var io = new Sim(); var l = new AirRadarLedger(io);
                l.Tick(1, "cap", "air", true);
                if (l.Tick(1, "cap", "air", false) != "restore" || io.Calls.Count != 2 || io.Calls[1] != "set 1 air off") return "ledger: unconfirmed is switched off anyway";
                io.Now = 10; if (io.Read(1, "air").On) return "ledger: warm-up cancelled";
            }
            // Release(false): no calls; Release(true) restores
            {
                var io = new Sim(); var l = new AirRadarLedger(io);
                l.Tick(1, "cap", "air", true); io.Now = 5; l.Tick(1, "cap", "air", true);
                l.Release(1, "cap", false);
                if (io.Calls.Count != 1 || l.Has(1) || !io.AirOn) return "ledger: release without restore";
                io = new Sim(); l = new AirRadarLedger(io);
                l.Tick(1, "cap", "air", true); io.Now = 5; l.Tick(1, "cap", "air", true);
                if (l.Release(1, "cap", true) != "restore" || io.AirOn || l.Has(1)) return "ledger: release with restore";
            }
            // two holders: give back when nobody wants it
            {
                var io = new Sim(); var l = new AirRadarLedger(io);
                l.Tick(1, "cap", "air", true); io.Now = 5; l.Tick(1, "cap", "air", true); l.Tick(1, "asw", "air", true);
                if (io.Calls.Count != 1) return "ledger: two holders one set";
                if (l.Tick(1, "cap", "air", false) != "idle" || !io.AirOn) return "ledger: one holder still wants";
                if (l.Tick(1, "asw", "air", false) != "restore" || io.AirOn) return "ledger: last holder gives back";
            }
            // remap
            {
                var io = new Sim(); var l = new AirRadarLedger(io);
                l.Tick(1, "asw", "air", true); io.Now = 5; l.Tick(1, "asw", "air", true);
                l.Remap(1, 2);
                if (l.Has(1) || !l.Has(2) || l.StateOf(2) != "on") return "ledger: remap";
                if (l.Tick(2, "asw", "air", false) != "restore") return "ledger: restore after remap";
            }
            // export / import round trip, bad json
            {
                var io = new Sim(); var l = new AirRadarLedger(io);
                l.Tick(1, "cap", "air", true); io.Now = 5; l.Tick(1, "cap", "air", true);
                var d = l.Export(1);
                if (d == null || !d.SavedOn || !d.Confirmed || d.Player || d.Untouched || d.List != "air" || d.Want != "air") return "ledger: export";
                var o = Json.Parse(AirRadarRules.ToJson(d)) as Dictionary<string, object>;
                var back = AirRadarRules.FromJson(o);
                if (back == null || back.Want != "air" || !back.SavedOn || !back.Confirmed || back.Player) return "ledger: json round trip";
                var l2 = new AirRadarLedger(io); l2.Import(1, "cap", back);
                if (l2.StateOf(1) != "on") return "ledger: import state";
                if (l2.Tick(1, "cap", "air", false) != "restore" || io.AirOn) return "ledger: restore after import";
                if (AirRadarRules.FromJson(null) != null || AirRadarRules.FromJson(new Dictionary<string, object>()) != null) return "ledger: bad json";
                if (l.Export(99) != null) return "ledger: export none";
            }
            // T-246: a foreign ReleaseAll after an import keeps the mod's switch-on; the entry is gone only for its own holder
            {
                var io = new Sim(); var l = new AirRadarLedger(io);
                l.Tick(1, "asw", "air", true); io.Now = 5; l.Tick(1, "asw", "air", true);
                var d = l.Export(1);
                io = new Sim { AirOn = true }; l = new AirRadarLedger(io);
                l.Import(1, "asw", d);
                l.ReleaseAll("cap");
                if (l.StateOf(1) != "on") return "ledger: foreign ReleaseAll after import " + l.StateOf(1);
                if (l.Tick(1, "asw", "air", true) != "idle" || l.StateOf(1) != "on") return "ledger: tick after the foreign release";
                if (l.Tick(1, "asw", "air", false) != "restore" || io.AirOn) return "ledger: restore after the foreign release";
                l.Import(2, "asw", d); l.ReleaseAll("asw");
                if (l.Has(2)) return "ledger: own ReleaseAll drops the entry";
            }
            // search_surface with a fallback to the air list
            {
                var io = new Sim { HasSurf = true }; var l = new AirRadarLedger(io);
                l.Tick(1, "asw", "search_surface", true);
                if (io.Calls.Count != 1 || io.Calls[0] != "set 1 surface on") return "ledger: surface list";
                io = new Sim { HasSurf = false }; l = new AirRadarLedger(io);
                l.Tick(1, "asw", "search_surface", true);
                if (io.Calls.Count != 1 || io.Calls[0] != "set 1 air on") return "ledger: surface falls back to air";
            }
            return null;
        }
    }
}
