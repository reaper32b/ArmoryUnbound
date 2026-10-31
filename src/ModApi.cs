using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ArmoryUnbound
{
    // Mod entry point. The game finds IModApi classes in every DLL inside a mod folder.
    public class ArmoryUnboundMod : IModApi
    {
        public const string Version = "1.0.0";
        public const string HarmonyId = "aj.armoryunbound";

        public void InitMod(Mod _modInstance)
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                List<string> patched = new List<string>();
                foreach (MethodBase m in harmony.GetPatchedMethods()) patched.Add(m.DeclaringType.Name + "." + m.Name);

                // Undead Legacy re-implements the name spots in its own windows; patch them only if it's loaded.
                // (All mod assemblies are loaded before any InitMod runs, so UL's types are visible here.)
                List<string> ul = new List<string>();
                List<string> ulMissing = new List<string>();
                Names.PatchUndeadLegacy(harmony, ul, ulMissing);
                patched.AddRange(ul);

                Log.Out("[ArmoryUnbound] loaded v" + Version + " - patched: " + (patched.Count > 0 ? string.Join(", ", patched.ToArray()) : "(nothing)")
                    + " - pick-up prompt sites: " + Names.PickupPromptSites);
                if (ulMissing.Count > 0)
                    Log.Warning("[ArmoryUnbound] Undead Legacy is loaded but these UI spots weren't found, engraved names won't show there: " + string.Join(", ", ulMissing.ToArray()));
            }
            catch (Exception e)
            {
                Log.Error("[ArmoryUnbound] failed to load: " + e);
            }
            CheckRequirement();
            try
            {
                SkinRegistry.Load(_modInstance.Path);
                StockParts.Load(_modInstance.Path);
                SkinItems.AddLocalization();   // in case localization was loaded before us; SelectLanguage postfix covers reloads
            }
            catch (Exception e)
            {
                Log.Error("[ArmoryUnbound] failed to load skins: " + e);
            }
        }

        // The game drops a requirement whose name doesn't resolve without a word (the effect then fires at every quality),
        // so run the game's own parser on our name once and say what it produced.
        private static void CheckRequirement()
        {
            try
            {
                System.Xml.Linq.XElement x = System.Xml.Linq.XElement.Parse("<requirement name=\"" + AuPerkQuality.XmlName + "\" min=\"21\" max=\"40\"/>");
                AuPerkQuality r = RequirementBase.ParseRequirement(x) as AuPerkQuality;
                if (r != null && r.Min == 21 && r.Max == 40)
                    Log.Out("[ArmoryUnbound] requirement '" + AuPerkQuality.XmlName + "' resolves (min/max parsed) - on-hit/on-kill perks scale by quality band");
                else
                    Log.Error("[ArmoryUnbound] requirement '" + AuPerkQuality.XmlName + "' does NOT resolve - banded perks would fire at every quality");
            }
            catch (Exception e)
            {
                Log.Error("[ArmoryUnbound] requirement check failed: " + e);
            }
        }
    }
}
