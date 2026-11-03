using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>T-171: one weapon of the "Weapon" submenu of the rings: Key = the id the module understands, Label = "Harpoon · 67 nm".</summary>
    internal sealed class RingWeaponChoice { public string Key, Label; }

    /// <summary>T-160 / T-171: state of the range-ring items of one unit (filled by the range_rings module). null on UnitView = no ring items.
    /// Weapons = the weapons the unit can fire now; Checked = the checked weapon keys (several at once, ADR-0068 supplement 2026-10-22); Sonar = the
    /// effective sonar-ring check (HasSonar = the unit has a sonar).</summary>
    internal sealed class RingMenuView
    {
        public bool Sonar, HasSonar;
        public List<RingWeaponChoice> Weapons = new List<RingWeaponChoice>();
        public List<string> Checked = new List<string>();
    }

    /// <summary>T-171: the weapons of an identified foreign class for the contact menu; Checked = the checked keys (several at once), empty = none.</summary>
    internal sealed class RingContactWeapons { public List<RingWeaponChoice> Weapons = new List<RingWeaponChoice>(); public List<string> Checked = new List<string>(); }

    /// <summary>Snapshot of one unit for the menu model (T-068). Pure data, no game types.</summary>
    internal sealed class UnitView
    {
        public double Uid;
        public string Name, Kind;                                        // Kind: ship|sub|air|helo (T-099; "group" is not a unit kind: see InGroup)
        public bool InGroup;                                             // the unit stands in a formation: the "whole group" items appear
        public RingMenuView Rings;                                       // T-160: null = no range-ring items
        public bool InterceptActive;                                     // T-160: the unit runs an intercept (ship/sub; filled by the intercept module)
        public string InterceptEta;                                      // "~0:31" or null
        public string FormationRing;                                     // T-243: null | "ready" | "on_station" (FormationMath.RingState)
        public string RtbRing;                                           // T-243: null | "ready" | "returning" | "no_base"
        public RingGroupsView Groups;                                    // T-328 (ADR-0127): air/helo group membership for the "+" / "-" sectors; null = no sectors
        public bool ModHolds;                                            // T-243: diagnostics only (the run decides itself)
        public bool WiredTorpedoes;                                      // a submarine with wire-guided torpedoes aboard (smart attack sector, T-102 R2)
        public Dictionary<string, bool> Flags = new Dictionary<string, bool>();
        public string PostLaunch;                                        // off|evade|retarget|null
        public HashSet<string> Caps = new HashSet<string>();             // unit_flags, radar_overlay, ...
        public bool AllowAttack;
        public Dictionary<string, bool> ModOn = new Dictionary<string, bool>();   // state of the "mod" toggles by flag: radar_coverage, overview
        public bool OverlayOn { get { bool b; return ModOn.TryGetValue("radar_coverage", out b) && b; } set { ModOn["radar_coverage"] = value; } }
    }

    /// <summary>One menu item. Kind: action (combo) | toggle | value (a mode value) | group (submenu / arc).</summary>
    internal sealed class MenuNode
    {
        public string Id, ActionId, Value;
        public string Kind;
        public string Label, Short, SayRu, Reason, Current;
        /// <summary>T-328: arc of the children. ArcSpan = width in degrees (0 = the default 120), ArcPage = cells per page (0 = no paging); Caption / Badge = the text under / beside the glyph of an arc cell.</summary>
        public double ArcSpan;
        public int ArcPage;
        public string Caption, Badge;
        /// <summary>Glyph key from the registry (T-140, ADR-0047); null for UI-only nodes.</summary>
        public string Icon;
        public bool? Checked;
        public bool Enabled = true;
        public List<MenuNode> Children = new List<MenuNode>();
    }

    /// <summary>
    /// Shared model of the "XO" (Старпом) context submenu and the radial ring (T-068, ADR-0028). Pure C#: availability, check marks
    /// and the confirmation rule live here; Menu.cs and Radial.cs only draw. Items that need a confirmation from the menu are never shown.
    /// </summary>
    internal static class MenuModel
    {
        private static string Tx(string key) { return UiText.Get(key); }
        private static string Tx(string key, params object[] a) { return UiText.Fmt(key, a); }
        private static bool Ru { get { return UiText.Lang == "ru"; } }

        /// <summary>Action caption: the language table (menu.action.id), else the label from actions.json (shared with the voice mod).</summary>
        private static string Lbl(ActionDef a)
        {
            string k = "menu.action." + a.Id;
            if (UiText.Has(UiText.DefaultSection, k)) return UiText.Get(k);
            return Ru ? a.LabelRu : a.Label;
        }
        private static string Shrt(ActionDef a)
        {
            string k = "menu.action." + a.Id + ".short";
            if (UiText.Has(UiText.DefaultSection, k)) return UiText.Get(k);
            var s = Ru ? a.ShortRu : a.Short;
            return string.IsNullOrEmpty(s) ? Lbl(a) : s;
        }
        private static string ValLbl(ActionDef a, string v)
        {
            string k = "menu.action." + a.Id + ".value." + v, s;
            if (UiText.Has(UiText.DefaultSection, k)) return UiText.Get(k);
            return (Ru ? a.ValueLabelsRu : a.ValueLabels).TryGetValue(v, out s) ? s : v;
        }
        private static bool Applies(ActionDef a, UnitView u) { return u.Kind != null && Array.IndexOf(a.Applies, u.Kind) >= 0; }
        private static bool Flag(UnitView u, string f) { bool b; return u.Flags.TryGetValue(f, out b) && b; }

        internal static string NoWire { get { return Tx("menu.why.no_wire"); } }

        /// <summary>Setting = a persistent behaviour switch (unit flag / mode). Lives in Standing Orders; without them the "Modes" fallback group of the menu (T-098 F1).</summary>
        public static bool IsSetting(ActionDef a) { return a.Target != "mod" && (a.Kind == "toggle" || a.Kind == "mode"); }

        /// <summary>T-099 placement: null = legacy (menu per `menu`, ring per the layout), "menu" = menu only, "ring" = ring only, "none" = voice only.</summary>
        internal static bool OnMenu(ActionDef a) { return a.Place == null || a.Place == "menu"; }
        internal static bool OnRing(ActionDef a) { return a.Place == null || a.Place == "ring"; }

        /// <summary>Capability an interface-only action needs (the item is hidden without it).</summary>
        internal static string ActionCap(string id) { return id == "sub_smart_attack" ? "smart_attack" : null; }

        internal static string NoCap { get { return Tx("menu.why.no_cap"); } }
        internal static string AttackOff { get { return Tx("menu.why.attack_off"); } }

        /// <summary>Capability a unit flag/mode needs on top of unit_flags (T-091): the flag is useless without it.</summary>
        internal static string FlagCap(string flag)
        {
            switch (flag)
            {
                case "auto_evade": return "auto_evade";
                case "post_launch": return "post_launch";
                case "veto_neutral": return "fire_veto";
                default: return null;
            }
        }

        private static string NoCapWhy(string cap) { return NoCap + ": " + cap; }

        /// <summary>The node of one registry action for a unit, or null (not for this unit, not in the menu, or needs a confirmation).</summary>
        public static MenuNode NodeFor(ActionDef a, UnitView u)
        {
            if (!a.InMenu || !Applies(a, u)) return null;
            if (IsSetting(a) && u.Caps.Contains("standing_orders")) return null;   // lives in the game's Standing Orders window
            string needCap = ActionCap(a.Id);
            if (needCap != null && !u.Caps.Contains(needCap)) return null;
            var n = new MenuNode { Id = a.Id, ActionId = a.Id, Label = Lbl(a), Short = Shrt(a), SayRu = a.SayRu, Icon = a.Icon };
            bool flagsOk = u.Caps.Contains("unit_flags");
            if (a.Kind == "combo")
            {
                if (ActionRegistry.NeedsConfirm(a, null, "menu")) return null;
                n.Kind = "action";
                if ((ActionRegistry.IsAttackValue(a, null) || a.Id == "sub_smart_attack") && !u.AllowAttack) { n.Enabled = false; n.Reason = AttackOff; }
                else if (a.Id == "sub_smart_attack" && !u.WiredTorpedoes) { n.Enabled = false; n.Reason = NoWire; }
            }
            else if (a.Kind == "toggle")
            {
                if (ActionRegistry.NeedsConfirm(a, null, "menu")) return null;
                n.Kind = "toggle";
                bool cap = a.Target == "mod" ? u.Caps.Contains(ActionRegistry.ModCap(a.Flag)) : flagsOk;
                bool modOn = false;
                if (a.Target == "mod") u.ModOn.TryGetValue(a.Flag, out modOn);
                n.Checked = a.Target == "mod" ? modOn : Flag(u, a.Flag);
                if (!cap) { n.Enabled = false; n.Reason = NoCap; }
                else if (a.Target != "mod" && FlagCap(a.Flag) != null && !u.Caps.Contains(FlagCap(a.Flag))) { n.Enabled = false; n.Reason = NoCapWhy(FlagCap(a.Flag)); }
            }
            else
            {
                n.Kind = "group";
                string fcap = FlagCap(a.Flag);
                bool fcapOk = fcap == null || u.Caps.Contains(fcap);
                n.Current = u.PostLaunch == null ? null : ValLbl(a, u.PostLaunch);
                foreach (var v in a.Values)
                {
                    if (ActionRegistry.NeedsConfirm(a, v, "menu")) continue;
                    var c = new MenuNode { Id = a.Id + "=" + v, ActionId = a.Id, Value = v, Kind = "value", Label = ValLbl(a, v), Short = ValLbl(a, v), Checked = u.PostLaunch == v, Enabled = flagsOk };
                    if (!flagsOk) c.Reason = NoCap;
                    else if (!fcapOk) { c.Enabled = false; c.Reason = NoCapWhy(fcap); }
                    else if (ActionRegistry.IsAttackValue(a, v) && !u.AllowAttack) { c.Enabled = false; c.Reason = AttackOff; }
                    n.Children.Add(c);
                }
                if (n.Children.Count == 0) return null;
                if (!flagsOk) { n.Enabled = false; n.Reason = NoCap; }
                else if (!fcapOk) { n.Enabled = false; n.Reason = NoCapWhy(fcap); }
            }
            return n;
        }

        /// <summary>
        /// "XO" submenu (T-099, ADR-0037 section 3): actions placed in the menu, then the interface-only items.
        /// Settings (flags, modes) are not shown when Standing Orders are available (T-098 F1); without them they are collected in "Modes".
        /// </summary>
        public static List<MenuNode> Build(List<ActionDef> defs, UnitView u)
        {
            var res = new List<MenuNode>();
            var modes = new MenuNode { Id = "modes", Kind = "group", Label = Tx("menu.modes"), Short = Tx("menu.modes") };
            foreach (var pass in new[] { "toggle", "mode" })
                foreach (var a in defs)
                {
                    if (a.Kind != pass || !IsSetting(a) || !OnMenu(a)) continue;
                    var n = NodeFor(a, u);
                    if (n != null) modes.Children.Add(n);
                }
            if (!u.Caps.Contains("standing_orders") && u.Kind == "ship" && u.Caps.Contains("air_defense_ready"))
            {
                bool flagsOk = u.Caps.Contains("unit_flags");
                var ad = new MenuNode { Id = "air_defense_ready", ActionId = "air_defense_ready", Kind = "toggle", Label = Tx("menu.ad_ready"), Short = Tx("menu.ad_ready.short"), Checked = Flag(u, "air_defense_ready"), Enabled = flagsOk };
                if (!flagsOk) ad.Reason = NoCap;
                modes.Children.Add(ad);
            }
            if (modes.Children.Count > 0) res.Add(modes);
            foreach (var pass in new[] { "toggle", "combo" })
                foreach (var a in defs)
                {
                    if (a.Kind != pass || IsSetting(a) || !OnMenu(a)) continue;
                    var n = NodeFor(a, u);
                    if (n != null) res.Add(n);
                }
            res.AddRange(UiNodes(u));
            return res;
        }

        /// <summary>Items that are not registry actions (T-160: about the unit): "Cancel intercept", range-ring flags, "Release control of the whole group", SAR of ships (until T-161).</summary>
        public static List<MenuNode> UiNodes(UnitView u)
        {
            var res = new List<MenuNode>();
            if (u.InterceptActive && (u.Kind == "ship" || u.Kind == "sub"))
            {
                string lbl = string.IsNullOrEmpty(u.InterceptEta) ? Tx("menu.intercept_cancel") : Tx("menu.intercept_cancel.eta", u.InterceptEta);
                res.Add(new MenuNode { Id = "intercept_cancel", ActionId = "intercept_cancel", Kind = "action", Label = lbl, Short = lbl });
            }
            var rg = u.Rings;
            if (rg != null)
            {
                var g = new MenuNode { Id = "rings", Kind = "group", Label = Tx("menu.rings"), Short = Tx("menu.rings") };
                if (rg.Weapons != null && rg.Weapons.Count > 0)
                {
                    var wg = new MenuNode { Id = "rings_weapon", Kind = "group", Label = Tx("menu.rings_weapon"), Short = Tx("menu.rings_weapon") };
                    // ADR-0068 supplement 2026-10-22: every weapon is a check box of its own (a click toggles it); "Clear" (only when one is checked) unchecks all
                    bool any = rg.Checked != null && rg.Checked.Count > 0;
                    foreach (var c in rg.Weapons) wg.Children.Add(RingWeaponValue(c.Key, c.Label, any && rg.Checked.Contains(c.Key)));
                    if (any) wg.Children.Add(new MenuNode { Id = "rings_weapon=", ActionId = "rings_weapon", Value = "", Kind = "action", Label = Tx("menu.rings_contact_clear"), Short = Tx("menu.rings_contact_clear") });
                    g.Children.Add(wg);
                }
                if (rg.HasSonar) g.Children.Add(RingToggle("rings_sonar", rg.Sonar));
                if (g.Children.Count > 0) res.Add(g);
            }
            if (u.InGroup)
                res.Add(new MenuNode { Id = "release_control_group", ActionId = "release_control_group", Kind = "action", Label = Tx("menu.release_group"), Short = Tx("menu.release_group.short") });
            return res;
        }

        private static MenuNode RingToggle(string id, bool on)
        {
            return new MenuNode { Id = id, ActionId = id, Kind = "toggle", Label = Tx("menu." + id), Short = Tx("menu." + id), Checked = on };
        }

        private static MenuNode RingWeaponValue(string key, string label, bool on)
        {
            return new MenuNode { Id = "rings_weapon=" + key, ActionId = "rings_weapon", Value = key, Kind = "value", Label = label, Short = label, Checked = on };
        }

        /// <summary>T-171: the "Weapon >" item of the contact menu: one check box per weapon of the class (Value = the key, a click toggles it) + "Clear"
        /// (Value "", unchecks all) only when at least one is checked. null = no weapons.</summary>
        public static MenuNode ContactWeaponNode(RingContactWeapons w)
        {
            if (w == null || w.Weapons == null || w.Weapons.Count == 0) return null;
            var g = new MenuNode { Id = "rings_contact_weapon", Kind = "group", Label = Tx("menu.rings_contact_weapon"), Short = Tx("menu.rings_contact_weapon") };
            foreach (var c in w.Weapons)
                g.Children.Add(new MenuNode { Id = "rings_contact_weapon=" + c.Key, ActionId = "rings_contact_weapon", Value = c.Key, Kind = "value", Label = c.Label, Short = c.Label, Checked = w.Checked != null && w.Checked.Contains(c.Key) });
            if (w.Checked != null && w.Checked.Count > 0)
                g.Children.Add(new MenuNode { Id = "rings_contact_clear", ActionId = "rings_contact_clear", Value = "", Kind = "value", Label = Tx("menu.rings_contact_clear"), Short = Tx("menu.rings_contact_clear"), Checked = false });
            return g;
        }

        private static string GroupLbl(RadialSlot s)
        {
            string k = "radial.group." + s.GroupId;
            if (UiText.Has(UiText.DefaultSection, k)) return UiText.Get(k);
            return Ru ? s.LabelRu : s.Label;
        }

        /// <summary>Most sectors on the ring (T-152).</summary>
        public const int MaxSectors = 12;

        /// <summary>T-100: a ring item that is not in the action registry (actions.json): a UI sector of one unit kind, shown only with its capability.
        /// Side = layout slot (0 = N, clockwise). An action of the registry layout in the same slot wins (T-106 moves this into the JSON).</summary>
        internal sealed class UiRingSlot { public string Kind, Id, Cap, Icon; public int Side; public Func<UnitView, MenuNode> Make; }   // Make (T-243): builds the node itself, null = no sector

        internal static readonly UiRingSlot[] UiRingSlots =
        {
            new UiRingSlot { Kind = "air", Side = 0, Id = "evade_attack", Cap = "air_evasion", Icon = "evade_attack" },
            new UiRingSlot { Kind = "sub", Side = 0, Id = "sub_torpedo_alert", Cap = "sub_evasion", Icon = "torpedo_alert" },   // T-101 / ADR-0071
            // T-243: SW "return to formation" (every unit kind), E "return to base" (aircraft, helicopters)
            new UiRingSlot { Kind = "ship", Side = 5, Id = "formation_return", Cap = "formation_return", Icon = "formation_return", Make = FormationNode },
            new UiRingSlot { Kind = "sub", Side = 5, Id = "formation_return", Cap = "formation_return", Icon = "formation_return", Make = FormationNode },
            new UiRingSlot { Kind = "air", Side = 5, Id = "formation_return", Cap = "formation_return", Icon = "formation_return", Make = FormationNode },
            new UiRingSlot { Kind = "helo", Side = 5, Id = "formation_return", Cap = "formation_return", Icon = "formation_return", Make = FormationNode },
            new UiRingSlot { Kind = "air", Side = 2, Id = "air_rtb", Cap = "return_to_base", Icon = "air_rtb", Make = RtbNode },
            new UiRingSlot { Kind = "helo", Side = 2, Id = "air_rtb", Cap = "return_to_base", Icon = "air_rtb", Make = RtbNode },
            // T-328 (ADR-0127): NE "+" (move / add to a group) and NW "-" (leave the group) of aircraft and helicopters
            new UiRingSlot { Kind = "air", Side = 1, Id = "group_join", Icon = "group_add", Make = GroupJoinNode },
            new UiRingSlot { Kind = "helo", Side = 1, Id = "group_join", Icon = "group_add", Make = GroupJoinNode },
            new UiRingSlot { Kind = "air", Side = 7, Id = "group_leave", Icon = "group_remove", Make = GroupLeaveNode },
            new UiRingSlot { Kind = "helo", Side = 7, Id = "group_leave", Icon = "group_remove", Make = GroupLeaveNode },
        };

        private static MenuNode FormationNode(UnitView u)
        {
            if (u.FormationRing == null) return null;
            var n = new MenuNode { Id = "formation_return", ActionId = "formation_return", Kind = "action", Label = Tx("menu.action.formation_return"), Short = Tx("menu.action.formation_return.short"), Icon = "formation_return" };
            if (u.FormationRing == "on_station") { n.Enabled = false; n.Reason = Tx("menu.why.on_station"); }
            return n;
        }

        private static MenuNode RtbNode(UnitView u)
        {
            if (u.RtbRing == null) return null;
            var n = new MenuNode { Id = "air_rtb", ActionId = "air_rtb", Kind = "action", Label = Tx("menu.action.air_rtb"), Short = Tx("menu.action.air_rtb.short"), Icon = "air_rtb" };
            if (u.RtbRing == "returning") { n.Enabled = false; n.Reason = Tx("menu.why.rtb_already"); }
            else if (u.RtbRing == "no_base") { n.Enabled = false; n.Reason = Tx("menu.why.no_base"); }
            return n;
        }

        private static string KindLabel(string kind) { return Tx("radial.group.kind." + (kind ?? "cap")); }

        /// <summary>T-328: the "+" sector: an arc of the groups of the CAP and ASW windows the aircraft may join (grey = not now, with the reason). null = no sector.</summary>
        private static MenuNode GroupJoinNode(UnitView u)
        {
            var gv = u.Groups;
            if (gv == null || gv.Options.Count == 0) return null;
            bool move = gv.Current != null;
            string lbl = move ? Tx("menu.action.group_join.move", gv.Current.Name) : Tx("menu.action.group_join");
            var n = new MenuNode { Id = "group_join", ActionId = "group_join", Kind = "group", Label = lbl, Short = Tx("menu.action.group_join.short"), Icon = "group_add",
                Current = gv.Admissible.ToString(), ArcPage = RingGroups.PageSize, ArcSpan = RingGroups.ArcSpan(Math.Min(gv.Options.Count, RingGroups.PageSize)) };
            foreach (var o in gv.Options)
            {
                string val = RingGroups.Value(o.Window, o.Name);
                string item = Tx(move ? "radial.group.item" : "radial.group.item.add", o.Name, KindLabel(o.Kind), Tx("radial.group.members", o.Members));
                var c = new MenuNode { Id = "group_join=" + val, ActionId = "group_join", Value = val, Kind = "action", Label = item, Short = o.Name, Caption = o.Name, Badge = o.Members.ToString(),
                    Icon = "grp_" + (o.Kind ?? "cap"), Enabled = o.Why == null };
                if (o.Why != null) { c.Reason = RadialText.GroupWhy(o.Window, o.Why); c.Label = o.Name; }   // "<name>: <reason>" is composed where it is shown
                n.Children.Add(c);
            }
            return n;
        }

        /// <summary>T-328: the "-" sector: leave the current group (Current = its name). null = the aircraft is in no group.</summary>
        private static MenuNode GroupLeaveNode(UnitView u)
        {
            var gv = u.Groups;
            if (gv == null || gv.Current == null) return null;
            string lbl = Tx("menu.action.group_leave", gv.Current.Name);
            return new MenuNode { Id = "group_leave", ActionId = "group_leave", Kind = "action", Label = lbl, Short = Tx("menu.action.group_leave.short"), Icon = "group_remove",
                Value = RingGroups.Value(gv.Current.Window, gv.Current.Name), Current = gv.Current.Name };
        }

        private static MenuNode UiRingNode(UiRingSlot s)
        {
            return new MenuNode { Id = s.Id, ActionId = s.Id, Kind = "action", Label = Tx("menu.action." + s.Id), Short = Tx("menu.action." + s.Id + ".short"), Icon = s.Icon };
        }

        /// <summary>
        /// The ring (T-152): exactly the visible items, no empty sectors; order = layout slot order (N first, clockwise), so item 0 is on top.
        /// Only actions placed on the ring; legacy (place not set) actions missing from the layout go to "More" at the end.
        /// More than MaxSectors items: the 12th and the rest are folded into "More".
        /// </summary>
        public static MenuNode[] Ring(List<ActionDef> defs, Dictionary<string, RadialSlot[]> layout, UnitView u)
        {
            var ring = new MenuNode[8];   // by layout slot, compacted below
            RadialSlot[] slots;
            if (layout == null || u.Kind == null || !layout.TryGetValue(u.Kind, out slots)) slots = new RadialSlot[8];
            var placed = new HashSet<string>();
            Func<string, ActionDef> find = id => defs.FirstOrDefault(d => d.Id == id);
            for (int i = 0; i < 8; i++)
            {
                var s = slots[i];
                if (s == null) continue;
                if (s.ActionId != null)
                {
                    var a = find(s.ActionId);
                    if (a == null) continue;
                    placed.Add(a.Id);
                    ring[i] = OnRing(a) ? NodeFor(a, u) : null;
                }
                else
                {
                    var g = new MenuNode { Id = s.GroupId, Kind = "group", Label = GroupLbl(s), Short = GroupLbl(s) };
                    foreach (var id in s.Items)
                    {
                        var a = find(id);
                        if (a == null) continue;
                        placed.Add(a.Id);
                        var n = OnRing(a) ? NodeFor(a, u) : null;
                        if (n != null) g.Children.Add(n);
                    }
                    ring[i] = g.Children.Count == 0 ? null : g;
                }
            }
            foreach (var us in UiRingSlots)
                if (us.Kind == u.Kind && us.Side >= 0 && us.Side < 8 && ring[us.Side] == null && (slots[us.Side] == null) && (us.Cap == null || u.Caps.Contains(us.Cap)))
                    ring[us.Side] = us.Make != null ? us.Make(u) : UiRingNode(us);
            var more = new MenuNode { Id = "more", Kind = "group", Label = Tx("menu.more"), Short = Tx("menu.more") };
            foreach (var a in defs)
            {
                if (placed.Contains(a.Id) || a.Place != null) continue;   // an explicit place never lands in "More"
                var n = NodeFor(a, u);
                if (n != null) more.Children.Add(n);
            }
            var items = ring.Where(n => n != null).ToList();
            if (more.Children.Count > 0) items.Add(more);
            return Cap(items);
        }

        /// <summary>More than MaxSectors items: the first MaxSectors-1 stay, the rest (and the children of an existing "More") go into one "More" at the end.</summary>
        internal static MenuNode[] Cap(List<MenuNode> items)
        {
            if (items.Count <= MaxSectors) return items.ToArray();
            var oldMore = items.FirstOrDefault(n => n.Id == "more");
            var fold = new MenuNode { Id = "more", Kind = "group", Label = Tx("menu.more"), Short = Tx("menu.more") };
            fold.Children.AddRange(items.Skip(MaxSectors - 1).Where(n => n != oldMore));
            if (oldMore != null) fold.Children.AddRange(oldMore.Children);
            var res = items.Take(MaxSectors - 1).Where(n => n != oldMore).ToList();
            res.Add(fold);
            return res.ToArray();
        }

        /// <summary>One line for the Debug log: "id=on" or "id=off(reason)" for every executable node (a mode lists its values).</summary>
        public static string DescribeForLog(IEnumerable<MenuNode> nodes)
        {
            var parts = new List<string>();
            Describe(nodes, parts);
            return string.Join(", ", parts.ToArray());
        }

        private static void Describe(IEnumerable<MenuNode> nodes, List<string> parts)
        {
            foreach (var n in nodes)
            {
                if (n == null) continue;
                if (n.Kind == "group") { Describe(n.Children, parts); continue; }
                parts.Add(n.Id + "=" + (n.Enabled ? "on" : "off" + (n.Reason != null ? "(" + n.Reason + ")" : "")));
            }
        }

        /// <summary>"id" (combo), "id:toggle", "id=value": what a selection runs, for the Build/Ring parity check.</summary>
        public static IEnumerable<string> Executables(IEnumerable<MenuNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n == null) continue;
                if (n.Kind == "action") yield return string.IsNullOrEmpty(n.Value) ? n.ActionId : n.ActionId + "=" + n.Value;   // T-328: group_join=cap:Name
                else if (n.Kind == "toggle") yield return n.ActionId + ":toggle";
                else if (n.Kind == "value") yield return n.ActionId + "=" + n.Value;
                foreach (var c in Executables(n.Children)) yield return c;
            }
        }

        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }

        private static UnitView View(string kind, bool flags, bool attack)
        {
            var u = new UnitView { Uid = 1, Name = "U", Kind = kind, AllowAttack = attack };
            if (flags) u.Caps.Add("unit_flags");
            u.Caps.Add("auto_evade"); u.Caps.Add("post_launch"); u.Caps.Add("fire_veto");
            return u;
        }

        private static bool Same(IEnumerable<string> a, IEnumerable<string> b)
        {
            return a.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal));
        }

        private static MenuNode FindNode(IEnumerable<MenuNode> nodes, string id)
        {
            foreach (var n in nodes)
            {
                if (n == null) continue;
                if (n.Id == id) return n;
                var c = FindNode(n.Children, id);
                if (c != null) return c;
            }
            return null;
        }

        private static string Sides(MenuNode[] ring) { return string.Concat(ring.Select(n => n == null ? "." : n.Id == "more" ? "M" : "x")); }

        /// <summary>Checks on the embedded registry and layout plus a synthetic registry. null = ok.</summary>
        public static string SelfTest()
        {
            _fail = null; string err;
            using (UiText.Push("ru")) { return SelfTestRu(); }
        }

        private static string SelfTestRu()
        {
            string err;
            var text = ActionRegistry.LoadEmbedded();
            if (text == null) return "embedded registry missing";
            var defs = ActionRegistry.Parse(text, out err);
            if (defs == null) return "registry: " + err;
            var layout = ActionRegistry.ParseRadial(text, defs, out err);
            if (layout == null) return "radial: " + err;
            Eq("layout kinds ship,sub,air,helo", layout.Count == 4 && layout.ContainsKey("ship") && layout.ContainsKey("sub") && layout.ContainsKey("air") && layout.ContainsKey("helo"));
            Eq("every action has an explicit place", defs.All(d => d.Place != null));
            foreach (var gone in new[] { "silent_run", "decoy_salvo", "air_defense_up", "overview", "radar_coverage" })
                Eq(gone + " is voice-only (place none)", defs.First(d => d.Id == gone).Place == "none");

            // ---- ADR-0037 section 3 table, ring side (Standing Orders on or off: the ring does not change)
            var ship = View("ship", true, true); ship.Caps.Add("air_defense_ready");
            var sr = Ring(defs, layout, ship);
            Eq("ship ring: four items, no empty sectors (T-152): " + Sides(sr), Sides(sr) == "xxxx");
            Eq("ship ring: N torpedo_alert, E break_contact, S cease_all, W release_control", sr[0].ActionId == "torpedo_alert" && sr[1].ActionId == "break_contact" && sr[2].ActionId == "cease_all" && sr[3].ActionId == "release_control");

            Eq("ring node carries the registry icon (T-140)", sr[0] != null && sr[0].Icon == "torpedo_alert");
            var uiOnly = UiNodes(View("ship", true, true));
            Eq("UI-only nodes have no icon", uiOnly.All(x => x.Icon == null));

            var sub = View("sub", true, true); sub.Caps.Add("smart_attack"); sub.WiredTorpedoes = true;
            var subr = Ring(defs, layout, sub);
            Eq("sub ring: three items: " + Sides(subr), Sides(subr) == "xxx");
            Eq("sub ring: S cease_all, W release_control, NW smart attack", subr[0].ActionId == "cease_all" && subr[1].ActionId == "release_control" && subr[2].ActionId == "sub_smart_attack" && subr[2].Enabled);
            Eq("sub ring without sub_evasion: no torpedo alert", FindNode(subr, "torpedo_alert") == null && FindNode(subr, "break_contact") == null && FindNode(subr, "sub_torpedo_alert") == null);
            sub.Caps.Add("sub_evasion");
            var subTa = Ring(defs, layout, sub);
            Eq("sub ring with sub_evasion: four items: " + Sides(subTa), Sides(subTa) == "xxxx");
            Eq("sub ring: N sub_torpedo_alert (T-101), icon, label, plain action", subTa[0].ActionId == "sub_torpedo_alert" && subTa[0].Icon == "torpedo_alert" && subTa[0].Label == "Торпедная тревога" && subTa[0].Kind == "action" && subTa[0].Enabled && subTa[0].SayRu == null);
            Eq("sub ring: then S cease_all, W release_control, NW smart attack", subTa[1].ActionId == "cease_all" && subTa[2].ActionId == "release_control" && subTa[3].ActionId == "sub_smart_attack");
            Eq("sub_torpedo_alert is not in the XO menu", FindNode(Build(defs, sub), "sub_torpedo_alert") == null);
            var shipTa = View("ship", true, true); shipTa.Caps.Add("sub_evasion");
            Eq("ship ring ignores the submarine sector", FindNode(Ring(defs, layout, shipTa), "sub_torpedo_alert") == null);
            sub.Caps.Remove("sub_evasion");
            sub.WiredTorpedoes = false;
            var subNoWire = FindNode(Ring(defs, layout, sub), "sub_smart_attack");
            Eq("smart attack greyed without wire torpedoes", subNoWire != null && !subNoWire.Enabled && subNoWire.Reason == NoWire);
            sub.WiredTorpedoes = true; sub.AllowAttack = false;
            var subNoAtk = FindNode(Ring(defs, layout, sub), "sub_smart_attack");
            Eq("smart attack greyed without AllowAttack", subNoAtk != null && !subNoAtk.Enabled && subNoAtk.Reason == AttackOff);
            sub.AllowAttack = true; sub.Caps.Remove("smart_attack");
            Eq("smart attack hidden without the capability: the ring shrinks to 2 sectors", Ring(defs, layout, sub).Length == 2 && FindNode(Ring(defs, layout, sub), "sub_smart_attack") == null);

            var air = View("air", true, true); air.PostLaunch = "evade";
            var ar = Ring(defs, layout, air);
            Eq("air ring without air_evasion: two items: " + Sides(ar), Sides(ar) == "xx");
            Eq("air ring: S cease_all, W release_control", ar[0].ActionId == "cease_all" && ar[1].ActionId == "release_control");
            air.Caps.Add("air_evasion");
            var ae = Ring(defs, layout, air);
            Eq("air ring with air_evasion: three items, N evade_attack (T-100): " + Sides(ae), Sides(ae) == "xxx");
            Eq("air ring: N evade_attack, then S cease_all, W release_control", ae[0].ActionId == "evade_attack" && ae[1].ActionId == "cease_all" && ae[2].ActionId == "release_control");
            Eq("evade_attack node: icon, label, plain action", ae[0].Icon == "evade_attack" && ae[0].Label == "Уклонение от атаки" && ae[0].Kind == "action" && ae[0].Enabled && ae[0].SayRu == null);
            Eq("evade_attack is not in the XO menu", FindNode(Build(defs, air), "evade_attack") == null);
            var heloAe = View("helo", true, true); heloAe.Caps.Add("air_evasion");
            Eq("helo ring ignores the air sector: " + Sides(Ring(defs, layout, heloAe)), Sides(Ring(defs, layout, heloAe)) == "xx");
            air.Caps.Remove("air_evasion");
            var helo = View("helo", true, true);
            var hr = Ring(defs, layout, helo);
            Eq("helo ring: two items: " + Sides(hr), Sides(hr) == "xx");

            // ---- T-243: SW "return to formation", E "return to base"
            var fs = View("ship", true, true); fs.Caps.Add("formation_return");
            Eq("formation sector hidden without FormationRing: " + Sides(Ring(defs, layout, fs)), FindNode(Ring(defs, layout, fs), "formation_return") == null && Sides(Ring(defs, layout, fs)) == "xxxx");
            fs.FormationRing = "ready";
            var fr = Ring(defs, layout, fs);
            var fn = FindNode(fr, "formation_return");
            Eq("formation sector ready: five items, enabled, icon, label: " + Sides(fr), fn != null && fn.Enabled && fn.Icon == "formation_return" && fn.Label == "Вернуться в строй" && Sides(fr) == "xxxxx");
            Eq("formation sector sits after S cease_all and before W release_control (SW)", Array.IndexOf(fr, fn) == Array.FindIndex(fr, x => x.ActionId == "cease_all") + 1);
            fs.FormationRing = "on_station";
            fn = FindNode(Ring(defs, layout, fs), "formation_return");
            Eq("formation sector on station: grey with the reason", fn != null && !fn.Enabled && fn.Reason == "уже на месте");
            fs.FormationRing = "ready"; fs.Caps.Remove("formation_return");
            Eq("formation sector needs the capability", FindNode(Ring(defs, layout, fs), "formation_return") == null);
            foreach (var kind in new[] { "sub", "air", "helo" })
            {
                var kv = View(kind, true, true); kv.Caps.Add("formation_return"); kv.FormationRing = "ready";
                Eq(kind + " ring has the formation sector", FindNode(Ring(defs, layout, kv), "formation_return") != null);
                Eq(kind + " formation sector is not in the XO menu", FindNode(Build(defs, kv), "formation_return") == null);
            }
            foreach (var kind in new[] { "air", "helo" })
            {
                var rtbv = View(kind, true, true); rtbv.Caps.Add("return_to_base");
                Eq(kind + " rtb hidden without RtbRing", FindNode(Ring(defs, layout, rtbv), "air_rtb") == null);
                rtbv.RtbRing = "ready";
                var rn = FindNode(Ring(defs, layout, rtbv), "air_rtb");
                Eq(kind + " rtb ready: enabled, label", rn != null && rn.Enabled && rn.Label == "Вернуться на базу" && rn.Icon == "air_rtb");
                rtbv.RtbRing = "returning"; rn = FindNode(Ring(defs, layout, rtbv), "air_rtb");
                Eq(kind + " rtb returning: grey", rn != null && !rn.Enabled && rn.Reason == "уже возвращается");
                rtbv.RtbRing = "no_base"; rn = FindNode(Ring(defs, layout, rtbv), "air_rtb");
                Eq(kind + " rtb without base: grey", rn != null && !rn.Enabled && rn.Reason == "нет базы");
                rtbv.Caps.Remove("return_to_base");
                Eq(kind + " rtb needs the capability", FindNode(Ring(defs, layout, rtbv), "air_rtb") == null);
            }
            var shipRtb = View("ship", true, true); shipRtb.Caps.Add("return_to_base"); shipRtb.RtbRing = "ready";
            Eq("ship ring has no return-to-base sector", FindNode(Ring(defs, layout, shipRtb), "air_rtb") == null);

            // ---- menu side, Standing Orders ON: no settings anywhere
            foreach (var k in new[] { "ship", "sub", "air", "helo" })
            {
                var so = View(k, true, true); so.Caps.Add("standing_orders"); so.Caps.Add("air_defense_ready"); so.Caps.Add("smart_attack"); so.WiredTorpedoes = true;
                var sob = Build(defs, so);
                var sor = Ring(defs, layout, so);
                string all = DescribeForLog(sob) + "|" + DescribeForLog(sor);
                Eq(k + ": no setting in the menu or the ring with standing_orders (" + all + ")", !all.Contains("auto_evade") && !all.Contains("veto_neutral") && !all.Contains("post_launch") && !all.Contains("air_defense_ready"));
                Eq(k + ": no Modes group with standing_orders", FindNode(sob, "modes") == null && FindNode(sor, "modes") == null);
                Eq(k + ": menu empty with standing_orders and no windows", sob.Count == 0);
            }
            Eq("IsSetting: auto_evade yes, overview no, combo no", IsSetting(defs.First(d => d.Id == "auto_evade")) && !IsSetting(defs.First(d => d.Id == "overview")) && !IsSetting(defs.First(d => d.Id == "cease_all")));

            // ---- menu side, fallback (no standing_orders): the Modes group
            var sb = Build(defs, ship);
            var modes = FindNode(sb, "modes");
            Eq("ship fallback: Build is only the Modes group", sb.Count == 1 && modes != null);
            Eq("ship Modes: auto_evade, veto_neutral, air_defense_ready", modes != null && modes.Children.Select(c => c.Id).SequenceEqual(new[] { "auto_evade", "veto_neutral", "air_defense_ready" }));
            Eq("ship air_defense_ready is a toggle node, off", modes != null && modes.Children[2].Kind == "toggle" && modes.Children[2].Checked == false && modes.Children[2].Enabled);
            ship.Flags["air_defense_ready"] = true;
            Eq("air_defense_ready follows the flag", FindNode(Build(defs, ship), "air_defense_ready").Checked == true);
            var ab = Build(defs, air);
            var amodes = FindNode(ab, "modes");
            Eq("air Modes: veto_neutral, post_launch", amodes != null && amodes.Children.Select(c => c.Id).SequenceEqual(new[] { "veto_neutral", "post_launch" }));
            var apl = FindNode(ab, "post_launch");
            Eq("air post_launch: 3 values, current checked", apl != null && apl.Kind == "group" && apl.Children.Count == 3 && apl.Children.Count(c => c.Checked == true) == 1 && apl.Children.First(c => c.Checked == true).Value == "evade");
            Eq("air post_launch current label", apl != null && apl.Current == "Отворот");
            Eq("retarget has no confirmation item", apl != null && apl.Children.Any(c => c.Value == "retarget" && c.Enabled && c.Reason == null));
            var hm = FindNode(Build(defs, helo), "modes");
            Eq("helo Modes: veto_neutral only", hm != null && hm.Children.Select(c => c.Id).SequenceEqual(new[] { "veto_neutral" }));
            var sm = FindNode(Build(defs, sub), "modes");
            Eq("sub Modes: veto_neutral only (one place: Standing Orders, ADR-0071)", sm != null && sm.Children.Select(c => c.Id).SequenceEqual(new[] { "veto_neutral" }));
            var noAtk = View("air", true, false); noAtk.PostLaunch = "off";
            var rt = FindNode(Build(defs, noAtk), "post_launch").Children.FirstOrDefault(c => c.Value == "retarget");
            Eq("retarget greyed with a reason without AllowAttack", rt != null && !rt.Enabled && rt.Reason == AttackOff);
            Eq("other values stay enabled without AllowAttack", FindNode(Build(defs, noAtk), "post_launch").Children.Where(c => c.Value != "retarget").All(c => c.Enabled));
            var noCap = Build(defs, View("ship", false, true));
            Eq("toggles grey without unit_flags", FindNode(noCap, "modes").Children.All(c => !c.Enabled && c.Reason == NoCap));
            Eq("ring combos stay enabled without unit_flags", Ring(defs, layout, View("ship", false, true))[0].Enabled);
            // T-091: unit flags and modes need their own capability on top of unit_flags
            var ncap = View("ship", true, true); ncap.Caps.Remove("auto_evade"); ncap.Caps.Remove("fire_veto");
            var nb = Build(defs, ncap);
            var nae = FindNode(nb, "auto_evade");
            var nvn = FindNode(nb, "veto_neutral");
            Eq("auto_evade toggle greyed without its capability", nae != null && !nae.Enabled && nae.Reason != null && nae.Reason.Contains("auto_evade"));
            Eq("veto_neutral toggle greyed without fire_veto", nvn != null && !nvn.Enabled && nvn.Reason != null && nvn.Reason.Contains("fire_veto"));
            Eq("toggles active with their capabilities", FindNode(sb, "auto_evade").Enabled && FindNode(sb, "veto_neutral").Enabled);
            var npl = View("air", true, true); npl.Caps.Remove("post_launch");
            var npn = FindNode(Build(defs, npl), "post_launch");
            Eq("post_launch values greyed without the post_launch capability", npn != null && !npn.Enabled && npn.Children.Count > 0 && npn.Children.All(c => !c.Enabled && c.Reason != null && c.Reason.Contains("post_launch")));
            var desc = DescribeForLog(nb);
            Eq("DescribeForLog lists off(reason)", desc.Contains("auto_evade=off(" + NoCap + ": auto_evade)"));
            Eq("DescribeForLog lists on", DescribeForLog(sr).Contains("torpedo_alert=on"));

            // ---- group and unit items (T-160)
            var gv = View("ship", true, true); gv.Caps.Add("standing_orders"); gv.Caps.Add("asw_menu"); gv.InGroup = true;
            var gb = Build(defs, gv);
            Eq("group ship: only release group (no ASW even with asw_menu)", gb.Select(n => n.Id).SequenceEqual(new[] { "release_control_group" }));
            Eq("group label", gb[0].Label == "Вернуть управление всей группе");
            var ga = View("air", true, true); ga.Caps.Add("standing_orders"); ga.InGroup = true;
            Eq("group air: only release group", Build(defs, ga).Select(n => n.Id).SequenceEqual(new[] { "release_control_group" }));
            var lone = View("ship", true, true); lone.Caps.Add("standing_orders"); lone.Caps.Add("asw_menu");
            Eq("single ship: empty even with asw_menu", Build(defs, lone).Count == 0);
            Eq("single ship without caps: nothing", Build(defs, View("ship", true, true)).All(n => n.Id == "modes"));
            var rv = View("ship", true, true); rv.Caps.Add("standing_orders"); rv.Rings = new RingMenuView { HasSonar = true };
            var rb = Build(defs, rv);
            Eq("ring items: one group", rb.Select(n => n.Id).SequenceEqual(new[] { "rings" }) && rb[0].Kind == "group" && rb[0].Label == "Круги");
            var rk = rb[0].Children;
            Eq("ring group without weapons: only the sonar rings", rk.Select(n => n.Id).SequenceEqual(new[] { "rings_sonar" }) && rk[0].Kind == "toggle" && rk[0].Checked == false && rk[0].Label == "Кольца ГАС");
            Eq("no weapons: no weapon submenu", FindNode(rb, "rings_weapon") == null);
            rv.Rings.Sonar = true;
            rv.Rings.Weapons.Add(new RingWeaponChoice { Key = "harpoon", Label = "Harpoon · 67 миль" });
            rv.Rings.Weapons.Add(new RingWeaponChoice { Key = "sm2", Label = "Standard · 90 миль" });
            var rb2 = Build(defs, rv);
            Eq("sonar item follows the state", FindNode(rb2, "rings_sonar").Checked == true);
            var wnode = FindNode(rb2, "rings_weapon");
            Eq("weapon submenu: one check box per weapon, no Clear without a check", wnode != null && wnode.Kind == "group" && wnode.Label == "Оружие" && wnode.Children.Count == 2
                && wnode.Children.All(c => c.Kind == "value" && c.Checked == false) && wnode.Children[0].Value == "harpoon" && wnode.Children[1].Label == "Standard · 90 миль");
            Eq("weapon submenu before the sonar rings; no category items", FindNode(rb2, "rings").Children.Select(n => n.Id).SequenceEqual(new[] { "rings_weapon", "rings_sonar" }));
            rv.Rings.Checked.Add("sm2");
            var wn2 = FindNode(Build(defs, rv), "rings_weapon");
            Eq("checked weapon is checked, Clear appears last", wn2.Children.Count == 3 && wn2.Children[1].Checked == true && wn2.Children[0].Checked == false
                && wn2.Children[2].Kind == "action" && wn2.Children[2].Value == "" && wn2.Children[2].Label == "Снять");
            rv.Rings.Checked.Add("harpoon");
            var wn3 = FindNode(Build(defs, rv), "rings_weapon");
            Eq("several weapons checked at once (2026-10-22)", wn3.Children[0].Checked == true && wn3.Children[1].Checked == true);
            Eq("ring values run as rings_weapon=key", Executables(Build(defs, rv)).Contains("rings_weapon=sm2") && Executables(Build(defs, rv)).Contains("rings_weapon") && Executables(Build(defs, rv)).Contains("rings_sonar:toggle"));
            rv.Rings.HasSonar = false;
            Eq("no sonar: no sonar item", FindNode(Build(defs, rv), "rings_sonar") == null && FindNode(Build(defs, rv), "rings_weapon") != null);
            rv.Rings.Weapons.Clear();
            Eq("nothing to show: the empty group is not added", Build(defs, rv).All(n => n.Id != "rings"));
            rv.Rings = null;
            Eq("Rings null: no ring items", Build(defs, rv).All(n => !n.Id.StartsWith("rings")));
            // the contact menu item of a foreign class (T-171)
            Eq("contact weapons: none -> null", ContactWeaponNode(null) == null && ContactWeaponNode(new RingContactWeapons()) == null);
            var cw = new RingContactWeapons();
            cw.Weapons.Add(new RingWeaponChoice { Key = "h", Label = "Harpoon · до 67 миль" }); cw.Weapons.Add(new RingWeaponChoice { Key = "s", Label = "Standard · до 90 миль" });
            var cn = ContactWeaponNode(cw);
            Eq("contact weapons: values, no Clear without a choice", cn != null && cn.Kind == "group" && cn.Label == "Оружие" && cn.Children.Count == 2 && cn.Children.All(c => c.Kind == "value" && c.Checked == false) && cn.Children[0].Value == "h");
            cw.Checked.Add("s");
            var cn2 = ContactWeaponNode(cw);
            Eq("contact weapons: the chosen one is checked, Clear appears", cn2.Children.Count == 3 && cn2.Children[1].Checked == true && cn2.Children[0].Checked == false && cn2.Children[2].ActionId == "rings_contact_clear" && cn2.Children[2].Value == "" && cn2.Children[2].Label == "Снять");
            cw.Checked.Add("h");
            var cn3 = ContactWeaponNode(cw);
            Eq("contact weapons: several checked at once, one Clear", cn3.Children.Count == 3 && cn3.Children[0].Checked == true && cn3.Children[1].Checked == true);
            var iv = View("ship", true, true); iv.Caps.Add("standing_orders"); iv.InterceptActive = true;
            var ib = Build(defs, iv);
            Eq("intercept cancel: first, action, enabled, plain label", ib.Count == 1 && ib[0].Id == "intercept_cancel" && ib[0].Kind == "action" && ib[0].Enabled && ib[0].Label == "Отменить перехват");
            iv.InterceptEta = "~0:31";
            Eq("intercept cancel with ETA", Build(defs, iv)[0].Label == "Отменить перехват · ~0:31");
            iv.InterceptActive = false;
            Eq("no intercept: no cancel item", Build(defs, iv).Count == 0);
            var air2 = View("air", true, true); air2.Caps.Add("standing_orders"); air2.InterceptActive = true;
            Eq("air: no intercept cancel", Build(defs, air2).Count == 0);
            var fv = View("ship", true, true); fv.InGroup = true; fv.InterceptActive = true; fv.InterceptEta = "~0:31";
            fv.Rings = new RingMenuView { HasSonar = true };
            Eq("full order without Standing Orders", Build(defs, fv).Select(n => n.Id).Where(x => x != "torpedo_alert" && x != "break_contact" && x != "cease_all" && x != "release_control" && x != "air_defense_ready" && x != "sub_smart_attack" && x != "smart_attack").SequenceEqual(new[] { "modes", "intercept_cancel", "rings", "release_control_group" }));
            // ---- T-161: the rescue of ships and subs lives in the SAR tab of the Aviation window; the XO menu has no sar_* item
            foreach (var k in new[] { "ship", "sub", "helo", "air" })
            {
                var sv = View(k, true, true); sv.Caps.Add("sar_ship");
                Eq(k + ": no sar_* item in the XO menu", !Executables(Build(defs, sv)).Any(x => x.StartsWith("sar_", StringComparison.Ordinal)) && FindNode(Build(defs, sv), "sar_ship") == null);
            }
            Func<UnitView> rwEn = () => { var x = View("ship", true, true); x.Caps.Add("standing_orders"); x.Rings = new RingMenuView(); x.Rings.Weapons.Add(new RingWeaponChoice { Key = "k", Label = "K" }); return x; };
            using (UiText.Push("en"))
            {
                Eq("english group labels", Build(defs, gv)[0].Label == "Release control of the whole group");
                Eq("english unit items", FindNode(Build(defs, fv), "rings").Label == "Rings" && FindNode(Build(defs, fv), "rings_sonar").Label == "Sonar rings" && FindNode(Build(defs, fv), "intercept_cancel").Label == "Cancel intercept · ~0:31");
                var encw = new RingContactWeapons(); encw.Checked.Add("x"); encw.Weapons.Add(new RingWeaponChoice { Key = "x", Label = "X" });
                Eq("english contact weapon labels", ContactWeaponNode(encw).Label == "Weapon" && ContactWeaponNode(encw).Children[1].Label == "Clear");
                Eq("english weapon submenu", FindNode(Build(defs, rwEn()), "rings_weapon").Label == "Weapon" && FindNode(Build(defs, rwEn()), "rings_weapon").Children[0].Label == "K");
                Eq("english reasons", NoCap == "mod capability missing" && AttackOff == "Attack is forbidden in the mod config");
            }

            // ---- the rule: an action stands in the ring OR the menu OR nowhere, per unit type
            foreach (var k in new[] { "ship", "sub", "air", "helo" })
            {
                foreach (var so in new[] { false, true })
                {
                    var uv = View(k, true, true); uv.Caps.Add("air_defense_ready"); uv.Caps.Add("smart_attack"); uv.Caps.Add("asw_menu"); uv.WiredTorpedoes = true; uv.InGroup = true; uv.Rings = new RingMenuView { HasSonar = true }; uv.InterceptActive = true;
                    if (so) uv.Caps.Add("standing_orders");
                    var menuIds = Executables(Build(defs, uv)).ToList();
                    var ringIds = Executables(Ring(defs, layout, uv)).ToList();
                    Eq(k + (so ? " (so)" : "") + ": no action in both the menu and the ring", !menuIds.Select(x => x.Split(':', '=')[0]).Intersect(ringIds.Select(x => x.Split(':', '=')[0])).Any());
                    Eq(k + ": no asw_* in the menu", !menuIds.Any(x => x.StartsWith("asw_")));
                    Eq(k + ": no duplicates in the ring", ringIds.Count == ringIds.Distinct().Count());
                    Eq(k + ": no voice-only action is shown", !menuIds.Concat(ringIds).Any(x => x.StartsWith("silent_run") || x.StartsWith("decoy_salvo") || x.StartsWith("air_defense_up") || x.StartsWith("overview") || x.StartsWith("radar_coverage")));
                }
            }
            // ---- T-328: "+" (NE) and "-" (NW) sectors of aircraft and helicopters
            {
                Func<string, int, bool, UnitView> mkv = (kind, extra, inGroup) =>
                {
                    var uv = View(kind, true, true); uv.Caps.Add("air_evasion"); uv.Caps.Add("formation_return"); uv.Caps.Add("return_to_base");
                    uv.FormationRing = "ready"; uv.RtbRing = "ready";
                    var g = new RingGroupsView();
                    if (inGroup) g.Current = new RingGroupOption { Window = "cap", Name = "ПВО-1", Kind = "cap", Members = 3 };
                    g.Options.Add(new RingGroupOption { Window = "cap", Name = "ПВО-2", Kind = "cap", Members = 2, Why = "strike_loadout" });
                    g.Options.Add(new RingGroupOption { Window = "asw", Name = "ПЛО-1", Kind = "asw", Members = 4 });
                    for (int q = 0; q < extra; q++) g.Options.Add(new RingGroupOption { Window = "asw", Name = "Е" + q, Kind = "sar", Members = q });
                    foreach (var o in g.Options) if (o.Why == null) g.Admissible++;
                    uv.Groups = g;
                    return uv;
                };
                var noG = View("air", true, true); noG.Caps.Add("air_evasion"); noG.Caps.Add("formation_return"); noG.Caps.Add("return_to_base"); noG.FormationRing = "ready"; noG.RtbRing = "ready";
                Eq("T-328: air without Groups has no group sectors: " + Sides(Ring(defs, layout, noG)), FindNode(Ring(defs, layout, noG), "group_join") == null && FindNode(Ring(defs, layout, noG), "group_leave") == null && Ring(defs, layout, noG).Length == 5);
                var gr = Ring(defs, layout, mkv("air", 0, true));
                Eq("T-328: air in a group: 7 sectors (" + string.Join(",", gr.Select(n => n.Id).ToArray()) + ")", gr.Length == 7);
                Eq("T-328: order N, NE '+', E rtb, ..., NW '-' last", gr.Length == 7 && gr[0].Id == "evade_attack" && gr[1].Id == "group_join" && gr[2].Id == "air_rtb" && gr[gr.Length - 1].Id == "group_leave");
                var lv = FindNode(gr, "group_leave");
                Eq("T-328: leave node", lv != null && lv.Current == "ПВО-1" && lv.Value == "cap:ПВО-1" && lv.Icon == "group_remove" && lv.Kind == "action" && lv.Label == "Убрать из группы ПВО-1");
                var jn = FindNode(gr, "group_join");
                Eq("T-328: join node", jn != null && jn.Kind == "group" && jn.Icon == "group_add" && jn.ArcPage == 6 && jn.ArcSpan == RingGroups.ArcSpan(2) && jn.Current == "1" && jn.Children.Count == 2 && jn.Label == "Перевести в группу… (из ПВО-1)");
                var c1 = jn == null ? null : jn.Children.FirstOrDefault(c => c.Value == "asw:ПЛО-1");
                Eq("T-328: join child", c1 != null && c1.Kind == "action" && c1.ActionId == "group_join" && c1.Icon == "grp_asw" && c1.Caption == "ПЛО-1" && c1.Badge == "4" && c1.Enabled && c1.Label == "Перевести в ПЛО-1 (ПЛО · 4 борт.)");
                var c2 = jn == null ? null : jn.Children.FirstOrDefault(c => c.Value == "cap:ПВО-2");
                Eq("T-328: grey child with a reason", c2 != null && !c2.Enabled && c2.Reason == "ударная загрузка" && c2.Icon == "grp_cap" && c2.Label == "ПВО-2");
                var free = Ring(defs, layout, mkv("air", 0, false));
                var jf = FindNode(free, "group_join");
                Eq("T-328: not in a group: no '-', '+' says Add", FindNode(free, "group_leave") == null && jf != null && jf.Label == "Добавить в группу…" && free.Length == 6 && jf.Children[1].Label.StartsWith("Добавить в ПЛО-1"));
                Eq("T-328: helo has the sectors, 6 with no evade", FindNode(Ring(defs, layout, mkv("helo", 0, true)), "group_join") != null && Ring(defs, layout, mkv("helo", 0, true)).Length == 6);
                foreach (var k in new[] { "ship", "sub" })
                { var sv = mkv(k, 0, true); Eq("T-328: " + k + " gets no group sectors", FindNode(Ring(defs, layout, sv), "group_join") == null && FindNode(Ring(defs, layout, sv), "group_leave") == null); }
                Eq("T-328: XO menu has no group items", !Executables(Build(defs, mkv("air", 0, true))).Any(x => x.StartsWith("group_")));
                var many = FindNode(Ring(defs, layout, mkv("air", 7, true)), "group_join");
                Eq("T-328: many groups: 9 children, page 6, span 192", many != null && many.Children.Count == 9 && many.ArcSpan == 192 && many.ArcPage == 6);
                var rid = Executables(gr).ToList();
                Eq("T-328: no duplicates in the ring with groups", rid.Count == rid.Distinct().Count() && rid.Contains("group_join=asw:ПЛО-1") && rid.Contains("group_leave=cap:ПВО-1"));
                var none = mkv("air", 0, false); none.Groups.Options.Clear();
                Eq("T-328: no options and no group: no sectors", FindNode(Ring(defs, layout, none), "group_join") == null && FindNode(Ring(defs, layout, none), "group_leave") == null);
                using (UiText.Push("en"))
                {
                    var eg = Ring(defs, layout, mkv("air", 0, true));
                    Eq("T-328: en labels", FindNode(eg, "group_leave").Label == "Remove from group ПВО-1" && FindNode(eg, "group_join").Label == "Move to group… (from ПВО-1)" && FindNode(eg, "group_join").Short == "To group");
                }
            }
            using (UiText.Push("en"))
            {
                var en = Ring(defs, layout, ship);
                Eq("english labels", en[0] != null && en[0].Label == "Torpedo alert" && en[0].Short == "Torpedo alert" && FindNode(Build(defs, ship), "modes").Label == "Modes");
                var enAir = FindNode(Build(defs, air), "post_launch");
                Eq("english mode value labels", enAir != null && enAir.Current == "Break away");
            }
            // T-152: ring size cap
            {
                Func<int, List<MenuNode>> mk = k => Enumerable.Range(0, k).Select(q => new MenuNode { Id = "p" + q, Kind = "action", Short = "P" }).ToList();
                Eq("cap: 12 items stay", Cap(mk(12)).Length == 12 && Cap(mk(12)).All(n => n.Id != "more"));
                var c15 = Cap(mk(15));
                Eq("cap: 15 items -> 12 sectors, More is last and holds the 4 folded", c15.Length == 12 && c15[11].Id == "more" && c15[11].Children.Count == 4 && c15[0].Id == "p0" && c15[10].Id == "p10");
                var l = mk(14); var mo = new MenuNode { Id = "more", Kind = "group", Short = "M" }; mo.Children.Add(new MenuNode { Id = "x1", Kind = "action" }); l.Add(mo);
                var c15m = Cap(l);
                Eq("cap: an existing More is merged, not duplicated", c15m.Count(n => n.Id == "more") == 1 && c15m.Length == 12 && c15m[11].Children.Count == 4);
                Eq("ring: no nulls", sr.All(n => n != null) && Ring(defs, layout, View("sub", true, true)).All(n => n != null));
            }
            Eq("short labels", sr[0] != null && sr[0].Short == "Торп. тревога");

            // synthetic registry: menu_confirm_values hides a value, menu:false hides an action, unplaced -> More, overlay by capability
            const string Json2 = "{\"actions\":["
                + "{\"id\":\"c1\",\"kind\":\"combo\",\"applies\":[\"ship\"],\"label_ru\":\"К1\",\"label\":\"C1\",\"steps\":[{\"cmd\":\"cease_fire\"}]},"
                + "{\"id\":\"c2\",\"kind\":\"combo\",\"applies\":[\"ship\"],\"label_ru\":\"К2\",\"label\":\"C2\",\"steps\":[{\"cmd\":\"cease_fire\"}]},"
                + "{\"id\":\"c3\",\"kind\":\"combo\",\"applies\":[\"ship\"],\"menu\":false,\"label_ru\":\"К3\",\"label\":\"C3\",\"steps\":[{\"cmd\":\"cease_fire\"}]},"
                + "{\"id\":\"c4\",\"kind\":\"combo\",\"applies\":[\"ship\"],\"confirm\":true,\"label_ru\":\"К4\",\"label\":\"C4\",\"steps\":[{\"cmd\":\"cease_fire\"}]},"
                + "{\"id\":\"ov\",\"kind\":\"toggle\",\"target\":\"mod\",\"flag\":\"radar_coverage\",\"applies\":[\"ship\"],\"label_ru\":\"О\",\"label\":\"O\"},"
                + "{\"id\":\"m1\",\"kind\":\"mode\",\"flag\":\"post_launch\",\"applies\":[\"ship\"],\"values\":[\"off\",\"evade\",\"retarget\"],\"confirm_values\":[\"retarget\"],\"menu_confirm_values\":[\"evade\"],\"label_ru\":\"М\",\"label\":\"M\"}],"
                + "\"radial\":{\"ship\":{\"N\":\"c1\"}}}";
            var sd = ActionRegistry.Parse(Json2, out err);
            Eq("synthetic registry parses: " + err, sd != null);
            if (sd != null)
            {
                var sl = ActionRegistry.ParseRadial(Json2, sd, out err);
                var su = View("ship", true, true); su.Caps.Add("radar_overlay"); su.OverlayOn = true;
                var sbuild = Build(sd, su);
                var ids = sbuild.Select(n => n.Id).ToList();
                Eq("synthetic: menu:false and confirm-needing combos hidden", !ids.Contains("c3") && !ids.Contains("c4") && ids.Contains("c1") && ids.Contains("c2"));
                var m1 = FindNode(sbuild, "m1");
                Eq("synthetic: a setting without a place lands in Modes", FindNode(sbuild, "modes") != null && FindNode(sbuild, "modes").Children.Any(n => n.Id == "m1"));
                Eq("synthetic: value that needs a menu confirmation is not shown", m1 != null && m1.Children.Count == 2 && m1.Children.All(c => c.Value != "evade"));
                var ov = sbuild.FirstOrDefault(n => n.Id == "ov");
                Eq("synthetic: overlay toggle follows radar_overlay and OverlayOn", ov != null && ov.Enabled && ov.Checked == true);
                var sring = Ring(sd, sl, su);
                Eq("synthetic: first item is c1 (top)", sring[0] != null && sring[0].Id == "c1");
                var more = sring.FirstOrDefault(n => n != null && n.Id == "more");
                Eq("synthetic: unplaced actions go to More right after the placed ones, no empty sectors", sring.Length == 2 && sring[1].Id == "more" && more != null && more.Children.Select(c => c.Id).OrderBy(x => x).SequenceEqual(new[] { "c2", "m1", "ov" }));
                Eq("synthetic parity", Same(Executables(sbuild), Executables(sring)));
                Eq("ring never holds an empty sector", sring.All(n => n != null));
            }
            // T-100: a slot taken in the registry layout beats the UI sector
            const string Json4 = "{\"actions\":[{\"id\":\"a1\",\"kind\":\"combo\",\"applies\":[\"air\"],\"label_ru\":\"А1\",\"label\":\"A1\",\"steps\":[{\"cmd\":\"cease_fire\"}]}],\"radial\":{\"air\":{\"N\":\"a1\"}}}";
            var sd4 = ActionRegistry.Parse(Json4, out err);
            Eq("synthetic air registry parses: " + err, sd4 != null);
            if (sd4 != null)
            {
                var sl4 = ActionRegistry.ParseRadial(Json4, sd4, out err);
                var u4 = View("air", true, true); u4.Caps.Add("air_evasion");
                var r4 = Ring(sd4, sl4, u4);
                Eq("registry N slot beats the UI sector: " + Sides(r4), r4.Length == 1 && r4[0].Id == "a1" && FindNode(r4, "evade_attack") == null);
            }
            // overview toggle (T-070): follows the `overview` capability and ModOn["overview"], not the radar overlay
            const string Json3 = "{\"actions\":[{\"id\":\"ovw\",\"kind\":\"toggle\",\"target\":\"mod\",\"flag\":\"overview\",\"applies\":[\"ship\"],\"label_ru\":\"О\",\"label\":\"O\"}],\"radial\":{\"ship\":{\"N\":\"ovw\"}}}";
            var sd3 = ActionRegistry.Parse(Json3, out err);
            Eq("synthetic overview registry parses: " + err, sd3 != null);
            if (sd3 != null)
            {
                var s3 = View("ship", true, true); s3.Caps.Add("radar_overlay"); s3.OverlayOn = true;
                var n3 = Build(sd3, s3).FirstOrDefault(n => n.Id == "ovw");
                Eq("synthetic: overview toggle is disabled without the overview capability", n3 != null && !n3.Enabled && n3.Checked != true);
                s3.Caps.Add("overview"); s3.ModOn["overview"] = true;
                n3 = Build(sd3, s3).FirstOrDefault(n => n.Id == "ovw");
                Eq("synthetic: overview toggle follows overview cap and ModOn", n3 != null && n3.Enabled && n3.Checked == true);
                s3.ModOn["overview"] = false;
                n3 = Build(sd3, s3).FirstOrDefault(n => n.Id == "ovw");
                Eq("synthetic: overview toggle is off when the window is closed", n3 != null && n3.Enabled && n3.Checked == false);
            }
            return _fail;
        }
    }
}
