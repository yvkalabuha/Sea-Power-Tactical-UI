using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-238 (R4): the deck_down warning is not pushed out by the 6-line cap and comes back after a load while the deck is still down.
    internal static class AswT238Deck
    {
        private const string Sh3 = "usn_sh-3h";

        private static int Count(AswFakeGame f, string needle) { return f.Logs.Count(l => l.Contains(needle)); }
        private static bool HasDeckWarn(AswController c) { return c.View().Warnings.Exists(w => w.StartsWith("deck_down", StringComparison.Ordinal)); }

        /// <summary>A strike group G on the hangar rows of deck 77 (two SH-3H); the deck is knocked out when <paramref name="out77"/>.</summary>
        private static AswController Start(AswFakeGame f)
        {
            var d = new AswFakeDeck { HangarReady = true, HangarPrepS = 0 };
            d.AddHangar(Sh3, "SH-3H", 2);
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = f.Tgt.Lat, Lon = f.Tgt.Lon };
            f.LoadoutList.Add(new LoadoutOption { Name = "ASWHunter", Display = "ASWHunter", Capacity = 2, ReadyUpMin = 0, Available = true, Weapons = new Dictionary<string, int> { { "usn_mk46", 2 } } });
            var p = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 12);
            f.Subs.Add(new AswContact { Id = 7, Name = "Sub #7", Lat = p.Lat, Lon = p.Lon, Source = "live", Side = "hostile", Live = true, FixS = f.Now, ClassName = "Kilo", Classified = true, MapPosture = "Enemy" });
            var c = AswT173AttackTests.Make(f);
            c.Tick(f.Now);
            string uids = AswMath.HangarSlotUid(77, Sh3, 1) + "," + AswMath.HangarSlotUid(77, Sh3, 2);
            string r = c.Group(AswT173AttackTests.P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + uids + "]}"));
            if (AswController.IsErr(r)) throw new InvalidOperationException(r);
            r = c.Order(AswT173AttackTests.P("{\"group\":\"G\",\"order\":\"strike\",\"airborne\":2}"));
            if (AswController.IsErr(r)) throw new InvalidOperationException(r);
            return c;
        }

        public static string Sticky()
        {
            var f = new AswFakeGame { DeckOut = true };
            var c = Start(f);
            AswT173AttackTests.Run(c, f, 60);
            if (!HasDeckWarn(c)) return "no deck_down warning";
            for (int i = 0; i < 8; i++) c.WarnForTest("G", "w" + i, "x");
            if (!HasDeckWarn(c)) return "deck_down pushed out by the cap: " + string.Join("|", c.View().Warnings.ToArray());
            AswT173AttackTests.Run(c, f, 20 * 60);
            if (!HasDeckWarn(c)) return "deck_down gone after 20 min";
            if (Count(f, "asw deck down G deck=77") != 1) return "deck down journal lines " + Count(f, "asw deck down G deck=77");
            f.DeckOut = false;
            AswT173AttackTests.Run(c, f, 4);
            if (Count(f, "asw deck up G deck=77") != 1) return "no deck up line";
            if (HasDeckWarn(c)) return "the warning stays after deck up";
            return null;
        }

        public static string Load()
        {
            var f = new AswFakeGame { DeckOut = true };
            var c = Start(f);
            AswT173AttackTests.Run(c, f, 60);
            if (!HasDeckWarn(c)) return "no deck_down warning";
            c.ForgetWarningsForTest();   // what a load does
            AswT173AttackTests.Run(c, f, 1);
            if (!HasDeckWarn(c)) return "the warning did not come back after the load";
            AswT173AttackTests.Run(c, f, 5);
            if (Count(f, "asw deck down G deck=77") != 1) return "deck down journal lines " + Count(f, "asw deck down G deck=77");
            return null;
        }
    }
}
