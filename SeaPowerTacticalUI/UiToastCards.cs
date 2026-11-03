using System;

namespace VoiceBotBridge
{
    /// <summary>T-291: the pure pass over the visible cards of the shared stack (no Unity): every body is drawn under the id scope of its card key,
    /// so the buttons of two cards ("show", "hide"...) never share an id (painter latch, hit zones).</summary>
    internal static class UiToastCards
    {
        /// <summary>One pass: separator, the body under k.IdScope = card key, the close [x] (outside the scope), "+N more".
        /// A body that throws closes its card and is logged (as before).</summary>
        public static void Draw(UiKit k, UiToastStackModel m, Action<string> log)
        {
            var list = m.Visible();
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (i > 0) k.Separator();
                string prev = k.IdScope;
                try
                {
                    k.IdScope = t.Key;
                    try { if (t.Body != null) t.Body(k); }
                    finally { k.IdScope = prev; }
                }
                catch (Exception e) { m.Close(t.Key); if (log != null) log("toast body " + t.Key + " failed: " + e.GetType().Name + ": " + e.Message); continue; }
                if (k.IconButton("toast.close." + t.Key, "x", UiText.Get("toast.close"))) m.Close(t.Key);
            }
            int folded = m.Folded;
            if (folded > 0) k.Hint(UiText.Fmt("toast.more", folded));
        }
    }
}
