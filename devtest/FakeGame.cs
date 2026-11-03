using System;
using System.Collections.Generic;
using System.IO;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge.Dev
{
    /// <summary>Fake game side of the stand: the real DiagHost slot with fake sections, and a fake `link` slot that records every call.
    /// Tracks: 101 hostile, 102 neutral, 103 friendly, 104 unknown, anything else absent. Own unit 1 "Kirov". ASW proposals p1 -> 101, p2 -> 102, p3 -> 106.
    /// 0.27.10: 106 unknown but map-red (Submarine), 107 neutral painted red.</summary>
    internal static class FakeGame
    {
        private sealed class DeckMod : IUiDiag
        {
            public string DiagSection { get { return "deck"; } }
            public string DiagJson(int limit) { return "{\"decks\":[{\"ship_uid\":1,\"ship\":\"Kirov\",\"queue\":[{\"key\":\"a\",\"kind\":\"cap\",\"prio\":1,\"hold_s\":0}]}]}"; }
        }

        public static readonly List<string> Calls = new List<string>();
        public static readonly Dictionary<string, string> Fail = new Dictionary<string, string>();   // link cmd -> error code
        public static string CallsFile;
        public static bool Stall;

        public static void Install(bool diag, bool link)
        {
            Uninstall();
            if (diag)
            {
                var src = new DiagSources();
                src.Mission = () => "{\"in_mission\":true,\"paused\":false,\"time_compression\":1}";
                src.Selfcheck = () => "{\"checks\":10,\"missing\":0,\"missing_list\":[],\"capabilities\":[\"asw\",\"cap\"]}";
                src.Degradation = () => "{\"capabilities\":[]}";
                src.Windows = () => "[{\"name\":\"overview\",\"open\":true,\"rect\":[0,0,100,100]},{\"name\":\"radar\",\"open\":false},{\"name\":\"asw\",\"open\":false},{\"name\":\"cap\",\"open\":false}]";
                src.Modules = () => "{\"enabled\":true,\"init_order\":[\"a\",\"b\"],\"failed\":[]}";
                src.Relation = id =>
                {
                    switch (id)
                    {
                        case 101: return "{\"rel\":\"hostile\",\"class\":\"Destroyer\",\"kind\":\"Surface\"}";
                        case 102: return "{\"rel\":\"neutral\",\"class\":\"Tanker\",\"kind\":\"Surface\"}";
                        case 103: return "{\"rel\":\"friendly\",\"class\":\"Frigate\",\"kind\":\"Surface\"}";
                        case 104: return "{\"rel\":\"unknown\",\"class\":\"?\",\"kind\":\"Surface\"}";
                        case 106: return "{\"rel\":\"unknown\",\"class\":\"?\",\"kind\":\"Submarine\",\"map_hostile\":true}";   // painted red, not identified
                        case 107: return "{\"rel\":\"neutral\",\"class\":\"Tanker\",\"kind\":\"Surface\",\"map_hostile\":true}";
                        default: return null;
                    }
                };
                src.Unit = uid => uid == 1 ? "{\"uid\":1,\"name\":\"Kirov\"}" : null;
                src.Sections["asw"] = lim => "{\"active\":true,\"groups\":[{\"name\":\"A\",\"order\":\"search\",\"members\":2}],\"proposals\":[{\"proposal_id\":\"p1\",\"group\":\"A\",\"contact_id\":101},{\"proposal_id\":\"p2\",\"group\":\"A\",\"contact_id\":102},{\"proposal_id\":\"p3\",\"group\":\"A\",\"contact_id\":106}]}";
                src.Sections["sar"] = src.Sections["asw"];
                src.Sections["units"] = lim => "{\"units\":[{\"uid\":1,\"name\":\"Kirov\",\"kind\":\"ship\",\"lat\":10.5,\"lon\":20.25}],\"total\":1}";
                DiagHost.Register(src, new IUiDiag[] { new DeckMod() }, "0.20.0");
            }
            if (link) SetLink(1);
        }

        public static void SetLink(object version)
        {
            var slot = new Dictionary<string, object> { { "link", version }, { "version", "0.20.0" }, { "call", (Func<string, string>)Call } };
            AppDomain.CurrentDomain.SetData(DevInfo.LinkSlotKey, slot);
        }

        public static void Uninstall()
        {
            DiagHost.Unregister();
            AppDomain.CurrentDomain.SetData(DevInfo.LinkSlotKey, null);
            Calls.Clear();
            Fail.Clear();
            Stall = false;
        }

        private static string Call(string line)
        {
            if (Stall) System.Threading.Thread.Sleep(3000);
            lock (Calls) Calls.Add(line);
            if (CallsFile != null) { lock (Calls) File.AppendAllText(CallsFile, line + "\n"); }
            var d = (Dictionary<string, object>)Json.Parse(line);
            string cmd = (string)d["cmd"], id = d.ContainsKey("id") ? Json.Num((double)d["id"]) : "null";
            string err;
            if (Fail.TryGetValue(cmd, out err)) return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":false,\"error\":" + Json.Str(err) + "}";
            return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":true,\"data\":{\"echo\":" + Json.Str(cmd) + "}}";
        }
    }
}
