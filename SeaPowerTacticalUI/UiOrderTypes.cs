// T-148: two small types shared by the order panels of the CAP and ASW windows (CapOrderPanel, AswOrderPanel). Pure: no Unity, no game.
// (The plan declared them in CapOrderPanel.cs; a file of their own keeps aswwintest free of the CAP controller.)
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>One command of an "apply": name + arguments as for the link.</summary>
    internal sealed class UiCmd { public string Name; public Dictionary<string, object> Args; }

    /// <summary>State of an action button: id, text, tooltip (the reason when it is grey) and whether it is enabled.</summary>
    internal sealed class UiButtonState { public string Id, Label, Tip; public bool Enabled; }
}
