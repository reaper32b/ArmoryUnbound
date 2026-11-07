using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;

namespace ArmoryUnbound
{
    // Shows a gun's engraved name ("au.name") and its perks in the UI spots that show ONE specific item.
    // ItemClass.GetLocalizedItemName() is per weapon TYPE and has no ItemValue, so each spot is patched
    // where the ItemValue is in hand (decoded from Assembly-CSharp V2.6 b14 and UndeadLegacy.dll IL):
    //   vanilla  XUiC_ItemStack.get_ItemNameText            (item stack "tooltip" binding)
    //            XUiC_ItemInfoWindow.GetBindingValueInternal ("itemname", "itemdescription" = perk list)
    //            XUiC_AssembleWindow.GetBindingValueInternal ("itemname" - mod screen header)
    //            XUiC_ItemPartStack.GetPartName              (part slot hover; slots normally hold mods)
    //            PlayerMoveController.Update                 (dropped-item "pick up" prompt: transpiler)
    //   UL       patched by name only when UndeadLegacy.dll is loaded - see PatchUndeadLegacy.
    public static class Names
    {
        public const string PerkColor = "[E8C36A]";
        public const string CorruptColor = "[FF5050]";
        public const string GreyColor = "[A0A0A0]";
        public static int PickupPromptSites;

        // "Hard Hitter (Q72)": the perk name in the perk colour (corrupted perks in red), "(Q72)" in the game's quality colour
        // for that quality. Undead Legacy prefixes QualityInfo.GetQualityColorHex (H_QualityPatch.Qualitycolorhex) with its own
        // 1-90 band colours and uses it the same way itself (ULM_Items.QualityPrefix: "[{hex}]...[-]"), so this is UL's colour.
        public static string PerkLine(PerkEntry e)
        {
            string name = Loc(e.Id, e.Id);
            if (!Perks.IsPerk(e.Id)) return GreyColor + name + "[-] " + QualityTag(e.Quality);
            return (Perks.IsCorrupted(e.Id) ? CorruptColor : PerkColor) + name + "[-] " + QualityTag(e.Quality);
        }

        public static string QualityTag(int q)
        {
            return "[" + QualityHex(q) + "](Q" + q + ")[-]";
        }

        // Vanilla only has colours for 1-6, so on a vanilla game an out-of-range quality falls back to the top colour, then white.
        private static readonly Dictionary<int, string> hexCache = new Dictionary<int, string>();
        public static string QualityHex(int q)
        {
            // UL: GetQualityColorHex prefix maps q -> ceil(q/10) and returns that band's colour (same call UL's own UI makes).
            string h;
            lock (hexCache) { if (hexCache.TryGetValue(q, out h)) return h; }
            h = TryHex(q);
            if (h != null) { lock (hexCache) { hexCache[q] = h; } return h; }   // only real answers are cached
            return TryHex(Math.Min(q, 6)) ?? "FFFFFF";
        }

        private static string TryHex(int q)
        {
            try
            {
                string s = QualityInfo.GetQualityColorHex(q);
                if (string.IsNullOrEmpty(s)) return null;
                s = s.Trim().TrimStart('#', '[').TrimEnd(']');
                if (s.Length != 6 && s.Length != 8) return null;
                foreach (char c in s) if (!Uri.IsHexDigit(c)) return null;
                return s.Substring(0, 6);
            }
            catch (Exception) { return null; }
        }

        // Engraved name of a gun, or null (not a gun, or not named).
        public static string Engraved(ItemValue iv)
        {
            if (iv == null || iv.Metadata == null || !GunData.IsSupported(iv)) return null;
            return GunData.GetName(iv);
        }

        public static string Engraved(ItemStack stack)
        {
            if (stack == null || stack.IsEmpty()) return null;
            return Engraved(stack.itemValue);
        }

        // Replaces "ItemClass.GetForId(iv.type).GetLocalizedItemName()" in PlayerMoveController.Update.
        public static string DisplayName(ItemValue iv)
        {
            string n = Engraved(iv);
            if (n != null) return n;
            ItemClass ic = iv == null ? null : ItemClass.GetForId(iv.type);
            return ic == null ? "" : ic.GetLocalizedItemName();
        }

        // Text put above the item description: the gun type (when engraved) and the perk list by name.
        public static string InfoBlock(ItemValue iv)
        {
            // A perk part: what it adds at ITS quality, above its own (range) description.
            string partPerk = iv == null ? null : XUiC_AuGunsmithWindow.PerkOf(iv);
            if (partPerk != null)
            {
                int q = XUiC_AuGunsmithWindow.PartQuality(iv);
                string what = PerkText.At(partPerk, q);
                if (what == null) return null;
                return string.Format(Loc("auUIPartAt", "This part (Q{0}) adds {1}: {2}"), q,
                    (Perks.IsCorrupted(partPerk) ? CorruptColor : PerkColor) + Loc(partPerk, partPerk) + "[-]", what);
            }
            if (iv == null || iv.Metadata == null || !GunData.IsSupported(iv)) return null;
            string name = GunData.GetName(iv);
            List<PerkEntry> perks = GunData.GetPerks(iv);
            string skinId = GunData.GetSkin(iv);
            if (name == null && perks.Count == 0 && skinId == null) return null;
            StringBuilder sb = new StringBuilder();
            if (name != null) sb.Append(GreyColor).Append(iv.ItemClass.GetLocalizedItemName()).Append("[-]\n");
            if (perks.Count > 0)
            {
                sb.Append(PerkColor).Append(Loc("auUIPerks", "Perks")).Append("[-]");
                sb.Append(GreyColor).Append(" (").Append(perks.Count).Append('/').Append(GunData.PerkLimit(iv)).Append(")[-]");
                foreach (PerkEntry pe in perks)
                {
                    // "- Hard Hitter (Q72): +15-35% damage, by part quality." - corrupted perks in red, (Qnn) in the quality colour
                    string id = pe.Id;
                    sb.Append('\n');
                    if (!Perks.IsPerk(id)) { sb.Append(GreyColor).Append("- ").Append(id).Append("[-] ").Append(QualityTag(pe.Quality)); continue; }
                    sb.Append(Perks.IsCorrupted(id) ? CorruptColor : PerkColor).Append("- [-]").Append(PerkLine(pe));
                    string desc = PerkText.At(id, pe.Quality);   // exact effect at this perk's quality
                    if (desc != null) sb.Append(": ").Append(desc);
                }
            }
            if (skinId != null)
            {
                // "Skin: <name>" under the perks (scope item 8). A skin whose image was removed shows its id, greyed.
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                SkinInfo skin = SkinRegistry.Get(skinId);
                sb.Append(PerkColor).Append(Loc("auUISkin", "Skin")).Append(":[-] ");
                if (skin != null) sb.Append(skin.DisplayName);
                else sb.Append(GreyColor).Append(skinId).Append(" (").Append(Loc("auUISkinMissing", "image missing")).Append(")[-]");
            }
            return sb.ToString();
        }

        // Localization.Get returns the key itself when it's missing.
        private static string Loc(string key, string fallback)
        {
            string s = Localization.Get(key, false);
            return string.IsNullOrEmpty(s) || s == key ? fallback : s;
        }

        public static string WithInfoBlock(ItemValue iv, string description)
        {
            string block = InfoBlock(iv);
            if (block == null) return description;
            return string.IsNullOrEmpty(description) ? block : block + "\n\n" + description;
        }

        // ---------- Undead Legacy (its windows re-implement these spots and never call the vanilla code) ----------
        [ThreadStatic] private static ItemValue ulTooltipItem;
        private static FieldInfo ulHudItemClass;

        public static void PatchUndeadLegacy(Harmony harmony, List<string> patched, List<string> missing)
        {
            if (AccessTools.TypeByName("XUiC_ULM_ItemStack") == null) return; // UL not loaded
            Type[] tipByClass = new Type[] { typeof(XUi), typeof(XUiController), typeof(ItemClass) };
            Type[] tipByStack = new Type[] { typeof(XUi), typeof(XUiController), typeof(ItemStack) };
            Try(harmony, "XUiC_ULM_ItemStack", "GetBindingValueInternal", null, null, "UlStackBinding", null, patched, missing);
            Try(harmony, "XUiC_ULM_ItemInfoWindow", "GetBindingValueInternal", null, null, "UlInfoBinding", null, patched, missing);
            Try(harmony, "XUiC_ULM_AssembleWindow", "GetBindingValueInternal", null, null, "UlAssembleBinding", null, patched, missing);
            Type hud = AccessTools.TypeByName("XUiC_ULM_HUD_ActiveItem");
            ulHudItemClass = hud == null ? null : AccessTools.Field(hud, "itemClass");
            if (ulHudItemClass != null) Try(harmony, "XUiC_ULM_HUD_ActiveItem", "GetBindingValueInternal", null, null, "UlHudBinding", null, patched, missing);
            else missing.Add("XUiC_ULM_HUD_ActiveItem.itemClass");
            // Stack hover tooltip: ULM_Items.Tooltip(xui, controller, ItemClass|ItemStack) -> GetLocalizedNameWithColor(ItemClass).
            Try(harmony, "ULM_Items", "Tooltip", tipByClass, "UlTooltipClassPrefix", null, "UlTooltipFinalizer", patched, missing);
            Try(harmony, "ULM_Items", "Tooltip", tipByStack, "UlTooltipStackPrefix", null, "UlTooltipFinalizer", patched, missing);
            Try(harmony, "ULM_Items", "GetLocalizedNameWithColor", new Type[] { typeof(ItemClass) }, null, "UlNameWithColorPostfix", null, patched, missing);
        }

        private static HarmonyMethod Hm(string name)
        {
            return name == null ? null : new HarmonyMethod(AccessTools.Method(typeof(Names), name));
        }

        private static void Try(Harmony harmony, string typeName, string method, Type[] args,
            string prefix, string postfix, string finalizer, List<string> patched, List<string> missing)
        {
            string label = typeName + "." + method + (args != null && args.Length == 3 ? "(" + args[2].Name + ")" : "");
            try
            {
                Type t = AccessTools.TypeByName(typeName);
                MethodInfo target = t == null ? null : (args == null ? AccessTools.DeclaredMethod(t, method) : AccessTools.DeclaredMethod(t, method, args));
                if (target == null) { missing.Add(label); return; }
                harmony.Patch(target, Hm(prefix), Hm(postfix), null, Hm(finalizer), null); // HarmonyX: 6-arg overload (5-arg is obsolete)
                patched.Add("UL:" + label);
            }
            catch (Exception e)
            {
                missing.Add(label + " (" + e.Message + ")");
            }
        }

        // XUiC_ULM_ItemStack "itemName"
        public static void UlStackBinding(XUiController __instance, ref string __0, string __1)
        {
            if (__1 != "itemName") return;
            XUiC_ItemStack s = __instance as XUiC_ItemStack;
            string n = s == null ? null : Engraved(s.itemStack);
            if (n != null) __0 = n;
        }

        // XUiC_ULM_ItemInfoWindow "itemName" (UL strips newlines from the name) and "itemDescription"
        public static void UlInfoBinding(XUiController __instance, ref string __0, string __1)
        {
            if (__1 != "itemName" && __1 != "itemDescription") return;
            XUiC_ItemInfoWindow w = __instance as XUiC_ItemInfoWindow;
            ItemValue iv = InfoItem(w);
            if (iv == null) return;
            if (__1 == "itemName")
            {
                string n = Engraved(iv);
                if (n != null) __0 = n.Replace("\n", " ");
            }
            else __0 = WithInfoBlock(iv, __0);
        }

        // XUiC_ULM_AssembleWindow "itemname" (mod screen header; UL reads the virtual ItemStack property)
        public static void UlAssembleBinding(XUiController __instance, ref string __0, string __1)
        {
            if (__1 != "itemname") return;
            XUiC_AssembleWindow w = __instance as XUiC_AssembleWindow;
            string n = w == null ? null : Engraved(w.ItemStack);
            if (n != null) __0 = n;
        }

        // XUiC_ULM_HUD_ActiveItem "hudActiveItemName" (held item on the HUD)
        public static void UlHudBinding(XUiController __instance, ref string __0, string __1)
        {
            if (__1 != "hudActiveItemName") return;
            ItemClass ic = ulHudItemClass.GetValue(__instance) as ItemClass;
            if (ic == null || __instance.xui == null || __instance.xui.playerUI == null) return;
            EntityPlayerLocal p = __instance.xui.playerUI.entityPlayer;
            if (p == null || p.inventory == null) return;
            ItemValue iv = p.inventory.holdingItemItemValue;
            if (iv == null || iv.ItemClass != ic) return;
            string n = Engraved(iv);
            if (n != null) __0 = n;
        }

        public static void UlTooltipClassPrefix(XUiController __1, ItemClass __2)
        {
            XUiC_ItemStack s = __1 as XUiC_ItemStack;
            ulTooltipItem = (s != null && s.itemStack != null && s.itemStack.itemValue != null && s.itemStack.itemValue.ItemClass == __2) ? s.itemStack.itemValue : null;
        }

        public static void UlTooltipStackPrefix(ItemStack __2)
        {
            ulTooltipItem = __2 == null ? null : __2.itemValue;
        }

        public static Exception UlTooltipFinalizer(Exception __exception)
        {
            ulTooltipItem = null;
            return __exception;
        }

        // Swap the type name for the engraved name inside UL's coloured name, keeping UL's colour.
        public static void UlNameWithColorPostfix(ItemClass __0, ref string __result)
        {
            ItemValue iv = ulTooltipItem;
            if (iv == null || __0 == null || iv.ItemClass != __0 || __result == null) return;
            string n = Engraved(iv);
            if (n == null) return;
            string typeName = __0.GetLocalizedItemName();
            if (!string.IsNullOrEmpty(typeName) && __result.Contains(typeName)) __result = __result.Replace(typeName, n);
        }

        // The info window's item, only when it really is the item the window shows.
        public static ItemValue InfoItem(XUiC_ItemInfoWindow w)
        {
            if (w == null || w.itemStack == null || w.itemStack.IsEmpty()) return null;
            ItemValue iv = w.itemStack.itemValue;
            if (iv == null || w.itemClass == null || iv.ItemClass != w.itemClass) return null;
            return iv;
        }
    }

    // ---------------- Vanilla patches ----------------

    // Item stack hover tooltip ("tooltip" binding -> ItemNameText). Toolbelt slots are item stacks too.
    [HarmonyPatch(typeof(XUiC_ItemStack), "ItemNameText", MethodType.Getter)]
    public static class Patch_ItemStack_ItemNameText
    {
        public static void Postfix(XUiC_ItemStack __instance, ref string __result)
        {
            string n = Names.Engraved(__instance.itemStack);
            if (n != null) __result = n;
        }
    }

    // Item info panel: "itemname" header and "itemdescription" (gun type + perk list prepended).
    [HarmonyPatch(typeof(XUiC_ItemInfoWindow), "GetBindingValueInternal")]
    public static class Patch_ItemInfoWindow_Binding
    {
        public static void Postfix(XUiC_ItemInfoWindow __instance, ref string __0, string __1)
        {
            if (__1 != "itemname" && __1 != "itemdescription") return;
            ItemValue iv = Names.InfoItem(__instance);
            if (iv == null) return;
            if (__1 == "itemname")
            {
                string n = Names.Engraved(iv);
                if (n != null) __0 = n;
            }
            else __0 = Names.WithInfoBlock(iv, __0);
        }
    }

    // Mod-assembly screen header ("itemname").
    [HarmonyPatch(typeof(XUiC_AssembleWindow), "GetBindingValueInternal")]
    public static class Patch_AssembleWindow_Binding
    {
        public static void Postfix(XUiC_AssembleWindow __instance, ref string __0, string __1)
        {
            if (__1 != "itemname") return;
            string n = Names.Engraved(__instance.itemStack);
            if (n != null) __0 = n;
        }
    }

    // Part slot hover name. Part slots hold mods, so this only matters if a gun ever sits in one.
    [HarmonyPatch(typeof(XUiC_ItemPartStack), "GetPartName")]
    public static class Patch_ItemPartStack_GetPartName
    {
        public static void Postfix(XUiC_ItemPartStack __instance, ref string __result)
        {
            string n = Names.Engraved(__instance.itemValue);
            if (n != null) __result = n;
        }
    }

    // Dropped-item "pick up" prompt in PlayerMoveController.Update (IL_1e0a..IL_1e20 in V2.6 b14):
    //   ldfld EntityItem::itemStack; ldfld ItemStack::itemValue; ldfld ItemValue::type; call ItemClass::GetForId; callvirt GetLocalizedItemName
    // becomes  ldfld EntityItem::itemStack; ldfld ItemStack::itemValue; call Names.DisplayName; nop; nop
    [HarmonyPatch(typeof(PlayerMoveController), "Update")]
    public static class Patch_PlayerMoveController_Update
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            FieldInfo fItemStack = AccessTools.Field(typeof(EntityItem), "itemStack");
            FieldInfo fItemValue = AccessTools.Field(typeof(ItemStack), "itemValue");
            FieldInfo fType = AccessTools.Field(typeof(ItemValue), "type");
            MethodInfo mGetForId = AccessTools.Method(typeof(ItemClass), "GetForId", new Type[] { typeof(int) });
            MethodInfo mName = AccessTools.Method(typeof(ItemClass), "GetLocalizedItemName");
            MethodInfo mOurs = AccessTools.Method(typeof(Names), "DisplayName");
            int hits = 0;
            for (int i = 0; i + 4 < list.Count; i++)
            {
                if (list[i].LoadsField(fItemStack) && list[i + 1].LoadsField(fItemValue) && list[i + 2].LoadsField(fType)
                    && list[i + 3].Calls(mGetForId) && list[i + 4].Calls(mName))
                {
                    list[i + 2].opcode = OpCodes.Call; list[i + 2].operand = mOurs;
                    list[i + 3].opcode = OpCodes.Nop; list[i + 3].operand = null;
                    list[i + 4].opcode = OpCodes.Nop; list[i + 4].operand = null;
                    hits++;
                }
            }
            Names.PickupPromptSites = hits;
            if (hits != 1)
                Log.Warning("[ArmoryUnbound] PlayerMoveController.Update: expected 1 dropped-item name site, found " + hits + " - the pick-up prompt " + (hits == 0 ? "shows the normal name." : "was patched at every match."));
            return list;
        }
    }
}
