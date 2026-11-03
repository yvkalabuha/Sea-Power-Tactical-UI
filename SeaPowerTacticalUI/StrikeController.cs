using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132: the orchestrator of the strike window over IStrikeGame. Phase A: groups, the target, the plan on command, the stale check, the view for ui_state.
    // No ownership of the aircraft (phase B), no routes, no launches. Main thread only.
    internal sealed partial class StrikeController
    {
        public const string GroupPrefix = "Strike";

        private readonly IStrikeGame _g;
        private readonly StrikeParams _p;
        private readonly Action<string> _log;
        private readonly List<StrikeGroup> _groups = new List<StrikeGroup>();
        private int _seq, _planId;
        private double _lastStale = double.MinValue, _lastTick = double.MinValue;

        /// <summary>Phase B: the ownership shared with the CAP / ASW windows (one machine = one automation) and the deck broker. Both may be null (the stand without them).</summary>
        public AirOwnership Ownership;
        public IAirDeck Deck;

        /// <summary>Who owns the aircraft already (group name of another automation, null = nobody). Phase A: null; phase B: AirOwnership.</summary>
        public Func<int, string> OwnerOf;

        /// <summary>The veto chain of the mod as json (mode, chain, vetoed_n, last) for the view; null = the empty default.</summary>
        public Func<string> VetoJson;

        public StrikeController(IStrikeGame g, StrikeParams p, Action<string> log)
        {
            _g = g; _p = p ?? new StrikeParams(); _log = log ?? (s => { });
        }

        public IReadOnlyList<StrikeGroup> Groups { get { return _groups; } }

        public static bool IsErr(string s) { return s != null && s.Length > 0 && s[0] == '!'; }
        private static string Fail(string code, string detail = null) { return "!" + code + (detail == null ? "" : "|" + detail); }
        private static string Q(string s) { return Json.Str(s ?? ""); }
        private static string N(double d) { return Json.Num(Math.Round(d, 3)); }
        private static string Ack(string group, string extra) { return "{\"ok\":true,\"group\":" + Q(group) + (extra == null ? "" : "," + extra) + "}"; }

        private static string Str(Dictionary<string, object> c, string k) { object v; return c != null && c.TryGetValue(k, out v) ? v as string : null; }
        private static bool IsInt(double d) { return Math.Abs(d - Math.Round(d)) < 1e-9; }

        private StrikeGroup Find(string name) { return _groups.FirstOrDefault(g => g.Name == name); }

        private StrikeGroup GroupOf(int uid) { return _groups.FirstOrDefault(g => g.Members.Contains(uid)); }

        public bool IsMember(int uid, out string group)
        {
            var g = GroupOf(uid);
            group = g == null ? null : g.Name;
            return g != null;
        }

        private StrikeAircraft Air(int uid)
        {
            var list = _g.Aircraft();
            if (list != null) foreach (var a in list) if (a != null && a.Uid == uid) return a;
            return null;
        }

        private StrikeShip Ship(int id)
        {
            var list = _g.Ships();
            if (list != null) foreach (var s in list) if (s != null && s.TrackId == id) return s;
            return null;
        }

        /// <summary>Every TickS of the mission clock (the air tick calls it once per second): the members, the wait point, the fuel, the deck. Every StaleCheckS: the stale check and the wait point check.</summary>
        public void Tick(double t)
        {
            if (t < _lastTick || t - _lastTick >= _p.TickS) _lastTick = t;
            else return;
            try { TickGroups(t); } catch (Exception e) { _log("[Strike] strike tick failed: " + e.GetType().Name + ": " + e.Message); }
            try { TickEw(t); } catch (Exception e) { _log("[Strike] strike ew tick failed: " + e.GetType().Name + ": " + e.Message); }
            try { TickExec(t); } catch (Exception e) { _log("[Strike] strike exec tick failed: " + e.GetType().Name + ": " + e.Message); }
            try { TickKill(t); } catch (Exception e) { _log("[Strike] strike kill tick failed: " + e.GetType().Name + ": " + e.Message); }
            try { TickClear(t); } catch (Exception e) { _log("[Strike] strike clear tick failed: " + e.GetType().Name + ": " + e.Message); }
            try { Warm(t); } catch (Exception e) { _log("[Strike] strike warm failed: " + e.GetType().Name + ": " + e.Message); }
            try { TickAuto(t); } catch (Exception e) { _log("[Strike] strike auto tick failed: " + e.GetType().Name + ": " + e.Message); }
            if (t < _lastStale || t - _lastStale >= _p.StaleCheckS) _lastStale = t;
            else return;
            try { CheckStale(t); } catch (Exception e) { _log("[Strike] strike stale check failed: " + e.Message); }
            try { CheckWait(t); } catch (Exception e) { _log("[Strike] strike wait check failed: " + e.Message); }
        }

        /// <summary>The group name of the unit for AirOwnership.Register("strike", ...).</summary>
        public string GroupNameOf(int uid) { var g = GroupOf(uid); return g == null ? null : g.Name; }

        public static string SelfTest() { return null; }
    }
}
