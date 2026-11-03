using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    // Services that the UI modules give each other through UiContext.Provide / Get (T-118, ADR-0044 section 4.4).
    // They replace the delegate fields that used to link the functions on GameApi. IRadarView lives in RadarFeature.cs (Unity types).

    /// <summary>T-190: diagnostics of the radar coverage map layer (owner: the radar module) for ui_state radar.layer.</summary>
    internal interface ICoverageLayerDiag { string DiagJson(); }

    /// <summary>SAR of ships and submarines: the rows of the SAR tab (T-161) and the busy check of the interception (owner: the sar module).</summary>
    internal interface ISarShips
    {
        string ShipCmd(Dictionary<string, object> c);
        bool ShipActive(int uid);
        string Caption(int uid);
        string Busy(int uid);
        /// <summary>T-161: own ships / subs with their rescue state. Reads the game: at most once a second.</summary>
        IList<SarShipRow> Rows();
    }

    /// <summary>The ASW window (owner: the asw module).</summary>
    internal interface IAswWindow
    {
        bool Visible { get; set; }
        void Open();
        bool SelectByTarget(string name);
        /// <summary>T-178 I3: presses "Undo" of the card of "Stop all" / "Disband" (dev command ui_undo); the text of the result, null = nothing to undo.</summary>
        string Undo();
        /// <summary>T-288: opens the window in the "In area" mode with the draft rect filled (8 numbers lat/lon); the player picks the group and presses Start.</summary>
        void OpenAreaDraft(double[] rect);
    }

    /// <summary>The CAP window (owner: the cap module).</summary>
    internal interface ICapWindow
    {
        bool Visible { get; set; }
        void Open();
        /// <summary>T-178 I3: presses "Undo" of the card of "Stop all" / "Disband" (dev command ui_undo); the text of the result, null = nothing to undo.</summary>
        string Undo();
    }

    /// <summary>The Strike window (owner: the strike module, T-341): visibility only (dev command window {name: strike}).</summary>
    internal interface IStrikeWindow
    {
        bool Visible { get; set; }
        void Open();
    }

    /// <summary>The settings window (owner: the settings module, T-144).</summary>
    internal interface ISettingsWindow
    {
        bool Visible { get; set; }
        bool Dirty { get; }
    }

    /// <summary>Map / 3D pick of a target for the smart attack (owner: the smart_attack module).</summary>
    internal interface ISmartPick
    {
        void Begin(int uid);
    }

    /// <summary>Does the smart attack control this unit? (owner: the smart_attack module).</summary>
    internal interface ISmartBusy
    {
        bool Busy(int uid);
    }

    /// <summary>T-138: the intercept entry of the contact context menu (owner: the intercept module). Null item = no entry for this object.</summary>
    internal interface IInterceptMenu
    {
        IcMenu Item(object targetObject, out Action click);
        /// <summary>T-160: the unit runs an intercept; eta "~m:ss" or null.</summary>
        bool UnitActive(int ownUid, out string eta);
        /// <summary>T-160: "Cancel intercept" of the XO menu. null = ok | "not_active".</summary>
        string CancelUnit(int ownUid);
    }

    /// <summary>T-160: the range-ring items of the XO menu and the contact menu (owner: the range_rings module).</summary>
    internal interface IRingMenu
    {
        /// <summary>null = no ring items for this unit (module off, not in a mission, not the player's own unit).</summary>
        RingMenuView View(int uid);
        /// <summary>The sonar rings of one unit: flips the override at the moment of the call (on pins the unit), returns the new state.</summary>
        bool ToggleSonar(int uid);
        /// <summary>T-171 / 2026-10-22: toggles one weapon of the unit (several may be checked at once); "" = clear (unchecks all). Re-checks the
        /// side at the moment of the call. Returns the new state of that weapon (false for "").</summary>
        bool ChooseWeapon(int uid, string key);
        /// <summary>The weapon list for a clicked identified foreign object; null = no item (unidentified, no class record or no weapons, own / allied
        /// unit, module off). ChooseWeapon re-checks at the moment of the click.</summary>
        RingContactItem ContactItem(object targetObject);
    }

    /// <summary>T-168 / ADR-0064: the hits-to-kill estimate of the Overview (owner: the hit_estimate module). The Overview reads it lazily (GameApi.HitEstimate), once per rebuild.</summary>
    internal interface IHitEstimateDetail
    {
        /// <summary>A new Overview pass: the answers of the previous pass are dropped.</summary>
        void NewPass();
        /// <summary>The row of this track has a "+" (an identified foreign track).</summary>
        bool Expandable(int trackId);
        /// <summary>Cells of the columns hits / dmg; null = "—".</summary>
        HeCell Cell(int trackId);
        /// <summary>The block under the expanded row; null = none. Cached until the next NewPass.</summary>
        HeDetail Detail(int trackId);
        /// <summary>The "◂ ▸" control: moves the weapon choice (dir +1 / -1) and drops the cached block. false = nothing to choose.</summary>
        bool Cycle(int trackId, int dir);
    }

    /// <summary>T-171 / 2026-10-22: the weapon submenu of an identified foreign class in the contact menu (ChooseWeapon(key): a key toggles, "" = clear;
    /// re-checks at the moment of the click).</summary>
    internal sealed class RingContactItem { public RingContactWeapons Weapons; public Action<string> ChooseWeapon; }
}
