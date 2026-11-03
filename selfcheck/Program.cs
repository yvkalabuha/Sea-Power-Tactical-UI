using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VoiceBotBridge;

internal static class Program
{
    private static int Main(string[] args)
    {
#if UI_CHECKS
        UiText.LoadForTests("ru"); // real ini tables for the UI checks (S0.2)
#endif
        if (args.Length > 0 && args[0] == "manifest") { Console.WriteLine(ModManifest.Json()); return 0; }
        if (args.Length > 0 && args[0] == "journal-sample") { Console.WriteLine(ActionJournal.SampleJson()); return 0; }
#if UI_CHECKS
        if (args.Length > 0 && args[0] == "asw-save-sample") { Console.WriteLine(AswSave.Sample()); return 0; }
#endif
#if FLAVOR_VOICE
        if (args.Length > 0 && args[0] == "serve") return Serve(args);
#endif
#if UI_CHECKS
        if (args.Length > 3 && args[1] == "coverage-table") return CoverageTable(args[2], args.Skip(3).ToArray());
#endif
#if FLAVOR_UI
        if (args.Length > 0 && args[0] == "link-smoke") return LinkSmoke(args);
#endif
        const string DefaultManaged = "/home/user/drive/SteamLibrary/steamapps/common/Sea Power/Sea Power_Data/Managed";
#if FLAVOR_UI
        // T-120: "member-sig <Type> <Member>" and "specs" read the game like the normal run, with the default Managed folder
        string sub = args.Length > 0 && (args[0] == "member-sig" || args[0] == "specs") ? args[0] : null;
#else
        string sub = null;
#endif
        string managed = args.Length > 0 && sub == null ? args[0] : DefaultManaged;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly.LoadFrom(Path.Combine(managed, "Seapower-Scripts.dll"));
        var unirx = Path.Combine(managed, "UniRx.dll");
        if (File.Exists(unirx)) Assembly.LoadFrom(unirx); // the game has it loaded; offline it is only found on demand
        var api = new GameApi();
#if FLAVOR_UI
        if (sub == "member-sig") return MemberSig(api, args);
        if (sub == "specs") return SpecsTable(api);
#endif
        api.SelfCheck();
#if FLAVOR_VOICE
        api.SelfCheckMission();
#endif
        var swt = api.FindType("SeaPower.ObjectBase").GetMethod("setWaypointTask");
        Console.WriteLine("setWaypointTask params: " + string.Join("; ", swt.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name + " hasDef=" + p.HasDefaultValue + " def=" + (p.DefaultValue ?? "null") + " (" + p.DefaultValue?.GetType().Name + ")")));
        var gp = api.FindType("SeaPower.GeoPosition");
        Console.WriteLine("GeoPosition fields: " + string.Join(",", gp.GetFields().Where(f => !f.IsStatic).Select(f => f.Name)));
        var cpp = api.FindType("SeaPower.ObjectBase").GetProperty("CommandPosition");
        Console.WriteLine("CommandPosition type " + cpp.PropertyType + " canwrite " + cpp.CanWrite);
        var gaw = api.FindType("SeaPower.ObjectBase").GetMethod("GetAllWaypointsGeoPositions");
        Console.WriteLine("GetAllWaypoints ret " + gaw.ReturnType + " tupleItem1=" + gaw.ReturnType.GetGenericArguments()[0].GetGenericArguments()[0].GetField("Item1")?.FieldType.Name);
        foreach (var nm in new[] { "Enable", "Disable" })
        {
            var rm = api.FindType("SeaPower.SensorSystemRadar")?.GetMethod(nm);
            Console.WriteLine("SensorSystemRadar." + nm + " params: " + (rm == null ? "MISSING" : string.Join("; ", rm.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name + " hasDef=" + p.HasDefaultValue))) + " (set_radar uses CallOpt = default args)");
        }
#if FLAVOR_VOICE
        AlertChecks(api);
#endif
        AviationChecks(api);
        TacticsChecks(api);
        var own = Ownership.SelfTest();
        T(api, "Ownership.SelfTest" + (own == null ? "" : ": " + own), own == null);
        Console.WriteLine(own == null ? "ownership selftest ok" : "ownership selftest FAIL: " + own);
#if UI_CHECKS
        CoreChecks(api);
        CoverageChecks(api);
        RadialChecks(api);
        OverviewChecks(api, managed);
        AswChecks(api);
        V16Checks(api);
        SarChecks(api);
        CapChecks(api);
        DevAirReadyChecks(api);
        // T-165: the optional patch targets of light smoke (a missing one only makes the option "unavailable")
        Console.WriteLine("light_smoke patch " + (api.PatchTarget(PerfSpecs.PatchSpawnEffect) != null ? "ok" : "missing"));
        Console.WriteLine("light_smoke_direct patch " + (api.PatchTarget(PerfSpecs.PatchDirect) != null ? "ok" : "missing"));
        Console.WriteLine("light_smoke_chaff patch " + (api.PatchTarget(PerfSpecs.PatchChaff) != null ? "ok" : "missing"));
        Console.WriteLine("msg_alert_text patch " + (api.PatchTarget(MessageColorSpecs.PatchAlertText) != null ? "ok" : "missing"));   // T-329
        Console.WriteLine("msg_classified patch " + (api.PatchTarget(MessageColorSpecs.PatchClassified) != null ? "ok" : "missing"));
        Console.WriteLine("save_game patch " + (api.PatchTarget(SaveHookSpecs.PatchSaveGame) != null ? "ok" : "missing"));   // T-198
        Console.WriteLine("buoy_revive patch " + (api.PatchTarget(BuoyReviveSpecs.PatchLaunchWeapons) != null ? "ok" : "missing"));   // T-094
        // T-120: one line per registry module ("<module> members ok checks=N missing=M duck=D")
        if (api.Registry != null) foreach (var m in api.Registry.Modules.OrderBy(x => x)) Console.WriteLine(api.Registry.ModuleLine(m));
#endif
        foreach (var c in api.Checks.Where(c => !c.Present)) Console.WriteLine("MISSING " + c.Member + " [" + c.Capability + (c.Required ? ",required" : ",optional") + "]");
        Console.WriteLine("checks=" + api.Checks.Count + " missing=" + api.Checks.Count(c => !c.Present));
        Console.WriteLine("capabilities=" + string.Join(",", api.Capabilities.OrderBy(x => x)));
        return api.Capabilities.Count == 0 ? 2 : 0;
    }

#if UI_CHECKS
    // T-118: the self-tests of the UI mod core (UiContext, UiCore, HotkeyBinding, ReleaseRegistry)
    private static void CoreChecks(GameApi api)
    {
        void S(string name, string r)
        {
            T(api, "core " + name + (r == null ? "" : ": " + r), r == null);
            Console.WriteLine("core " + name + (r == null ? " selftest ok" : " selftest FAILED: " + r));
        }
        S("MemberResolver", MemberResolver.SelfTest());   // T-120
        S("UiRegistry", UiRegistry.SelfTest());
        S("UiDegradation", VoiceBotBridge.Ui.UiDegradation.SelfTest());
        S("GameMemberAccess", api.SelfTestMemberAccess());
        S("FrameState", api.SelfTestFrameState());   // T-125
        Console.WriteLine("core FrameState bench " + api.FrameStateBenchLine());
        S("OwnWiring", api.SelfTestOwnWiring());   // T-120 M18: the OwnOnly guard is wired to the player's own objects
        S("UiContext", VoiceBotBridge.Ui.UiContext.SelfTest());
        S("UiCore", VoiceBotBridge.Ui.UiCore.SelfTest());
        S("UiSettingsRegistry", VoiceBotBridge.Ui.UiSettingsRegistry.SelfTest());   // T-144
        S("SettingsDraft", VoiceBotBridge.Ui.SettingsDraft.SelfTest());   // T-144 (includes HotkeyRecorder)
        S("HotkeyBinding", VoiceBotBridge.Ui.HotkeyBinding.SelfTest());
        S("UiConfigMigrator", VoiceBotBridge.Ui.UiConfigMigrator.SelfTest());
        S("ReleaseRegistry", VoiceBotBridge.Ui.ReleaseRegistry.SelfTest());
        S("DiagHost", VoiceBotBridge.Ui.DiagHost.SelfTest());
    }
#endif

#if UI_CHECKS
    // ADR-0061: the dev-only air_ready members; optional, printed but never counted as missing of the UI mod
    private static void DevAirReadyChecks(GameApi api)
    {
        var miss = new List<string>();
        foreach (var s in VoiceBotBridge.Dev.DevAirReadySpecs.All)
        {
            string why;
            if (MemberResolver.Resolve(api.FindType(s.Type), s, out why) == null) miss.Add(s.Key + " (" + why + ")");
        }
        Console.WriteLine("dev_air_ready members " + (miss.Count == 0 ? "ok" : "MISSING " + string.Join("; ", miss)) + " checks=" + VoiceBotBridge.Dev.DevAirReadySpecs.All.Length + " missing=" + miss.Count + " (optional, dev mod only)");
    }
#endif

#if FLAVOR_UI
    // T-120: every overload of Type.Member with full parameter and return types (to write a MemberSpec signature)
    private static int MemberSig(GameApi api, string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage: member-sig <Type> <Member | * | ~substring>"); return 2; }
        api.LoadNoesisOffline();   // Noesis types are not loaded by the game assembly alone
        var t = api.FindType(args[1]);
        if (t == null) { Console.WriteLine("type not found: " + args[1]); return 1; }
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        int n = 0;
        for (var cur = t; cur != null; cur = cur.BaseType)
            foreach (var m in cur.GetMembers(all).Where(x => x.Name == args[2] || args[2] == "*" || (args[2].StartsWith("~") && x.Name.IndexOf(args[2].Substring(1), StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                bool st = (m as MethodBase)?.IsStatic ?? (m as FieldInfo)?.IsStatic ?? ((m as PropertyInfo)?.GetMethod?.IsStatic ?? false);
                var mi0 = m as MethodInfo;
                Console.WriteLine((st ? "static " : "") + m.MemberType + " " + MemberResolver.Describe(m) + (mi0 != null && mi0.IsVirtual ? (mi0.Attributes.HasFlag(MethodAttributes.NewSlot) ? " [virtual newslot]" : " [override]") : ""));
                n++;
            }
        if (n == 0) Console.WriteLine("member not found: " + args[1] + "." + args[2]);
        return n == 0 ? 1 : 0;
    }

    // T-120: TSV of the registry against the installed game: module, capability, kind, key, required, own_only, duck, present
    private static int SpecsTable(GameApi api)
    {
        Console.WriteLine("module\tcapability\tkind\tkey\trequired\town_only\tduck\tpresent");
        foreach (var set in UiRegistry.All())
        {
            foreach (var m in set.Members)
                Console.WriteLine(string.Join("\t", new[] { set.Module, m.Capability, m.Kind.ToString(), m.Key, m.Required ? "1" : "0", m.OwnOnly ? "1" : "0", m.Duck ? "1" : "0", m.Duck ? "-" : api.Present(m) ? "1" : "0" }));
            foreach (var p in set.Patches)
                Console.WriteLine(string.Join("\t", new[] { set.Module, p.Capability, "Patch" + p.Kind, p.Key, p.Required ? "1" : "0", "0", "0", api.PatchTarget(p) != null ? "1" : "0" }));
        }
        return 0;
    }
#endif

#if FLAVOR_VOICE
    // Protocol smoke server: real Server/Json/Dispatcher/GameApi code, no game. Commands needing the game answer with errors.
    private static int Serve(string[] args)
    {
        int port = int.Parse(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "47653");
        int li = Array.IndexOf(args, "--link");
        if (li >= 0 && li + 1 < args.Length) LoadLink(args[li + 1]); // the interface mod puts its slot into the AppDomain
        var api = new GameApi();
        api.DecideOwnership(true, Console.WriteLine);
        var srv = new Server(Console.WriteLine, () => api.HelloJson(1, false, "offline", "offline"), "");
        srv.Start(port);
        Console.WriteLine("serving on 127.0.0.1:" + port + " (Ctrl+C to stop)");
        double next = 0;
        while (true)
        {
            Server.Incoming inc;
            while (srv.Inbox.TryDequeue(out inc)) Dispatcher.Handle(api, inc, true, ModInfo.Version);
            api.PumpLinkEvents();
            var evs = api.DrainEvents();
            if (evs != null) foreach (var e in evs) srv.Broadcast(e, false);
            if (api.Clock() >= next) { next = api.Clock() + 1; srv.Broadcast(api.BuildState(ModInfo.Version), true); }
            System.Threading.Thread.Sleep(50);
        }
    }

#endif
    private static void T(GameApi api, string name, bool ok)
    {
        api.Checks.Add(new GameApi.Check { Capability = "selftest", Member = name, Required = true, Present = ok });
    }

    // T-060: pure aviation helpers (altitude presets, loadout choice, type match) + signatures the commands rely on.
    private static void AviationChecks(GameApi api)
    {
        var e2c = new float[] { 200, 1000, 3000, 6000, 10000, 20000, 30000, 40000 };
        var sh2f = new float[] { 20, 200, 500, 1000, 3000, 6000, 10000 };
        T(api, "NearestPreset E-2C 19000 -> 20000", GameApi.NearestPreset(e2c, 19000) == 5);
        T(api, "NearestPreset E-2C exact 3000", GameApi.NearestPreset(e2c, 3000) == 2);
        T(api, "NearestPreset E-2C 0 -> lowest", GameApi.NearestPreset(e2c, 0) == 0);
        T(api, "NearestPreset E-2C tie 25000 -> lower", GameApi.NearestPreset(e2c, 25000) == 5);
        T(api, "NearestPreset E-2C far above -> top", GameApi.NearestPreset(e2c, 90000) == 7);
        // v0.8.1: a dying track that flips to "unknown" keeps the side/class the player knew
        var dying = new GameApi.ContactSnap { TrackId = 7001, Side = "unknown", Kind = "Invisible", Class = null, Identified = false };
        var known = new GameApi.ContactSnap { TrackId = 7001, Side = "hostile", Kind = "Surface", Class = "wp_pt_p6", Identified = true };
        var merged = GameApi.MergeSnap(dying, known);
        T(api, "MergeSnap unknown -> cached hostile", merged.Side == "hostile" && merged.Kind == "Surface" && merged.Class == "wp_pt_p6" && (bool)merged.Identified);
        T(api, "MergeSnap fresh known side wins", GameApi.MergeSnap(new GameApi.ContactSnap { Side = "neutral", Kind = "Surface" }, known).Side == "neutral");
        T(api, "MergeSnap no cache", GameApi.MergeSnap(dying, null).Side == "unknown");
        T(api, "IsWeaponKind missile/shell/torpedo", GameApi.IsWeaponKind("Missile") && GameApi.IsWeaponKind("Invisible") && GameApi.IsWeaponKind("Torpedo"));
        T(api, "IsWeaponKind ship/air/sub are units", !GameApi.IsWeaponKind("Surface") && !GameApi.IsWeaponKind("Air") && !GameApi.IsWeaponKind("Submarine") && !GameApi.IsWeaponKind("Helicopter"));
        T(api, "NearestPreset SH-2F 100 -> 20 (80 ft off vs 100)", GameApi.NearestPreset(sh2f, 100) == 0);
        T(api, "NearestPreset SH-2F 400 -> 500", GameApi.NearestPreset(sh2f, 400) == 2);
        T(api, "NearestPreset empty/null", GameApi.NearestPreset(new float[0], 100) == -1 && GameApi.NearestPreset(null, 100) == -1);

        var items = new List<GameApi.LoadoutInfo>
        {
            new GameApi.LoadoutInfo { Name = "Old", Display = "Old fit", Year = 1995, CanLaunch = true },
            new GameApi.LoadoutInfo { Name = "ASW", Display = "Anti-Submarine", Year = 1980, CanLaunch = true },
            new GameApi.LoadoutInfo { Name = "Transport", Display = "Transport", Year = 0, CanLaunch = false },
            new GameApi.LoadoutInfo { Name = "SAR", Display = "Search and Rescue", Year = 1970, CanLaunch = true },
        };
        string err;
        T(api, "PickLoadout default skips future year", GameApi.PickLoadout(items, null, 1988, out err) == 1 && err == null);
        T(api, "PickLoadout default no year filter", GameApi.PickLoadout(items, "", 0, out err) == 0);
        T(api, "PickLoadout by Name (case)", GameApi.PickLoadout(items, "sar", 1988, out err) == 3);
        T(api, "PickLoadout by DisplayName", GameApi.PickLoadout(items, "anti-submarine", 1988, out err) == 1);
        T(api, "PickLoadout substring", GameApi.PickLoadout(items, "rescue", 1988, out err) == 3);
        T(api, "PickLoadout impossible -> no_ammo", GameApi.PickLoadout(items, "Transport", 1988, out err) == -1 && err == "no_ammo");
        T(api, "PickLoadout unknown", GameApi.PickLoadout(items, "Torpedo", 1988, out err) == -1 && err == "unknown_loadout");
        T(api, "PickLoadout future year is unknown", GameApi.PickLoadout(items, "Old", 1988, out err) == -1 && err == "unknown_loadout");
        T(api, "PickLoadout default but nothing launchable", GameApi.PickLoadout(new List<GameApi.LoadoutInfo> { items[2] }, null, 1988, out err) == -1 && err == "no_ammo");
        T(api, "PickLoadout empty list", GameApi.PickLoadout(new List<GameApi.LoadoutInfo>(), null, 1988, out err) == -1 && err == "unknown_loadout");

        T(api, "TypeMatches helicopter", GameApi.TypeMatches("helicopter", "Helicopter", "usn_sh-2f", "SH-2F") && !GameApi.TypeMatches("helicopter", "Plane", "usn_e-2c", "E-2C"));
        T(api, "TypeMatches plane/VTOL/STOL", GameApi.TypeMatches("plane", "Plane", "x", "x") && GameApi.TypeMatches("plane", "VTOL", "x", "x") && GameApi.TypeMatches("plane", "STOL", "x", "x") && !GameApi.TypeMatches("plane", "Helicopter", "x", "x"));
        T(api, "TypeMatches any/empty", GameApi.TypeMatches("any", "Helicopter", "x", "x") && GameApi.TypeMatches(null, "Plane", "x", "x"));
        T(api, "TypeMatches substring", GameApi.TypeMatches("E-2", "Plane", "usn_e-2c", "E-2C Hawkeye") && !GameApi.TypeMatches("sh-2", "Plane", "usn_e-2c", "E-2C Hawkeye"));

        // signatures the commands call (no game state needed)
        var fd = api.FindType("SeaPower.FlightDeck");
        var clt = fd == null ? null : fd.GetMethod("createLaunchTask");
        Console.WriteLine("createLaunchTask params: " + (clt == null ? "MISSING" : string.Join("; ", clt.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))));
        var ob = api.FindType("SeaPower.ObjectBase");
        foreach (var so in ob.GetMethods().Where(m => m.Name == "setOrder"))
            Console.WriteLine("setOrder(" + string.Join(", ", so.GetParameters().Select(p => p.ParameterType.Name)) + ")");
    }

    // T-064: pure tactics tables, the embedded action registry and the menu/patch signatures.
    private static void TacticsChecks(GameApi api)
    {
        var tm = TacticsMath.SelfTest();
        T(api, "TacticsMath.SelfTest" + (tm == null ? "" : ": " + tm), tm == null);
        Console.WriteLine(tm == null ? "tactics selftest ok" : "tactics selftest FAILED: " + tm);
        var aj = ActionJournal.SelfTest();
#if FLAVOR_VOICE
        aj = aj ?? ActionJournal.SelfTestRu();   // Russian ring text lives in VoiceBotBridge/VoiceOnly
#endif
        T(api, "ActionJournal.SelfTest" + (aj == null ? "" : ": " + aj), aj == null);
        Console.WriteLine(aj == null ? "action journal selftest ok" : "action journal selftest FAILED: " + aj);
        var cm = ControlMemory.SelfTest();
#if FLAVOR_VOICE
        cm = cm ?? ControlMemory.SelfTestRu();   // Russian summary lives in VoiceBotBridge/VoiceOnly
#endif
        T(api, "ControlMemory.SelfTest" + (cm == null ? "" : ": " + cm), cm == null);
        Console.WriteLine(cm == null ? "control memory selftest ok" : "control memory selftest FAILED: " + cm);
        T(api, "control hooks tolerate foreign objects", api.SelfTestControlHooks());
        var rt = ActionRegistry.SelfTest();
        T(api, "ActionRegistry.SelfTest" + (rt == null ? "" : ": " + rt), rt == null);
        string err;
        var reg = ActionRegistry.Parse(ActionRegistry.LoadEmbedded() ?? "", out err);
        T(api, "embedded registry parses", reg != null);
        Console.WriteLine(reg == null ? "registry FAILED: " + err : "registry ok (" + reg.Count + " actions)");
#if UI_CHECKS
        var ms = api.MenuSmoke();
        T(api, "menu items build with the game classes" + (ms == null ? "" : ": " + ms), ms == null);
        Console.WriteLine("menu smoke: " + (ms ?? "ok"));
        Console.WriteLine("menu ready=" + api.MenuReady + " capabilities actions=" + api.Capabilities.Contains("actions"));
#endif
        var ob = api.FindType("SeaPower.ObjectBase");
        foreach (var m in ob.GetMethods().Where(m => m.Name == "AddEngageTask"))
            Console.WriteLine("AddEngageTask(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " + m.ReturnType.Name);
        var vm = api.FindType("SeapowerUI.ViewModels.ObjectBaseViewModel");
        var gm = vm?.GetMethod("GetContextMenuItems", Type.EmptyTypes);
        Console.WriteLine("GetContextMenuItems() -> " + (gm == null ? "MISSING" : gm.ReturnType.Name));
        var ci = api.FindType("SeapowerUI.ViewModels.ContextMenuItem");
        if (ci != null)
            foreach (var c in ci.GetConstructors())
                Console.WriteLine("ContextMenuItem(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)) + ")");
        // guidance of a few real ammunitions (reads the enum names of the game, no game state)
        var gt = api.FindType("SeaPower.AmmunitionParameters+GuidanceType");
        if (gt != null)
        {
            var names = Enum.GetNames(gt);
            Console.WriteLine("GuidanceType: " + string.Join(",", names));
            bool order = true;
            string[] expect = { "None", "IRHoming", "SemiActiveRadarHoming", "ActiveRadarHoming", "AntiRadiationHoming", "LaserHoming", "TVHoming", "ActiveSonarHoming", "PassiveSonarHoming", "WakeHoming", "ActivePassiveSonarHoming" };
            for (int i = 0; i < expect.Length; i++) if (i >= names.Length || names[i] != expect[i]) order = false;
            T(api, "GuidanceType enum order matches TacticsMath.GuidanceName", order);
        }
    }

#if UI_CHECKS
    // T-068: menu model, radial geometry/gesture, the menu tree built with the game's classes, members of the ring
    private static void RadialChecks(GameApi api)
    {
        var mm = MenuModel.SelfTest();
        T(api, "MenuModel.SelfTest" + (mm == null ? "" : ": " + mm), mm == null);
        Console.WriteLine(mm == null ? "menu model selftest ok" : "menu model selftest FAILED: " + mm);
        var rm = RadialMath.SelfTest();
        T(api, "RadialMath.SelfTest" + (rm == null ? "" : ": " + rm), rm == null);
        Console.WriteLine(rm == null ? "radial selftest ok" : "radial selftest FAILED: " + rm);
        string rc;
        T(api, "RadialKeyCheck without the game is unknown", api.RadialKeyCheck("Q", out rc) == "unknown" && rc == null);
        double mx, my;
        T(api, "RadialCamera without the game is null", api.RadialCamera() == null);
        T(api, "RadialMapPoint without a map is false", !api.RadialMapPoint(null, null, 1080, out mx, out my));
        var mt = api.MenuTreeSmoke();
        T(api, "menu tree from MenuModel builds with the game classes" + (mt == null ? "" : ": " + mt), mt == null);
        Console.WriteLine("menu tree smoke: " + (mt ?? "ok"));
    }

#if UI_CHECKS
    // v0.16 (T-107): Standing Orders, own evasion, smart attack, map layer math, map menu windows, UiText, Overview debug gate.
    private static void V16Checks(GameApi api)
    {
        Action<string, string> S = (name, r) =>
        {
            T(api, name + ".SelfTest" + (r == null ? "" : ": " + r), r == null);
            Console.WriteLine(name + (r == null ? " selftest ok" : " selftest FAILED: " + r));
        };
        S("RadialText", RadialText.SelfTest());
        S("AirRadarLedger", AirRadarLedger.SelfTest());   // T-241
        { string ars = api.AirRadarSelfSmoke(); T(api, "air radar smoke" + (ars == null ? "" : ": " + ars), ars == null); }   // T-241
        S("RadialGlyphs", RadialGlyphs.SelfTest());   // T-140
        S("MapLayerMath", MapLayerMath.SelfTest());
        S("ContactInfoMath", VoiceBotBridge.Ui.ContactInfoMath.SelfTest());   // T-136
        S("RangeRingMath", VoiceBotBridge.Ui.RangeRingMath.SelfTest());   // T-137
        S("RangeRingModel", VoiceBotBridge.Ui.RangeRingModel.SelfTest());   // T-137
        S("RangeRingKin", VoiceBotBridge.Ui.RangeRingKin.SelfTest());   // T-321
        S("RtOverlays", VoiceBotBridge.RtOverlays.SelfTest());   // T-137
        S("OverviewColumns", VoiceBotBridge.Ui.OverviewColumns.SelfTest());   // T-153
        S("OverviewOwn", VoiceBotBridge.Ui.OverviewOwnMath.SelfTest());   // T-242
        S("HitEstimateMath", VoiceBotBridge.Ui.HitEstimateMath.SelfTest());   // T-168
        S("HitEstimateGeo", VoiceBotBridge.Ui.HitEstimateGeo.SelfTest());   // T-170
        S("HitLedger", VoiceBotBridge.Ui.HitLedger.SelfTest());   // T-168
        S("HitEstimateModel", VoiceBotBridge.Ui.HitEstimateModel.SelfTest());   // T-168
        S("HitEstimateInst", VoiceBotBridge.Ui.HitEstimateInst.SelfTest());   // T-170
        S("OverviewExpand", VoiceBotBridge.Ui.OverviewExpand.SelfTest());   // T-168
        S("OverviewDebugGate", OverviewDebugGate.SelfTest());
        S("EvasionMath", EvasionMath.SelfTest());
        S("WorldGeoGame", GameApi.WorldGeoSelfTest());   // T-354
        S("EvasionCourseGuard", EvasionCourseGuard.SelfTest());   // T-272
        S("EvasionReturnGuard", EvasionReturnGuard.SelfTest());   // T-278
        S("EvasionHelmMath", EvasionHelmMath.SelfTest());   // T-279
        S("EvasionDecoyMath", EvasionDecoyMath.SelfTest());   // T-286
        S("AntiTorpedoMath", AntiTorpedoMath.SelfTest());   // T-141
        S("DoctrineFlags", DoctrineFlagsTests.SelfTest());
        S("AirDefenseReady", AirDefenseReadyRules.SelfTest());
        S("SmartAttackMath", SmartAttackMath.SelfTest());
        S("SmartAttackSave", SmartAttackSave.SelfTest());
        S("SmartAttackCommands", SmartAttackCommandsParse.SelfTest());
        S("SmartPickModel", SmartPickModel.SelfTest());
        S("UiText", UiText.SelfTest());
        var ds = api.DoctrineSelfSmoke();
        T(api, "doctrine smoke" + (ds == null ? "" : ": " + ds), ds == null);
        Console.WriteLine(ds == null ? "doctrine smoke ok" : "doctrine smoke FAILED: " + ds);
        // own evasion outside a mission: the hook does nothing and does not throw
        bool evOk = true;
        try { api.PumpTactics(0.0); } catch (Exception e) { evOk = false; Console.WriteLine("evasion smoke threw: " + e.GetType().Name); }
        T(api, "evasion hook outside a mission is quiet", evOk);
        Console.WriteLine("capabilities v0.16: standing_orders=" + api.Capabilities.Contains("standing_orders") + " smart_attack=" + api.Capabilities.Contains("smart_attack")
            + " map_menu_windows=" + api.Capabilities.Contains("map_menu_windows") + " asw_deck_ops=" + api.Capabilities.Contains("asw_deck_ops"));
    }
#endif

#if UI_CHECKS
    // v0.17 (T-109): pure SAR code (the controller is tested in the sartest/aswtest stands) + smoke of the real ISarGame outside a mission + game members.
    private static void SarChecks(GameApi api)
    {
        Action<string, string> S = (name, r) =>
        {
            T(api, name + ".SelfTest" + (r == null ? "" : ": " + r), r == null);
            Console.WriteLine("sar " + name + (r == null ? " selftest ok" : " selftest FAILED: " + r));
        };
        S("AirOpsMath", AirOpsMath.SelfTest());
        S("SarMath", SarMath.SelfTest());
        S("SarRouteMath", SarRouteMath.SelfTest());
        S("SarSave", SarSave.SelfTest());
        S("SarCommands", SarCommands.SelfTest());
        S("SarWindow", SarWindow.SelfTest());
        // the real ISarGame outside a mission: every call answers empty or with a code and never throws
        string smoke = null;
        try
        {
            var c = api.CreateSarController(new SarParams(), null);
            c.Tick(0); c.FramePump(); api.PumpSar();
            var r1 = c.ShipCmd(new Dictionary<string, object> { { "uid", 1L }, { "op", "start" }, { "radius_nm", 10.0 } });
            c.ShipsJson(); c.RaftsJson(); c.Busy(1); c.MenuCaption(1); c.ShipActive(1); c.OnMissionChanged();
            var ld = api.AirOps.DeckTasks(1); var lo = api.AirOps.Loadouts(1, "x");
            if (ld == null || lo == null) smoke = "airops returned null";
        }
        catch (Exception e) { smoke = e.GetType().Name + ": " + e.Message; }
        T(api, "sar game smoke outside a mission" + (smoke == null ? "" : ": " + smoke), smoke == null);
        Console.WriteLine(smoke == null ? "sar game smoke ok" : "sar game smoke FAILED: " + smoke);
    }
#endif

#if UI_CHECKS
    // v0.18 (T-112): pure CAP code (the controller is tested in the captest stand with a fake game) + smoke of the real ICapGame outside a mission + game members.
    private static void CapChecks(GameApi api)
    {
        Action<string, string> S = (name, r) =>
        {
            T(api, name + ".SelfTest" + (r == null ? "" : ": " + r), r == null);
            Console.WriteLine("cap " + name + (r == null ? " selftest ok" : " selftest FAILED: " + r));
        };
        S("AirOwnership", AirOwnership.SelfTest());
        S("AirDeckBroker", AirDeckBroker.SelfTest());   // T-119
        S("AirTickClock", AirTickClock.SelfTest());
        foreach (var part in new[] { "math", "lanes", "threat", "relief" }) S("CapMath." + part, CapMath.SelfTest(part));
        S("CapAlertMath", CapAlertMath.SelfTest());
        S("CapSave", CapSave.SelfTest());
        S("CapCommands", CapCommands.SelfTest());
        S("CapWindow", CapWindow.SelfTest());
        S("CapWindowModel", CapWindowModel.SelfTest());
        // T-148: the shared control set and the CAP order draft
        S("UiKit", UiKitTests.Run());
        S("UiWindowStateStore", UiWindowStateStore.SelfTest());
        S("UiFrameMath", UiFrameMath.SelfTest());
        // T-178 (ADR-0077): the pure models of the widgets
        S("UiDropdownModel", UiDropdownModel.SelfTest());
        S("ToggleSet", ToggleSet.SelfTest());
        S("CompassMath", CompassMath.SelfTest());
        S("UiHit", UiHit.SelfTest());
        S("UiScale", UiScale.SelfTest());
        S("UnitFmt", UnitFmt.SelfTest());
        S("ShipPickModel", ShipPickModel.SelfTest());
        S("UiUndoSlot", UiUndoSlot.SelfTest());
        S("SubDatumMath", SubDatumMath.SelfTest());   // T-288
        S("ZoneEditMath", ZoneEditMath.SelfTest());
        S("UiMapPick", UiMapPick.SelfTest());
        S("AswStrikePanel", AswStrikePanel.SelfTest());
        S("CapOrderPanel", CapOrderPanel.SelfTest());
        // the real ICapGame outside a mission: every call answers empty or with a code and never throws
        string smoke = null;
        try
        {
            var own = new AirOwnership();
            var c = api.CreateCapController(new CapParams(), own);
            c.Tick(0); api.PumpCap();
            c.Group(new Dictionary<string, object> { { "op", "create" }, { "uids", new List<object> { 1L } } });
            c.Order(new Dictionary<string, object> { { "group", "X" }, { "rect", new List<object> { 1.0, 1.0, 2.0, 2.0 } } });
            c.Busy(1); c.View(); c.StateJson(); c.DeckPrio(); c.OnMissionChanged(null, 0); c.ReleaseUid(1);
            api.CapLoadoutNames("x"); api.CapUnitPos(1); api.CapSelectedUid();
            api.CapRaw("cap_stop", new Dictionary<string, object> { { "group", "all" } });
        }
        catch (Exception e) { smoke = e.GetType().Name + ": " + e.Message; }
        T(api, "cap game smoke outside a mission" + (smoke == null ? "" : ": " + smoke), smoke == null);
        Console.WriteLine(smoke == null ? "cap game smoke ok" : "cap game smoke FAILED: " + smoke);
    }
#endif

#if UI_CHECKS
    // T-095: pure ASW code (the controller itself is tested in the aswtest stand with a fake game) + the game members of Asw.cs.
    private static void AswChecks(GameApi api)
    {
        var am = AswMath.SelfTest();
        T(api, "AswMath.SelfTest" + (am == null ? "" : ": " + am), am == null);
        var asv = AswSave.SelfTest();
        T(api, "AswSave.SelfTest" + (asv == null ? "" : ": " + asv), asv == null);
        var acp = AswCommandsParse.SelfTest();
        T(api, "AswCommandsParse.SelfTest" + (acp == null ? "" : ": " + acp), acp == null);
        var awm = AswWindowModel.SelfTest();
        T(api, "AswWindowModel.SelfTest" + (awm == null ? "" : ": " + awm), awm == null);
        var arg = AswRouteGuard.SelfTest();
        T(api, "AswRouteGuard.SelfTest" + (arg == null ? "" : ": " + arg), arg == null);
        var agn = AswGroupNames.SelfTest();
        T(api, "AswGroupNames.SelfTest" + (agn == null ? "" : ": " + agn), agn == null);
        var apm = AswPatternMath.SelfTest();   // T-176
        T(api, "AswPatternMath.SelfTest" + (apm == null ? "" : ": " + apm), apm == null);
        var alm = AswLocalizeMath.SelfTest();
        T(api, "AswLocalizeMath.SelfTest" + (alm == null ? "" : ": " + alm), alm == null);
        var adp = AswDrawPlan.SelfTest();
        T(api, "AswDrawPlan.SelfTest" + (adp == null ? "" : ": " + adp), adp == null);
        bool ok = am == null && asv == null && acp == null && awm == null && arg == null && agn == null && apm == null && alm == null && adp == null;
        Console.WriteLine(ok ? "asw selftest ok" : "asw selftest FAILED: " + (am ?? asv ?? acp ?? awm ?? apm ?? alm ?? adp));
    }
#endif

    // T-070: NTDS icons, Overview rules, UI rectangles, members of the game the panel reads, contacts parity, the game's real icon file.
    private static void OverviewChecks(GameApi api, string managed)
    {
        var nt = NtdsIcons.SelfTest();
        T(api, "NtdsIcons.SelfTest" + (nt == null ? "" : ": " + nt), nt == null);
        Console.WriteLine(nt == null ? "ntds selftest ok" : "ntds selftest FAILED: " + nt);
        var om = OverviewMath.SelfTest();
        T(api, "OverviewMath.SelfTest" + (om == null ? "" : ": " + om), om == null);
        Console.WriteLine(om == null ? "overview selftest ok" : "overview selftest FAILED: " + om);
        var mr = ModUiRects.SelfTest();
        T(api, "ModUiRects.SelfTest" + (mr == null ? "" : ": " + mr), mr == null);
        Console.WriteLine(mr == null ? "ui rects selftest ok" : "ui rects selftest FAILED: " + mr);
        var cp = api.ContactsParity();
        T(api, "contacts json parity" + (cp == null ? "" : ": " + cp), cp == null);
        Console.WriteLine(cp == null ? "contacts json parity ok" : "contacts json parity FAILED: " + cp);
        // the real icon file of the installed game (read only); skipped when the game directory has no assets
        string file = Path.GetFullPath(Path.Combine(managed, "..", "globalgamemanagers.assets"));
        if (!File.Exists(file)) { Console.WriteLine("ntds file: not found (" + file + "), skipped"); return; }
        string err;
        var set = NtdsIcons.LoadFromFile(file, out err);
        T(api, "ntds file parses" + (err == null ? "" : ": " + err), set != null);
        if (set == null) { Console.WriteLine("ntds file FAILED: " + err); return; }
        Console.WriteLine("ntds file ok (" + set.Paths.Count + " geometries, " + set.Colours.Count + " colours)");
        T(api, "ntds file: no path errors" + (set.Errors.Count == 0 ? "" : ": " + set.Errors[0]), set.Errors.Count == 0);
        T(api, "ntds file: at least 72 geometries and 7 colours", set.Paths.Count >= 72 && set.Colours.Count >= 7);
        foreach (var posture in new[] { "Enemy", "Unknown" })
            foreach (var type in new[] { "Air", "Helicopter", "Surface", "Submarine", "Missile", "Torpedo" })
            {
                bool dot;
                var key = NtdsIcons.KeyFor(type, posture, out dot);
                T(api, "ntds key " + key + " present", key != null && set.Paths.ContainsKey(key));
            }
    }

    // T-066: coverage math tables, the worker with synthetic radars, horizon parity with the game's own function, member list.
    private static void CoverageChecks(GameApi api)
    {
        var cm = CoverageMath.SelfTest();
        T(api, "CoverageMath.SelfTest" + (cm == null ? "" : ": " + cm), cm == null);
        Console.WriteLine(cm == null ? "coverage selftest ok" : "coverage selftest FAILED: " + cm);
        double ms;
        var sm = GameApi.CoverageSmoke(out ms);
        T(api, "CoverageWorker smoke" + (sm == null ? "" : ": " + sm), sm == null);
        Console.WriteLine(sm == null ? "worker ok " + Math.Round(ms) + " ms" : "worker FAILED: " + sm);
        // T-182 (ADR-0081): the jamming model and the worker with jammed radars
        var jm = CoverageJamMath.SelfTest();
        T(api, "CoverageJamMath.SelfTest" + (jm == null ? "" : ": " + jm), jm == null);
        Console.WriteLine(jm == null ? "coverage jam selftest ok" : "coverage jam selftest FAILED: " + jm);
        var jc = api.JamChainCheck();   // T-206
        T(api, "jam chain" + (jc == null ? "" : ": " + jc), jc == null);
        Console.WriteLine(jc == null ? "jam chain ok" : "jam chain FAILED: " + jc);
        var js = GameApi.CoverageJamSmoke();
        T(api, "CoverageWorker jam smoke" + (js == null ? "" : ": " + js), js == null);
        Console.WriteLine(js == null ? "worker jam ok" : "worker jam FAILED: " + js);
        Console.WriteLine("coverage members ok (" + api.CoverageMembers + ") capability radar_coverage=" + api.Capabilities.Contains("radar_coverage"));
        // RadarCalculator.getRadarHorizon(float radarHeight, float targetHeight, bool visual, float terrainHeight), all heights in unity
        var rc = api.FindType("SeaPower.RadarCalculator");
        var gh = rc == null ? null : rc.GetMethod("getRadarHorizon");
        if (gh == null) { T(api, "getRadarHorizon present", false); return; }
        var pairs = new[] { new[] { 30.0, 18.0, 0.0 }, new[] { 30.0, 153.0, 0.0 }, new[] { 30.0, 3003.0, 0.0 }, new[] { 7600.0, 153.0, 0.0 }, new[] { 300.0, 15.0, 5.0 }, new[] { 12.0, 2.0, 0.3 } };
        bool ok = true;
        foreach (var pr in pairs)
        {
            double game;
            try { game = Convert.ToDouble(gh.Invoke(null, new object[] { (float)(pr[0] * CoverageMath.UnityPerM), (float)(pr[1] * CoverageMath.UnityPerM), false, (float)(pr[2] * CoverageMath.UnityPerM) })) / CoverageMath.UnityPerM; }
            catch (Exception e) { Console.WriteLine("horizon parity: cannot call the game function offline (" + (e.InnerException ?? e).GetType().Name + ")"); return; }
            double ours = CoverageMath.HorizonM(pr[0], pr[1], pr[2], false);
            if (Math.Abs(ours - game) > 1e-3 * Math.Max(1.0, game)) { ok = false; Console.WriteLine("horizon parity FAILED for " + string.Join("/", pr) + ": ours " + ours + " game " + game); }
        }
        T(api, "horizon parity with RadarCalculator.getRadarHorizon", ok);
        if (ok) Console.WriteLine("horizon parity ok");
    }

    // T-066: prints CoverageMath.RangeLimit for the default tiers of the named sensors.ini radars (parity test with tests/test_sea_power_radar_coverage.py).
    // Contract: antenna 30 m ASL, sea state 0, visible height 3 m, terrain 0. Lines: name TAB tier TAB metres TAB limit.
    private static int CoverageTable(string ini, string[] names)
    {
        var secs = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string> cur = null;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*\[([^\]]+)\]");
            if (m.Success) { cur = new Dictionary<string, string>(); secs[m.Groups[1].Value.Trim()] = cur; continue; }
            var line = System.Text.RegularExpressions.Regex.Split(raw, "//|#")[0].Trim();
            int eq = line.IndexOf('=');
            if (cur != null && eq > 0) cur[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }
        var waves = new Dictionary<string, double>
        {
            { "vhf-band", 1.4989623 }, { "uhf-band", 0.46121916 }, { "l-band", 0.19986163 }, { "s-band", 0.099930815 }, { "c-band", 0.049965408 },
            { "x-band", 0.033310276 }, { "ku-band", 0.019986162 }, { "k-band", 0.011991698 }, { "ka-band", 0.0085654985 }
        };
        double minPower = 2e-14;
        Dictionary<string, string> rcs;
        if (secs.TryGetValue("RCS", out rcs) && rcs.ContainsKey("MinDetectableReturnPowerRadar")) minPower = double.Parse(rcs["MinDetectableReturnPowerRadar"], System.Globalization.CultureInfo.InvariantCulture);
        string err;
        var tiers = CoverageMath.ParseTiers(CoverageMath.DefaultTiers, out err);
        Func<Dictionary<string, string>, string, double, double> num = (d, k, def) =>
        {
            string v; double x;
            return d.TryGetValue(k, out v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) ? x : def;
        };
        foreach (var name in names)
        {
            Dictionary<string, string> s;
            if (!secs.TryGetValue(name, out s)) { Console.Error.WriteLine("no such radar: " + name); return 3; }
            string role = (s.ContainsKey("Role") ? s["Role"] : "Air").ToLowerInvariant();
            double wave;
            waves.TryGetValue((s.ContainsKey("Frequency") ? s["Frequency"] : "").ToLowerInvariant(), out wave);
            var r = new RadarSpec
            {
                Name = name, Type = "Search", Air = role == "air" || role == "airandsurface", Surface = role == "surface" || role == "airandsurface", On = true,
                AntennaM = 30, MinRangeM = num(s, "MinRange", 0) * 1000, MaxRangeM = num(s, "MaxRange", 0) * 1000, MinAltM = num(s, "MinAltitude", -1), MaxAltM = num(s, "MaxAltitude", -1),
                PeakPowerKw = num(s, "PeakPower", 0), GainFactor = Math.Pow(10, num(s, "Gain", 0) / 10), WaveLengthM = wave, LookDownMult = num(s, "LookDownMultiplier", 1.0),
                FullCircle = true, VisibleHeightM = 3
            };
            for (int i = 0; i < tiers.Count; i++)
            {
                var lim = CoverageMath.RangeLimit(r, tiers[i], minPower);
                Console.WriteLine(name + "\t" + i + "\t" + lim.RangeM.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "\t" + lim.Limit);
            }
        }
        return 0;
    }

#endif
#if FLAVOR_VOICE
    // Offline alert-key checks (fallback table only: the language table needs the running game).
    private static void AlertChecks(GameApi api)
    {
        foreach (var t in new[] { "New visual contact! Designate track 7004", "Новый визуальный контакт! Присвоен номер 7004" })
            Console.WriteLine("alert match '" + t + "' -> " + (api.MatchAlertKey(t) ?? "null"));
        Console.WriteLine("alert templates: " + api.TemplateCount + " (language table: SKIP offline, check live: grep UNMATCHED in BepInEx log)");
    }
#endif

#if FLAVOR_VOICE
    // serve --link <selfcheck-ui.dll>: loads the interface mod's selfcheck assembly into this process and lets it register its
    // link slot (UI GameApi + UiLinkHost), so the real channel between two assemblies with the same namespace is exercised.
    private static void LoadLink(string path)
    {
        var asm = Assembly.LoadFrom(Path.GetFullPath(path));
        var prog = asm.GetType("Program", true);
        var m = prog.GetMethod("RegisterLinkForServe", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (m == null) throw new InvalidOperationException("RegisterLinkForServe not found in " + path);
        m.Invoke(null, null);
        Console.WriteLine("link: loaded " + asm.GetName().Name);
    }
#endif
#if FLAVOR_UI
    private static GameApi _linkApi;

    // Called by the voice selfcheck (serve --link) by reflection: creates the UI GameApi + link host and puts the slot.
    internal static void RegisterLinkForServe()
    {
        var api = new GameApi();
        api.Capabilities.Add("radar_overlay"); // smoke: these are mod settings, no game needed
        api.Capabilities.Add("radial_menu");
        api.Capabilities.Add("overview");
        _linkApi = api;
        UiLinkHost.Register(api, () => UiLinkHost.CapsInner(api));
    }


    private sealed class FakeWin : VoiceBotBridge.Ui.IAswWindow
    {
        public bool Visible { get; set; }
        public int Opened;
        public void Open() { Opened++; Visible = true; }
        public bool SelectByTarget(string name) { return false; }
        public string UndoText; public int Undone;
        public string Undo() { if (UndoText == null) return null; Undone++; return UndoText; }
        public double[] Draft;
        public void OpenAreaDraft(double[] rect) { Draft = rect; Visible = true; }
    }

    // link-smoke: calls the link slot like the voice mod would; every ack must be ok.
    private static int LinkSmoke(string[] args)
    {
        RegisterLinkForServe();
        var api = _linkApi;
        var slot = AppDomain.CurrentDomain.GetData(Ownership.SlotKey) as Dictionary<string, object>;
        T(api, "link slot present", slot != null);
        if (slot == null) { Console.WriteLine("link smoke FAIL: no slot"); return 1; }
        T(api, "slot link == 1", slot.ContainsKey("link") && slot["link"] is int && (int)slot["link"] == Ownership.LinkVersion);
        T(api, "slot version == ModInfo.Version", slot.ContainsKey("version") && (slot["version"] as string) == ModInfo.Version);
        T(api, "slot owner_tactics == true", slot.ContainsKey("owner_tactics") && slot["owner_tactics"] is bool && (bool)slot["owner_tactics"]);
        var call = slot.ContainsKey("call") ? slot["call"] as Func<string, string> : null;
        T(api, "slot call is Func<string,string>", call != null);
        if (call == null) { Console.WriteLine("link smoke FAIL: no call"); return 1; }
        int n = 0;
        Func<string, Dictionary<string, object>> ask = cmd =>
        {
            var line = "{\"id\":" + (++n) + ",\"cmd\":\"" + cmd + "\"}";
            var ack = call(line);
            Console.WriteLine("link " + cmd + " -> " + (ack.Length > 160 ? ack.Substring(0, 160) + "..." : ack));
            return Json.Parse(ack) as Dictionary<string, object>;
        };
        Func<Dictionary<string, object>, bool> ok = a => a != null && a.ContainsKey("ok") && a["ok"] is bool && (bool)a["ok"];
        foreach (var cmd in new[] { "link_hello", "link_state", "link_events", "actions" })
        {
            var a = ask(cmd);
            T(api, "link " + cmd + " ack ok", ok(a));
            if (cmd == "link_hello" && ok(a))
            {
                var d = a["data"] as Dictionary<string, object>;
                T(api, "link_hello data.link == 1 and version", d != null && d.ContainsKey("link") && (double)d["link"] == 1 && (d["version"] as string) == ModInfo.Version && d.ContainsKey("capabilities") && d.ContainsKey("missing"));
            }
            if (cmd == "link_state" && ok(a))
            {
                var d = a["data"] as Dictionary<string, object>;
                T(api, "link_state has overlay/radial/overview/tactics/flags_by_uid", d != null && d.ContainsKey("overlay") && d.ContainsKey("radial") && d.ContainsKey("overview") && d.ContainsKey("tactics") && d.ContainsKey("flags_by_uid") && d["overlay"] != null);
            }
            if (cmd == "link_events" && ok(a))
            {
                var d = a["data"] as Dictionary<string, object>;
                T(api, "link_events data.events is a list", d != null && d.ContainsKey("events") && d["events"] is List<object>);
            }
        }
        // v0.16 commands outside a mission: arguments are checked first (bad_args), then the session (not_in_session)
        Func<string, string, string> code = (cmd, extra) =>
        {
            var line = "{\"id\":" + (++n) + ",\"cmd\":\"" + cmd + "\"" + extra + "}";
            var a = Json.Parse(call(line)) as Dictionary<string, object>;
            string c = a != null && a.ContainsKey("error") ? a["error"] as string : null;
            Console.WriteLine("link " + cmd + " -> " + (c ?? "ok") + (a != null && a.ContainsKey("detail") ? " (" + a["detail"] + ")" : ""));
            return c;
        };
        T(api, "set_doctrine without args -> bad_args", code("set_doctrine", "") == "bad_args");
        T(api, "set_doctrine outside a mission -> not_in_session", code("set_doctrine", ",\"uid\":1,\"flag\":\"auto_evade\",\"value\":true") == "not_in_session");
        T(api, "doctrine outside a mission -> not_in_session", code("doctrine", ",\"uid\":1") == "not_in_session");
        T(api, "asw_set_tactics outside a mission is an error ack", code("asw_set_tactics", ",\"tactics\":[\"mad\"]") != null);
        T(api, "smart_attack outside a mission is an error ack", code("smart_attack", ",\"uid\":1,\"target_id\":1") != null);
        // T-139 P3: the diag factory on a GameApi without a mission
        {
            var dctx = new VoiceBotBridge.Ui.UiContext(api, new VoiceBotBridge.Ui.UiContext.TestHost());
            api.Checks.Add(new GameApi.Check { Capability = "smoke", Member = "smoke.member", Required = false, Present = true });
            var ds = VoiceBotBridge.Ui.DiagWiring.Build(dctx);
            var mis = Json.Parse(ds.Mission()) as Dictionary<string, object>;
            T(api, "diag mission in_mission=false", mis != null && mis.ContainsKey("in_mission") && (bool)mis["in_mission"] == false);
            T(api, "diag relation(1) -> none (no track)", ds.Relation(1) == null);
            var wins = Json.Parse(ds.Windows()) as List<object>;
            T(api, "diag windows: 5 windows (strike T-341) + game_popup with open", wins != null && wins.Count == 6 && wins.All(w => (w as Dictionary<string, object>).ContainsKey("open")));
            var sc = Json.Parse(ds.Selfcheck()) as Dictionary<string, object>;
            T(api, "diag selfcheck.checks > 0", sc != null && (double)sc["checks"] > 0);
            Json.Parse(ds.Degradation());
            T(api, "diag unit(1) -> null outside a mission", ds.Unit(1) == null);
        }
        // T-139 P4: ui_window
        {
            T(api, "ui_window without name -> bad_args", code("ui_window", "") == "bad_args");
            T(api, "ui_window x -> bad_args", code("ui_window", ",\"name\":\"x\"") == "bad_args");
            T(api, "ui_window asw without a window -> unsupported", code("ui_window", ",\"name\":\"asw\"") == "unsupported");
            var fw = new FakeWin();
            api.AswWindow = fw;
            T(api, "ui_window asw on -> ok, Open called", code("ui_window", ",\"name\":\"asw\",\"on\":true") == null && fw.Opened == 1 && fw.Visible);
            T(api, "ui_window asw off -> hidden", code("ui_window", ",\"name\":\"asw\",\"on\":false") == null && !fw.Visible);
            api.AswWindow = null;
        }
        // T-178 I3: ui_undo (the card "Undo" of "Stop all" / "Disband")
        {
            T(api, "ui_undo without window -> bad_args", code("ui_undo", "") == "bad_args");
            T(api, "ui_undo x -> bad_args", code("ui_undo", ",\"window\":\"x\"") == "bad_args");
            T(api, "ui_undo asw without a window -> unsupported", code("ui_undo", ",\"window\":\"asw\"") == "unsupported");
            var fw = new FakeWin();
            api.AswWindow = fw;
            T(api, "ui_undo asw with nothing to undo -> no_undo", code("ui_undo", ",\"window\":\"asw\"") == "no_undo");
            fw.UndoText = "Restored 2 of 2";
            T(api, "ui_undo asw -> ok, Undo called", code("ui_undo", ",\"window\":\"asw\"") == null && fw.Undone == 1);
            api.AswWindow = null;
        }
        // T-288 (ADR-0115): sub_datum_*: unsupported while the module is off; arguments first, then the sector, then the session
        {
            T(api, "sub_datum_list with the module off -> unsupported", code("sub_datum_list", "") == "unsupported");
            api.SubDatum = new SubDatumBook(new SubDatumParams(), m => { });
            T(api, "sub_datum_list outside a mission -> not_in_session", code("sub_datum_list", "") == "not_in_session");
            // the link envelope "id" is also the sector id here, so "no id" cannot be sent: the request id is taken as the sector (-> no_datum)
            T(api, "sub_datum_search with a wrong id type -> bad_args", code("sub_datum_search", ",\"id\":\"x\"") == "bad_args");
            T(api, "sub_datum_dismiss with a wrong id type -> bad_args", code("sub_datum_dismiss", ",\"id\":\"x\"") == "bad_args");
            T(api, "sub_datum_search with an unknown id -> no_datum", code("sub_datum_search", ",\"id\":99") == "no_datum");
            api.SubDatum = null;
        }
        var bad = ask("no_such_cmd");
        T(api, "unknown cmd is an error ack", bad != null && bad.ContainsKey("ok") && !ok(bad));
        UiLinkHost.Unregister();
        T(api, "slot removed after Unregister", AppDomain.CurrentDomain.GetData(Ownership.SlotKey) == null);
        int fails = api.Checks.Count(c => !c.Present);
        foreach (var c in api.Checks.Where(c => !c.Present)) Console.WriteLine("MISSING " + c.Member);
        Console.WriteLine("checks=" + api.Checks.Count + " missing=" + fails);
        Console.WriteLine(fails == 0 ? "link smoke ok" : "link smoke FAIL");
        return fails == 0 ? 0 : 1;
    }
#endif
}
