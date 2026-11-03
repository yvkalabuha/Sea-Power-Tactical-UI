using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-095 / ADR-0036: read-only snapshot the controller hands to the window and the overlay. DTO only, no game references.

    internal sealed class AswRow
    {
        public int Uid;
        public string Name, Type, Base, State /* air|deck_ready|deck_prep|deck_service|player|sar|game_patrol */, Loadout, Group;
        public string Order;                   // T-172: the order of the member's group (course|area|sar|none), null = no group: "rows" reads "2+2" in a course order and "buoys" in an area one
        public double ReadyInS, FuelFrac, MinutesOnStation;
        public double HangarPrepS = -1;        // T-192: hangar rows: preparation time of an order, -1 = not a hangar row
        public double TakeoffInS = -1;         // deck row of a launch task: ready + launch delay; -1 = no launch task
        public int Passive, ActiveBuoys, Vlad, Torpedoes;
        public bool Dipping, Mad;
        public string DipSrc, DipNote;   // T-188
        public bool LoadKnown = true;          // T-154: false = load not known yet (the window does not grey tactics because of it)
        public string Tactic;                  // dip|rows|cross|mad|reserve for a group member, else null
        public string Roles;                   // CSV, may be null
        // T-109 (SAR): people on board / seats ("On board x/y"), the raft the helicopter is flying to
        public int Onboard, Capacity, TargetRaft;
        public double TargetDistNm = -1;
        public bool HasPos;                    // Lat/Lon are valid (aircraft in the air)
        public double Lat, Lon;
        public int DeckUid;                    // T-150: the ship of a deck row (the window centres the map on it)
        public int ShipUid;                    // T-155: the ship of the "Ship" column (the deck's ship, or the home ship of a machine in the air); 0 = unknown, no link
        public string DeckKey;                 // T-181: deck rows: task:|hangar:<type>#k|unit:|cool:
        public int Slot;                       // T-181: k of a hangar slot row, 0 = none
        public int HangarN;                    // T-181: machines of the type in that hangar (Numbers), 0 = not a hangar row
        public string StrikeTask, StrikeArg;   // T-173: the "Task" column of a strike group member: target | idle | deck; the arg is the track / the time
        public string RetreatTo;               // T-180: null = not retreating; the name of the cover ship; "" = a retreat without cover (mode away)
    }

    /// <summary>An aircraft the controller does not use, with the reason (AswMath.ExclusionReason code).</summary>
    internal sealed class AswExcludedRow
    {
        public AswRow Row;
        public string OwnerWindow;             // T-186: "cap" | "asw" when Reason is in_group:<name>, else null
        public string Reason;                  // no_asw_load|aew|player|ext_sar|game_patrol|game_sar_zone|landed|lost; SAR lists: no_capacity|in_group:<name>|...
    }

    internal sealed class AswLineView
    {
        public string Kind;            // rows|cross|dip|mad|area|contact|preview|strike_aim (T-173: shooter -> the drop point); T-176: corridor_row|barrier_line|dip_chain|area_node|area_sector|route|standoff|deferred|lost_ring
        public string Tactic;          // preview only: the tactic the dashed line stands for (rows|cross|dip|mad|area)
        public int Lane = -1;          // T-176: the lane (member index) of a route line, -1 = none
        public string State;           // T-176: plan | done | deferred (null = as before)
        public int BandId = -1;   // T-322: band of a barrier row / drop line (BarrierBand.Id); -1 = untagged; -2 = the ring of a standing target
        public int Seq = -1;      // T-322: BarrierRec.Seq of the row (ring drop lines: their own counter); -1 = none
        public float Alpha = 1f;  // T-322: map fade of a stale barrier (1 = current, 0 = gone); view only
        public string Stale;      // T-322: null = current; order | reset | passed | buoys | track_passed
        public List<GeoPt> Points = new List<GeoPt>();
    }

    internal sealed class AswBuoyView
    {
        public double Lat, Lon, LifeFrac;   // LifeFrac: 1 = fresh, 0 = expired
        public string Ammo;
        public bool Pre;                    // dropped by a member before the order and taken into the group
    }

    internal sealed class AswContactView
    {
        public int Id;
        public string Name;
        public double Lat, Lon, RNm;
        public bool Held;
    }

    /// <summary>T-142 K: a target of the "by course" order: one of our surface ships or a formation (the course of its leader).</summary>
    internal sealed class AswTargetOption
    {
        public int Uid;                        // the ship, or the leader of the formation
        public string Name, Kind = "ship";     // ship | formation
        public bool Selected;                  // selected by the player in the game now
        public double WidthNm;                 // formation: the width across the course (stations + margin); ship: 0
        public bool HasPos;                    // T-178 A2: Lat / Lon are valid (the ship pick lists the distance and rings the ship on the map)
        public double Lat, Lon;
    }

    /// <summary>T-142 K: one vehicle type of a group with the loadout chosen for it ("auto" = the configured list) and the ASW loadouts of its deck.</summary>
    internal sealed class AswLoadoutView
    {
        public string Type, Label, Chosen = "auto";
        public List<string> Names = new List<string>();
    }

    /// <summary>T-176 (ADR-0076): the plan of a by-course / area order for the window summary and link_state.</summary>
    internal sealed class AswPlanView
    {
        public string Tactic, Warn;            // Warn: null | buoys_short | corridor_too_wide | life_short | barrier_rows_short
        public double XNm, StandoffNm;
        public int RowsL, RowsR, RowsMax, NodesTotal, NodesCovered, Deferred;
        public int Lines, LinesLaid;           // T-180: barrier lines in the plan / already laid
        public double GapNm;                   // T-229: the interval of the barrier rows
    }

    internal sealed class AswGroupView
    {
        public AswPlanView Plan = new AswPlanView();                  // T-176
        public AswCourseSpec Course = new AswCourseSpec();            // T-176: a copy of the order of the group (the window draft starts from it)
        public string AreaPattern = "auto";                           // T-176: auto | grid | dip | mad
        // T-142 K: "by course" target and plan, loadouts per type
        public int TargetUid;
        public string TargetKind = "ship";     // ship | formation
        public double AxisDeg = -1;            // the axis the route is laid along now, -1 = none
        public double ReplanInS = -1;          // the target turned more than the limit and waits for the interval: seconds left, -1 = nothing waits
        public List<AswLoadoutView> LoadoutRows = new List<AswLoadoutView>();
        public List<AswTargetOption> CourseTargets = new List<AswTargetOption>();   // the same list for every group (the window body gets only the group)
        public double DeckWaitS = -1;          // T-119 I1: seconds the group's tasks wait for the deck (CAP has priority), -1 = not waiting
        public bool AutoName;                  // T-150: the name is made by the mod and follows the order
        // T-173 (ADR-0074): the strike group
        public int RefUid;                     // the guarded ship (the reference of the bearing in the cards)
        public string RefName, Targets = "hostile_unknown";
        public double StrikeRadiusNm;          // 0 = no limit
        public int Airborne;
        public List<AswStrikeTargetView> StrikeTargets = new List<AswStrikeTargetView>();
        public string Name, Order /* course|area|sar|strike|none */, TargetName, Depth, RSource, Status /* none|searching|contact|gap|no_target|empty */, TargetText;
        public List<string> Tactics = new List<string>();
        public List<int> Members = new List<int>();
        public double RNm, NextBarrierInS = -1, ReliefInS = -1, CoveredFrac = 1;
        public double GapMin = -1;             // hole in the coverage, minutes; -1 = none
        public List<AswRow> MemberRows = new List<AswRow>();   // the members with tactic and state, in group order
        public int BuoysInWater;
        public double[] Rect;                  // 8 numbers lat/lon of the area corners, or null
        public bool Blink;
        public List<AswLineView> Lines = new List<AswLineView>();
        public List<AswLineView> AreaPlan = new List<AswLineView>();   // T-174 W3: the rest of the area plan (Kind "plan"), empty for the other orders
        public List<AswBuoyView> Buoys = new List<AswBuoyView>();
        public List<AswContactView> Contacts = new List<AswContactView>();
        // order "sar" (T-109)
        public string SarArea = "all";         // all|rect|circle
        public string SarOnComplete = "rtb";   // rtb|loiter
        public int SarRafts, SarPeople, SarActive, SarRescued;   // rafts and people in the area, helicopters at work, people picked by the group
        public double SarNextLaunchS = -1;     // seconds to the next launch from the deck, -1 = none
        public double[] SarRect;               // rect: lat0, lon0, lat1, lon1
        public double SarLat, SarLon, SarRadiusNm;   // circle
        public AswRetreatView Retreat;         // T-180: null = the group neither retreats nor has its launches held
        public List<AswRetreatArrow> RetreatArrows = new List<AswRetreatArrow>();
    }

    /// <summary>A raft for the window and the overlay (T-109). State: free|claimed|covered|threat|unreachable.</summary>
    internal sealed class AswRaftView
    {
        public int Id, People;
        public string Type, State;
        public double Lat, Lon;
        public List<int> ClaimedBy = new List<int>();
    }

    /// <summary>T-173: one target of a strike group (the "Contacts" table of the window).</summary>
    internal sealed class AswStrikeTargetView
    {
        public int ContactId, ShooterUid, Second;
        public string Track, Side /* hostile|unknown */, Status /* asw.strike.st.<code> */, StatusArg, Shooter;
        public int Torpedoes, Hits = -1;
        public bool Skipped;
        public double Lat, Lon;
    }

    /// <summary>T-173: the "Submarine detected" card of one track (the toast feed shows it).</summary>
    internal sealed class AswSubAlertView
    {
        public int ContactId, RefUid, Serial;
        public string Track, Side, Class, Source, RefName, Group, Line /* asw.toast.<code> */, Episode /* detected|hold_own_sub|hold_neutral|hold_air|killed|lost|summary */;
        public string[] LineArgs = new string[0];
        public double BrgDeg, DistNm;
    }

    internal sealed class AswView
    {
        public List<AswRow> Aircraft = new List<AswRow>();       // every aircraft the game reports (compat)
        public List<AswRow> Available = new List<AswRow>();      // free ASW-capable aircraft, in the air and on the decks
        public List<AswExcludedRow> Excluded = new List<AswExcludedRow>();   // not usable, with the reason
        public List<AswGroupView> Groups = new List<AswGroupView>();
        public List<AswSubAlertView> SubAlerts = new List<AswSubAlertView>();   // T-173
        public List<AswRow> AvailableStrike = new List<AswRow>();               // free machines that can serve in a strike group
        public List<AswExcludedRow> ExcludedStrike = new List<AswExcludedRow>();
        public List<string> Warnings = new List<string>();
        public AswPatternParams Defaults = new AswPatternParams();            // T-176: a copy of [Asw] for the window defaults
        public double AreaSizeNm = 10;                                         // T-176: [Asw] AreaSizeNm (the default side of a new area)
        public int SelectedShipUid;                                            // T-176: the ship the player selected in the game now, 0 = none
        public List<AswTargetOption> CourseTargets = new List<AswTargetOption>();   // T-142 K

        public int BuoysInWaterTotal;
        public bool RestoreWaiting;            // T-211: the file is read, the groups wait for the world after a load
        public string RestorePrompt;           // non-null in RestoreOnLoad=ask: text of the button
        public double RefLat, RefLon;          // a point near the action: the map projection is fitted around it
        public bool Active;                    // at least one group has an order
        public List<AswLineView> Preview = new List<AswLineView>();   // dry-run route of the order being composed (Kind "preview"), empty when none
        public double PreviewUntilS;           // mission time at which the preview expires, 0 = none
        // T-109 (SAR)
        public List<AswRow> AvailableSar = new List<AswRow>();             // free machines that can serve in a SAR group
        public List<AswExcludedRow> ExcludedSar = new List<AswExcludedRow>();   // with the reason (no_capacity, ext_sar, in_group:<name>, ...)
        public List<AswRaftView> Rafts = new List<AswRaftView>();          // rafts shown while a SAR group or ship is active
        public bool SarIdle;                                               // T-192: no SAR work: the rafts are in the view and ui_state, but not drawn on the map
        public bool RaftsDrawn { get { return !SarIdle && Rafts.Count > 0; } }
        public string ShipsSarJson = "[]";                                 // ships and submarines on a rescue run (SarController.ShipsJson)
        public string RaftScanJson;                                        // 0.27.10: raw counts of the last raft read (SarRaftScan.ToJson), null = never read
        public int RaftsInWater = -1, PeopleInWater;                       // 0.27.10: rescue candidates of that read (-1 = unknown) and the people on them
    }

    /// <summary>T-180: why and how a group retreats from an air threat (the window chip, link_state "air_retreat").</summary>
    internal sealed class AswRetreatView
    {
        public int Members;            // how many members retreat now
        public bool LaunchBlocked;     // launches are held (stage 2 of the group)
        public double Nm = -1;         // distance to the worst threat, nm (-1 = unknown)
        public string Cover;           // the cover ship of the first retreating member, null = none
        public string Why;             // dist | eta | missile | unknown
        public string ResumeWhen;      // "recede" (a helicopter group) | "beyond" (planes)
        public double ResumeNm;        // for "beyond": RetreatNm + ClearMarginNm
    }

    /// <summary>T-180: an arrow on the map from a retreating machine to its cover ship (or the retreat point); Away = no cover.</summary>
    internal sealed class AswRetreatArrow { public int Uid; public double FromLat, FromLon, ToLat, ToLon; public bool Away; }
}
