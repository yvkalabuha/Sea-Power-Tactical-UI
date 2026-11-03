// T-341 (ADR-0132 section 6): the one place that fills the engage veto slot of the game API (Common/Veto.cs ExtraEngageVeto) is this file:
// it holds the chain of the UI mod's participants (cap_ff of CAP, strike of the strike window). Common/ is not touched.
namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        private EngageVetoChain _engageVetos;

        /// <summary>The chain of the veto participants; created on first use, then it is the veto of the game API.</summary>
        internal EngageVetoChain EngageVetos
        {
            get
            {
                if (_engageVetos == null) { _engageVetos = new EngageVetoChain(); ExtraEngageVeto = _engageVetos.Veto; }
                return _engageVetos;
            }
        }
    }
}
