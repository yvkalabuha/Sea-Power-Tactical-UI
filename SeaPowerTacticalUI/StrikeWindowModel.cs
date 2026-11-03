using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 phase B (ADR-0132 section 11): the view of the strike window. Plain data: the controller fills it, the window body draws it. No Unity, no game objects.
    internal sealed class StrikeView
    {
        public List<StrikeGroupView> Groups = new List<StrikeGroupView>();
        public List<StrikeAirRow> Air = new List<StrikeAirRow>();
        public List<StrikeShipRow> Ships = new List<StrikeShipRow>();
        public string Calib;
        public bool Estimate = true, Ready = true;
        public double KillThreshold = 0.8;
        public bool KillEstimate = true, BonusKnown = true, Ledger = true;   // T-342: P(sunk) is an estimate; the difficulty bonus was read; the hit counter works
        public string CalibId = "estimate";   // phase E1: the id of the calibration of the line (shown when the odds are not estimates any more)
        public double Now;
    }

    internal sealed class StrikeGroupView
    {
        public string Name, Status, StaleWhy, WaitWhy, Gather, Mode, Goal = "sink";   // Goal (T-342): breach | sink
        public int Seq, Chosen = -1, MainId;
        public GeoPt? WaitPt;
        public double WaitR, AxisDeg = double.NaN, DeckHeldS = -1;
        public GeoPt? TargetPt;
        public List<int> Members = new List<int>(), Targets = new List<int>();
        public List<string> DeckKeys = new List<string>(), Left = new List<string>();
        public List<StrikeCardView> Cards = new List<StrikeCardView>();
        public List<StrikeTimelineRow> Timeline = new List<StrikeTimelineRow>();
        public List<StrikeLaunchView> Launches = new List<StrikeLaunchView>();   // the chosen variant: the launch points on the map
        // phase C: the execution, the result, the launch button
        public string Exec = "none", Roe, LaunchWhy;   // Exec: none | executing | done | cancelled; LaunchWhy: null (can launch) | no_plan | executing | gate | wait_no_sea
        public int Wave = 1;
        public bool CanLaunch;
        public List<StrikeResultRow> Results = new List<StrikeResultRow>();
        public List<StrikeEwRow> Ew = new List<StrikeEwRow>();                   // phase D: the jammers (the execution state, else the plan of the chosen variant)
        public List<StrikeRoutePrim> Routes = new List<StrikeRoutePrim>();       // the gate / descent / launch points and the SAM legs of the chosen variant or of the rows not launched yet
        // T-342 phase B: the deck orders of the group and the summary of the last deck planning
        public List<StrikeDeckOrderRow> DeckOrders = new List<StrikeDeckOrderRow>();
        public string DeckBlock, DeckKind;                                       // DeckBlock: null | player | loaded
        public bool HasDeckPlan, ShortAfterAll, NowOk, WithOk, AllOk;
        public int DeckAircraft, DeckMissiles, NowN, WithN, AllN;
        public double DeckLastReadyS = -1, NowP, WithP, AllP;
        public List<string> DeckStock = new List<string>(), DeckRejected = new List<string>(), DeckNotes = new List<string>();
        // T-342 phase C: the auto mode and the clearing
        public bool AutoOn;
        public int AutoWave, AutoMax = 3;
        public string AutoState = "off", AutoWhy, AutoStopWhy;                  // AutoState: off | wait | replan | launch | stop
        public List<string> AutoNotes = new List<string>();
        public string ClearStage, ClearHow;                                     // null = no clearing; stage1 | wait_sunk | release | released | red_wait | timeout | fuel | lost
        public int ClearNeighbour, ClearMain, ClearRewaves;
        public double ClearWaitS, ClearTimeoutS = -1, ClearFuelS = -1;
        public string ClearName;                                                // the class name of the neighbour (for the line)
        // T-342 phase D: the fighter threats of the group, the ones nobody jams, the notes of the jam
        public List<StrikeFighterRow> FighterThreats = new List<StrikeFighterRow>();
        public List<int> FighterUncovered = new List<int>();
        public List<string> EwNotes = new List<string>();
    }

    /// <summary>One ordered aircraft of the group (T-342 B). State: sent | preparing | await_lift | launch | in_group | failed | cancelled | aborted; Err: no_ammo | no_airframes | launches_disabled | deck_inoperable | unknown_loadout | late | ...</summary>
    internal sealed class StrikeDeckOrderRow
    {
        public string Key, DeckName, Type, Loadout, Display, Ammo, State, Err;
        public int Missiles;
        public double ReadyS;   // seconds left to the ready time (0 = ready)
        public bool Live;
    }

    /// <summary>One jammer of a group or of a card (phase D): the station, the cone, what it covers, the lateness and the state. State: plan | wait | to_station | on_station | jamming | off | back | guard | retreat | no_station | lost.</summary>
    internal sealed class StrikeEwRow
    {
        public int Uid;
        public string Name, Type, State = "plan", Why;
        public bool On;
        public double ConeDeg, BearingDeg, DistToStationNm = -1, TOnS = double.NaN, LateS, ShiftS, ClearLeftS = -1;   // ClearLeftS (T-342): seconds of the clear time left before the return (clear_wait), -1 = none   // TOnS: seconds from now to the switch on (NaN = unknown)
        public GeoPt? Station, Aim;
        public List<GeoPt> Route = new List<GeoPt>(), UncoveredPts = new List<GeoPt>(), CoverPts = new List<GeoPt>();
        public List<int> Cover = new List<int>(), Uncovered = new List<int>();
        public List<string> BandsMissing = new List<string>();
        // T-342 phase D: the mode (transit | attack | retreat), the role (ships | fighters = the reserve), what is jammed by the object, the ships nobody jams while the aircraft is on a fighter
        public string Mode = "transit", Role = "ships", JamKind, FighterCls, FighterWhy;
        public int FighterId;
        public double FighterDistNm = -1, FighterRtNm = -1;
        public bool FighterIrOnly;
        public GeoPt? JamPos, FighterPt;
        public List<int> ShipsLost = new List<int>();
    }

    /// <summary>A hostile fighter that threatens the strikers (T-342 D). JammedBy = the jammer that holds it in its cone (0 = nobody).</summary>
    internal sealed class StrikeFighterRow { public int Id, JammedBy; public string Cls; public GeoPt Pt; public double RNm, DistNm, EtaS = -1; public bool IrOnly; }

    internal sealed class StrikeResultRow { public int Id, Fired, Hits, Arrived, ShotDown; public string Name, State; public double SpreadS; }

    /// <summary>One aircraft of the plan or of the execution on the map: its points and the part of the way inside the SAM zones (miles / seconds / under the horizon).</summary>
    internal sealed class StrikeRoutePrim { public int Uid, TargetId, Stage; public GeoPt? Descent; public GeoPt Gate, Launch; public bool Launched; public double InSamNm, InSamS; public bool UnderHorizon; }

    internal sealed class StrikeLaunchView { public int Uid, Stage; public string Name; public GeoPt Pt; public int TargetId; }

    /// <summary>One aircraft (or a deck task) of the window. Why: null = a striker | helicopter | no_asm | tv_only | no_ammo | mount_inoperable | ew_later | in_group:&lt;g&gt; | owned:&lt;window&gt;:&lt;g&gt; | deck_not_ready | loadout_unknown.</summary>
    internal sealed class StrikeAirRow
    {
        public int Uid;
        public string DeckKey, Type, Name, Group, Why, Where;
        public List<string> Missiles = new List<string>();
        public double FuelPct = -1, ReadyInS = -1, Lat, Lon;
        public bool Ew, Airborne;
    }

    /// <summary>A surface contact. Excl: null (a target) | civilian | neutral | friendly | not_identified | not_hostile. Why: the key of the priority (ad | tons | low_role).</summary>
    internal sealed class StrikeShipRow
    {
        public int Id, Prio, Need;
        public string ClassName, AdText, Why, Excl, AdSource, Role;
        public double Tons, SamNm, HorizonNm, Ksub, Ssub, Lat, Lon;
        public bool Hostile;
    }

    internal sealed class StrikeCardView
    {
        public string Kind, Goal = "breach";   // Kind: min_missiles | max_targets | clear_pause | flank | damage_only
        public bool KillEstimate = true;
        public int Missiles, Targets, Index;
        public double T, FuelMin = -1, MainP;   // T: seconds from now to the arrival; FuelMin: the smallest share of the endurance left over the plan, percent (-1 = unknown)
        public bool Red, Estimate;
        public List<StrikeCardTarget> PerTarget = new List<StrikeCardTarget>();
        public List<StrikeCardCarrier> Carriers = new List<StrikeCardCarrier>();
        public List<string> Warnings = new List<string>(), Why = new List<string>();
        public List<StrikeEwRow> Ew = new List<StrikeEwRow>();   // phase D: the plan of every jammer of the variant
        public int ClearNeighbour;                                // T-342 C: clear_pause: the neighbour sunk first (0 = not clear_pause)
        public double TStage2 = -1;
        public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);   // T-349: "uid:<n>" -> aircraft name, "ammo:<key>" -> missile name
    }

    internal sealed class StrikeCardTarget { public int Id, N, ClearedBy = -1, H1 = -1, MSink = -1, ShortBy; public string Name; public double P, SpreadS, PSunk = -1, PBreach = -1, PIfAlive = -1; public bool Short; public int Stage; }   // T-342: H1 / MSink / PSunk / PBreach / Short / ShortBy: the sinking model (-1 = breach or not read)

    internal sealed class StrikeCardCarrier { public int Uid, Salvo, TargetId; public string Name, Ammo, AmmoName; }

    internal sealed class StrikeTimelineRow
    {
        public int Uid, Salvo, Target;
        public string Name, Ammo, State, Why;
        public double TGo, TIssue, TLaunch, TArrive;
        public double FactIssue = -1, FactLaunch = -1, LateS, DtOrderS = -1, InSamNm, InSamS;   // facts: seconds from now (negative = in the past), -1 = not yet
        public bool UnderHorizon;
    }
}
