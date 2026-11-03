using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 3: the data model of the strike window. Plain types only (no game objects, no Unity, no reflection).

    /// <summary>One row of the weapon list of an aircraft (raw game values + the class StrikeAirMath gives it).</summary>
    internal sealed class StrikeMissile
    {
        public string Ammo, Name, Type, TargetType, Guidance, MidCourse, SeekerBand;   // raw values of the game
        public int Count;
        public bool Operable = true, AntiRadiation, TargetRequired;
        public double MinNm, MaxNm, SpeedKn, MinLaunchAltFt, MaxLaunchAltFt, CruiseAltFt, FrontalM2, MassKg, DefBonus;
        public double ProfileAltFt, ProfileKn;   // phase E1: the altitude / speed the game's interception chance sees (PointValue ProfileForMissile: the terminal altitude, else the final flight phase, else the attack altitude); 0 = not read (the cruise values)
        public string Klass;   // StrikeAirMath.MissileClass: asm | illum | tv | arm | short | unguided | not_asuw
        public bool SeaSkim, NeedsIllum;
        // T-342 phase A: the warhead (HeAmmo values; the game layer fills them)
        public double WarheadPower, DudPower, DamageMult = 1;
        public string Warhead = "normal";   // normal | heat | frag | cluster
        public StrikeMissile Copy() { return (StrikeMissile)MemberwiseClone(); }   // T-342 B: the virtual aircraft of the deck planner get their own rows
    }

    /// <summary>The offensive jammer of an aircraft (phase D): the best station Offensive | Universal. Capable = the game layer can give the order (JamTask) and switch the station.</summary>
    internal sealed class StrikeEw
    {
        public double RangeNm, ConeDeg; public string[] Bands; public bool On;
        public string StationName, Type;                 // AN/ALQ-99 | Buket_OECM | Azaliya_OECM | AN/ALT-28; Offensive | Universal
        public int Stations; public bool Operable = true, Capable = true;
    }

    /// <summary>One AA zone on the track of a missile (neighbours in the order of passing, the target last). K is already multiplied by the missile factor f.</summary>
    internal struct StrikeZone
    {
        public double K, S, W, C;   // W: share of the track inside the zone; C: eta / shared factor / (1 - P of the clearing)
        public bool IsTarget;
        // phase E1: the ship of the zone (for the log) and the calibration scale (alpha x difficulty); K is used as K x Scale, capped by S x PCap. 0 = not set = 1
        public int TrackId;
        public string ClassIni;
        public double Scale;
        public double F;   // the missile factor already inside K (log only; 0 = 1)
        public bool IsCleared;   // phase E1: a neighbour that an early wave hit ("clearing"); Gone is the share of its air defence that is gone (ClearGain x P, 1 = skipped), C stays eta x shared
        public double Gone;
        public double KScaled { get { double sc = Scale > 0 ? Scale : 1; return Math.Min(K * sc, S * StrikeCalib.PCap); } }
        public static StrikeZone Target(double k, double s) { return new StrikeZone { K = k, S = s, W = 1, C = 1, IsTarget = true, Scale = 1 }; }
        public static StrikeZone Near(double k, double s, double w, double c) { return new StrikeZone { K = k, S = s, W = w, C = c, Scale = 1 }; }
    }

    /// <summary>The AA capacity of a ship against a reference raid: K kills of S shots (sub / supersonic sea skimmers).</summary>
    internal struct StrikeAd { public double Ksub, Ssub, Ksup, Ssup; public string Source; }   // Source: cache | aaw | role | none

    internal sealed class StrikeSam { public string Ammo; public double Pk, MinAltFt = -500000, MaxAltFt = 500000, MaxAttackKn, RangeNm, SpeedMult = 1.4, OutOfAltPen = 0.5, SizeMult = 1.0; }

    internal sealed class StrikeAircraft
    {
        public int Uid, DeckUid;
        public string Type, Name, ObjType, Role, Why;   // ObjType: Aircraft | Helicopter; Role: striker | ew | excluded; Why: null or the reason key
        public bool Airborne;
        public double Lat, Lon, AltFt, FuelFrac, EnduranceS, CruiseKn, CruiseAltFt;
        public List<StrikeMissile> Missiles = new List<StrikeMissile>();
        public StrikeEw Ew;
        // phase B: the fuel as the CAP reads it (T-337): the path left before the return / the full tank, in nm (-1 = unknown); FuelFracLive = the game's own live fraction
        public double ReserveNm = -1, MaxRangeNm;
        public bool FuelFracLive;
        public string DeckKey;   // not null = a prepared deck task, not an aircraft yet (Uid = 0)
        public double ReadyInS = -1;
        // phase C: the heading (for the turn onto the target) and the speed of the aircraft
        public double HeadingDeg = -1, SpeedKn;   // HeadingDeg -1 = unknown (the turn is taken as 90 degrees)
    }

    /// <summary>A prepared launch task of a deck whose loadout carries anti-ship missiles (phase B: the window lists it, the group may hold its key).</summary>
    internal sealed class StrikeDeckItem
    {
        public int DeckUid;
        public string Key, Type, Loadout, State, DeckName, Why;
        public double RemainS, Lat, Lon;
        public List<StrikeMissile> Missiles = new List<StrikeMissile>();
        public StrikeEw Ew;   // phase D: not null = a task of a deck with a jammer aircraft (EA-6B ...)
        public int VehicleUid;   // T-342 B: the aircraft of a launching task (0 = not known)
    }

    // ---- T-342 phase B: the deck ordering (ADR-0133 section 3, 7, 8) ----

    /// <summary>One loadout of a hangar type, read from Loadouts(deck, type) of the SAME row.</summary>
    internal sealed class StrikeDeckLoadout
    {
        public string Name, Display;
        public bool Available;                         // DeckCanLaunch (stock and points)
        public double ReadyS;                          // AirOpsMath.ReadyS(lo, mode, 0)
        public List<StrikeMissile> Missiles = new List<StrikeMissile>();   // Count = per aircraft
        public bool Unknown;                           // a weapon of the loadout could not be read
        public Dictionary<string, int> Cats;           // the magazine categories one aircraft takes (the game counts the deck stock by category); null = the main ammunition by its file name
    }

    internal sealed class StrikeHangarRow
    {
        public int DeckUid, Numbers;                   // Numbers = min(VtNumbers, sum of Squadron.Numbers)
        public string Type, ObjType;                   // file name; Aircraft | Helicopter
        public double MaxRangeNm = -1, CruiseKn = 450, CruiseAltFt = 25000;   // -1 = the range of the type is not known
        public List<StrikeDeckLoadout> Loadouts = new List<StrikeDeckLoadout>();
    }

    internal sealed class StrikeDeckInfo
    {
        public int DeckUid; public string Name; public double Lat, Lon;
        public bool Down, LaunchesDisabled;
        public Dictionary<string, int> Stock, StockMax;   // category (or ammo) -> left / max of the deck magazine; null = not readable
        public List<StrikeHangarRow> Rows = new List<StrikeHangarRow>();
    }

    /// <summary>One (deck, type, loadout) the planner may order.</summary>
    internal sealed class StrikeDeckOffer
    {
        public int DeckUid, PerAircraft, Free, Units, Rank;   // PerAircraft: strike missiles on one aircraft; Free: airframes free for the strike; Units: what may be ordered (Free, stock)
        public string DeckName, Type, Loadout, Display, Ammo, Why;   // Why null = can order; else range | range_unknown | no_airframes | no_ammo | stock | launches_disabled | deck_down | backoff | not_striker
        public double ReadyS, AxisOffNm, DistNm, DeckLat, DeckLon;
        public bool Illum;                             // AS-2: needs an illuminator
        public StrikeHangarRow Row; public StrikeDeckLoadout Lo;
        public Dictionary<string, int> Stock;   // the magazine of the deck (shared by its loadouts); null = not readable
        public Dictionary<string, int> Needs;   // category (or the main ammunition) -> what ONE aircraft of the offer takes from the magazine
    }

    /// <summary>One ordered aircraft (OrderLaunch makes one task).</summary>
    internal sealed class StrikeDeckOrder
    {
        public string Key, DeckName, Type, Loadout, Display, Ammo, Err;
        public string State = "sent";                  // sent | preparing | await_lift | launch | in_group | failed | cancelled | aborted
        public int DeckUid, Missiles, Uid;             // Uid of the adopted aircraft (in_group)
        public double OrderedT, ReadyS, RemainS = -1;
        public bool Live { get { return State == "sent" || State == "preparing" || State == "await_lift" || State == "launch"; } }
    }

    internal struct StrikeDeckEval { public bool Ok; public double P; public int N; }   // the variant to order for: threshold reached, P of the main target, missiles used

    internal sealed class StrikeDeckSummary
    {
        public int Aircraft, Missiles;                 // live orders
        public double LastReadyS = -1;
        public StrikeDeckEval Now, With, All;          // airborne / + live orders / + everything the decks can give
        public bool ShortAfterAll;
        public string Kind;                            // min_missiles | max_targets
        public List<string> Notes = new List<string>();   // the last messages: deck_ordered:..., deck_err:..., deck_blocked:..., deck_ready
        public List<string> Rejected = new List<string>();   // "<deck>|<type>|<why>" of the offers the filter turned down
        public List<string> StockRows = new List<string>();  // "<deck>|<ammo>|<left>|<max>"
    }

    internal sealed class StrikeLeave { public int Uid; public string Why; public double T; }   // fuel_margin | fuel_floor | fuel_hard | no_missiles | lost | released

    internal sealed class StrikeShip
    {
        public int TrackId;
        public double Lat, Lon, CourseDeg, SpeedKn, FixAgeS, DisplacementT, SamMaxNm, SamCeilFt, RadarFt;
        public bool Identified, Hostile, Civilian, Carrier, Excluded;
        public string Side, ClassIni, ClassName, Role, Why;
        public int AawCap = -1;
        public StrikeAd Ad;
        public List<StrikeSam> Sams = new List<StrikeSam>();
        public string[] RadarBands;
        // T-342 phase A
        public double KillBudget = -1;   // B = 5 * maxI of the class (HitEstimateMath.Budget); -1 = unknown
        public bool HasCiws;             // the class carries an AA gun (CIWS)
        public double PriorLoss;         // the sum of L of the own hits seen this mission (unweighted), filled by the controller
        public int PriorHits;
        public double RadarMaxNm;   // phase D: the longest search / targeting radar of the class, nm (0 = unknown); RadarFt above = the radar height of the class (100 = unknown)
    }

    internal sealed class StrikeTargetSet
    {
        public string Mode = "single";   // single | spread
        public int MainId;
        public List<int> Ships = new List<int>();
    }

    internal sealed class StrikeAssignment
    {
        public int Uid, TargetId, Salvo;
        public string Ammo;
        public GeoPt GatePt, LaunchPt;
        public double LaunchAltFt, LaunchNm, TofS, TLaunch, TIssue, TArrive, InSamNm, InSamS, ReadyS;
        public bool UnderHorizon;
        // phase C: the descent point (the first point of the route at which the aircraft goes down to LaunchAltFt), the time to leave the gate, the order of the assignments of one aircraft
        public GeoPt DescentPt;
        public bool DescentAtStart;
        public double TGo, CruiseAltFt;
        public int Order;
        public string GateWhy;   // ok | land_shifted | no_gate
        public double LaunchKn;  // T-342: the planned speed of the run in (the cruise speed of the plan), kn; 0 = the old behaviour
        public int Stage;        // T-342 C: 0 = a plain strike; 1 = clear_pause stage 1 (the neighbour); 2 = stage 2 (the main target, held at the gate)
    }

    internal sealed class StrikeTargetPlan
    {
        public int TrackId, N, ClearedBy = -1;
        public double P, TArrive, SpreadS;
        public string ClassName;
        // phase E1: what the probability was computed from (the log of the calibration; not in the save)
        public string ClassIni, Ammo;
        public bool Sup;
        // T-342 phase A: the sinking model
        public string Goal = "breach";   // breach | sink
        public int H = -1, H1 = -1, MSink = -1;   // hits to sink, with the margin, missiles for KillThreshold (-1 = over KillMaxM or not sink)
        public double PSunk = -1, PBreach = -1, Sigma = -1, Budget = -1, BudgetLeft = -1, LossPerHit = -1;
        public bool Short; public int ShortBy;
        public List<StrikeZone> Zones = new List<StrikeZone>();
        // T-342 phase C
        public int Stage;                       // 0 = a plain strike; 1 / 2 = the stages of clear_pause
        public double PIfAlive = -1;            // clear_pause stage 2: P(sunk) of the main target if the neighbour stays (the fallback count); -1 = not counted
    }

    /// <summary>The plan of one jammer aircraft in a variant (phase D, ADR-0132 section 8).</summary>
    internal sealed class StrikeEwPlan
    {
        public int Uid;
        public GeoPt StationPt;
        public List<int> CoverIds = new List<int>();
        public List<int> Uncovered = new List<int>();
        public double TDepart, TOn, TOff, LateS;
        public GeoPt AimPt; public double ConeDeg, BearingDeg, StationNm, TStation, TDetect, AltFt, ShiftS;
        public List<GeoPt> Route = new List<GeoPt>();                                // from the aircraft / deck to the station, outside the SAM zones
        public List<string> BandsMissing = new List<string>();                       // "<track>:<band>"
        public string Why = "ok";                                                    // ok | no_station:no_sea|in_sam|cone|horizon|route_in_sam | no_targets
        public bool Deck;                                                            // the aircraft is not airborne yet
        public string Role = "ships";                                                // T-342 D: ships | fighters (the reserve: jams the ships like "ships" and takes the fighter threats in every active state, 43 of the plan)
    }

    /// <summary>The execution state of one jammer aircraft (phase D).</summary>
    internal sealed class StrikeEwState
    {
        public int Uid; public string State = "wait";                                // wait | to_station | on_station | jamming | off | back | guard | retreat | no_station | lost
        public bool On; public double OnT = -1, OffT = -1, ArriveT = -1, LastArriveT = -1; public string Why, Sent;
        public GeoPt JamPt;                                                          // the point of the last JamTask
        public int Tries; public double NextTryT;
        public StrikeEwPlan Plan;                                                    // the plan this aircraft flies (the station may be replanned after a guard)
        public double LastThreatT = -1;                                              // T-342: the last mission time an AirGuard threat (stage >= Warn) was seen for this aircraft
        // T-342 phase D: the jam of a hostile fighter by its object (the layer over the states above; the point jam of the ships is the other JamKind)
        public string Role = "ships", Mode = "transit";                               // Mode: transit | attack | retreat (ui_state ew[].mode)
        public string JamKind;                                                       // null | point | object
        public int FighterId;                                                        // the track jammed by object (0 = none)
        public List<int> FighterCover = new List<int>();                             // the other fighters in the same cone
        public double FighterOnT = -1, FighterLastThreatT = -1, FighterNextT, StandoffNextT;
        public int FighterTries; public string FighterWhy;                           // FighterWhy: null | no_cover:<track> | jam_refused | uncovered
        public GeoPt? StandoffPt;
        public List<int> ShipsLost = new List<int>();                                // covered ships nobody else jams while this aircraft is on a fighter (shown in the window)
    }

    /// <summary>T-342 D: a hostile air track as the strike sees it (the game layer fills it; only identified tracks carry a class).</summary>
    internal sealed class StrikeFighter
    {
        public int TrackId; public string ClassIni, ClassName;
        public double Lat, Lon, CourseDeg, SpeedKn, RNm;
        public string RSrc;                    // class | default | no_aaw (AirGuardMath.ThreatRadiusNm)
        public bool Identified, Hostile, Helicopter, IsFighter, IrOnly;
        public bool Globe;                     // T-347: world metric at the track (AswMath.GlobeFromMetric); false = flat (stable)
        public string FighterSrc;              // role | aam_radar | none
    }
    internal sealed class StrikeFighterThreat { public StrikeFighter F; public double DistNm, ClosingKn, EtaS; public bool Inside; public int NearUid; public double SinceT = -1, EtaInS = -1, OutSinceT = -1; }   // T-346 D3: SinceT / EtaInS = the entry (the record lives on), OutSinceT = since when the entry or stay condition is not met
    internal sealed class StrikeEwJammer { public int Uid; public GeoPt Pos; public double ConeDeg; public string Role = "ships"; public bool Free = true; public int Current; public double CurrentSinceT = -1; }
    internal sealed class StrikeFighterJob { public int Uid, TargetId; public List<int> Cover = new List<int>(); }

    internal sealed class StrikeVariant
    {
        public string Kind;   // min_missiles | max_targets | clearing | flank | damage_only
        public string Goal = "breach";   // T-342
        public bool Red;                  // T-342 damage_only: the kill threshold is not reached
        public double AxisDeg, Score, T, FuelMinFrac = 1;
        public int MissilesUsed, TargetsHit;
        public bool Estimate = StrikeCalib.Estimate;
        public List<StrikeTargetPlan> PerTarget = new List<StrikeTargetPlan>();
        public List<StrikeAssignment> Assignments = new List<StrikeAssignment>();
        public List<StrikeEwPlan> Ew = new List<StrikeEwPlan>();
        public List<string> Warnings = new List<string>();
        public List<string> Why = new List<string>();
        // T-342 phase C: clear_pause
        public int ClearNeighbour;              // the track of the neighbour (0 = not clear_pause)
        public double TStage2 = -1;             // the estimate of T of stage 2 (shown; the real T is planned at the release)
    }

    /// <summary>Phase C: the execution row of one assignment (one aircraft, one ammunition, one target). The state machine is StrikeExecMath.Step.</summary>
    internal sealed class StrikeExecRow
    {
        public int Uid, TargetId, Salvo, Order;
        public string Ammo;
        public double TofPredS = -1;   // T-350: the flight expected at the last tick (not saved)
        public GeoPt Gate, Descent, Launch;
        public GeoPt Beyond;       // the run in goes on to this point past the launch point (the aircraft keeps its heading toward the target while it waits for the time of the order)
        public bool HasBeyond;
        public double LaunchAltFt, CruiseAltFt, MinNm, MaxNm, VmaxKn, LaunchNm;   // LaunchNm: the planned range of the launch (the order goes out within 2 nm of it, inside the window of the missile)
        public bool DescentAtStart;
        public double TGo, TLaunch, TArrive;                // the plan, game seconds
        public string State = "to_gate";                    // to_gate | at_gate | run_in | issued | launched | held | skipped | cancelled | failed
        public double IssuedT = -1, LaunchedT = -1, LateS, DtOrderS = -1, HeldSince = -1, NextTryT, TurnS, GateT = -1;
        public int AmmoBefore = -1, LaunchedN, Tries;
        public string Why;                                  // held: contact_lost | land | <attack error>; skipped: hold_timeout | target_gone | late_window | excluded
        public string Sent;                                 // the last route order: gate | orbit_gate | run_in | descent (not repeated)
        public double LaunchKn;                             // T-342: the planned speed of the run in, kn (0 = the old behaviour)
        public string SpeedSent;                            // T-342: null | set | restored
        public double InSamNm, InSamS;
        public bool UnderHorizon;
        public string ClassIni;                             // T-342 C: the class of the target when the plan was launched (re-checked before the order)
        public string Logged;                               // the last reason the controller wrote to the log (held / skipped), so a repeat is not logged again
    }

    /// <summary>A missile of an aircraft of the strike in flight (the id is a hash of the game object).</summary>
    internal sealed class StrikeMslSeen { public int Id, Uid, TargetId; public double Lat, Lon, T; }

    internal sealed class StrikeTargetResult
    {
        public int TrackId, Fired, Hits;
        public string State = "unknown";   // sunk | damaged | intact | unknown
        public double FirstArriveT = -1, LastArriveT = -1, SpreadS;
        public string ClassName;
        public int Arrived, ShotDown;   // missiles that vanished at the target / on the way (the last seen distance)
    }

    internal sealed class StrikeEvent { public int Uid; public string Name; public double PlanT, FactT = -1; }   // gate | go | issue | launched | arrive | result

    internal sealed class StrikeSnapTarget { public int Id; public double Lat, Lon, CourseDeg, SpeedKn; }

    internal sealed class StrikeSnapAir { public int Uid, Missiles; }

    /// <summary>What the plan was computed from: positions and courses of the targets, the ships with AA, the aircraft with their missiles.</summary>
    internal sealed class StrikeSnapshot
    {
        public double T;   // mission time of the snapshot
        public List<StrikeSnapTarget> Targets = new List<StrikeSnapTarget>();
        public List<int> AdShipIds = new List<int>();
        public List<StrikeSnapAir> Air = new List<StrikeSnapAir>();
    }

    internal sealed class StrikeTimeline
    {
        public int PlanId;
        public double CreatedT;
        public string Status = "draft";   // draft | stale | armed | executing | done | cancelled
        public string StaleWhy;
        public StrikeSnapshot Snapshot = new StrikeSnapshot();
        public List<StrikeEvent> Events = new List<StrikeEvent>();
        // phase C: the execution
        public List<StrikeExecRow> Exec = new List<StrikeExecRow>();
        public List<StrikeTargetResult> Results = new List<StrikeTargetResult>();
        public double StartT = -1, DoneT = -1;
        public string Roe;   // veto | hold: the effective mode of the launch
        public Dictionary<int, int> HitBase = new Dictionary<int, int>();   // phase E1: the hits the game's ledger already counted on every target at the launch (the ledger is not reset by Restart Mission and counts older waves)
        public double TDetect = -1;   // phase D: when the first aircraft or missile of the strike enters the detection range of the radars of the covered targets
    }

    internal sealed class StrikeGroup
    {
        public string Name;
        public int Seq;
        public List<int> Members = new List<int>();
        public StrikeTargetSet Target = new StrikeTargetSet();
        public List<StrikeVariant> Variants = new List<StrikeVariant>();
        public int Chosen = -1;
        public StrikeTimeline Timeline;
        public GeoPt? WaitPt;
        public double WaitR;
        public string TofSrc = "rough";
        public string WaitWhy;                                 // ok | land_shifted | rotated | no_sea | no_hostiles | null (no point)
        public string Gather = "idle";                         // idle | gather | waiting
        public List<string> DeckKeys = new List<string>();
        public List<StrikeLeave> Left = new List<StrikeLeave>();
        public int Wave = 1;
        // phase C: what the last strike left: the rows (facts), the results by target, the end state (done | cancelled)
        public List<StrikeExecRow> LastExec = new List<StrikeExecRow>();
        public List<StrikeTargetResult> Results = new List<StrikeTargetResult>();
        public string ExecStatus;
        public double ResultT = -1;
        // phase D: the jammer aircraft of the strike (state per aircraft), the shift of T asked by the player
        public List<StrikeEwState> EwExec = new List<StrikeEwState>();
        public double ShiftS;
        public string Goal = "sink";   // T-342: breach | sink (saved)
        // T-342 phase B: the deck orders of the group (not saved, ADR-0133 section 8)
        public List<StrikeDeckOrder> DeckOrders = new List<StrikeDeckOrder>();
        public string DeckBlock;                       // null | player | loaded
        public StrikeDeckSummary Deck;                 // null = no deck planning yet
        // T-342 phase C: the auto mode and the clear_pause state (neither is restored from the save, ADR-0133 section 8)
        public StrikeAutoState Auto = new StrikeAutoState();
        public StrikeClearState Clear;                 // null = no clear_pause running
        // T-342 phase D: the fighter threats of the strike (not saved)
        public List<StrikeFighterThreat> FighterThreats = new List<StrikeFighterThreat>();
        public List<int> FighterUncovered = new List<int>();
        public List<string> EwNotes = new List<string>();   // ewf_on:<uid>:<tgt> | ewf_off:<uid>:<why> | ewf_no_cover:<uid>:<cls> | ewf_uncovered:<n> | ewf_ships_lost:<uid>:<n>
    }

    /// <summary>T-342 C: the "Auto" flag of a group and what it does now.</summary>
    internal sealed class StrikeAutoState
    {
        public bool On; public int Waves;
        public string State = "off", Why, StopWhy;      // State: off | wait | replan | launch | stop
        public double OnT = -1, LastReplanT = -1, LastLaunchT = -1;
        public bool ChosenByPlayer;                      // the player chose the variant of the current plan by hand (valid for this wave only)
        public bool WasOnAtSave;                         // the save had the auto on (only for the message after the load)
        public List<string> Notes = new List<string>();  // auto_on | auto_launch:<wave>:<kind> | auto_red:<p>:<th> | auto_stop:<why> | auto_off_loaded | clear_*:...
    }

    /// <summary>T-342 C: a running clear_pause (stage 1 sinks the neighbour, stage 2 waits at its gate for the observed sinking).</summary>
    internal sealed class StrikeClearState
    {
        public string Stage = "stage1";                  // stage1 | wait_sunk | release | released | red_wait | timeout | fuel | lost
        public int NeighbourId, MainId, Rewaves;
        public List<int> Stage1Uids = new List<int>(), Stage2Uids = new List<int>();
        public GeoPt Gate2; public bool Gate2Sent; public double Gate2NextT;
        public double WaitStartT = -1, TimeoutT = -1, FuelT = -1, SunkT = -1;
        public string How;                               // destroyed | gone_after_hits
        public bool RedConfirmed, Stage2Green;           // the player confirmed a red card at the launch; stage 2 was green on the card
        public double SensorT = -1;                      // the last Destroyed read (once per second)
        public string Neighbour = "alive";               // the last read of the sensor
        public int Hits;                                 // own hits on the neighbour (the ledger) at the last read
        public bool RewaveOpen, PendingMain;             // a rewave by the neighbour is planned / flies / waits for the button; the draft "by the main target without clearing" waits for the end of a flying rewave
        public int Replans;                              // the plans of stage 2 asked for (a stale one is asked again)
    }

    internal sealed class StrikeParams
    {
        public double PkThreshold = 0.9, TotToleranceS = 10, TurnLeadS = 2, StandoffNm = 10, EwHoldS = 15, StaleNm = 3, StaleTurnDeg = 30,
                      HoldS = 60, ArriveMarginS = 30, PlanBudgetMs = 20, StaleCheckS = 5;
        public int MaxVariants = 3;
        // phase B: the wait point, the fuel (T-337 rule, miles), the deck, the tick
        public double FuelMarginNm = 5, FuelFloorPct = 0, WaitOrbitNm = 5, WaitArriveNm = 3, LandStepNm = 1, LandMaxShiftNm = 50, TickS = 1, AdoptGapS = 600;
        public string RoeMode = "veto";   // veto | hold
        // phase C: TurnLeadS above is a reserve on top of TurnBaseS + |dpsi| / TurnRateDps (the probes of 2026-10-31; refitted on the live calibration E1: 2.5 s and 11.5 deg/s); the gate, the land check, the retries, the result
        public double TurnBaseS = 2.5, TurnRateDps = 11.5, GateMarginNm = 5, GateMinBackNm = 15, LandCheckNm = 5, IssueRetryS = 5, LaunchConfirmS = 45, ResultDelayS = 20, ArriveNearNm = 0.6, LateSkipS = 90, RunInReserveS = 15;   // ArriveNearNm: a missile that vanished farther than this from the target was shot down on the way (the live check of 2026-10-31: CIWS kills at 1-6 nm, hits at 0-0.2 nm); RunInReserveS: the run in starts this early (live check of 2026-10-31: the orbit exit and the low-level speed cost 11-19 s); TurnRateDps 8: the low-level turn of an A-6E (16 deg/s at 5000 ft)
        public int WarmPerTick = 1;
        // phase D: the jammer (ADR-0132 section 8): behind the launch line, the cone margin, the lead of the switch on, the settling time, the margin of the detection range, the orbit, the altitude (the game's own jam attack: 25 000 ft)
        public double EwBehindNm = 5, EwConeMarginDeg = 2, EwOnLeadS = 60, EwSettleS = 30, EwOnMarginNm = 10, EwOrbitNm = 3, EwAltFt = 25000, EwStationTolNm = 3, EwGuardFrac = 0.5, EwRetryS = 10;
        public double KillThreshold = 0.8, MixShiftMaxS = 120, EwReturnClearS = 60, TofRewarmNm = 10;   // T-342 phase A
        public double DeckLateS = 600;   // T-342 phase B: a deck order counts as failed when the aircraft is not in the air this long after its ready time
        public int AutoWaves = 3;                  // T-342 phase C: the waves (issued stages) the auto mode makes at most
        public double ClearTimeoutS = 900;         // T-342 C: the seconds the stage-2 aircraft wait for the sinking of the neighbour
        public double EwFighterEtaS = 600, FighterStandoffNm = 10;   // T-342 phase D: the jam of the fighters
        public double BookWinS = 35;   // review of phase C: the window of the launch book (the game may create its own task later than 20 s after the order)
    }

    /// <summary>The game as the strike controller sees it (read only in phase A). The game implementation is StrikeGameImpl; the stand has a fake.</summary>
    /// <summary>T-342: an own hit seen by the game layer (a missile on a ship) -> the controller.</summary>
    internal sealed class StrikeHitSeen { public int TrackId, WeaponId; public string Ammo, Warhead = "normal"; public double Power, DamageMult = 1, T, CiwsDmg = -1, DmgFrac = -1; public bool Dud; }

    internal interface IStrikeGame
    {
        double Now { get; }                                   // game time, s
        IList<StrikeAircraft> Aircraft();                     // own aircraft and helicopters (StrikeAirMath classifies, not the game)
        IList<StrikeShip> Ships();                            // surface contacts (class only for Identified ones)
        bool Tof(int uid, StrikeMissile m, double fromLat, double fromLon, double altFt, double kn, StrikeShip target, double aimLat, double aimLon, out double tofS);   // false = no EstimateShot -> rough formula
        string CalibSource { get; }                           // cache | aaw | role: what was read
        // phase B: orders (every method catches inside; null = ok, else an error code) and the reads they need
        string Goto(int uid, IList<GeoPt> pts, double altFt);   // altFt 0 = the cruise altitude of the game
        string Orbit(int uid, GeoPt center, double radiusNm);
        string Rtb(int uid);                                    // to the own deck / airfield, as the return of the CAP
        bool IsLand(GeoPt p);                                   // no terrain data = sea (false)
        IList<StrikeDeckItem> DeckItems();                      // the prepared deck tasks with an anti-ship loadout
        // phase C (every method catches inside)
        string Issue(int uid, int targetId, string ammo, int salvo);   // null = ok, else the code of Attack: not_hostile | out_of_range | neutral_near | no_launcher | unit_not_found | excluded | ...
        string SetRoe(int uid, string roe, out string previous);       // hold | tight | free; previous = the weapon status before (null = unknown)
        string Fate(int trackId, out int hits);                        // sunk | damaged | intact | unknown: only what the player can see
        IList<StrikeMslSeen> InFlight(int uid);                        // the missiles of the aircraft in flight (the last target and the position)
        // phase D (every method catches inside)
        string Jam(int uid, GeoPt pt, bool on);                        // null = ok | no_api | no_station | inoperable | unit_not_found | failed: switch the stations on and give the JamTask by the point / switch them off
        bool? HasAttackTask(int uid, int targetId);                    // null = not readable
        // phase E1
        double Difficulty();                                           // 1 - Globals._missileInterceptChanceReduction; 1 when not readable
        string DiffSrc { get; }                                        // game | default
        int GameId(int uid);                                           // the id of the aircraft in the event log of the game; -1 = not readable
        bool EwCapable { get; }                                        // capability strike_ew
        // T-342 phase A
        double DamageBonus(out bool known);                 // Globals._damageBonus; unknown -> StrikeCalib.BonusFallback
        IList<StrikeHitSeen> NewHits();                     // the own hits added to the ledger since the last call (missiles on ships only); empty when the ledger is off
        string SetSpeed(int uid, double kn);                // the throttle step nearest above kn; remembers the step before; null = ok
        string RestoreSpeed(int uid);                       // the remembered step; "none" = nothing to restore
        bool HitsAvailable { get; }                         // the ledger works
        // T-342 phase B
        IList<StrikeDeckInfo> Decks();                      // own decks with their hangar rows and loadouts (cached 5 s, catches inside)
        string DeckTimingsMode();                           // FlightDeckTimingsMode name
        int TaskVehicleUid(string key);                     // the aircraft a deck task "task:<uid>" has spawned (LaunchTask.Vehicle); 0 = not known
        // T-342 phase C
        // T-342 phase D (every method catches inside). The jam of an object is behind this one interface: if the live check shows a JamTask by an aircraft does not hold, only the game layer changes.
        IList<StrikeFighter> Fighters();                    // hostile air tracks (not helicopters), class / R_t / IsFighter only for identified ones; cached by class
        string JamObject(int uid, int trackId, bool on);    // null = ok | no_api | no_station | inoperable | unit_not_found | no_track | failed:<..>; on = stations on + JamTask by the object of the track; off = cease + IsOn false
        string Destroyed(int trackId);                      // destroyed | alive | gone | unknown (catches inside): the contact is in the table and its object IsDestroyed (or the object is null) -> destroyed; the contact left the table -> gone
    }

    internal static class StrikeTypes
    {
        public static string SelfTest()
        {
            var p = new StrikeParams();
            if (p.PkThreshold != 0.9 || p.TurnLeadS != 2 || p.StandoffNm != 10 || p.MaxVariants != 3 || p.StaleCheckS != 5) return "params defaults";
            if (StrikeCalib.FormulaVersion != 153) return "calib";
            var v = new StrikeVariant();
            if (v.Estimate != StrikeCalib.Estimate || v.Warnings == null || v.Why == null) return "variant defaults";
            var g = new StrikeGroup();
            if (g.Target.Mode != "single" || g.Chosen != -1) return "group defaults";
            var s = new StrikeSam();
            if (s.SpeedMult != 1.4 || s.OutOfAltPen != 0.5) return "sam defaults";
            if (p.FuelMarginNm != 5 || p.WaitOrbitNm != 5 || p.WaitArriveNm != 3 || p.RoeMode != "veto" || p.TickS != 1 || p.FuelFloorPct != 0) return "phase B params";
            if (StrikeCalib.ClearShowFrac != 1.1 || StrikeCalib.TofBaseS != 2 || Math.Abs(StrikeCalib.TofSpeedFrac - 528.0 / 540.0) > 1e-12) return "phase B calib";
            var a = new StrikeAircraft();
            if (a.ReserveNm != -1 || a.FuelFracLive || a.DeckKey != null) return "aircraft defaults";
            if (g.Gather != "idle" || g.DeckKeys == null || g.Left == null || g.WaitPt.HasValue) return "group phase B defaults";
            if (p.TurnBaseS != 2.5 || p.TurnRateDps != 11.5 || p.GateMarginNm != 5 || p.GateMinBackNm != 15 || p.LandCheckNm != 5 || p.IssueRetryS != 5 || p.LaunchConfirmS != 45 || p.ResultDelayS != 20 || p.ArriveNearNm != 0.6 || p.LateSkipS != 90 || p.RunInReserveS != 15 || p.WarmPerTick != 1) return "phase C params";
            if (StrikeCalib.TofCacheNm != 1 || StrikeCalib.TofScaleMin != 0.8 || StrikeCalib.TofScaleMax != 1.25) return "phase C calib";
            if (new StrikeExecRow().State != "to_gate" || a.HeadingDeg != -1 || g.Wave != 1) return "phase C defaults";
            if (p.EwOnLeadS != 60 || p.EwSettleS != 30 || p.EwOnMarginNm != 10 || p.EwAltFt != 25000 || p.EwBehindNm != 5 || p.EwConeMarginDeg != 2 || p.EwOrbitNm != 3 || p.EwStationTolNm != 3 || p.BookWinS != 35) return "phase D params";
            if (new StrikeEwState().State != "wait" || g.EwExec == null || new StrikeEwPlan().Why != "ok" || new StrikeTimeline().TDetect != -1 || !new StrikeEw().Operable) return "phase D defaults";
            if (new StrikeGroup().Goal != "sink" || new StrikeVariant().Goal != "breach" || new StrikeVariant().Red || new StrikeTargetPlan().Goal != "breach" || new StrikeTargetPlan().MSink != -1) return "T-342 defaults";
            if (p.KillThreshold != 0.8 || p.MixShiftMaxS != 120 || p.EwReturnClearS != 60 || p.TofRewarmNm != 10) return "T-342 params";
            if (new StrikeShip().KillBudget != -1 || new StrikeShip().HasCiws || new StrikeMissile().DamageMult != 1 || new StrikeEwState().LastThreatT != -1 || new StrikeExecRow().SpeedSent != null) return "T-342 ship defaults";
            if (p.DeckLateS != 600 || g.DeckOrders == null || g.DeckOrders.Count != 0 || g.DeckBlock != null || g.Deck != null || new StrikeDeckOrder().State != "sent" || new StrikeHangarRow().MaxRangeNm != -1 || new StrikeDeckItem().VehicleUid != 0) return "T-342 B defaults";
            if (p.AutoWaves != 3 || p.ClearTimeoutS != 900 || g.Auto == null || g.Auto.On || g.Auto.State != "off" || g.Clear != null || new StrikeClearState().Stage != "stage1" || new StrikeAssignment().Stage != 0 || new StrikeTargetPlan().PIfAlive != -1 || new StrikeVariant().ClearNeighbour != 0 || new StrikeVariant().TStage2 != -1) return "T-342 C defaults";
            if (p.EwFighterEtaS != 600 || p.FighterStandoffNm != 10 || new StrikeEwPlan().Role != "ships" || new StrikeEwState().Mode != "transit" || new StrikeEwState().FighterId != 0 || new StrikeEwState().JamKind != null || g.FighterThreats == null || g.FighterThreats.Count != 0 || g.EwNotes == null) return "T-342 D defaults";
            return null;
        }
    }
}
