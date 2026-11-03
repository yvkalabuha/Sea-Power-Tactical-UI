using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 phase D (plan 34.4): the game side of the jammer aircraft. Own stations are read (type Offensive | Universal, bands, cone, range, state); the order is a JamTask by a point
    // (the constructor runs the order, the same call as Order.Type.Jam of the game, decompiled JamTask.cs / Order.cs); switching off = ECM.ceaseOffensiveJam + IsOn = false (a JamTask by a point
    // cannot be repeated to stop, only a task by an object toggles). The radars of a hostile class come from the encyclopedia entry of the class (MuseumRadarEntry: band, range), only for identified
    // tracks. Every method catches inside.
    internal sealed partial class GameApi
    {
        private sealed partial class StrikeGameImpl
        {
            public bool EwCapable
            {
                get
                {
                    try { return _a.Present(StrikeSpecs.JamTaskCtor) && _a.Present(StrikeSpecs.EcmCease) && _a.Present(RangeRingSpecs.GeoNew) && _a.Present(StrikeSpecs.ObpEcmSystems); }
                    catch (Exception) { return false; }
                }
            }

            /// <summary>The short band name of a game frequency ("X_Band" -> "X", "Ku_Band" -> "Ku", "VHF_Band" -> "VHF"); "" = not a band.</summary>
            internal static string BandName(string raw)
            {
                if (string.IsNullOrEmpty(raw)) return "";
                string s = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), "(?i)[-_ ]*band", "").Replace("_", "").Replace("-", "").Trim();   // "X_Band" (enum), "X-Band" (ini), "Ku-Band"
                switch (s.ToUpperInvariant())
                {
                    case "VHF": return "VHF";
                    case "UHF": return "UHF";
                    case "L": return "L";
                    case "S": return "S";
                    case "C": return "C";
                    case "X": return "X";
                    case "KU": return "Ku";
                    case "K": return "K";
                    case "KA": return "Ka";
                }
                return "";
            }

            private static IEnumerable<object> Seq(object o)
            {
                var e = o as IEnumerable;
                if (e == null) yield break;
                foreach (var x in e) if (x != null) yield return x;
            }

            private IList<object> EwStations(object unit)
            {
                var res = new List<object>();
                var obp = _a.Get(unit, AirOpsSpecs.ObjObp);
                foreach (var sys in Seq(_a.Get(obp, StrikeSpecs.ObpEcmSystems)))
                {
                    var ecm = _a.Get(sys, RadarSpecs.EcmSysEcm);
                    var ep = ecm == null ? null : _a.Get(ecm, RadarSpecs.EcmEp);
                    var type = ep == null ? null : _a.Get(ep, RadarSpecs.EpType);
                    if (type != null && StrikeAirMath.IsEwStation(type.ToString())) res.Add(sys);
                }
                return res;
            }

            internal IList<object> StationsOf(object unit) { try { return EwStations(unit); } catch (Exception) { return new List<object>(); } }

            internal bool AnyJamming(object unit)
            {
                try { foreach (var s in EwStations(unit)) if (BoolOf(_a.Get(s, StrikeSpecs.EcmGeoTargeted))) return true; } catch (Exception) { }
                return false;
            }

            /// <summary>The best jammer of an own aircraft (the operable station Offensive | Universal with the most bands); null = no such station at all.</summary>
            private StrikeEw ReadEw(object unit)
            {
                if (!_a.Present(StrikeSpecs.ObpEcmSystems)) return null;
                var stations = EwStations(unit);
                if (stations.Count == 0) return null;
                var ew = new StrikeEw { Capable = EwCapable, Operable = false, Bands = new string[0] };
                int bestBands = -1;
                foreach (var sys in stations)
                {
                    bool inop = BoolOf(_a.RxGet(sys, RadarSpecs.EcmInoperable)), destroyed = BoolOf(_a.Get(sys, RadarSpecs.EcmIsDestroyed));
                    if (inop || destroyed) continue;
                    ew.Stations++;
                    var ep = _a.Get(_a.Get(sys, RadarSpecs.EcmSysEcm), RadarSpecs.EcmEp);
                    var bands = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var f in Seq(_a.Get(ep, RadarSpecs.EpFrequencies))) { string b = BandName(f.ToString()); if (b.Length > 0) bands.Add(b); }
                    if (bands.Count <= bestBands) continue;
                    bestBands = bands.Count;
                    ew.Operable = true;
                    ew.Bands = bands.ToArray();
                    ew.Type = (_a.Get(ep, RadarSpecs.EpType) ?? "").ToString();
                    ew.StationName = _a.Get(sys, StrikeSpecs.SysSystemName) as string;
                    double cone = GameApi.D(_a.Get(ep, StrikeSpecs.EpConeFov)) ?? 0;
                    ew.ConeDeg = cone > 0 && cone < 360 ? cone : 30;
                    double range = (GameApi.D(_a.Get(ep, StrikeSpecs.EpMaxRange)) ?? 0) / UnitsPerNm;
                    ew.RangeNm = range > 0 ? range : 540;
                    ew.On = BoolOf(_a.RxGet(sys, RadarSpecs.EcmIsOn));
                }
                return ew;
            }

            /// <summary>The encyclopedia entry of a class (aircraft or ship), variant 0; null = not cached yet.</summary>
            private object ClassEntry(string ini)
            {
                try
                {
                    var enc = _a.RrEncyclopedia();
                    var entries = enc == null ? null : _a.Get(enc, RangeRingSpecs.EncEntries) as IEnumerable;
                    if (entries == null || ini == null) return null;
                    foreach (var e in entries)
                        if (e != null && (_a.Get(e, RangeRingSpecs.EntryIniRef) as string) == ini && GameApi.D(_a.Get(e, RangeRingSpecs.EntryVariantIndex)) == 0) return e;
                }
                catch (Exception) { }
                return null;
            }

            /// <summary>The radars of an identified hostile class: the bands and the longest range (nm); false = the entry or the members are not there (unknown).</summary>
            private bool ClassRadars(string ini, out string[] bands, out double maxNm)
            {
                bands = null; maxNm = 0;
                try
                {
                    if (!_a.Present(StrikeSpecs.RadarEntryType)) return false;
                    var entry = ClassEntry(ini);
                    if (entry == null) return false;
                    var set = new SortedSet<string>(StringComparer.Ordinal);
                    bool any = false;
                    foreach (var s in Seq(_a.Get(entry, RangeRingSpecs.EntrySensors)))
                    {
                        if (!_a.IsOfType(s, StrikeSpecs.RadarEntryType)) continue;
                        any = true;
                        foreach (string part in ((_a.Get(s, StrikeSpecs.RadarEntryFreq) as string) ?? "").Split(new[] { ',', '/', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string b = BandName(part);
                            if (b.Length > 0) set.Add(b);
                        }
                        maxNm = Math.Max(maxNm, RrNm(_a, _a.Get(s, StrikeSpecs.RadarEntryMax)));
                    }
                    if (!any) return false;
                    bands = set.ToArray();
                    return true;
                }
                catch (Exception) { return false; }
            }

            /// <summary>The deck task of a jammer aircraft: the loadout is the jammer mission ("EW", the loadout of the EA-6B task of QA 03) or the class has an offensive jammer entry (JamChannels > 0, type Offensive | Universal);
            /// the bands come from the entry when it can be read (else unknown = no band check).</summary>
            private StrikeEw DeckEw(string type, string loadout)
            {
                try
                {
                    bool nameEw = string.Equals(loadout, "EW", StringComparison.OrdinalIgnoreCase);
                    var bands = new SortedSet<string>(StringComparer.Ordinal);
                    bool entryEw = false;
                    var entry = _a.Present(StrikeSpecs.EcmEntryType) ? ClassEntry(type) : null;
                    if (entry != null)
                        foreach (var s in Seq(_a.Get(entry, RangeRingSpecs.EntrySensors)))
                        {
                            if (!_a.IsOfType(s, StrikeSpecs.EcmEntryType)) continue;
                            string kind = (_a.Get(s, StrikeSpecs.EcmEntryKind) as string) ?? "";
                            bool offensive = kind.StartsWith("Offensive", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("Universal", StringComparison.OrdinalIgnoreCase);
                            if (!offensive || (GameApi.D(_a.Get(s, StrikeSpecs.EcmEntryChannels)) ?? 0) <= 0) continue;
                            entryEw = true;
                            foreach (var f in Seq(_a.Get(s, StrikeSpecs.EcmEntryFreqs))) { string b = BandName(f.ToString()); if (b.Length > 0) bands.Add(b); }
                        }
                    if (!nameEw && !entryEw) return null;
                    return new StrikeEw { Capable = EwCapable, Operable = true, ConeDeg = 30, RangeNm = 540, Bands = bands.ToArray(), Stations = 1, Type = "Offensive", StationName = type };
                }
                catch (Exception) { return null; }
            }

            /// <summary>null = ok | no_api | no_station | inoperable | unit_not_found | failed:&lt;exception&gt;. on: the stations are switched on (IsOn first: JamTask refuses an off station) and the task is given by the point;
            /// off: the offensive jam is ceased and the stations are switched off (Universal ones too).</summary>
            public string Jam(int uid, GeoPt pt, bool on)
            {
                try
                {
                    var u = _a.FindOwnUnit(uid);
                    if (u == null) return "unit_not_found";
                    if (!EwCapable) return "no_api";
                    var stations = EwStations(u);
                    if (stations.Count == 0) return "no_station";
                    var live = stations.Where(s => !BoolOf(_a.RxGet(s, RadarSpecs.EcmInoperable)) && !BoolOf(_a.Get(s, RadarSpecs.EcmIsDestroyed))).ToList();
                    if (on)
                    {
                        if (live.Count == 0) return "inoperable";
                        foreach (var s in live) _a.RxSet(s, RadarSpecs.EcmIsOn, true);
                        var geo = _a.New(RangeRingSpecs.GeoNew, pt.Lat, pt.Lon, 0.0);
                        try { _a.New(StrikeSpecs.JamTaskCtor, u, null, geo, false, true); }   // the constructor executes the order
                        catch (Exception e) { _a.StrikeLog("[Strike] strike jam ctor threw (the check of the stations decides): " + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message); }
                        if (_a.Present(StrikeSpecs.EcmGeoTargeted) && !live.Any(s => BoolOf(_a.Get(s, StrikeSpecs.EcmGeoTargeted)))) return "failed:not_targeted";
                        return null;
                    }
                    foreach (var s in stations)
                    {
                        try { var ecm = _a.Get(s, RadarSpecs.EcmSysEcm); if (ecm != null) _a.Call(ecm, StrikeSpecs.EcmCease); } catch (Exception) { }
                        try { _a.RxSet(s, RadarSpecs.EcmIsOn, false); } catch (Exception) { }
                    }
                    return null;
                }
                catch (Exception e) { _a.StrikeLog("[Strike] strike jam failed: " + (e.InnerException ?? e)); return "failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            /// <summary>T-342 D: the jam by the OBJECT of a hostile track (JamTask with the second ObjectBase set and no point: the cone follows the track). null = ok | no_api | no_station | inoperable | unit_not_found | no_track | failed:&lt;..&gt;.
            /// The object of the track is only handed to the order, as the interface of the game does when it is told to jam a contact; nothing is read from it. off: the same as Jam(off) (ECM.ceaseOffensiveJam + IsOn false):
            /// a repeated JamTask by the same object toggles, which could switch the jam on again when the game has dropped the task already. A JamTask by an object has not been called live yet (the live check is one series after phase D),
            /// so this method is the only place to change if it does not hold.</summary>
            public string JamObject(int uid, int trackId, bool on)
            {
                try
                {
                    if (!on) return Jam(uid, default(GeoPt), false);
                    var u = _a.FindOwnUnit(uid);
                    if (u == null) return "unit_not_found";
                    if (!EwCapable) return "no_api";
                    var stations = EwStations(u);
                    if (stations.Count == 0) return "no_station";
                    var live = stations.Where(s => !BoolOf(_a.RxGet(s, RadarSpecs.EcmInoperable)) && !BoolOf(_a.Get(s, RadarSpecs.EcmIsDestroyed))).ToList();
                    if (live.Count == 0) return "inoperable";
                    var cv = _a.FindContact(trackId);
                    var obj = cv == null ? null : _a.Get(cv, CoreSpecs.VehObject);
                    if (obj == null) return "no_track";
                    foreach (var s in live) _a.RxSet(s, RadarSpecs.EcmIsOn, true);
                    try { _a.New(StrikeSpecs.JamTaskCtor, u, obj, null, false, true); }
                    catch (Exception e)
                    {
                        // unlike Jam(point) there is no station member that says "held by object" (EcmGeoTargeted is for a point), so a thrown constructor is the only failure signal: the order is not given
                        var ie = e.InnerException ?? e;
                        _a.StrikeLog("[Strike] strike jam object ctor threw: " + ie.GetType().Name + ": " + ie.Message);
                        return "failed:ctor_" + ie.GetType().Name;
                    }
                    return null;   // no member says which target a station holds: the live check reads target_jammers of the track (dev probe)
                }
                catch (Exception e) { _a.StrikeLog("[Strike] strike jam object failed: " + (e.InnerException ?? e)); return "failed:" + (e.InnerException ?? e).GetType().Name; }
            }

            /// <summary>The game still holds an attack task of the aircraft on this track (_currentEngageTasks); null = not readable.</summary>
            public bool? HasAttackTask(int uid, int targetId)
            {
                try
                {
                    if (!_a.Present(CapSpecs.ObjEngageTasks) || !_a.Present(StrikeSpecs.TaskTarget)) return null;
                    var u = _a.FindOwnUnit(uid);
                    if (u == null) return null;
                    var tasks = _a.Get(u, CapSpecs.ObjEngageTasks) as IEnumerable;
                    if (tasks == null) return null;
                    foreach (var t in tasks)
                    {
                        if (t == null) continue;
                        var target = _a.Get(t, StrikeSpecs.TaskTarget);
                        if (target != null && IsAlive(target) && _a.RrMenuTrack(target) == targetId) return true;
                    }
                    return false;
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>strike_probe op=ew (dev): without "on" the status (stations, the task of the aircraft, what the target says about jammers); with "on" the same order the strike gives.</summary>
        private string ProbeEw(IStrikeGame g, int uid, int tid, bool? on, GeoPt? at, bool byObject = false)
        {
            if (uid <= 0) return "!bad_args|uid";
            var a = g.Aircraft().FirstOrDefault(x => x.Uid == uid);
            if (a == null) return "!no_aircraft";
            var u = FindOwnUnit(uid);
            if (u == null) return "!no_aircraft";
            GeoPt pt = at ?? new GeoPt(a.Lat, a.Lon);
            var ship = tid < 0 ? null : g.Ships().FirstOrDefault(x => x.TrackId == tid);
            bool airTarget = tid >= 0 && ship == null && g.Fighters().Any(f => f.TrackId == tid);   // T-342 D: a hostile air track (target_kind air)
            if (on.HasValue && byObject)
            {
                string oerr = g.JamObject(uid, tid, on.Value);
                StrikeLog("[Strike] strike probe ew uid=" + uid.ToString(CultureInfo.InvariantCulture) + " on=" + on.Value + " obj=" + tid.ToString(CultureInfo.InvariantCulture) + " -> " + (oerr ?? "ok"));
                if (oerr != null) return "!" + oerr;
            }
            else if (on.HasValue)
            {
                if (ship != null) pt = new GeoPt(ship.Lat, ship.Lon);
                else if (!at.HasValue) return "!bad_args|target_id";
                string err = g.Jam(uid, pt, on.Value);
                StrikeLog("[Strike] strike probe ew uid=" + uid.ToString(CultureInfo.InvariantCulture) + " on=" + on.Value + " pt=" + pt.Lat.ToString("0.0000", CultureInfo.InvariantCulture) + "," + pt.Lon.ToString("0.0000", CultureInfo.InvariantCulture) + " -> " + (err ?? "ok"));
                if (err != null) return "!" + err;
            }
            var sb = new System.Text.StringBuilder("{\"ok\":true,\"op\":\"ew\",\"uid\":" + uid.ToString(CultureInfo.InvariantCulture) + ",\"capable\":" + Json.Bool(g.EwCapable) + ",\"stations\":[");
            int n = 0;
            var impl = g as StrikeGameImpl;
            foreach (var s in (impl == null ? new List<object>() : impl.StationsOf(u)))
            {
                var ep = Get(Get(s, RadarSpecs.EcmSysEcm), RadarSpecs.EcmEp);
                var bands = new List<string>();
                foreach (var f in (Get(ep, RadarSpecs.EpFrequencies) as IEnumerable) ?? new object[0]) bands.Add(Json.Str(StrikeGameImpl.BandName(f.ToString())));
                if (n++ > 0) sb.Append(',');
                sb.Append("{\"type\":" + Json.Str((Get(ep, RadarSpecs.EpType) ?? "").ToString()) + ",\"name\":" + Json.Str(Get(s, StrikeSpecs.SysSystemName) as string ?? "") + ",\"bands\":[" + string.Join(",", bands)
                          + "],\"is_on\":" + Json.Bool(BoolOf(RxGet(s, RadarSpecs.EcmIsOn))) + ",\"inoperable\":" + Json.Bool(BoolOf(RxGet(s, RadarSpecs.EcmInoperable))) + ",\"cone_deg\":" + Json.Num(Math.Round(D(Get(ep, StrikeSpecs.EpConeFov)) ?? 0, 1))
                          + ",\"geo_targeted\":" + Json.Bool(BoolOf(Get(s, StrikeSpecs.EcmGeoTargeted))) + "}");
            }
            sb.Append("],\"task\":" + Json.Str(impl != null && impl.AnyJamming(u) ? "jam" : "none"));
            if (ship != null || airTarget)
            {
                // DEV ONLY: the hostile object's own list of jammers on it and the range of its search radar against the aircraft (the true game state, for the check of the jam order)
                int jammers = -1;
                var cv = FindContact(tid);
                var tobj = cv == null ? null : Get(cv, CoreSpecs.VehObject);   // DEV ONLY: the true object behind the track
                try { var list = tobj == null ? null : Get(tobj, StrikeSpecs.ObjJammersOfMe) as ICollection; if (list != null) jammers = list.Count; } catch (Exception) { }
                sb.Append(",\"target_kind\":" + Json.Str(airTarget ? "air" : "surface"));
                sb.Append(",\"target_jammers\":" + (jammers < 0 ? "null" : jammers.ToString(CultureInfo.InvariantCulture)));
                double rng = -1; string rngIssue = null; var rngList = new List<string>();   // T-346: every search radar with its range and the issue (the reason the range is cut), the maximum keeps its issue
                try
                {
                    if (tobj != null)
                    {
                        var obp = Get(tobj, RadarSpecs.ObjObp);
                        foreach (var rs in (Get(obp, RadarSpecs.ParamsSearch) as IEnumerable) ?? new object[0])
                        {
                            if (rs == null || !Present(RadarSpecs.SysGetRange)) continue;
                            var r = Call(rs, RadarSpecs.SysGetRange, u);
                            double v = D(r != null ? (Get(r, RadarSpecs.DistDistance) ?? r) : null) ?? -1;
                            string issue = r == null ? "" : (Get(r, RadarSpecs.DistIssue) ?? "").ToString();
                            rngList.Add("{\"radar\":" + Json.Str(Get(rs, StrikeSpecs.SysSystemName) as string ?? "") + ",\"nm\":" + (v < 0 ? "null" : Json.Num(Math.Round(v / 27.559496, 1))) + ",\"issue\":" + Json.Str(issue) + "}");
                            if (v > rng) { rng = v; rngIssue = issue; }
                        }
                    }
                }
                catch (Exception) { }
                sb.Append(",\"target_range_nm\":" + (rng < 0 ? "null" : Json.Num(Math.Round(rng / 27.559496, 1))));
                sb.Append(",\"target_range_issue\":" + (rngIssue == null ? "null" : Json.Str(rngIssue)) + ",\"target_ranges\":[" + string.Join(",", rngList) + "]");
            }
            return sb.Append('}').ToString();
        }
    }
}
