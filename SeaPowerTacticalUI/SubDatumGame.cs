using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-288 / ADR-0115: the game side of the sub launch area. Main thread. Reads the player's own plotting table (torpedo, air and submarine tracks)
    // and the own ships; offers the tracks to the SubDatumBook (pure). Never reads a position of a foreign object: only tracks.
    internal sealed partial class GameApi
    {
        internal SubDatumBook SubDatum;                                         // null = module off
        internal ISubDatumHost SubDatumHost;                                    // set by the feature: the ASW groups for the button and the search order

        /// <summary>What the game side needs from the module (a service, not a delegate field: ADR-0044 guard).</summary>
        internal interface ISubDatumHost
        {
            /// <summary>Main thread. The ASW groups with their orders and the ETA to (lat, lon); null = no ASW.</summary>
            List<SubDatumGroupIn> Groups(double lat, double lon);
            /// <summary>(sector id, group?) -> null | error code.</summary>
            string Search(int id, string group);
        }

        private sealed class SdFix { public double FirstLat, FirstLon, FirstS, LastLat, LastLon, LastS; }
        private sealed class SdRun { public bool Identified; public double RunNm; public string Ammo; }
        private sealed class SdOwn { public double Lat, Lon; }
        private sealed class SdTorp { public int Id; public string Rel; public double Lat, Lon; public object Veh; }

        private readonly Dictionary<int, SdFix> _sdFix = new Dictionary<int, SdFix>();
        private readonly Dictionary<int, SdRun> _sdRun = new Dictionary<int, SdRun>();
        private readonly List<double[]> _sdAir = new List<double[]>();          // t, lat, lon of hostile / unknown air tracks
        private bool _sdErrLogged;

        /// <summary>Torpedo / air / submarine tracks -> Offer / NoteSub. One round per call (the feature calls it once a second).</summary>
        internal void SubDatumPoll(double nowS)
        {
            var book = SubDatum;
            if (book == null || !Frame.InMission) return;
            try
            {
                var p = book.Params;
                var owns = SdOwns();
                var torps = new List<SdTorp>();
                var subs = new List<double[]>();
                var live = new HashSet<int>();
                var vehicles = Vehicles();
                if (vehicles != null)
                    foreach (var c in vehicles)
                    {
                        string kind = Kind(c);
                        if (kind != "Torpedo" && kind != "Submarine" && kind != "Air" && kind != "Helicopter") continue;
                        bool gone = IsGone(c);
                        string rel = Relation(c);
                        var pos = TrackPos(c);
                        if (kind == "Torpedo")
                        {
                            var id = D(Get(c, CoreSpecs.VehId));
                            if (!id.HasValue) continue;
                            if (!TorpedoTrack.Live(gone, rel, pos != null)) continue;
                            live.Add((int)id.Value);
                            torps.Add(new SdTorp { Id = (int)id.Value, Rel = rel, Lat = pos[0], Lon = pos[1], Veh = c });
                        }
                        else if (gone || pos == null || (rel != "hostile" && rel != "unknown")) continue;
                        else if (kind == "Submarine") subs.Add(pos);
                        else _sdAir.Add(new[] { nowS, pos[0], pos[1] });
                    }
                _sdAir.RemoveAll(a => nowS - a[0] > p.AirSkipS || nowS < a[0]);
                foreach (var t in torps) SdOffer(book, p, t, owns, nowS);
                foreach (var s in subs) book.NoteSub(s[0], s[1], nowS);
                SdPrune(live);
                book.Tick(nowS);
            }
            catch (Exception e)
            {
                if (!_sdErrLogged) { _sdErrLogged = true; Log("[SubDatum] poll failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private List<SdOwn> SdOwns()
        {
            var res = new List<SdOwn>();
            var tf = GetStatic(CoreSpecs.PlayerTaskforce);
            foreach (var listSpec in CoreSpecs.OwnShipLists)
            {
                var list = Get(tf, listSpec) as IEnumerable;
                if (list == null) continue;
                foreach (var o in list)
                {
                    double la, lo, h;
                    if (o != null && OwnPos(o, out la, out lo, out h)) res.Add(new SdOwn { Lat = la, Lon = lo });
                }
            }
            return res;
        }

        private void SdOffer(SubDatumBook book, SubDatumParams p, SdTorp t, List<SdOwn> owns, double nowS)
        {
            SdFix f;
            if (!_sdFix.TryGetValue(t.Id, out f)) _sdFix[t.Id] = f = new SdFix { FirstLat = t.Lat, FirstLon = t.Lon, FirstS = nowS, LastLat = t.Lat, LastLon = t.Lon, LastS = nowS };
            else if (WorldDistanceNm(f.LastLat, f.LastLon, t.Lat, t.Lon) > 0.005) { f.LastLat = t.Lat; f.LastLon = t.Lon; f.LastS = nowS; }

            // P0 = the first fix of the track: the launch lies behind it, wherever the torpedo is now
            var tin = new SubDatumTrackIn { TrackId = t.Id, Lat = f.FirstLat, Lon = f.FirstLon, NowS = nowS, Relation = t.Rel };
            tin.Globe = AswMath.WorldGlobe;   // T-352
            var run = SdRunOf(t);
            tin.Identified = run.Identified; tin.Ammo = run.Ammo; tin.RunNm = run.RunNm > 0 ? run.RunNm : p.UnknownRunNm;

            var ax = tin.Axis;
            double vx, vz;
            if (SdVelocity(t.Veh, t.Lat, t.Lon, out vx, out vz))
            {
                ax.VelKn = Math.Sqrt(vx * vx + vz * vz) * OverviewMath.KnPerUnity;
                ax.VelCourseDeg = (Math.Atan2(vx, vz) * 180.0 / Math.PI + 360.0) % 360.0;
            }
            if (f.LastS - f.FirstS > 0)
            {
                ax.FixDtS = f.LastS - f.FirstS;
                ax.FixDistNm = WorldDistanceNm(f.FirstLat, f.FirstLon, f.LastLat, f.LastLon);
                ax.FixCourseDeg = WorldBearing(f.FirstLat, f.FirstLon, f.LastLat, f.LastLon);
            }
            double best = double.PositiveInfinity; SdOwn nearest = null;
            foreach (var o in owns)
            {
                double d = WorldDistanceNm(o.Lat, o.Lon, f.FirstLat, f.FirstLon);
                if (d < best) { best = d; nearest = o; }
                tin.OwnShipsFromP0.Add(SubDatumMath.ToLocal(f.FirstLat, f.FirstLon, o.Lat, o.Lon, tin.Globe));
            }
            tin.NearestOwnNm = best;
            if (nearest != null) ax.LosBrgDeg = WorldBearing(nearest.Lat, nearest.Lon, f.FirstLat, f.FirstLon);
            foreach (var a in _sdAir)
                if (WorldDistanceNm(a[1], a[2], f.FirstLat, f.FirstLon) <= p.AirSkipNm) { tin.AirNear = true; break; }
            book.Offer(tin);
        }

        private bool SdVelocity(object veh, double lat, double lon, out double vx, out double vz)
        {
            return GridVelTrue(veh, SubDatumSpecs.VehVelocity, SubDatumSpecs.VecX, SubDatumSpecs.VecZ, lat, lon, out vx, out vz);   // T-308: true axes at the track
        }

        /// <summary>Identified: the stage ranges / max launch range of the torpedo class from the game; the class name for the card. Cached per track once identified.</summary>
        private SdRun SdRunOf(SdTorp t)
        {
            SdRun r;
            if (_sdRun.TryGetValue(t.Id, out r)) return r;
            r = new SdRun();
            var p = SubDatum == null ? new SubDatumParams() : SubDatum.Params;
            try
            {
                if (!BoolOf(RxGet(t.Veh, SubDatumSpecs.VehIdentified))) { r.RunNm = p.UnknownRunNm; return r; }   // not cached: the player may identify it later
                r.Identified = true;
                r.Ammo = Get(Get(t.Veh, SubDatumSpecs.VehClass), SubDatumSpecs.SourcedValue) as string;
                var ap = Get(Get(t.Veh, SubDatumSpecs.VehObject), SubDatumSpecs.WpnAp);
                var stages = new List<double>();
                var steps = ap == null ? null : Get(ap, SubDatumSpecs.ApSpeedSettings) as IEnumerable;
                if (steps != null) foreach (var s in steps) stages.Add((D(Get(s, SubDatumSpecs.VecY)) ?? 0) / SaUnityPerNm);
                double maxLaunch = ap == null ? 0 : (D(Get(ap, SubDatumSpecs.ApMaxLaunchRange)) ?? 0);
                r.RunNm = SubDatumMath.RunNm(true, stages, maxLaunch, p.UnknownRunNm);
            }
            catch (Exception) { r.RunNm = p.UnknownRunNm; }
            _sdRun[t.Id] = r;
            return r;
        }

        private void SdPrune(HashSet<int> live)
        {
            List<int> dead = null;
            foreach (int k in _sdFix.Keys) if (!live.Contains(k)) (dead ?? (dead = new List<int>())).Add(k);
            if (dead != null) foreach (int k in dead) { _sdFix.Remove(k); _sdRun.Remove(k); }
        }

        // ---------- commands ----------

        /// <summary>sub_datum_list {limit?} | sub_datum_search {id, group?} | sub_datum_dismiss {id}. Main thread. Null = not ours.
        /// Order: unsupported, bad_args, no_datum, not_in_session.</summary>
        internal string SubDatumCmd(string id, string name, Dictionary<string, object> cmd)
        {
            if (name != "sub_datum_list" && name != "sub_datum_search" && name != "sub_datum_dismiss") return null;
            var book = SubDatum;
            if (book == null) return Err(id, "unsupported", "sub_datum module is off");
            int dId, limit; bool hasId, hasLimit;
            if (!IcInt(cmd, "id", out dId, out hasId) || !IcInt(cmd, "limit", out limit, out hasLimit)) return Err(id, "bad_args", null);
            object go = null;
            if (cmd != null) cmd.TryGetValue("group", out go);
            if (go != null && !(go is string)) return Err(id, "bad_args", "group must be a string");
            string group = go as string;
            double now = CiMissionTimeS();
            if (name == "sub_datum_list")
            {
                if (!Frame.InMission) return Err(id, "not_in_session", null);
                return SmartAck(id, "{\"datums\":" + book.Json(hasLimit && limit > 0 ? limit : 20, now) + "}");
            }
            if (!hasId || dId <= 0) return Err(id, "bad_args", "id is required");
            var d = book.Find(dId);
            if (d == null) return Err(id, "no_datum", null);
            if (!Frame.InMission) return Err(id, "not_in_session", null);
            if (name == "sub_datum_dismiss")
                return book.Dismiss(dId, now) ? SmartAck(id, "{\"result\":\"dismissed\"}") : Err(id, "no_datum", null);
            if (d.State != "live") return Err(id, "closed", null);
            var host = SubDatumHost;
            if (host == null) return Err(id, "no_group", null);
            string code = host.Search(dId, group);
            if (code != null) return Err(id, code, null);
            var rect = new System.Text.StringBuilder("[");
            if (d.SearchRect != null) for (int i = 0; i < d.SearchRect.Length; i++) { if (i > 0) rect.Append(','); rect.Append(Json.Num(d.SearchRect[i])); }
            rect.Append(']');
            return SmartAck(id, "{\"id\":" + dId + ",\"group\":" + Json.Str(d.SearchGroup) + ",\"rect\":" + rect + "}");
        }

        /// <summary>Event "sub_datum" {kind: new|refine|merge|search|closed, id, ...} for the dev channel.</summary>
        internal void SubDatumEvent(string dataJson) { try { EvLine("sub_datum", dataJson); } catch (Exception) { } }

        internal void SubDatumLeaveMission()
        {
            _sdFix.Clear(); _sdRun.Clear(); _sdAir.Clear();
            if (SubDatum != null) SubDatum.Clear();
        }
    }
}
