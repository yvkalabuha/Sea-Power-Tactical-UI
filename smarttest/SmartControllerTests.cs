using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceBotBridge
{
    internal static class SmartControllerTests
    {
        private sealed class TF : Exception { public TF(string m) : base(m) { } }

        private static void Check(bool c, string m) { if (!c) throw new TF(m); }

        private static WireAmmo Ammo(string file, string ini) { var a = WireAmmo.FromIni(file, ini); a.Count = 4; return a; }

        private sealed class Rig
        {
            public SmartFakeGame G;
            public SmartAttackController C;
            public SmartParams P = new SmartParams();
            public double Dt = 0.5;
            public int Pumps;

            public Rig(bool debug = false, int uid = 20)
            {
                G = new SmartFakeGame(Ammo("adcap", SmartAttackMath.AdcapIni));
                G.SubS.Uid = uid;
                G.Dir = Path.Combine(Path.GetTempPath(), "smarttest-" + Guid.NewGuid().ToString("N"));
                C = new SmartAttackController(G, P, debug);
            }

            public string Start(out string ack, string ammo = null, bool confirm = false, int tid = 12)
            {
                return C.Start(new SmartAttackArgs { Uid = G.SubS.Uid, TargetId = tid, Ammo = ammo, ConfirmNeutral = confirm }, out ack);
            }

            public void Go()
            {
                string ack;
                string e = Start(out ack);
                Check(e == null, "start: " + e);
            }

            public bool Run(double secs, Func<bool> until = null)
            {
                double end = G.NowS + secs;
                while (G.NowS < end)
                {
                    G.Frame++; G.Advance(Dt); C.Pump(G.NowS); Pumps++;
                    if (until != null && until()) return true;
                }
                return until == null;
            }

            public void RunTo(Func<bool> until, double secs, string what)
            {
                Check(Run(secs, until), "timeout waiting for " + what + " state=" + State() + " t=" + G.NowS);
            }

            public string State()
            {
                var root = (Dictionary<string, object>)Json.Parse(C.LinkStateJson());
                var l = (List<object>)root["attacks"];
                return l.Count == 0 ? "none" : (string)((Dictionary<string, object>)l[0])["state"];
            }

            public double Odo()
            {
                var root = (Dictionary<string, object>)Json.Parse(C.LinkStateJson());
                var l = (List<object>)root["attacks"];
                return l.Count == 0 ? 0 : (double)((Dictionary<string, object>)l[0])["leg_odo_nm"];
            }

            public string Side()
            {
                var root = (Dictionary<string, object>)Json.Parse(C.LinkStateJson());
                return (string)((Dictionary<string, object>)((List<object>)root["attacks"])[0])["side"];
            }

            public int Active() { return ((List<object>)((Dictionary<string, object>)Json.Parse(C.LinkStateJson()))["attacks"]).Count; }
            public bool Has(string call) { return G.Calls.Contains(call); }
            public int Events(string needle) { return G.Events.Count(x => x.Contains(needle)); }
            public bool Logged(string needle) { return G.Logs.Any(x => x.Contains(needle)); }
        }

        private static NeutralTrack Ship(int id, double x, double y) { return new NeutralTrack { Id = id, Cls = "Cargo", Side = "neutral", Domain = "Surface", Pos = new SaVec(x, y) }; }

        // ---------------------------------------------------------------------------------------------------------------

        private static void Codes()
        {
            string ack;
            var r = new Rig(); r.G.SubS.IsSub = false;
            Check(r.Start(out ack) == "not_sub", "not_sub");
            r = new Rig(); r.Go();
            Check(r.Start(out ack) == "busy", "busy");
            r = new Rig(); r.G.Ammos.Clear();
            Check(r.Start(out ack) == "no_wire_torpedo", "no_wire_torpedo");
            r = new Rig(); r.G.Ammos.Clear(); r.G.AmmoErr = "target_not_allowed";
            Check(r.Start(out ack) == "target_not_allowed", "target_not_allowed");
            r = new Rig(); r.G.SubS.FreeWire = false;
            Check(r.Start(out ack) == "no_free_wire", "no_free_wire");
            r = new Rig(); r.G.Ammos.Clear(); r.G.Ammos.Add(Ammo("t71", SmartAttackMath.SynthIni)); r.G.Info.Domain = "Subsurface";
            Check(r.Start(out ack) == "no_wire_depth", "no_wire_depth");
            r = new Rig(); r.G.Info.State = "bearing_only";
            Check(r.Start(out ack) == "track_bearing_only", "track_bearing_only");
            r = new Rig(); r.G.TargetPos = new SaVec(0, 40);
            Check(r.Start(out ack) == "out_of_range", "out_of_range");
            r = new Rig(); r.G.NeutralsThrow = true;
            Check(r.Start(out ack) == "geometry_error", "geometry_error (fail closed)");
            Check(r.Active() == 0, "geometry_error must leave nothing running");

            // ammo choice: the valid one with the longest stage-0 range; invalid ones are logged
            r = new Rig();
            r.G.Ammos.Clear();
            r.G.Ammos.Add(Ammo("ugst", SmartAttackMath.UgstIni));
            r.G.Ammos.Add(new WireAmmo { File = "bad", WireGuided = true, SpeedsKn = new double[0], RangesNm = new double[0] });
            r.G.Ammos.Add(Ammo("adcap", SmartAttackMath.AdcapIni));
            Check(r.Start(out ack) == null, "ammo choice start");
            var d = (Dictionary<string, object>)Json.Parse(ack);
            var plan = (Dictionary<string, object>)d["plan"];
            Check((string)plan["ammo"] == "adcap", "longest range chosen: " + plan["ammo"]);
            Check(r.G.Logs.Any(x => x.Contains("ammo_params_invalid bad")), "invalid ammo logged");
            Check(d.ContainsKey("attack_id") && plan.ContainsKey("side") && plan.ContainsKey("phi") && plan.ContainsKey("leg_nm") && plan.ContainsKey("stages") && d["warnings"] is List<object>, "ack shape");
            r = new Rig();
            Check(r.Start(out ack, "nope") == "no_wire_torpedo", "named ammo missing");
        }

        private static void NeutralPreflight()
        {
            string ack;
            var left = Ship(1, -3.6, 1.5); var right = Ship(2, 3.6, 1.5);
            var r = new Rig(); r.G.NeutralList.AddRange(new[] { left, right });
            Check(r.Start(out ack) == null && ack.Contains("question"), "question ack");
            Check(r.C.Pending != null && r.C.Pending.Kind == "neutral_preflight" && r.C.Pending.Text.Contains("Атаковать?"), "question text");
            using (UiText.Push("en"))
            {
                var re = new Rig(); re.G.NeutralList.AddRange(new[] { left, right });
                re.Start(out ack);
                Check(re.C.Pending != null && re.C.Pending.Text.Contains("Attack?") && !re.C.Pending.Text.Contains("Атаковать"), "question text en: " + (re.C.Pending == null ? "-" : re.C.Pending.Text));
                re.C.Answer(re.C.Pending.Id, "yes");
                re.RunTo(() => re.Has("fire:adcap:True"), 200, "fire with confirm en");
                Check(re.G.Messages.Any(m => m.StartsWith("Smart attack: target #")), "start message en: " + string.Join("|", re.G.Messages));
            }
            Check(r.C.Pending.Options.SequenceEqual(new[] { "yes", "no" }), "options");
            r.C.Answer(r.C.Pending.Id, "yes");
            r.RunTo(() => r.Has("fire:adcap:True"), 200, "fire with confirm");

            r = new Rig(); r.G.NeutralList.AddRange(new[] { left, right });
            r.Start(out ack); r.C.Answer(r.C.Pending.Id, "no");
            r.Run(60);
            Check(r.C.Pending == null && r.Active() == 0 && !r.G.Calls.Any(x => x.StartsWith("fire")), "no -> failed, no fire");

            r = new Rig(); r.G.NeutralList.AddRange(new[] { left, right });
            r.Start(out ack); r.Run(35);
            Check(r.C.Pending == null && r.Active() == 0 && !r.G.Calls.Any(x => x.StartsWith("fire")), "timeout -> failed");

            r = new Rig(); r.G.Direct = "neutral_check_failed";
            Check(r.Start(out ack) == "neutral_check_failed", "check failed");

            r = new Rig(); r.G.Direct = "neutral_near";
            Check(r.Start(out ack) == null && r.C.Pending != null, "direct corridor asks");
            r.C.Answer(r.C.Pending.Id, "yes");
            r.RunTo(() => r.Has("fire:adcap:True"), 200, "fire after direct yes");

            r = new Rig(); r.G.NeutralList.AddRange(new[] { left, right });
            Check(r.Start(out ack, null, true) == null && r.C.Pending == null, "confirm_neutral skips the question");
            r.RunTo(() => r.Has("fire:adcap:True"), 200, "fire with confirm arg");
        }

        private static void Positioning()
        {
            var r = new Rig(); r.G.SubS.Telegraph = 5; r.G.SubS.SpeedKn = 25;
            r.Go();
            Check(r.Has("remember:telegraph=5") && r.Has("telegraph:3") && r.Has("note:telegraph=3"), "telegraph limit " + string.Join(",", r.G.Calls));
            Check(r.Has("preset:4"), "dive below the layer");
            double depthAtFire = -1, speedAtFire = -1;
            r.RunTo(() => { if (r.Has("fire:adcap:False") && depthAtFire < 0) { depthAtFire = r.G.SubS.DepthFt; speedAtFire = r.G.SubS.SpeedKn; return true; } return false; }, 400, "fire");
            Check(depthAtFire >= 380 && speedAtFire <= 19.5, "ready before firing: depth " + depthAtFire + " speed " + speedAtFire);

            r = new Rig(); r.G.DiveFtPerS = 0;
            r.Go();
            double firedAt = -1;
            r.RunTo(() => { if (r.Has("fire:adcap:False")) { firedAt = r.G.NowS; return true; } return false; }, 400, "fire after the position timeout");
            Check(firedAt >= 179 && r.Events("position_timeout") == 1, "position_timeout at " + firedAt);
        }

        private static void Launch()
        {
            var r = new Rig(); r.G.SubS.Telegraph = 5; r.G.SubS.SpeedKn = 25; r.G.LaunchSpeeds["adcap"] = 1;
            r.Go();
            r.RunTo(() => r.G.FoundFrame >= 0, 400, "torpedo found");
            int ls = r.G.Calls.IndexOf("launchspeed:0"), fire = r.G.Calls.FindIndex(x => x.StartsWith("fire"));
            Check(ls >= 0 && fire > ls, "launch speed 0 before fire");
            Check(r.G.DepthFrame == r.G.FoundFrame && r.G.SpeedFrame == r.G.FoundFrame && r.G.LaunchRestoreFrame == r.G.FoundFrame, "depth/speed/launch speed in the finding frame");
            Check(r.Has("wiredepth:330") && r.Has("wirespeed:0"), "honest depth 330 and stage 0");
            Check(r.G.LaunchSpeeds["adcap"] == 1, "launch speed given back");

            r = new Rig(); r.G.IgnoreDepthOrders = true; r.G.DepthIgnoreCount = 2;
            r.Go();
            r.RunTo(() => r.G.DepthOrdered.HasValue, 400, "depth accepted");
            Check(r.G.WireDepthCalls >= 3 && r.G.DepthOrdered == 330, "depth repeated until accepted: " + r.G.WireDepthCalls);

            r = new Rig(); r.G.LaunchDelayS = 1000; r.G.SubS.Telegraph = 5; r.G.SubS.SpeedKn = 25;
            r.Go();
            r.RunTo(() => r.Has("fire:adcap:False"), 400, "fire");
            double t0 = r.G.NowS;
            r.RunTo(() => r.Active() == 0, 200, "launch timeout");
            Check(r.G.NowS - t0 >= 89 && r.G.NowS - t0 < 95, "launch timeout 90 s: " + (r.G.NowS - t0));
            Check(r.G.LaunchSpeeds["adcap"] == 1 && r.C.ResultOf("sa-1") == "failed", "failed + launch speed back");
            Check(r.G.Calls.Last(x => x.StartsWith("telegraph")) == "telegraph:5", "telegraph back");

            r = new Rig(); r.G.FireError = "no_free_wire"; r.G.SubS.Telegraph = 5; r.G.SubS.SpeedKn = 25;
            r.Go();
            r.RunTo(() => r.Active() == 0, 400, "fire error");
            Check(r.C.ResultOf("sa-1") == "failed" && r.G.Calls.Last(x => x.StartsWith("telegraph")) == "telegraph:5", "fire error -> failed, fields back");
            Check(r.G.LaunchSpeeds["adcap"] == 1, "launch speed after fire error");
        }

        private static Rig ToLeg(Action<Rig> setup = null)
        {
            var r = new Rig();
            if (setup != null) setup(r);
            r.Go();
            r.RunTo(() => r.State() == "Leg", 400, "Leg");
            return r;
        }

        private static void Leg()
        {
            var r = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            bool measured = false;
            r.RunTo(() =>
            {
                if (r.G.Torp.SeekerActive) throw new TF("seeker active during the leg");
                if (r.State() == "Leg" && r.Odo() > 1.5 && !measured)
                {
                    measured = true;
                    double ang = Math.Abs(((r.G.Torp.CourseDeg - SaVec.Bearing(r.G.Torp.Pos, r.G.TargetPos)) + 540) % 360 - 180);
                    Check(Math.Abs(ang - 60) <= 10, "lead-off angle " + ang);
                }
                return r.State() != "Leg";
            }, 800, "end of the leg");
            Check(measured && r.State() == "Approach" && r.G.LastShiftNull && r.Odo() >= 3 - 0.1, "leg ends in Approach with direct aim: odo " + r.Odo());
            r.RunTo(() => r.Active() == 0, 1800, "done");
            Check(r.C.ResultOf("sa-1") == "hit", "result " + r.C.ResultOf("sa-1"));
            Check(r.Events("\"Terminal\"") >= 1, "passed Terminal");
            Check(r.Events("\"cone\"") == 1, "cone warned once: " + r.Events("\"cone\""));
            Check(r.G.Calls.Last(x => x.StartsWith("telegraph")) == "telegraph:5", "telegraph given back at the end");

            // live 2026-10-22: the Mk48 hit the merchant (game report), the contact stayed on the plot -> the mod said "miss"
            r = ToLeg(x => x.G.HitRemovesTrack = false);
            r.RunTo(() => r.Active() == 0, 1800, "hit, track stays");
            Check(r.C.ResultOf("sa-1") == "hit" && r.Logged("hit_report"), "hit by the report while the track stays: " + r.C.ResultOf("sa-1"));

            r = ToLeg(x => { x.G.HitRemovesTrack = false; x.G.NoHitReport = true; });
            r.RunTo(() => r.Active() == 0, 1800, "gone without a report");
            Check(r.C.ResultOf("sa-1") == "miss", "no report, track stays: miss: " + r.C.ResultOf("sa-1"));

            r = ToLeg(x => { x.G.HitRemovesTrack = false; x.G.HitCount = -1; });
            r.RunTo(() => r.Active() == 0, 1800, "hit, counter off");
            Check(r.C.ResultOf("sa-1") == "hit" && r.Logged("near_target"), "counter off: gone next to the target while homing = hit: " + r.C.ResultOf("sa-1"));

            r = ToLeg(x => { x.G.HitRemovesTrack = false; x.G.HitCount = -1; });
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.Torp.Alive = false;
            r.Run(3);
            Check(r.Active() == 0 && r.C.ResultOf("sa-1") == "miss", "counter off: gone on the leg = miss: " + r.C.ResultOf("sa-1"));

            // no depth order from the tick the seeker homes: the game climbs to the hull by itself
            r = ToLeg(x => { x.G.IgnoreDepthOrders = true; x.G.DepthIgnoreCount = 1000000; });
            int callsBefore = 0; bool checkedTerminal = false;
            r.RunTo(() =>
            {
                if (r.State() == "Terminal" && !checkedTerminal)
                {
                    checkedTerminal = true;
                    Check(r.G.WireDepthCalls == callsBefore, "no depth order in the tick that enters Terminal: " + callsBefore + " -> " + r.G.WireDepthCalls);
                }
                if (!checkedTerminal) callsBefore = r.G.WireDepthCalls;
                return r.Active() == 0;
            }, 1800, "done after Terminal");
            Check(checkedTerminal && callsBefore > 1 && r.G.WireDepthCalls == callsBefore, "depth re-applied before Terminal, never after: " + callsBefore + " / " + r.G.WireDepthCalls);

            r = ToLeg(x => x.G.IgnoreShift = true);
            r.Run(60);
            Check(r.Events("offset_unstable") == 1, "offset_unstable once");

            r = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.SubSpeedOverride = 25;
            r.Run(10);
            Check(r.Active() == 0 && r.C.ResultOf("sa-1") == "wire_lost", "wire lost: " + r.C.ResultOf("sa-1"));
            Check(r.G.Calls.Last(x => x.StartsWith("telegraph")) == "telegraph:5", "fields back after wire loss");

            r = ToLeg();
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.Info.State = "bearing_only";
            r.Run(70);
            Check(r.C.ResultOf("sa-1") == "track_lost" && r.G.Calls.Last(x => x.StartsWith("shift")) == "shift:null", "track lost after 60 s");
            Check(r.Events("\"track_lost\"") >= 1, "track_lost warn");

            r = ToLeg();
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.Info.State = "bearing_only";
            r.Run(20);
            r.G.Info.State = "live";
            r.Run(80);
            Check(r.Active() == 1, "track returns -> goes on");

            r = ToLeg();
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.IsPaused = true;
            int before = r.G.ShiftCalls; double odo = r.Odo();
            r.Run(30);
            Check(r.G.ShiftCalls == before && r.Odo() == odo, "no ticks while paused");
            r.G.IsPaused = false;
            r.Dt = 10;
            before = r.G.ShiftCalls; int p0 = r.Pumps;
            r.Run(80);
            Check(r.G.ShiftCalls - before <= r.Pumps - p0 && r.G.ShiftCalls > before, "at most one tick per Pump");
        }

        private static void TurnTarget(Rig r, double course)
        {
            r.G.TargetVel = SaVec.FromBearing(course, 10);
        }

        private static void Final()
        {
            var r = ToLeg(x => { x.G.TargetVel = SaVec.FromBearing(90, 10); x.G.TargetPos = new SaVec(-4, 12); });
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            TurnTarget(r, 115);
            r.RunTo(() => r.State() == "Final", 80, "Final after the turn");
            Check(r.Has("wiredepth:40") && r.G.Calls.Count(x => x.StartsWith("wirespeed:")) >= 2, "final order: min depth (6 ft clamped to 40) and a stage");
            Check(r.G.LastShiftNull, "direct aim in Final");
            Check(r.C.StepsJson("sa-1").Contains("noticed") && r.C.StepsJson("sa-1").Contains("final"), "journal steps");
            r.RunTo(() => r.Active() == 0, 1800, "done");
            Check(r.C.ResultOf("sa-1") == "hit" || r.C.ResultOf("sa-1") == "miss", "finished");

            // sub target: depth below/above the layer, final depth follows the track estimate
            r = ToLeg(x => { x.G.Info.Domain = "Subsurface"; x.G.TargetDepth = 200; x.G.TargetVel = SaVec.FromBearing(90, 10); x.G.TargetPos = new SaVec(-4, 12); });
            Check(r.Has("preset:4") && r.Has("wiredepth:330"), "sub above the layer: below it, torpedo 330");
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            TurnTarget(r, 115); r.G.TargetDepth = 180;
            r.RunTo(() => r.State() == "Final", 80, "Final for a sub");
            r.Run(3);
            Check(r.Has("wiredepth:180") || r.Has("wiredepth:200"), "final depth from the track");
            r.G.TargetDepth = 170;
            r.Run(3);
            Check(r.Has("wiredepth:170"), "final depth updated every tick");
        }

        private static void NeutralOnPath()
        {
            var r = ToLeg();
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.NeutralList.Add(Ship(7, 2.0, 5.0));
            r.RunTo(() => r.C.Pending != null, 20, "question");
            Check(r.C.Pending.Kind == "neutral_on_path" && r.C.Pending.Options.SequenceEqual(new[] { "continue", "cancel" }) && r.Active() == 1, "on-path question");
            r.G.NeutralList.Add(Ship(8, 2.5, 4.5));
            r.Run(6);
            Check(r.C.Pending.NeutralIds.Contains(8), "a second neutral joins the same question");
            r.C.Answer(r.C.Pending.Id, "continue");
            r.Run(30);
            Check(r.C.Pending == null && r.Active() == 1, "continue: not asked again");

            r = ToLeg();
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.NeutralList.Add(Ship(7, 2.0, 5.0));
            r.RunTo(() => r.C.Pending != null, 20, "question");
            r.C.Answer(r.C.Pending.Id, "cancel");
            Check(r.C.ResultOf("sa-1") == "cancelled" && r.G.Calls.Last(x => x.StartsWith("shift")) == "shift:null", "cancel answer");

            r = ToLeg();
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            string side0 = r.Side();
            r.G.NeutralList.Add(Ship(7, 2.0, 5.0));
            r.RunTo(() => r.C.Pending != null, 20, "question");
            double t0 = r.G.NowS;
            r.RunTo(() => r.C.Pending == null, 30, "question timeout");
            Check(r.G.NowS - t0 >= 19 && r.G.NowS - t0 < 22, "20 s timeout: " + (r.G.NowS - t0));
            Check(r.Active() == 1 && r.Events("neutral_avoid") >= 1 && r.Side() != side0, "avoid plan taken: " + side0 + "->" + r.Side());
            Check(r.State() == "Leg" && r.Odo() < 1.0, "back in Leg with a new plan");

            r = ToLeg();
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.NeutralList.Add(Ship(7, 2.0, 5.0)); r.G.NeutralList.Add(Ship(8, -2.0, 5.0));
            r.RunTo(() => r.C.Pending != null, 20, "question");
            r.Run(25);
            Check(r.C.ResultOf("sa-1") == "cancelled" && r.Events("neutral_unavoidable") >= 1, "unavoidable -> cancel");

            r = ToLeg();
            r.RunTo(() => r.State() == "Terminal", 1800, "Terminal");
            r.G.NeutralList.Add(Ship(9, 0.3, 10.5));
            r.Run(8);
            Check(r.C.Pending == null && r.Events("neutral_on_path") >= 1, "Terminal: warning only");
        }

        private static void CancelAndRelease()
        {
            var r = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            Check(r.C.Cancel(null, "nope", "x") == "not_found", "not_found");
            Check(r.C.Cancel(20, null, "test") == "cancelled" && r.Active() == 0, "cancel by uid");
            Check(r.G.Calls.Last(x => x.StartsWith("shift")) == "shift:null", "shift reset on cancel");
            Check(r.Has("preset:2") && r.G.Calls.Last(x => x.StartsWith("telegraph")) == "telegraph:5", "fields returned by the ControlMemory rule");
            Check(r.G.LaunchSpeeds["adcap"] == 1 || r.G.LaunchSpeeds["adcap"] == 0 && false, "launch speed given back");

            r = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            r.RunTo(() => r.Odo() > 0.5, 300, "leg progress");
            r.G.SubS.Telegraph = 4;                                  // the player changed it
            int tg = r.G.Calls.Count(x => x.StartsWith("telegraph"));
            r.C.ReleaseUnit(20);
            Check(r.G.Calls.Count(x => x.StartsWith("telegraph")) == tg, "player_changed: telegraph not touched");
            Check(r.G.Logs.Any(x => x.Contains("restore_telegraph") && x.Contains("player_changed")), "journal says player_changed");

            r = ToLeg();
            r.G.SubSpeedOverride = 19.8;
            int tg2 = r.G.Calls.Count(x => x.StartsWith("telegraph"));
            r.Run(5);
            Check(r.Events("wire_speed") == 1 && r.G.Calls.Count(x => x.StartsWith("telegraph")) == tg2, "wire_speed warned, speed not pulled back");

            r = ToLeg();
            r.G.SubS.BottomFt = r.G.SubS.DepthFt + 30;
            r.Run(5);
            Check(r.Events("wire_seabed") == 1, "wire_seabed");
        }

        private static void EventsAndStatus()
        {
            Check(SmartAttackController.WarnCodes.Length == 13 && SmartAttackController.WarnCodes.Distinct().Count() == 13, "13 warn codes");
            foreach (string c in new[] { "no_layer", "depth_unknown", "leg_skipped", "fuel_short", "cone", "offset_unstable", "neutral_on_path", "neutral_avoid", "neutral_unavoidable", "wire_speed", "wire_seabed", "track_lost", "position_timeout" })
                Check(SmartAttackController.WarnCodes.Contains(c), "code " + c);
            var r = new Rig(true);
            r.G.Info.Domain = "Surface"; r.G.SubS.LayerFt = null;
            r.Go();
            r.RunTo(() => r.State() == "Leg", 400, "Leg");
            r.Run(5);
            foreach (string e in r.G.Events)
            {
                int sp = e.IndexOf(' ');
                Json.Parse(e.Substring(sp + 1));
            }
            Check(r.G.Events.Any(x => x.StartsWith("smart_attack_state") && x.Contains("\"Positioning\"")), "state events");
            Check(r.G.Events.Any(x => x.StartsWith("smart_attack_warn") && x.Contains("no_layer")), "no_layer warning without a layer");
            Json.Parse(r.C.StatusJson(null)); Json.Parse(r.C.StatusJson(20)); Json.Parse(r.C.LinkStateJson());
            Check(r.G.Logs.Any(x => x.Contains(" tick D=")), "debug line per tick");
            Check(r.C.StepsJson("sa-1").Contains("\"cmd\":\"preflight\"") && r.C.StepsJson("sa-1").Contains("\"cmd\":\"launch\"") && r.C.StepsJson("sa-1").Contains("wire_depth"), "ADR section 11 steps");
            r.C.Cancel(20, null, "x");
            Check(r.G.Logs.Any(x => x.Contains("smart action smart_attack ->")), "summary line");
            Check(!r.G.Logs.Any(x => x.StartsWith(SmartAttackMath.LogPrefix, StringComparison.Ordinal)), "controller lines carry no prefix (the game sink adds one)");
            Check(SmartAttackController.SelfTestPlanLines().StartsWith("smart_attack plan"), "plan line");
        }

        private static void SaveLoad()
        {
            var r = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            r.RunTo(() => r.Odo() > 1.0, 600, "leg progress");
            r.C.OnSaveMarker(r.G.SavePath, r.G.NowS);
            string file = Path.Combine(r.G.Dir, SmartAttackSave.Key(r.G.SavePath) + ".json");
            Check(File.Exists(file) && !File.Exists(file + ".tmp"), "state file written atomically");
            string err;
            var d = SmartAttackSave.Parse(File.ReadAllText(file), out err);
            Check(d != null && d.State == "Leg" && d.History.Count > 0 && d.Saved.ContainsKey("telegraph") && d.AmmoParams != null, "file content " + err);

            var c2 = new SmartAttackController(r.G, r.P, false);
            int shifts = r.G.ShiftCalls;
            c2.OnMissionChanged(r.G.SavePath, r.G.NowS);
            Check(c2.LinkStateJson().Contains("\"active\":1"), "restored: " + c2.LinkStateJson());
            for (int i = 0; i < 6; i++) { r.G.Frame++; r.G.Advance(0.5); c2.Pump(r.G.NowS); }
            Check(r.G.ShiftCalls > shifts, "aim shift set again after load");

            int callsBefore = r.G.Calls.Count;
            c2.OnMissionLeft();   // T-130: leaving the mission forgets the attacks and asks the game for nothing
            Check(c2.LinkStateJson().Contains("\"active\":0") && c2.Pending == null && r.G.Calls.Count == callsBefore, "OnMissionLeft: " + c2.LinkStateJson());

            var c3 = new SmartAttackController(r.G, r.P, false);
            c3.OnMissionChanged(r.G.SavePath, r.G.NowS + 100);
            Check(File.Exists(file) && c3.LinkStateJson().Contains("\"active\":0"), "T-156: stale-time slot kept, not restored");

            // torpedo gone after the load: wire_lost, fields still returned
            r.C.OnSaveMarker(r.G.SavePath, r.G.NowS);
            Check(File.Exists(file), "rewritten");
            r.G.Torp.Alive = false;
            var c4 = new SmartAttackController(r.G, r.P, false);
            c4.OnMissionChanged(r.G.SavePath, r.G.NowS);
            Check(c4.LinkStateJson().Contains("\"active\":0") && c4.ResultOf("sa-1") == "wire_lost", "torpedo missing -> wire_lost");
            Check(r.G.Calls.Last(x => x.StartsWith("telegraph")) == "telegraph:5", "fields returned after a lost wire");

            // new mission without a .sav clears everything; RestoreOnLoad ask
            c4.OnMissionChanged(null, 0);
            Check(c4.Pending == null, "cleared");
            r.G.Torp.Alive = true;
            r.C.OnSaveMarker(r.G.SavePath, r.G.NowS);
            r.P.RestoreOnLoad = "ask";
            var c5 = new SmartAttackController(r.G, r.P, false);
            c5.OnMissionChanged(r.G.SavePath, r.G.NowS);
            Check(c5.Pending != null && c5.Pending.Kind == "restore" && c5.LinkStateJson().Contains("\"active\":0"), "ask mode asks");
            c5.Answer(c5.Pending.Id, "yes");
            Check(c5.LinkStateJson().Contains("\"active\":1"), "ask yes restores");

            // T-122: the second write keeps a backup; a broken main file is read from .bak
            r.P.RestoreOnLoad = "auto";
            r.C.OnSaveMarker(r.G.SavePath, r.G.NowS);
            Check(File.Exists(file + ".bak") && !File.Exists(file + ".tmp"), "backup after the second write");
            File.WriteAllText(file, "{broken");
            var c6 = new SmartAttackController(r.G, r.P, false);
            c6.OnMissionChanged(r.G.SavePath, r.G.NowS);
            Check(c6.LinkStateJson().Contains("\"active\":1"), "restored from .bak");
            // T-156: a new mission (last_save still at the old slot): finishing its attack must not erase the old slot
            var r2 = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            r2.C.OnSaveMarker(r2.G.SavePath, r2.G.NowS);
            string file2 = Path.Combine(r2.G.Dir, SmartAttackSave.Key(r2.G.SavePath) + ".json");
            Check(File.Exists(file2), "slot written");
            r2.C.OnMissionLeft();   // owner forgotten, as after a new mission
            r2.Go();
            r2.RunTo(() => r2.State() != null, 50, "new attack");
            r2.C.Cancel(20, null, "x");
            Check(File.Exists(file2), "slot of the previous mission survives a new attack's end");
            // T-156: a slot that is unparseable (main and backup) is still removed
            File.WriteAllText(file, "{broken"); File.WriteAllText(file + ".bak", "{broken");
            var c7 = new SmartAttackController(r.G, r.P, false);
            c7.OnMissionChanged(r.G.SavePath, r.G.NowS);
            Check(!File.Exists(file) && c7.LinkStateJson().Contains("\"active\":0"), "broken slot removed");
        }

        /// <summary>T-198 (ADR-0086): the file carries the save stamp; a load on a restarted clock restores by the stamp and moves the times; the hook flag silences the marker observer.</summary>
        private static void SaveLoad198()
        {
            var utc = new DateTime(2026, 10, 23, 2, 7, 37, DateTimeKind.Utc);
            var r = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            r.RunTo(() => r.Odo() > 1.0, 600, "leg progress");
            double tSave = r.G.NowS;
            string file = Path.Combine(r.G.Dir, SmartAttackSave.Key(r.G.SavePath) + ".json");
            r.C.OnSaveMarker(r.G.SavePath, tSave, SavStamp.Now(tSave, utc, "hook"), true);
            r.C.OnSaveMarker(r.G.SavePath, tSave, SavStamp.Now(tSave + 5, utc, "hook"), true);   // force: the same path is written again
            string err; var d = SmartAttackSave.Parse(File.ReadAllText(file), out err);
            Check(d != null && d.Sav != null && Math.Abs(d.Sav.ElapsedS - (tSave + 5)) < 1e-9, "stamp written, forced second write " + err);

            // the clock restarts: the game says 2 s, the header says tSave + 5
            var g = r.G; double old = g.NowS; g.NowS = 2;
            var c2 = new SmartAttackController(g, r.P, false);
            c2.OnMissionChanged(g.SavePath, 2, new SavStamp { ElapsedS = tSave + 5, Created = "2026-10-23T02:07:37Z", Src = "sav" });
            Check(c2.LinkStateJson().Contains("\"active\":1"), "restored by the stamp: " + c2.LinkStateJson());
            var c3 = new SmartAttackController(g, r.P, false);
            c3.OnMissionChanged(g.SavePath, 2, new SavStamp { ElapsedS = 99999, Src = "sav" });
            Check(File.Exists(file) && c3.LinkStateJson().Contains("\"active\":0") && g.Logs.Any(x => x.Contains("stamp_mismatch")), "other save: kept, not restored");
            g.NowS = old;

            // hook flag: the plain marker is silent; a forced save without an attack drops the slot (T-156 keeps foreign slots otherwise)
            r.C.SaveHookActive = true;
            File.Delete(file);
            r.C.OnSaveMarker(r.G.SavePath, r.G.NowS);
            Check(!File.Exists(file), "marker observer silent while the hook is active");
            r.C.OnSaveMarker(r.G.SavePath, r.G.NowS, SavStamp.Now(r.G.NowS, utc, "hook"), true);
            Check(File.Exists(file), "hook writes");
            var e = new Rig(); e.G.Dir = r.G.Dir; e.G.SavePath = r.G.SavePath;
            e.C.OnSaveMarker(e.G.SavePath, 10, SavStamp.Now(10, utc, "hook"), true);
            Check(!File.Exists(file), "hook save without an attack drops the slot of that path");
        }

        /// <summary>T-154 E1: the dry check at pick-click time reports the same refusals as Start and changes nothing.</summary>
        private static void PreflightDry()
        {
            string detail;
            var r = new Rig(); r.G.TargetPos = new SaVec(0, 40);
            Check(r.C.Preflight(20, 12, out detail) == "out_of_range", "preflight out_of_range");
            Check(detail != null && detail.Contains("40.0") && detail.Contains("max"), "preflight detail: " + detail);
            r = new Rig();
            Check(r.C.Preflight(20, 12, out detail) == null, "preflight in range: null");
            Check(r.Active() == 0 && r.C.Pending == null && r.G.Events.Count == 0 && r.G.Calls.Count == 0, "preflight leaves no trace: " + r.Active() + " " + string.Join(",", r.G.Calls));
            string ack;
            Check(r.Start(out ack) == null, "Start still works after preflight");
            Check(r.C.Preflight(20, 12, out detail) == "busy", "preflight busy while an attack runs");
            r = new Rig(); r.G.SubS.FreeWire = false;
            Check(r.C.Preflight(20, 12, out detail) == "no_free_wire", "preflight no_free_wire");
            r = new Rig(); r.G.Ammos.Clear(); r.G.AmmoErr = "target_not_allowed";
            Check(r.C.Preflight(20, 12, out detail) == "target_not_allowed", "preflight target_not_allowed");
            r = new Rig(); r.G.Info.State = "bearing_only";
            Check(r.C.Preflight(20, 12, out detail) == "track_bearing_only", "preflight bearing_only");
            r = new Rig(); r.G.NeutralsThrow = true;
            Check(r.C.Preflight(20, 12, out detail) == "geometry_error", "preflight fails closed");
        }


        // ---- T-101 / ADR-0071 section 4: the submarine evades a torpedo (UnitSteering holder "evasion") ----------------------------

        private static void EvasionRefuseStart()
        {
            var r = new Rig(); bool held = true; r.C.HeldByEvasion = u => held;
            string ack, detail;
            Check(r.Start(out ack) == "evading", "start refused while the submarine evades");
            Check(r.C.Preflight(20, 12, out detail) == "evading", "preflight refuses too");
            Check(r.Active() == 0 && r.G.Calls.Count == 0, "refusal leaves no trace");
            Check(Array.IndexOf(SmartAttackMath.RefusalCodes, "evading") >= 0 && !SmartAttackMath.RefusalText("evading", null).Contains("(evading)"), "refusal text exists");
            held = false;
            Check(r.Start(out ack) == null, "start after the evasion");
            r = new Rig(); r.C.HeldByEvasion = u => { throw new InvalidOperationException("boom"); };
            Check(r.Start(out ack) == null, "a throwing holder does not hold");
        }

        private static void EvasionHoldPositioning()
        {
            var r = new Rig(); r.G.SubS.Telegraph = 5; r.G.SubS.SpeedKn = 25; bool held = false; r.C.HeldByEvasion = u => held;
            r.Go();
            held = true;
            int calls = r.G.Calls.Count;
            r.Run(2 * r.P.PositionTimeoutS + 20);
            Check(r.State() == "Positioning", "still positioning: " + r.State());
            Check(!r.G.Calls.Skip(calls).Any(x => x.StartsWith("fire") || x.StartsWith("telegraph") || x.StartsWith("preset") || x.StartsWith("depthft")), "no order to the submarine while it evades: " + string.Join(",", r.G.Calls.Skip(calls)));
            Check(r.Events("position_timeout") == 0, "no position_timeout while held");
            Check(r.C.StepsJson("sa-1").Split(new[] { "evasion_hold" }, StringSplitOptions.None).Length == 2, "evasion_hold noted once: " + r.C.StepsJson("sa-1"));
            held = false;
            r.RunTo(() => r.Has("fire:adcap:False"), 400, "fire after the hold");
            Check(r.Events("position_timeout") == 0, "the timer was shifted by the hold");
        }

        private static void EvasionHoldWireContinues()
        {
            var r = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            bool held = true; r.C.HeldByEvasion = u => held;
            double odo = r.Odo(); int shifts = r.G.ShiftCalls, depths = r.G.WireDepthCalls;
            r.Run(30);
            Check(r.State() == "Leg" || r.State() == "Approach", "the attack goes on: " + r.State());
            Check(r.Odo() > odo, "the torpedo keeps moving along the leg");
            Check(r.G.ShiftCalls > shifts || r.G.WireDepthCalls > depths || r.G.Calls.Any(x => x.StartsWith("shift")), "torpedo orders go on while the submarine evades");
        }

        private static Rig EndedUnderHold(bool changeTelegraph, out Func<bool> release)
        {
            var r = ToLeg(x => { x.G.SubS.Telegraph = 5; x.G.SubS.SpeedKn = 25; });
            bool held = true; r.C.HeldByEvasion = u => held;
            release = () => { held = false; return true; };
            r.Run(2);
            if (changeTelegraph) r.G.SubS.Telegraph = 1;                 // the player turned the order while the submarine evaded
            r.G.Torp.OnWire = false; r.G.Torp.WireLost = true;
            r.Run(3);
            return r;
        }

        private static void EvasionWireLostDuringHold()
        {
            Func<bool> release;
            var r = EndedUnderHold(false, out release);
            Check(r.Active() == 0 && r.C.ResultOf("sa-1") == "wire_lost", "end wire_lost: " + r.C.ResultOf("sa-1") + " active " + r.Active());
            int tg = r.G.Calls.Count(x => x.StartsWith("telegraph")), pr = r.G.Calls.Count(x => x.StartsWith("preset"));
            r.Run(3);
            Check(r.G.Calls.Count(x => x.StartsWith("telegraph")) == tg && r.G.Calls.Count(x => x.StartsWith("preset")) == pr, "fields wait for the end of the hold");
        }

        private static void EvasionDeferredRestore()
        {
            Func<bool> release;
            var r = EndedUnderHold(false, out release);
            int tg = r.G.Calls.Count(x => x.StartsWith("telegraph")), pr = r.G.Calls.Count(x => x.StartsWith("preset"));
            release();
            r.Run(2);
            Check(r.G.Calls.Count(x => x.StartsWith("telegraph")) == tg + 1 && r.G.Calls.Last(x => x.StartsWith("telegraph")) == "telegraph:5", "telegraph given back after the hold");
            Check(r.G.Calls.Count(x => x.StartsWith("preset")) == pr + 1, "depth preset given back after the hold");
            int again = r.G.Calls.Count(x => x.StartsWith("telegraph"));
            r.Run(3);
            Check(r.G.Calls.Count(x => x.StartsWith("telegraph")) == again, "restored once");

            r = EndedUnderHold(true, out release);
            int tg2 = r.G.Calls.Count(x => x.StartsWith("telegraph"));
            release();
            r.Run(2);
            Check(r.G.Calls.Count(x => x.StartsWith("telegraph")) == tg2, "player_changed telegraph is not touched");
        }

        private static void EvasionLeaveMissionClearsDeferred()
        {
            Func<bool> release;
            var r = EndedUnderHold(false, out release);
            int tg = r.G.Calls.Count(x => x.StartsWith("telegraph"));
            r.C.OnMissionLeft();
            release();
            r.Run(3);
            Check(r.G.Calls.Count(x => x.StartsWith("telegraph")) == tg, "the deferred restore of the left mission is dropped");
        }

        public static string Run()
        {
            var tests = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("codes", Codes),
                new KeyValuePair<string, Action>("neutral_preflight", NeutralPreflight),
                new KeyValuePair<string, Action>("positioning", Positioning),
                new KeyValuePair<string, Action>("launch", Launch),
                new KeyValuePair<string, Action>("leg", Leg),
                new KeyValuePair<string, Action>("final", Final),
                new KeyValuePair<string, Action>("neutral_on_path", NeutralOnPath),
                new KeyValuePair<string, Action>("cancel", CancelAndRelease),
                new KeyValuePair<string, Action>("events", EventsAndStatus),
                new KeyValuePair<string, Action>("save_load", SaveLoad),
                new KeyValuePair<string, Action>("save_load_t198", SaveLoad198),
                new KeyValuePair<string, Action>("preflight_dry", PreflightDry),
                new KeyValuePair<string, Action>("evasion_refuse_start", EvasionRefuseStart),
                new KeyValuePair<string, Action>("evasion_hold_positioning", EvasionHoldPositioning),
                new KeyValuePair<string, Action>("evasion_hold_wire_continues", EvasionHoldWireContinues),
                new KeyValuePair<string, Action>("evasion_wire_lost_during_hold", EvasionWireLostDuringHold),
                new KeyValuePair<string, Action>("evasion_deferred_restore", EvasionDeferredRestore),
                new KeyValuePair<string, Action>("evasion_leave_mission_clears_deferred", EvasionLeaveMissionClearsDeferred),
            };
            foreach (var t in tests)
            {
                try { t.Value(); }
                catch (TF e) { return t.Key + ": " + e.Message; }
                catch (Exception e) { return t.Key + ": EXC " + e.GetType().Name + ": " + e.Message + " @ " + e.StackTrace.Split('\n')[0]; }
            }
            return null;
        }
    }
}
