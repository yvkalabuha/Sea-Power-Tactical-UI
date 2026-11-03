// T-178 Kf2: tests of UiKit.ShipPick.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class UiKitShipPickTests
    {
        public static string Run()
        {
            foreach (double s in new[] { 1.0, 0.85, 1.33 })
            {
                string tag = "shippick@" + s + ": ";
                var ships = new List<ShipItem>
                {
                    new ShipItem { Uid = 5, Name = "Ticonderoga", Icon = "ntds:Surface:Own", DistNm = 12, Own = true },
                    new ShipItem { Uid = 1, Name = "Yorktown CG-48", Icon = "ntds:Surface:Own", DistNm = 4.3, Own = true },
                    new ShipItem { Uid = 8, Name = "Allied frigate", Icon = "ntds:Surface:Friend", DistNm = 2, Own = false },
                };
                var c = new WidgetCtx(s, 340f);
                int uid = 1; bool map = false;
                Action<UiKit> body = k => { uid = k.ShipPick("sh", ships, uid, "Опорный корабль", out map); };
                c.Frame(body);
                var b = c.P.Find("sh");
                if (b == null || !b.Text.StartsWith("Yorktown CG-48")) return tag + "closed view shows the selected ship";
                if (c.P.Find("sh.map") == null || !c.P.Find("sh.map").Text.Contains("На карте")) return tag + "the map button";
                c.P.Click("sh"); c.Frame(body);
                if (c.Count("sh.i.") != 2) return tag + "only own ships are listed: " + c.Count("sh.i.");
                if (c.P.FindText("4,3 мили") == null || c.P.FindText("12 миль") == null) return tag + "distances with the declension";
                if (c.P.Of("icon").Count != 2) return tag + "icons";
                if (c.P.Find("sh.i.0").Overlay == false) return tag + "list is an overlay";
                c.P.Click("sh.i.1"); c.Frame(body); c.Frame(body);
                if (uid != 5) return tag + "the pick by the list: " + uid;
                if (map) return tag + "mapPick is not raised by the list";
                c.P.Click("sh.map"); c.Frame(body);
                if (!map) return tag + "the button raises mapPick";
                c.Frame(body);
                if (map) return tag + "mapPick is one event only";
                if (c.P.CheckLayout(c.Width, 10000) != null) return tag + "layout: " + c.P.CheckLayout(c.Width, 10000);
                // an empty list: grey with the reason; the map button still works
                var e = new WidgetCtx(s, 340f);
                bool m2 = false;
                e.P.Click("sh.map");
                e.Frame(k => { k.ShipPick("sh", new ShipItem[0], 0, "t", out m2); });
                if (e.P.Find("sh").Enabled || e.P.Find("sh").Tip != "Нет своих кораблей") return tag + "empty list: grey with the reason";
                if (!m2) return tag + "empty list: the map button works";
            }
            return null;
        }
    }
}
