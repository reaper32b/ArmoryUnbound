using System;
using HarmonyLib;

namespace ArmoryUnbound
{
    // Crafted perk parts always come out at quality 1 (scope item 6); they're upgraded at UL's Maintenance Station.
    // Read from the game and UndeadLegacy.dll (V2.6 b14 / UL 2.7.40) before writing:
    //   vanilla XUiC_RecipeStack.outputStack(): new ItemValue(recipe.itemValueType, outputQuality, outputQuality, false, null, 1f)
    //     (IL_0043..IL_0062) - so outputQuality = 1 before it runs gives a Q1 part.
    //   Undead Legacy H_Recipe+RecipeStackOutputStack: Prefix returns false (skips vanilla); its Postfix builds the item itself
    //     with new ItemValue(recipe.itemValueType, 1, 1, false, null, 1f) (IL_00b1..IL_00c6) and hands it to the
    //     workstation/inventory inside the postfix - UL crafts at Q1 already and nothing later can change it.
    // This prefix runs first (Priority.First), so the part is Q1 on both paths.
    [HarmonyPatch(typeof(XUiC_RecipeStack), "outputStack")]
    public static class Patch_RecipeStack_OutputStack
    {
        public static readonly FastTags<TagGroup.Global> PartTag = FastTags<TagGroup.Global>.Parse("auPerkPart");

        [HarmonyPriority(Priority.First)]
        public static void Prefix(XUiC_RecipeStack __instance)
        {
            try
            {
                Recipe r = __instance.recipe;
                if (r == null) return;
                ItemClass ic = ItemClass.GetForId(r.itemValueType);
                if (ic == null || !ic.HasAnyTags(PartTag)) return;
                if (__instance.outputQuality != 1)
                    Log.Out("[ArmoryUnbound] crafting " + ic.Name + ": output quality " + __instance.outputQuality + " -> 1 (crafted parts start at Q1)");
                __instance.outputQuality = 1;
            }
            catch (Exception e)
            {
                Log.Warning("[ArmoryUnbound] crafted-part quality hook failed: " + e.Message);
            }
        }
    }
}
