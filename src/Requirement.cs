using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace ArmoryUnbound
{
    // XML requirement: true when the Armory Unbound perk whose effects are running has a quality in [min, max].
    //   <requirement name="ArmoryUnbound.AuPerkQuality, ArmoryUnbound" min="61" max="80"/>
    // RequirementBase.ParseRequirement (V2.6 b14) does Type.GetType(name) on the raw name and Activator.CreateInstance,
    // then castclass RequirementBase and ParseXAttribute for every attribute; a leading "!" sets invert.
    // An unknown name returns null and the requirement is silently dropped, so ModApi checks at startup that this
    // exact name resolves (log line "requirement ... resolves").
    // Outside a perk (PerkContext.Current == 0) it is false.
    public class AuPerkQuality : RequirementBase
    {
        public const string XmlName = "ArmoryUnbound.AuPerkQuality, ArmoryUnbound";
        public int Min = GunData.MinQuality;
        public int Max = GunData.MaxQuality;

        public override bool IsValid(MinEventParams _params)
        {
            int q = PerkContext.Current;
            bool ok = q > 0 && q >= Min && q <= Max;
            return invert ? !ok : ok;
        }

        public override bool ParseXAttribute(XAttribute _attribute)
        {
            string n = _attribute.Name.LocalName;
            int v;
            if (n == "min" && int.TryParse(_attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { Min = v; return true; }
            if (n == "max" && int.TryParse(_attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { Max = v; return true; }
            return base.ParseXAttribute(_attribute);
        }

        public override void GetInfoStrings(ref List<string> list)
        {
            list.Add("Perk quality " + Min + "-" + Max);
        }
    }
}
