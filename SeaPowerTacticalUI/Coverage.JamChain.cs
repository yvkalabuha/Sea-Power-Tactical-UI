namespace VoiceBotBridge
{
    /// <summary>T-206: offline type-chain check of the jammer reads (selfcheck only; names the specs but reads no game value).</summary>
    internal sealed partial class GameApi
    {
        /// <summary>
        /// T-206 selfcheck: the chain of types behind the jammer reads. The element type of _ECMSystemsTryingToJamMe must be (a subtype of) the declaring type of the specs
        /// read on it, and the type of its _ecm field must be (a subtype of) the declaring type of EcmEp. A member-exists check alone missed ECM._ep read on SensorSystemECM.
        /// Returns null when ok.
        /// </summary>
        internal string JamChainCheck()
        {
            string why;
            var listMember = MemberResolver.Resolve(FindType(RadarSpecs.ObjJammers.Type), RadarSpecs.ObjJammers, out why);
            if (listMember == null) return "jam chain: no _ECMSystemsTryingToJamMe (" + why + ")";
            var lt = listMember is System.Reflection.FieldInfo ? ((System.Reflection.FieldInfo)listMember).FieldType : ((System.Reflection.PropertyInfo)listMember).PropertyType;
            if (!lt.IsGenericType || lt.GetGenericArguments().Length != 1) return "jam chain: _ECMSystemsTryingToJamMe is " + lt.Name + ", not a List<T>";
            var elem = lt.GetGenericArguments()[0];
            foreach (var spec in new[] { RadarSpecs.EcmIsOn, RadarSpecs.EcmInoperable, RadarSpecs.EcmIsDestroyed, RadarSpecs.EcmBaseObject, RadarSpecs.EcmSysEcm })
            {
                var decl = FindType(spec.Type);
                if (decl == null || !decl.IsAssignableFrom(elem)) return "jam chain: " + spec.Key + " is declared on " + spec.Type + " but the list holds " + elem.FullName;
            }
            var coreMember = MemberResolver.Resolve(FindType(RadarSpecs.EcmSysEcm.Type), RadarSpecs.EcmSysEcm, out why);
            if (coreMember == null) return "jam chain: no SensorSystemECM._ecm (" + why + ")";
            var ct = coreMember is System.Reflection.FieldInfo ? ((System.Reflection.FieldInfo)coreMember).FieldType : ((System.Reflection.PropertyInfo)coreMember).PropertyType;
            var epDecl = FindType(RadarSpecs.EcmEp.Type);
            if (epDecl == null || !epDecl.IsAssignableFrom(ct)) return "jam chain: " + RadarSpecs.EcmEp.Key + " is declared on " + RadarSpecs.EcmEp.Type + " but _ecm is " + ct.FullName;
            return null;
        }
    }
}
