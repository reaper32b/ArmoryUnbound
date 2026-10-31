using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ArmoryUnbound
{
    // Exact effect text of a perk at one quality, e.g. Hard Hitter Q30 -> "+21.5% damage."
    // Template = Localization "<perkId>At"; tokens are filled from the LOADED perk's own effects, so XML tuning shows up as is:
    //   {P:Passive[:n][:abs]}   n-th passive_effect of that name (default 1), value at quality q (same Lerp as PassiveEffect.ModValue),
    //                           shown as a signed percent ("+21.5", "-35"); abs drops the sign.
    //   {T:trigger[:n][:abs]}   n-th triggered_effect on that trigger whose AuPerkQuality band holds q: ModifyStats -> its value,
    //                           any other action -> its RandomRoll value (chance %), or 100 when it has no roll.
    // Read from Assembly-CSharp: MinEffectController.EffectGroups, MinEffectGroup.PassiveEffects / TriggeredEffects
    // (Dictionary<MinEventTypes, List<MinEventActionBase>>), PassiveEffect.Type/Levels/Values, MinEventActionBase.Requirements
    // (RequirementGroup.reqs), MinEventActionModifyStats.value, RequirementBase.value (RandomRoll).
    // No "<perkId>At" key -> the range description "<perkId>Desc".
    public static class PerkText
    {
        private static readonly Regex Token = new Regex(@"\{([PT]):(\w+)(?::(\d+))?(?::(abs))?\}", RegexOptions.Compiled);
        private static readonly HashSet<string> warned = new HashSet<string>();

        public static string At(string perkId, int quality)
        {
            string tpl = Loc(perkId + "At");
            if (tpl == null) return Loc(perkId + "Desc");
            ItemClassModifier ic = Perks.GetPerkClass(perkId);
            if (ic == null || ic.Effects == null || ic.Effects.EffectGroups == null) return Loc(perkId + "Desc");
            int q = GunData.ClampQuality(quality);
            try
            {
                return Token.Replace(tpl, m =>
                {
                    int n = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
                    bool abs = m.Groups[4].Success;
                    float? v = m.Groups[1].Value == "P" ? Passive(ic, m.Groups[2].Value, n, q) : Triggered(ic, m.Groups[2].Value, n, q);
                    if (v == null)
                    {
                        lock (warned) { if (warned.Add(perkId + m.Value)) Log.Warning("[ArmoryUnbound] perk text " + perkId + "At: no effect for " + m.Value); }
                        return "?";
                    }
                    float x = v.Value;
                    if (m.Groups[1].Value == "P") x *= 100f;
                    if (abs) x = Math.Abs(x);
                    string s = Num(x);
                    return (!abs && m.Groups[1].Value == "P" && x > 0) ? "+" + s : s;
                });
            }
            catch (Exception e)
            {
                Log.Warning("[ArmoryUnbound] perk text for " + perkId + " failed: " + e.Message);
                return Loc(perkId + "Desc");
            }
        }

        // whole numbers stay whole, otherwise one decimal ("21.5")
        private static string Num(float x)
        {
            double r = Math.Round(x, 1, MidpointRounding.AwayFromZero);
            return Math.Abs(r - Math.Round(r)) < 0.001 ? Math.Round(r).ToString("0", CultureInfo.InvariantCulture) : r.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static float? Passive(ItemClassModifier ic, string name, int n, int q)
        {
            PassiveEffects type;
            try { type = (PassiveEffects)Enum.Parse(typeof(PassiveEffects), name); } catch (Exception) { return null; }
            int seen = 0;
            foreach (MinEffectGroup g in ic.Effects.EffectGroups)
            {
                if (g == null || g.PassiveEffects == null) continue;
                foreach (PassiveEffect p in g.PassiveEffects)
                {
                    if (p == null || p.Type != type) continue;
                    if (++seen == n) return ValueAt(p.Levels, p.Values, q);
                }
            }
            return null;
        }

        // Same rule as PassiveEffect.ModValue for equal-length Levels/Values: Lerp inside the segment that holds the level.
        public static float? ValueAt(float[] levels, float[] values, float q)
        {
            if (values == null || values.Length == 0) return null;
            if (levels == null || levels.Length != values.Length || values.Length == 1) return values[0];
            if (q <= levels[0]) return values[0];
            for (int i = 1; i < levels.Length; i++)
                if (q <= levels[i]) return values[i - 1] + (values[i] - values[i - 1]) * (q - levels[i - 1]) / (levels[i] - levels[i - 1]);
            return values[values.Length - 1];
        }

        private static float? Triggered(ItemClassModifier ic, string trigger, int n, int q)
        {
            MinEventTypes ev;
            try { ev = (MinEventTypes)Enum.Parse(typeof(MinEventTypes), trigger); } catch (Exception) { return null; }
            int seen = 0;
            foreach (MinEffectGroup g in ic.Effects.EffectGroups)
            {
                List<MinEventActionBase> acts;
                if (g == null || g.TriggeredEffects == null || !g.TriggeredEffects.TryGetValue(ev, out acts) || acts == null) continue;
                foreach (MinEventActionBase a in acts)
                {
                    if (a == null) continue;
                    AuPerkQuality band = null; RandomRoll roll = null;
                    if (a.Requirements != null && a.Requirements.reqs != null)
                        foreach (IRequirement r in a.Requirements.reqs)
                        {
                            if (r is AuPerkQuality) band = (AuPerkQuality)r;
                            else if (r is RandomRoll) roll = (RandomRoll)r;
                        }
                    if (band == null || q < band.Min || q > band.Max) continue;
                    if (++seen != n) continue;
                    MinEventActionModifyStats ms = a as MinEventActionModifyStats;
                    if (ms != null) return ms.value;
                    return roll != null ? roll.value : 100f;
                }
            }
            return null;
        }

        private static string Loc(string key)
        {
            string s = Localization.Get(key, false);
            return string.IsNullOrEmpty(s) || s == key ? null : s;
        }
    }
}
