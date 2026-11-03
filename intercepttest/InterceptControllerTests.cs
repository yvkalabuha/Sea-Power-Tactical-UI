using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>T-138 stream C: InterceptController on the fake game (menu, start, tick, cancel, status, events, journal).</summary>
    internal static class InterceptControllerTests
    {
        private sealed class Rig
        {
            public InterceptFakeGame G = new InterceptFakeGame();
            public UnitSteering S = new UnitSteering();
            public IcParams P = new IcParams();
            public InterceptRoute R;
            public InterceptController C;
            public bool SmartAttack, Sar, Evasion, AirEvasion;

            public Rig(FakeLand land = null)
            {
                if (land != null) G.Height = land.Height;
                R = new InterceptRoute(g => G.Height(g), a => a());
                S.Register(UnitSteering.SmartAttack, u => SmartAttack && u == InterceptFakeGame.OwnUid);
                S.Register(UnitSteering.Sar, u => Sar && u == InterceptFakeGame.OwnUid);
                S.RegisterPreempt(UnitSteering.AirEvasion, u => AirEvasion && u == InterceptFakeGame.OwnUid);
                S.Register(UnitSteering.Evasion, u => Evasion && u == InterceptFakeGame.OwnUid);
                C = new InterceptController(G, S, R, P);
            }

            public string Start(int target, out string ack) { return C.Start(InterceptFakeGame.OwnUid, target, out ack); }

            public string Start(int target) { string ack; return C.Start(InterceptFakeGame.OwnUid, target, out ack); }

            /// <summary>Pumps frames until the routing job is done (the stand runs Solve synchronously).</summary>
            public void Pump(int frames = 80) { for (int i = 0; i < frames; i++) C.FramePump(); }

            public void Run(int seconds, double dt = 1)
            {
                for (int i = 0; i < seconds; i++) { G.Advance(dt); C.FramePump(); C.Tick(); }
            }

            public int Count(string prefix) { int n = 0; foreach (string c in G.Calls) if (c.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }

            public bool Event(string state) { foreach (string e in G.Events) if (e.Contains("\"state\":\"" + state + "\"")) return true; return false; }

            public bool Logged(string step) { foreach (string l in G.Logs) if (l.Contains(step)) return true; return false; }
        }

        private static void Req(bool ok, string msg) { if (!ok) throw new Exception(msg); }

        private static string Dump(Rig r) { return "calls=[" + string.Join(",", r.G.Calls) + "] events=[" + string.Join(";", r.G.Events) + "]"; }

        // ---------------------------------------------------------------- menu

        public static string MenuOk()
        {
            var r = new Rig(); r.G.AddTrack(1, 14, 0, 0, 0);
            IcMenu m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(m != null && m.Enabled && !m.IsCancel && m.Reason == null, "enabled item expected");
            Req(m.Label.Contains("~1:00"), "ETA 14 nm at 14 kn = ~1:00, label: " + m.Label);
            Req(r.G.Calls.Count == 0, "the menu must not order anything");
            Req(r.Logged("menu_open"), "menu_open not logged");
            return null;
        }

        public static string MenuCpa()
        {
            var r = new Rig(); r.G.AddTrack(1, 5, 10, 0, 30);   // runs away faster than any step
            IcMenu m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(m != null && m.Enabled && m.Reason == null, "cpa item must be enabled, " + (m == null ? "null" : m.Label + " / " + m.Reason));
            Req(m.Label.Contains("CPA") || m.Label.Contains("CPA".ToLowerInvariant()), "cpa label: " + m.Label);
            return null;
        }

        public static string MenuOpening()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 30);
            IcMenu m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(m != null && !m.Enabled && m.Reason == "opening", "opening: " + (m == null ? "null" : m.Reason));
            r.G.Tracks[1].Lat = FakeLand.Lat0 + 400 / 60.0;
            m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(m != null && !m.Enabled && m.Reason == "too_far", "too far: " + m.Reason);
            return null;
        }

        public static string MenuReasons()
        {
            var r = new Rig(); var t = r.G.AddTrack(1, 10, 0, 0, 0);
            t.BearingOnly = true;
            Req(r.C.Menu(InterceptFakeGame.OwnUid, 1).Reason == "bearing_only", "bearing_only");
            t.BearingOnly = false; t.Kind = "Air";
            Req(r.C.Menu(InterceptFakeGame.OwnUid, 1).Reason == "target_domain", "target_domain");
            t.Kind = "Surface"; t.Relation = "friendly";
            Req(r.C.Menu(InterceptFakeGame.OwnUid, 1).Reason == "own_target", "own_target");
            t.Relation = "hostile"; t.AgeS = 100;
            Req(r.C.Menu(InterceptFakeGame.OwnUid, 1).Reason == "stale", "stale");
            t.AgeS = 500;
            Req(r.C.Menu(InterceptFakeGame.OwnUid, 1).Reason == "lost", "lost");
            t.AgeS = 5; r.G.OwnU.StepsKn = null;
            Req(r.C.Menu(InterceptFakeGame.OwnUid, 1).Reason == "no_steps", "no_steps");
            r.G.OwnU.StepsKn = InterceptFakeGame.Steps;
            Req(r.C.Menu(InterceptFakeGame.OwnUid, 99).Reason == "no_track", "no_track");
            Req(r.C.Menu(77, 1) == null, "not an own unit: no item");
            return null;
        }

        public static string MenuBusy()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.SmartAttack = true;
            IcMenu m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(!m.Enabled && m.Reason == "busy:smart_attack", "busy: " + m.Reason);
            r.SmartAttack = false; r.Sar = true;
            Req(r.C.Menu(InterceptFakeGame.OwnUid, 1).Reason == "busy:sar", "busy sar");
            r.Sar = false;
            string ack;
            r.SmartAttack = true;
            Req(r.Start(1, out ack) == "busy:smart_attack", "start must refuse a busy unit");
            Req(r.G.Calls.Count == 0 && r.S.Count == 0, "a refused start must order nothing and claim nothing");
            return null;
        }

        public static string MenuLand()
        {
            var land = new FakeLand().Disc(0, 10, 3);
            var r = new Rig(land); r.G.AddTrack(1, 0, 20, 0, 0);
            var r2 = new Rig(); r2.G.AddTrack(1, 0, 20, 0, 0);
            string withLand = r.C.Menu(InterceptFakeGame.OwnUid, 1).Label, plain = r2.C.Menu(InterceptFakeGame.OwnUid, 1).Label;
            Req(withLand.Length > plain.Length, "the land hint is missing: '" + withLand + "' vs '" + plain + "'");
            Req(withLand.Contains("~"), "the ETA of a detour is only an estimate: " + withLand);
            Req(r.R.ActiveJobs == 0, "the menu must not start an A* job");
            return null;
        }

        public static string MenuCancelAndReplace()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0); r.G.AddTrack(2, 0, 10, 0, 0);
            Req(r.Start(1) == null, "start");
            IcMenu m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(m.IsCancel && m.Enabled, "cancel item on the intercepted contact: " + m.Label);
            IcMenu other = r.C.Menu(InterceptFakeGame.OwnUid, 2);
            Req(!other.IsCancel && other.Enabled, "another contact offers a start (replace)");
            Req(r.Start(2) == null, "replace");
            Req(r.C.ActiveCount == 1, "replace keeps one run");
            Req(r.Event("cancelled"), "replace is reported: " + Dump(r));
            return null;
        }

        // ---------------------------------------------------------------- start

        public static string StartFree()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            string ack;
            Req(r.Start(1, out ack) == null, "start failed");
            Req(ack.Contains("\"state\":\"active\"") && ack.Contains("\"route_pts\":1"), "ack: " + ack);
            Req(r.G.Calls.Count == 1 && r.G.Calls[0] == "Route:first:1", "free water: one goto, " + Dump(r));
            Req(r.S.Owner(InterceptFakeGame.OwnUid) == UnitSteering.Intercept, "the unit is claimed");
            // 25 nm: the budgeted RouteJob answers "direct": still one goto
            var r2 = new Rig(); r2.G.AddTrack(1, 25, 0, 0, 0);
            Req(r2.Start(1, out ack) == null && ack.Contains("\"state\":\"routing\""), "long leg goes through the job: " + ack);
            r2.Pump(); r2.C.Tick();
            Req(r2.G.Calls.Count == 1 && r2.G.Calls[0] == "Route:first:1", "direct after the job: " + Dump(r2));
            Req(r2.Event("active") || r2.C.StatusJson(InterceptFakeGame.OwnUid).Contains("\"active\""), "active after the job");
            return null;
        }

        public static string StartIsland()
        {
            var land = new FakeLand().Disc(0, 10, 3);
            var r = new Rig(land); r.G.AddTrack(1, 0, 20, 0, 0);
            string ack;
            Req(r.Start(1, out ack) == null, "start failed");
            Req(ack.Contains("\"state\":\"routing\""), "ack while routing: " + ack);
            Req(r.G.Calls.Count == 0, "no order while the route is laid");
            r.Pump(200);
            Req(r.G.Calls.Count >= 1 && r.G.Calls[0].StartsWith("Route:first:", StringComparison.Ordinal), "route given: " + Dump(r));
            int n = r.G.OwnU.Waypoints.Count;
            Req(n >= 2, "detour needs more than one point: " + n);
            Req(AswMath.DistNm(r.G.OwnU.Waypoints[n - 1], InterceptFakeGame.At(0, 20)) < 0.1, "the last point is the intercept point");
            string st = r.C.StatusJson(InterceptFakeGame.OwnUid);
            Req(st.Contains("\"state\":\"active\""), "status: " + st);
            // ETA over the route is longer than the straight 20 nm at 14 kn (= 5143 s)
            double eta = EtaOf(st);
            Req(eta > 5143 + 60, "ETA follows the route length: " + eta);
            return null;
        }

        private static double EtaOf(string json)
        {
            int i = json.IndexOf("\"eta_s\":", StringComparison.Ordinal);
            if (i < 0) return double.NaN;
            i += 8;
            int j = i;
            while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '.' || json[j] == '-')) j++;
            double v;
            return double.TryParse(json.Substring(i, j - i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        public static string StartLandTarget()
        {
            var land = new FakeLand().Disc(0, 20, 2);
            var r = new Rig(land); r.G.AddTrack(1, 0, 20, 0, 0);
            string ack;
            Req(r.Start(1, out ack) == "land_target", "land_target expected");
            Req(r.G.Calls.Count == 0 && r.S.Count == 0 && r.C.ActiveCount == 0, "nothing ordered, nothing claimed");
            return null;
        }

        public static string StartBay()
        {
            var land = new FakeLand().Ring(0, 20, 2.5, 4);
            var r = new Rig(land); r.G.AddTrack(1, 0, 20, 0, 0);
            string ack;
            Req(r.Start(1, out ack) == null && ack.Contains("routing"), "the bay is found out by the job: " + ack);
            r.Pump(300); r.C.Tick();
            Req(r.Event("unreachable"), "unreachable event: " + Dump(r));
            Req(r.G.Calls.Count == 0, "no order for an unreachable target: " + Dump(r));
            Req(r.C.ActiveCount == 0 && r.S.Count == 0, "claim released");
            bool red = false;
            foreach (IcView v in r.C.View()) if (v.Style == "red") red = true;
            Req(red, "the red line stays for a few seconds");
            return null;
        }

        public static string StartTelegraph()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 16);   // faster than 14 kn: step 4 (18 kn)
            Req(r.Start(1) == null, "start");
            r.Pump();
            Req(r.G.Calls.Count == 2 && r.G.Calls[0] == "Route:first:1" && r.G.Calls[1] == "Telegraph:4", "step up: " + Dump(r));
            var r2 = new Rig(); r2.G.AddTrack(1, 10, 0, 0, 0);
            r2.Start(1);
            Req(r2.Count("Telegraph") == 0, "enough speed: no set_telegraph, " + Dump(r2));
            var r3 = new Rig(); r3.G.OwnU.Telegraph = 0; r3.G.AddTrack(1, 10, 0, 0, 0);   // standing still: the first step ahead
            r3.Start(1);
            Req(r3.G.Calls.Contains("Telegraph:1"), "from a stop the first step ahead, " + Dump(r3));
            var r4 = new Rig(); r4.P.Speed = "current"; r4.G.AddTrack(1, 10, 0, 0, 16);
            r4.Start(1); r4.Pump();
            Req(r4.Count("Telegraph") == 0, "Speed=current never changes the step, " + Dump(r4));
            return null;
        }

        public static string StartErrors()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 30);
            string ack;
            Req(r.Start(1, out ack) == "opening" && r.G.Calls.Count == 0, "opening");
            r.G.Tracks[1].Kind = "Air";
            Req(r.Start(1, out ack) == "target_domain", "target_domain");
            Req(r.C.Start(55, 1, out ack) == "not_surface", "not_surface");
            Req(r.G.Logs.Exists(l => l.Contains("uid=55") && l.Contains("reason=not_surface")), "not_surface is logged");
            r.G.Tracks[1].Kind = "Surface"; r.G.Tracks[1].VyKn = 0;
            r.G.RouteError = "waypoint_failed";
            Req(r.Start(1, out ack) == "waypoint_failed", "an order error is returned");
            Req(r.S.Count == 0 && r.C.ActiveCount == 0, "a failed start leaves no claim");
            return null;
        }

        public static string CallsOnly()
        {
            var land = new FakeLand().Disc(0, 10, 3);
            var r = new Rig(land); r.G.AddTrack(1, 0, 20, 0, 5);
            r.Start(1); r.Pump(200); r.Run(120);
            Req(r.G.Calls.Count >= 1, "something was ordered");
            foreach (string c in r.G.Calls)
                Req(c.StartsWith("Route:", StringComparison.Ordinal) || c.StartsWith("Telegraph:", StringComparison.Ordinal) || c == "NoteFirst", "forbidden call " + c);
            return null;
        }

        // ---------------------------------------------------------------- tick

        public static string TickReplan()
        {
            var r = new Rig(); var t = r.G.AddTrack(1, 10, 0, 0, 0);
            Req(r.Start(1) == null, "start");
            t.Lon += 3.0 / 60.0; r.G.Tracks[1] = t;   // the target moved 3 nm east
            r.Run(25);
            Req(r.Count("Route") == 1, "less than RefreshS: no new plan, " + Dump(r));
            r.Run(10);
            Req(r.Count("Route:next:") == 1, "after RefreshS and a shifted IP: one new goto, " + Dump(r));
            Req(r.Logged("replan"), "replan logged");
            // a small shift (0.3 nm) never replans
            var q = new Rig(); var t2 = q.G.AddTrack(1, 10, 0, 0, 0);
            q.Start(1);
            t2.Lon += 0.3 / 60.0;
            q.Run(90);
            Req(q.Count("Route:next:") == 0, "0.3 nm shift: keep the route, " + Dump(q));
            return null;
        }

        /// <summary>The game world is flat (GeoPosition.FromUnity: the same degrees per Unity unit on both axes; GetDistanceNm = hypot(dLat,dLon)*60),
        /// so at 60 N the controller must NOT scale longitude by cos(lat): 14 nm due east at 14 kn = ETA 1:00, course 090 (a cos model gives 7 nm / 0:30).</summary>
        public static string Lat60Flat()
        {
            var r = new Rig();
            r.G.OwnU.Lat = 60.0; r.G.OwnU.Lon = 10.0;
            var t = r.G.AddTrack(1, 0, 0, 0, 0);
            t.Lat = 60.0; t.Lon = 10.0 + 14.0 / 60.0;   // 14 nm east in game miles
            IcMenu m = r.C.Menu(InterceptFakeGame.OwnUid, 1);
            Req(m != null && m.Enabled, "menu at 60N");
            Req(m.Label.Contains("~1:00") && m.Label.Contains("90"), "ETA 1:00 / course 90 (flat game rule), label: " + m.Label);
            // reference: equirectangular with cos(60) would be 7 nm = ~0:30
            Req(!m.Label.Contains("~0:30"), "cos(lat) scaling must not be applied: " + m.Label);
            Req(r.Start(1) == null, "start");
            r.Run(3700);
            double dNm = AswMath.DistNm(new GeoPt(r.G.OwnU.Lat, r.G.OwnU.Lon), new GeoPt(t.Lat, t.Lon));
            Req(dNm < 0.6 || r.Event("arrived"), "own reaches the target in ~1 h on the flat plane, dist=" + dNm + " " + Dump(r));
            return null;
        }

        // ---------------------------------------------------------------- labels (stream V)

        public static string LabelsBuild()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            var items = InterceptLabels.Build(r.C.View());
            Req(items.Count == 1, "one label per line, " + items.Count);
            Req(items[0].Key < -1000000 - 100000 && items[0].Key == InterceptLabels.KeyBase - InterceptFakeGame.OwnUid, "negative key outside the waypoint-label range: " + items[0].Key);
            Req(items[0].Text.Contains("10.0"), "text from the line: " + items[0].Text);
            Req(Math.Abs(items[0].Lat - (FakeLand.Lat0 + 10 / 60.0)) < 0.01, "label at the intercept point");
            Req(!items[0].Dim && items[0].Argb == InterceptLabels.ArgbActive, "active style");
            string sig = InterceptLabels.Signature(items);
            Req(sig == InterceptLabels.Signature(InterceptLabels.Build(r.C.View())), "stable signature");
            // grey (stale): dim; no label text: none; null list: empty
            var grey = new IcView { Uid = 5, Style = "grey", Ip = new GeoPt(1, 2), Label = "x" };
            Req(InterceptLabels.Build(new List<IcView> { grey })[0].Dim, "grey = dim");
            Req(InterceptLabels.Build(new List<IcView> { new IcView { Uid = 6, Style = "red", Ip = new GeoPt(1, 2), Label = "" } }).Count == 0, "no text, no label");
            Req(InterceptLabels.Build(null).Count == 0 && InterceptLabels.Signature(null) == "", "null");
            Req(InterceptLabels.ArgbOf("red") == InterceptLabels.ArgbRed, "red");
            return null;
        }

        public static string TickOffRoute()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            r.Run(31);
            Req(r.Count("Route:next:") == 0, "nothing changed yet");
            r.G.OwnU.Lon += 3.0 / 60.0;   // pushed off the route by 3 nm (the last point still is ours)
            r.Run(3);
            Req(r.Count("Route:next:") == 1, "off the route: replan, " + Dump(r));
            return null;
        }

        public static string TickKind()
        {
            var r = new Rig(); var t = r.G.AddTrack(1, 5, 10, 0, 0);
            r.Start(1);
            r.Run(31);
            t.VyKn = 30;   // it ran away: ok -> cpa
            r.Run(3);
            Req(r.Count("Route:next:") == 1, "the kind changed: replan, " + Dump(r));
            int before = r.Count("Route");
            t.Lat += 200 / 60.0;   // far, opening
            r.Run(70);
            Req(r.Count("Route") == before && r.C.ActiveCount == 1, "no solution on a replan: the old route stays, " + Dump(r));
            return null;
        }

        public static string TickLastLeg()
        {
            var land = new FakeLand().Disc(0, 10, 3);
            var r = new Rig(land); var t = r.G.AddTrack(1, 0, 20, 0, 0);
            r.Start(1); r.Pump(200);
            int n = r.G.OwnU.Waypoints.Count;
            Req(n >= 2, "detour " + n);
            List<GeoPt> pts = new List<GeoPt>(r.G.OwnU.Waypoints);
            // the unit passed all the detour points
            r.G.OwnU.Lat = pts[n - 2].Lat; r.G.OwnU.Lon = pts[n - 2].Lon;
            r.G.OwnU.Waypoints = new List<GeoPt> { pts[n - 1] };
            r.G.Calls.Clear();
            r.Run(2);
            Req(r.Count("NoteFirst") == 1, "a passed point keeps 'Return control' valid, " + Dump(r));
            Req(r.G.RestoreDecision() == "restore", "restore after a passed point");
            t.Lon += 3.0 / 60.0;
            r.Run(35);
            Req(r.Count("Route:next:1") == 1, "the last leg is a plain goto, " + Dump(r));
            return null;
        }

        public static string TickStale()
        {
            var r = new Rig(); var t = r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            t.AgeS = 100;   // stale
            t.Lon += 3.0 / 60.0;
            r.Run(60);
            Req(r.Count("Route:next:") == 0, "stale: no new order, " + Dump(r));
            bool grey = false;
            foreach (IcView v in r.C.View()) if (v.Style == "grey") grey = true;
            Req(grey, "stale line is grey");
            Req(r.Logged("stale"), "stale logged");
            t.AgeS = 5;
            r.Run(5);
            Req(r.Count("Route:next:") == 1, "fresh again: planned again, " + Dump(r));
            return null;
        }

        public static string TickLostGone()
        {
            var r = new Rig(); var t = r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            int wps = r.G.OwnU.Waypoints.Count;
            t.AgeS = 200;
            r.Run(2);
            Req(r.Event("track_lost") && r.C.ActiveCount == 0, "track_lost: " + Dump(r));
            Req(r.G.OwnU.Waypoints.Count == wps && r.S.Count == 0, "the route stays, the claim goes");
            var q = new Rig(); var t2 = q.G.AddTrack(1, 10, 0, 0, 0);
            q.Start(1);
            t2.Gone = true;
            q.Run(2);
            Req(q.Event("target_gone") && q.C.ActiveCount == 0, "target_gone: " + Dump(q));
            var z = new Rig(); z.G.AddTrack(1, 10, 0, 0, 0);
            z.Start(1);
            z.G.Tracks.Remove(1);
            z.Run(2);
            Req(z.Event("target_gone"), "a removed track is gone too");
            return null;
        }

        public static string TickArrived()
        {
            var r = new Rig(); r.G.AddTrack(1, 0.8, 0, 0, 0);
            Req(r.Start(1) == null, "start");
            r.Run(2);
            Req(r.Event("arrived") && r.C.ActiveCount == 0, "arrived: " + Dump(r));
            Req(r.Count("Route") == 1, "the route is left as it is");
            // the whole way
            var q = new Rig(); q.G.AddTrack(1, 5, 0, 0, 0);
            q.Start(1);
            q.Run(1500, 1);
            Req(q.Event("arrived"), "arrived after sailing: " + Dump(q));
            return null;
        }

        public static string TickPlayerChanged()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            r.G.OwnU.Waypoints = new List<GeoPt> { InterceptFakeGame.At(-5, 5) };   // the player set his own course
            r.Run(2);
            Req(r.Event("player_changed") && r.C.ActiveCount == 0 && r.S.Count == 0, "route: " + Dump(r));
            Req(r.Count("Route") == 1, "the mod does not fight back");
            var q = new Rig(); q.G.AddTrack(1, 10, 0, 0, 16);
            q.Start(1); q.Pump();
            q.G.OwnU.Telegraph = 1;   // the player changed the speed
            q.Run(2);
            Req(q.Event("player_changed"), "telegraph: " + Dump(q));
            // a multi-point route: a changed middle is not noticed, the last point is the key; a new last point is
            var land = new FakeLand().Disc(0, 10, 3);
            var m = new Rig(land); m.G.AddTrack(1, 0, 20, 0, 0);
            m.Start(1); m.Pump(200);
            m.G.OwnU.Waypoints.Add(InterceptFakeGame.At(5, 5));
            m.Run(2);
            Req(m.Event("player_changed"), "a point added by the player: " + Dump(m));
            return null;
        }

        public static string TickSuperseded()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            r.SmartAttack = true;
            r.Run(2);
            Req(r.Event("superseded") && r.C.ActiveCount == 0, "smart attack: " + Dump(r));
            Req(r.G.Events[r.G.Events.Count - 1].Contains("smart_attack"), "the owner is named");
            Req(r.S.Owner(InterceptFakeGame.OwnUid) == UnitSteering.SmartAttack, "the claim is gone, the resolver rules");
            var q = new Rig(); q.G.AddTrack(1, 10, 0, 0, 0);
            q.Start(1);
            q.Sar = true;
            q.Run(2);
            Req(q.Event("superseded"), "sar");
            return null;
        }

        public static string TickSuspend()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            r.Evasion = true;
            r.Run(2);
            Req(r.Event("suspended") && r.C.ActiveCount == 1, "suspended: " + Dump(r));
            Req(r.Logged("suspend"), "suspend logged");
            int calls = r.G.Calls.Count;
            r.G.OwnU.Waypoints = new List<GeoPt> { InterceptFakeGame.At(-3, -3) };   // the evasion turned the unit
            r.G.OwnU.Telegraph = 5;
            r.Run(40);
            Req(r.Event("suspended") && !r.Event("player_changed") && r.G.Calls.Count == calls, "nothing happens while suspended: " + Dump(r));
            Req(r.S.Count == 1 || r.S.HolderOtherThan(InterceptFakeGame.OwnUid, UnitSteering.Intercept) == UnitSteering.Evasion, "evasion holds the unit");
            r.Evasion = false;
            r.Run(3);
            Req(r.Count("Route:next:") == 1 && r.C.ActiveCount == 1, "resume plans again: " + Dump(r));
            Req(r.Logged("resume"), "resume logged");
            Req(r.C.StatusJson(InterceptFakeGame.OwnUid).Contains("\"state\":\"active\""), "active again: " + r.C.StatusJson(InterceptFakeGame.OwnUid));
            return null;
        }

        public static string SuspendAir()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            r.AirEvasion = true;
            r.Run(2);
            Req(r.Event("suspended") && r.C.ActiveCount == 1, "suspended by air evasion: " + Dump(r));
            Req(r.S.Owner(InterceptFakeGame.OwnUid) == UnitSteering.AirEvasion, "air evasion owns the unit");
            int calls = r.G.Calls.Count;
            r.Run(20);
            Req(r.G.Calls.Count == calls, "nothing happens while suspended: " + Dump(r));
            r.AirEvasion = false;
            r.Run(3);
            Req(r.Logged("resume") && r.C.ActiveCount == 1, "resumes: " + Dump(r));
            return null;
        }

        public static string TickOnce()
        {
            var r = new Rig(); r.P.Mode = "once"; var t = r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            t.Lon += 5.0 / 60.0;
            r.Run(100);
            Req(r.Count("Route") == 1, "once: one order only, " + Dump(r));
            Req(r.C.ActiveCount == 1, "still watching");
            return null;
        }

        public static string TickReplanNone()
        {
            var land = new FakeLand().Disc(0, 10, 3).Disc(40, 40, 3);
            var r = new Rig(land); var t = r.G.AddTrack(1, 0, 20, 0, 0);
            r.Start(1); r.Pump(200);
            int calls = r.G.Calls.Count;
            t.Lat = FakeLand.Lat0 + 40.0 / 60.0; t.Lon = FakeLand.Lon0 + 40.0 / 60.0;   // jumped onto an island
            r.Run(70); r.Pump(100);
            Req(r.G.Calls.Count == calls && r.C.ActiveCount == 1, "the old route stays on a failed replan, " + Dump(r));
            return null;
        }

        public static string RestoreOrder()
        {
            var land = new FakeLand().Disc(0, 10, 3);
            var r = new Rig(land); r.G.AddTrack(1, 0, 20, 0, 0);
            r.G.OwnU.Waypoints = new List<GeoPt> { InterceptFakeGame.At(-2, -2), InterceptFakeGame.At(-4, -4) };   // the player's own route
            r.Start(1); r.Pump(200);
            Req(r.G.HasMem && r.G.Saved.Count == 2, "the player's route is remembered at the first order");
            Req(r.G.RestoreDecision() == "restore", "restore right after the order");
            int n = r.G.OwnU.Waypoints.Count;
            Req(n >= 2, "multi-point route");
            r.G.OwnU.Waypoints.RemoveAt(0);   // the first point was passed
            Req(r.G.RestoreDecision() == "player_changed", "without the note the game would answer player_changed");
            r.Run(2);
            Req(r.G.RestoreDecision() == "restore", "the first remaining point was noted, " + Dump(r));
            return null;
        }

        // ---------------------------------------------------------------- cancel, status, events

        public static string CancelKeepsRoute()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            var wps = new List<GeoPt>(r.G.OwnU.Waypoints);
            int calls = r.G.Calls.Count;
            Req(r.C.Cancel(InterceptFakeGame.OwnUid, "menu") == null, "cancel");
            Req(r.G.Calls.Count == calls && r.G.OwnU.Waypoints.Count == wps.Count, "the route is not touched");
            Req(r.C.ActiveCount == 0 && r.S.Count == 0 && r.Event("cancelled"), "cancelled: " + Dump(r));
            Req(r.C.Cancel(InterceptFakeGame.OwnUid, "menu") == "not_active", "nothing to cancel");
            return null;
        }

        public static string UnitActive()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            string eta;
            Req(!r.C.UnitActive(InterceptFakeGame.OwnUid, out eta) && eta == null, "no run before the start");
            Req(r.Start(1) == null, "start");
            Req(r.C.UnitActive(InterceptFakeGame.OwnUid, out eta) && eta != null && eta.StartsWith("~"), "active with an ETA: " + eta);
            Req(r.C.Cancel(InterceptFakeGame.OwnUid, "xo_menu") == null, "cancel");
            Req(!r.C.UnitActive(InterceptFakeGame.OwnUid, out eta), "not active after the cancel");
            Req(r.Event("cancelled") && string.Join(";", r.G.Events).Contains("xo_menu"), "cancelled with the reason xo_menu: " + Dump(r));
            Req(r.C.Cancel(InterceptFakeGame.OwnUid, "xo_menu") == "not_active", "second cancel");
            var q = new Rig(); q.G.AddTrack(1, 0.8, 0, 0, 0);
            q.Start(1); q.Run(2);
            Req(q.Event("arrived") && !q.C.UnitActive(InterceptFakeGame.OwnUid, out eta), "not active after the arrival");
            return null;
        }

        public static string ReleaseUnit()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            r.Start(1);
            int calls = r.G.Calls.Count;
            r.C.ReleaseUnit(InterceptFakeGame.OwnUid);
            Req(r.C.ActiveCount == 0 && r.S.Count == 0 && r.G.Calls.Count == calls, "return control forgets the unit and orders nothing");
            r.C.ReleaseUnit(5);   // unknown unit: nothing
            return null;
        }

        public static string MissionLeft()
        {
            var land = new FakeLand().Disc(0, 10, 3);
            var r = new Rig(land); r.G.AddTrack(1, 0, 20, 0, 0);
            r.Start(1);   // the job is queued, nothing ordered yet
            Req(r.R.Busy(InterceptFakeGame.OwnUid), "job queued");
            r.C.OnMissionLeft();
            Req(r.C.ActiveCount == 0 && !r.R.Busy(InterceptFakeGame.OwnUid), "everything forgotten, jobs cancelled");
            return null;
        }

        public static string StatusAndEvents()
        {
            var r = new Rig(); r.G.AddTrack(1, 10, 0, 0, 0);
            Req(r.C.StatusJson(InterceptFakeGame.OwnUid).Contains("\"state\":\"none\""), "idle status");
            r.Start(1);
            string st = r.C.StatusJson(InterceptFakeGame.OwnUid);
            Req(st.Contains("\"target_id\":1") && st.Contains("\"state\":\"active\"") && st.Contains("\"eta_s\":"), "status: " + st);
            Req(r.C.StatusJson(0).Contains("\"intercepts\":["), "all");
            Req(r.G.Events.Count >= 1 && r.G.Events[0].StartsWith("intercept_state ", StringComparison.Ordinal), "event on start: " + Dump(r));
            IList<IcView> v = r.C.View();
            Req(v.Count == 1 && v[0].Style == "active" && v[0].Line.Count >= 2 && !string.IsNullOrEmpty(v[0].Label), "view");
            r.P.ShowLine = false;
            Req(r.C.View().Count == 0, "ShowLine=false hides the line");
            return null;
        }

        public static string Journal()
        {
            var r = new Rig(); var t = r.G.AddTrack(1, 10, 0, 0, 0);
            r.C.Menu(InterceptFakeGame.OwnUid, 1);
            r.Start(1);
            t.Lon += 3.0 / 60.0;
            r.Run(40);
            r.C.Cancel(InterceptFakeGame.OwnUid, "menu");
            foreach (string step in new[] { "menu_open", "start", "solve", "order", "replan", "result" })
                Req(r.Logged(step), step + " not logged: " + string.Join("|", r.G.Logs));
            var land = new FakeLand().Disc(0, 10, 3);
            var q = new Rig(land); q.G.AddTrack(1, 0, 20, 0, 0);
            q.Start(1); q.Pump(200);
            Req(q.Logged("route uid"), "route step logged");
            return null;
        }
    }
}
