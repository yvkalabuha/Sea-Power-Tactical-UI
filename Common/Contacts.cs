using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    /// <summary>One pass over the plotting table per tick, shared by the `contacts` command, state.contacts and the Overview window. T-070.</summary>
    internal sealed class ContactSnapshot
    {
        public double T;
        public bool Overview;                            // panel fields (velocity, altitude, icon type...) were read too
        public List<OvTrack> Tracks = new List<OvTrack>();
        public int Total;
        public Dictionary<int, object> VehicleById = new Dictionary<int, object>();
    }

    internal sealed partial class GameApi
    {
        private ContactSnapshot _snap;

        private object _lastOwn;
        private readonly Dictionary<int, double> _liveSeen = new Dictionary<int, double>();

        // ---------------------------------------------------------------- snapshot

        private static string StrOrNull(object o) { return o == null ? null : o.ToString(); }

        private uint ColourOfBrush(object brush)
        {
            var col = Get(brush, "Color");
            if (col == null) return 0;
            double? r = D(Get(col, "R")), g = D(Get(col, "G")), b = D(Get(col, "B")), a = D(Get(col, "A"));
            if (!r.HasValue || !g.HasValue || !b.HasValue) return 0;
            double scale = Math.Max(Math.Max(r.Value, g.Value), Math.Max(b.Value, a ?? 0)) > 1.0001 ? 1.0 : 255.0;
            Func<double, uint> c = x => (uint)Math.Max(0, Math.Min(255, Math.Round(x * scale)));
            double alpha = a ?? (scale == 255.0 ? 1.0 : 255.0);
            return (c(alpha) << 24) | (c(r.Value) << 16) | (c(g.Value) << 8) | c(b.Value);
        }

        private System.Reflection.MethodInfo _resolveVisuals;   // cached once the type is found: no FindType / GetMethods per track
        private bool _resolveTwoArgs;

        /// <summary>Picks the overload by its parameter types: Resolve(IUnit, MapViewModel) on the beta, Resolve(IUnit) on 261021 (then the count decides).</summary>
        private void PickResolveVisuals()
        {
            var t = FindType("SeapowerUI.MapContactVisuals");
            if (t == null) return;   // not loaded yet: ask again next time
            System.Reflection.MethodInfo two = null, twoAny = null, one = null;
            foreach (var c in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
            {
                if (c.Name != "Resolve") continue;
                var ps = c.GetParameters();
                if (ps.Length == 2)
                {
                    if (two == null && ps[1].ParameterType.Name == "MapViewModel") two = c;
                    if (twoAny == null) twoAny = c;
                }
                else if (ps.Length == 1 && one == null) one = c;
            }
            var pick = two ?? one ?? twoAny;
            _resolveVisuals = pick;
            _resolveTwoArgs = pick != null && pick.GetParameters().Length == 2;
            if (pick == null) _resolveVisuals = null;
        }

        /// <summary>Resolve(unit) on 261021, Resolve(unit, MapViewModel) on the beta; the map comes from the main view model (null = the game's day palette).</summary>
        private object ResolveVisuals(object v)
        {
            if (_resolveVisuals == null) PickResolveVisuals();
            var m = _resolveVisuals;
            if (m == null) return CallStatic("SeapowerUI.MapContactVisuals", "Resolve", v);
            if (!_resolveTwoArgs) return m.Invoke(null, new[] { v });
            object map = null;
            try { map = Get(Get(GetStatic("SeaPower.Globals", "_mainGameViewModel"), "Map"), "DisplayMap"); } catch { }
            return m.Invoke(null, new[] { v, map });
        }

        /// <summary>The main map's own colour rule: SeapowerUI.MapContactVisuals.Resolve(unit) -> (posture, brush). false on any error (T-092).</summary>
        private bool MapVisuals(object v, out string posture, out uint argb)
        {
            posture = null; argb = 0;
            try
            {
                var r = ResolveVisuals(v);
                if (r == null) return false;
                posture = StrOrNull(Get(r, "Item1"));
                argb = ColourOfBrush(Get(r, "Item2"));
                return posture != null && argb != 0;
            }
            catch { posture = null; argb = 0; return false; }
        }

        /// <summary>Reads one vehicle into a track (body of the Snapshot loop, testable without the game).</summary>
        internal OvTrack ReadTrack(object v, bool forOverview, double now)
        {
            var t = new OvTrack();
            var p = TrackPos(v);
            var id = D(Get(v, "Id"));
            t.HasId = id.HasValue; t.Id = id.HasValue ? (int)id.Value : -1;
            if (p != null) { t.Lat = p[0]; t.Lon = p[1]; }
            t.Side = Relation(v);
            t.Kind = Kind(v);
            var ident = RxGet(v, "Identified");
            t.IdentifiedKnown = ident is bool; t.Identified = ident is bool && (bool)ident;
            var cl = Get(v, "IsClassified");
            if (cl is bool) t.Classified = (bool)cl;
            t.Class = Get(Get(v, "Class"), "Value") as string;
            t.ClassName = ClassDisplayName(v, t.Class);
            if (forOverview)
            {
                try { FillPanelFields(t, v); } catch { }
                if (t.HasId && t.Live) _liveSeen[t.Id] = now;
            }
            return t;
        }

        /// <summary>One pass over Vehicles(); cached for the current tick (Clock). forOverview also reads velocity/altitude/icon fields.</summary>
        internal ContactSnapshot Snapshot(bool forOverview)
        {
            double now = Clock();
            if (_snap != null && _snap.T == now && (_snap.Overview || !forOverview)) return _snap;
            var s = new ContactSnapshot { T = now, Overview = forOverview };
            var vehicles = Vehicles();
            if (vehicles != null)
            {
                foreach (var v in vehicles)
                {
                    s.Total++;
                    try
                    {
                        var t = ReadTrack(v, forOverview, now);
                        s.Tracks.Add(t);
                        if (t.HasId) s.VehicleById[t.Id] = v;
                    }
                    catch { }
                }
            }
            _snap = s;
            return s;
        }

        private readonly Dictionary<string, string> _classNames = new Dictionary<string, string>();

        /// <summary>Display class name of the game (ObjectBase.ClassNameShort) when the track's class is the object's own; null otherwise (T-080).</summary>
        private string ClassDisplayName(object vehicle, string classIni)
        {
            if (string.IsNullOrEmpty(classIni)) return null;
            string cached;
            if (_classNames.TryGetValue(classIni, out cached)) return cached;
            string name = null;
            try
            {
                var obj = Get(vehicle, "Object");
                if (obj != null)
                {
                    var ini = Call(obj, "getObjectIniName") as string;
                    if (ini == classIni)
                    {
                        name = Get(obj, "ClassNameShort") as string;
                        if (string.IsNullOrWhiteSpace(name)) name = null;
                    }
                    else return null;   // misidentified contact: do not reveal the true object, not cached
                }
                else return null;
            }
            catch { return null; }
            _classNames[classIni] = name;
            return name;
        }

        /// <summary>Vehicle.Altitude (SourcedProperty&lt;PointEstimate&gt;?) -> Value -> Estimate; null when the player's track has no altitude. Never reads Object (T-209).</summary>
        private double? TrackAltitudeUnity(object vehicle)
        {
            try { return D(Get(Get(Get(vehicle, "Altitude"), "Value"), "Estimate")); } catch { return null; }
        }

        private void FillPanelFields(OvTrack t, object v)
        {
            // T-092: side, posture, type and colour follow the main map's rules; Vehicle.ViewModel is the SECOND map's model (never updated)
            t.Posture = OverviewMath.PostureOf(t.Side);
            t.VisualType = t.Kind;
            bool hasTf = Get(Get(v, "UnitTaskforce"), "Value") != null;
            t.SideSrc = hasTf ? "track" : (t.Side == "unknown" ? "none" : "override");
            t.Dormant = BoolOf(RxGet(v, "IsDormant"));
            var vm = Get(v, "ViewModel");
            t.VmPosture = StrOrNull(Get(vm, "VisualPosture"));
            bool destroyed = BoolOf(Get(Get(v, "Object"), "IsDestroyed"));
            string mp; uint argb;
            if (MapVisuals(v, out mp, out argb)) { t.MapPosture = mp; t.Colour = argb; t.ColourSrc = "map_visuals"; }
            else { t.Colour = OverviewMath.FallbackColour(t.Posture, t.Dormant, destroyed); t.ColourSrc = "fallback"; }
            var obj = Get(v, "Object");
            t.ObjType = StrOrNull(Get(obj, "_type"));   // leak-ok: ASW only (AswMath.DecoyName / AswStrikeMath.BiologicName); the Overview display uses Kind (T-210)
            double gvx, gvz;   // T-308: true axes (the track's own position picks the convergence)
            if (GridVelTrueOf(v, "UnityVelocityVector", t.Lat ?? double.NaN, t.Lon ?? double.NaN, out gvx, out gvz)) { t.Vx = gvx; t.Vz = gvz; }
            else { t.Vx = null; t.Vz = null; }
            t.AltFt = OverviewMath.TrackAltFt(TrackAltitudeUnity(v));   // track: the player's altitude estimate (T-209), never the true object
            var live = Get(v, "HasLiveTrack");
            t.Live = !(live is bool) || (bool)live;
            var fan = Get(v, "BearingFan");
            t.Fan = fan != null;
            if (fan != null) t.FanBearing = D(Get(fan, "Heading"));   // grid-ok: BearingFanViewModel.Heading is already true (the game converts it)
            // T-206: how the player's track is seen (flags) and how wide its position error is; both are the player's own track data
            try { var ds = Get(v, "DetectingSensors"); if (ds != null) t.Sensors = Convert.ToInt32(ds); } catch { t.Sensors = 0; }
            try { t.PosSigmaM = D(Get(Get(Get(v, "PositionEstimate"), "Item2"), "MajorStdDev")); } catch { t.PosSigmaM = null; }
            t.Gone = IsGone(v);
            try { var vis = Call(v, "IsVisible"); t.Visible = !(vis is bool) || (bool)vis; } catch { t.Visible = true; }
        }

        /// <summary>The `contacts` / state.contacts JSON. Same bytes as before T-070 (see ContactsParity).</summary>
        private string ContactsJson(int max, object sel)
        {
            double lat0 = 0, lon0 = 0, h; bool haveSel = sel != null && OwnPos(sel, out lat0, out lon0, out h);
            return ContactsJsonFrom(Snapshot(false), max, haveSel, lat0, lon0);
        }

        internal string ContactsJsonFrom(ContactSnapshot s, int max, object sel)
        {
            double lat0 = 0, lon0 = 0, h; bool haveSel = sel != null && OwnPos(sel, out lat0, out lon0, out h);
            return ContactsJsonFrom(s, max, haveSel, lat0, lon0);
        }

        internal static string ContactsJsonFrom(ContactSnapshot s, int max, bool haveSel, double lat0, double lon0)
        {
            var items = new List<KeyValuePair<double, string>>();
            foreach (var t in s.Tracks)
            {
                var sb = new StringBuilder("{\"id\":");
                sb.Append(t.HasId ? Json.Num(t.Id) : "null");
                bool hasPos = t.Lat.HasValue && t.Lon.HasValue;
                if (hasPos) sb.Append(",\"lat\":").Append(Json.Num(t.Lat.Value)).Append(",\"lon\":").Append(Json.Num(t.Lon.Value));
                sb.Append(",\"side\":").Append(Json.Str(t.Side));
                sb.Append(",\"kind\":").Append(Json.Str(t.Kind));
                sb.Append(",\"identified\":").Append(t.IdentifiedKnown ? Json.Bool(t.Identified) : "null");
                if (t.Classified.HasValue) sb.Append(",\"classified\":").Append(Json.Bool(t.Classified.Value));
                if (!string.IsNullOrEmpty(t.Class)) sb.Append(",\"class\":").Append(Json.Str(t.Class));
                if (!string.IsNullOrEmpty(t.ClassName)) sb.Append(",\"class_name\":").Append(Json.Str(t.ClassName));
                double dist = double.MaxValue;
                if (haveSel && hasPos)
                {
                    dist = MetricDistanceNm(lat0, lon0, t.Lat.Value, t.Lon.Value);
                    sb.Append(",\"bearing_deg\":").Append(Json.Num(Math.Round(MetricBearing(lat0, lon0, t.Lat.Value, t.Lon.Value), 1)));
                    sb.Append(",\"distance_nm\":").Append(Json.Num(Math.Round(dist, 2)));
                }
                items.Add(new KeyValuePair<double, string>(dist, sb.Append('}').ToString()));
            }
            items.Sort((a, b) => a.Key.CompareTo(b.Key));
            var o = new StringBuilder("{\"items\":[");
            for (int i = 0; i < items.Count && i < max; i++) { if (i > 0) o.Append(','); o.Append(items[i].Value); }
            return o.Append("],\"total\":").Append(s.Total).Append('}').ToString();
        }

        /// <summary>selfcheck: synthetic snapshot -> the exact string the pre-0.12 ContactsJson produced for the same data. null = ok.</summary>
        internal string ContactsParity()
        {
            var s = new ContactSnapshot { Total = 4 };
            s.Tracks.Add(new OvTrack { Id = 5, Lat = 15.5, Lon = 42.0, Side = "hostile", Kind = "Surface", IdentifiedKnown = true, Identified = true, Classified = true, Class = "wp_pt_p6", ClassName = "P-6" });   // class_name intentionally added by T-080
            s.Tracks.Add(new OvTrack { Id = 6, Side = "unknown", Kind = "Invisible" });
            s.Tracks.Add(new OvTrack { HasId = false, Lat = 15.1, Lon = 42.1, Side = "friendly", Kind = "Air", IdentifiedKnown = true, Identified = false, Classified = false, Class = "" });
            const string expect =
                "{\"items\":[{\"id\":null,\"lat\":15.1,\"lon\":42.1,\"side\":\"friendly\",\"kind\":\"Air\",\"identified\":false,\"classified\":false,\"bearing_deg\":44,\"distance_nm\":8.34}," +
                "{\"id\":5,\"lat\":15.5,\"lon\":42,\"side\":\"hostile\",\"kind\":\"Surface\",\"identified\":true,\"classified\":true,\"class\":\"wp_pt_p6\",\"class_name\":\"P-6\",\"bearing_deg\":0,\"distance_nm\":30}," +
                "{\"id\":6,\"side\":\"unknown\",\"kind\":\"Invisible\",\"identified\":null}],\"total\":4}";
            string got = ContactsJsonFrom(s, 10, true, 15.0, 42.0);
            if (got != expect) return "got " + got;
            string one = ContactsJsonFrom(s, 1, false, 0, 0);
            if (!one.StartsWith("{\"items\":[{\"id\":5,\"lat\":15.5,") || !one.EndsWith("],\"total\":4}") || one.Contains("bearing_deg")) return "unsorted/max case: " + one;
            return TrackSourcesSelfTest();
        }

        private sealed class FakeRx { public object Value; }
        private sealed class FakeVm { public string VisualPosture = "Enemy", VisualType = "Air"; }
        private sealed class FakeVehicle
        {
            public int Id = 7017;
            public object ViewModel = new FakeVm();
            public FakeRx Identified = new FakeRx { Value = false };
            public FakeRx UnitTaskforce = new FakeRx();
            public FakeRx IsDormant = new FakeRx { Value = false };
            public string Rel = "Unknown";
            public string CurrentRelationship() { return Rel; }
            public string GetMapType() { return "Air"; }
            public bool HasLiveTrack { get { return true; } }
            public int DetectingSensors = 0x200;
            public FakeEst PositionEstimate = new FakeEst();
            public object Altitude = null;   // Vehicle.Altitude: null until a sensor gave the track a height
        }
        private sealed class FakePe { public float Estimate; }
        private sealed class FakeAltSrc { public FakePe Value; }
        private sealed class FakeTrueObj { public FakeRx Altitude = new FakeRx { Value = 30000f }; }
        private sealed class FakeVehicleObj
        {
            public int Id = 7018;
            public FakeTrueObj Object = new FakeTrueObj();
            public object Altitude = null;
            public string GetMapType() { return "Air"; }
        }
        private sealed class FakeTypedObj { public string _type = "Aircraft"; }
        private sealed class FakeVehicleSurf
        {
            public int Id = 7019;
            public object ViewModel = new FakeVm();
            public FakeRx Identified = new FakeRx { Value = false };
            public FakeRx UnitTaskforce = new FakeRx();
            public FakeRx IsDormant = new FakeRx { Value = false };
            public FakeTypedObj Object = new FakeTypedObj();
            public string CurrentRelationship() { return "Unknown"; }
            public string GetMapType() { return "Surface"; }
            public bool HasLiveTrack { get { return true; } }
            public int DetectingSensors = 0x200;
            public FakeEst PositionEstimate = new FakeEst();
            public object Altitude = new FakeAltSrc { Value = new FakePe { Estimate = 40.8213f } };
        }
        private sealed class FakeEig { public float MajorStdDev = 1234f; }
        private sealed class FakeEst { public object Item1 = null; public FakeEig Item2 = new FakeEig(); }

        /// <summary>selfcheck: track sources come from the player's knowledge, not from the second map's view model. null = ok.</summary>
        internal string TrackSourcesSelfTest()
        {
            try
            {
                var f = new FakeVehicle();
                var t = ReadTrack(f, true, 0);
                if (t.Side != "unknown" || t.Posture != "Unknown") return "track sources: unknown side -> " + t.Side + "/" + t.Posture;
                if (t.VisualType != "Air" || t.SideSrc != "none") return "track sources: type/src " + t.VisualType + "/" + t.SideSrc;
                if (t.ColourSrc != "fallback" || t.Colour != OverviewMath.FallbackColour("Unknown", false, false)) return "track sources: colour " + t.Colour.ToString("X8") + " " + t.ColourSrc;
                if (t.VmPosture != "Enemy") return "track sources: vm posture";
                string dl = OverviewMath.DebugLine(t, OverviewMath.MakeRow(t, null, null, null, null, new ClosingFilter(3, 3, 1, 300), 0, null, "ru"));
                if (string.IsNullOrEmpty(dl) || !dl.Contains("src=fallback")) return "track sources: debug line";
                var h = new FakeVehicle { Rel = "Hostile" }; h.UnitTaskforce.Value = new object();
                var th = ReadTrack(h, true, 0);
                if (th.Side != "hostile" || th.SideSrc != "track" || th.Posture != "Enemy") return "track sources: hostile -> " + th.Side + "/" + th.SideSrc + "/" + th.Posture;
                if (t.Sensors != 0x200 || !t.PosSigmaM.HasValue || Math.Abs(t.PosSigmaM.Value - 1234) > 1) return "track sources: sensors/sigma " + t.Sensors + "/" + t.PosSigmaM;
                // T-209: altitude comes from the player's track, never from the true object
                if (t.AltFt.HasValue) return "track sources: altitude without a track estimate " + t.AltFt;
                if (OverviewMath.MakeRow(t, null, null, null, null, new ClosingFilter(3, 3, 1, 300), 0, null, "ru").Alt != "\u2014") return "track sources: no altitude -> dash";
                var fa = new FakeVehicle { Altitude = new FakeAltSrc { Value = new FakePe { Estimate = 40.8213f } } };
                var ta = ReadTrack(fa, true, 0);
                if (!ta.AltFt.HasValue || Math.Abs(ta.AltFt.Value - 9000) > 5) return "track sources: altitude estimate " + ta.AltFt;
                var tl = ReadTrack(new FakeVehicleObj(), true, 0);
                if (tl.AltFt.HasValue) return "track sources: true object altitude leaked " + tl.AltFt;
                // T-210: a surface icon over a truly-aircraft object must not open the altitude column; ASW still gets the true type
                var ts = ReadTrack(new FakeVehicleSurf(), true, 0);
                if (ts.ObjType != "Aircraft") return "track sources: true type not read for ASW " + ts.ObjType;
                if (OverviewMath.MakeRow(ts, null, null, null, null, new ClosingFilter(3, 3, 1, 300), 0, null, "ru").Alt != "") return "track sources: true type opened altitude";
                ReadTrack(new object(), true, 0);
            }
            catch (Exception e) { return "track sources: " + e.GetType().Name + " " + e.Message; }
            return null;
        }

        // ---------------------------------------------------------------- selection: last own unit

        private bool LastOwnAlive()
        {
            if (!IsAlive(_lastOwn)) return false;
            var d = Get(_lastOwn, "IsDestroyed");
            return !(d is bool && (bool)d);
        }

        private static bool IsFalse(object o) { return o is bool && !(bool)o; }

        /// <summary>Plugin.Update, once per frame: remembers the selected unit when it is an own controllable one.</summary>
        internal void PumpSelection()
        {
            if (!InMission()) { _lastOwn = null; return; }
            var rp = RenderPositionInstance();
            if (rp == null) return;
            object o;
            try { o = Call(rp, "getSelectedObject"); } catch { return; }
            if (!IsAlive(o)) return;
            if (BoolOf(Get(o, "IsPlayerObject")) && BoolOf(Get(o, "IsControllable"))) _lastOwn = o;
        }

        /// <summary>Id of the selected track (not an own unit), or -1.</summary>
        internal int SelectedTrackId()
        {
            try
            {
                var mgvm = GetStatic("SeaPower.Globals", "_mainGameViewModel");
                var map = Get(mgvm, "Map");
                var so = RxGet(map, "SelectedObject");
                if (so != null && so.GetType().Name == "Vehicle")
                {
                    var id = D(Get(so, "Id"));
                    if (id.HasValue) return (int)id.Value;
                }
                var rp = RenderPositionInstance();
                object cur = rp == null ? null : Call(rp, "getSelectedObject");
                if (IsAlive(cur) && IsFalse(Get(cur, "IsPlayerObject")))
                {
                    var vehicles = Vehicles();
                    if (vehicles != null)
                        foreach (var v in vehicles)
                            if (ReferenceEquals(Get(v, "Object"), cur) || ReferenceEquals(Get(v, "BaseObject"), cur))
                            {
                                var id = D(Get(v, "Id"));
                                if (id.HasValue) return (int)id.Value;
                            }
                }
            }
            catch { }
            return -1;
        }

        /// <summary>state.selected_contact: {"id":N} when a track (not an own unit) is selected, else "null".</summary>
        internal string SelectedContactJson()
        {
            int id = SelectedTrackId();
            return id < 0 ? "null" : "{\"id\":" + id + "}";
        }

    }
}
