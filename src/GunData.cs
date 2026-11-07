using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ArmoryUnbound
{
    // One perk on a gun: the perk id (an item_modifier tagged auPerk) and the quality of the part it came from.
    public struct PerkEntry
    {
        public string Id;
        public int Quality;   // 1..90 (Undead Legacy's quality range)

        public PerkEntry(string id, int quality)
        {
            Id = id;
            Quality = GunData.ClampQuality(quality);
        }
    }

    // Per-gun data stored in the game's own per-item metadata (ItemValue.Metadata).
    // ItemValue.Write saves it, ReadData restores it via SetMetadata, Clone deep-copies it,
    // so it follows the gun through saves, drops, containers and network packets.
    public static class GunData
    {
        public const string KeyName = "au.name";   // string
        public const string KeyPerks = "au.perks"; // comma list of perkId:quality, e.g. "auPerkHardHitter:72,auPerkSledge:15"
        public const string KeySkin = "au.skin";   // skin id ("auSkin<Name>", letters/digits only)
        public const int MinQuality = 1;
        public const int MaxQuality = 90;

        public enum AddPerkResult { Added, Full, Invalid }

        // Guns: first action is ranged; bows (ItemActionCatapult) are their own kind. Crossbows are ItemActionLauncher
        // (a ranged action) and count as guns, as in 1.0. (ItemClass.IsGun() is true for melee too - it only checks
        // ItemActionAttack.) Unchanged from 1.0 so guns behave exactly as before.
        public static bool IsGun(ItemValue iv)
        {
            return KindOf(iv) == ItemKind.Gun;
        }

        // What the bench can work on (1.1.0, melee scope). Decided by the first action's class, not tags (IZY's tags
        // are unreliable), read from Assembly-CSharp V2.6 b14 + the mods' DLLs:
        //   Gun   - ItemActionRanged and subclasses (Launcher: crossbows, rocket launcher, nailgun; UL's ULM_Ranged;
        //           note UL's chainsaw and auger are Ranged too, so they stay "guns"), except Catapult.
        //   Bow   - ItemActionCatapult (primitive, wooden, compound bow).
        //   Melee - ItemActionDynamic and subclasses: DynamicMelee (clubs, blades, knuckles, spears, sledges, axes,
        //           picks, shovels, hammers, wrenches), UL's ULM_MeleeAoE (sickles), Prospect (prospector pick),
        //           DensityHoe. Bare hands, torch and flashlight are DynamicMelee too: they have no quality, so the
        //           HasQuality check keeps them out (perks need mod slots and a quality item anyway).
        public enum ItemKind { None, Gun, Bow, Melee }

        public static ItemKind KindOf(ItemValue iv)
        {
            if (iv == null || iv.IsEmpty()) return ItemKind.None;
            ItemClass ic = iv.ItemClass;
            if (ic == null || ic.Actions == null || ic.Actions.Length == 0) return ItemKind.None;
            ItemAction a = ic.Actions[0];
            if (a == null) return ItemKind.None;
            if (a is ItemActionCatapult) return ic.HasQuality ? ItemKind.Bow : ItemKind.None;
            if (a is ItemActionRanged) return ItemKind.Gun;
            if (a is ItemActionDynamic) return ic.HasQuality ? ItemKind.Melee : ItemKind.None;
            return ItemKind.None;
        }

        // Anything the Gunsmith Bench, the console and the skins work on.
        public static bool IsSupported(ItemValue iv)
        {
            return KindOf(iv) != ItemKind.None;
        }

        // Melee weapons, tools and bows: skins follow the dye rule (only what a dye colours) instead of the
        // gun word list (AJ 2026-11-02).
        public static bool UsesDyeRule(ItemValue iv)
        {
            ItemKind k = KindOf(iv);
            return k == ItemKind.Melee || k == ItemKind.Bow;
        }

        public static int ClampQuality(int q)
        {
            return q < MinQuality ? MinQuality : (q > MaxQuality ? MaxQuality : q);
        }

        public static string GetName(ItemValue iv)
        {
            if (iv == null) return null;
            string s;
            if (iv.TryGetMetadata(KeyName, out s) && !string.IsNullOrEmpty(s)) return s;
            return null;
        }

        public static bool HasName(ItemValue iv)
        {
            return GetName(iv) != null;
        }

        public static void SetName(ItemValue iv, string name)
        {
            iv.SetMetadata(KeyName, name);
        }

        // Skin: set at the bench; the console may overwrite (test/admin).
        // Fast path for SkinApply: no dictionary allocation when the gun has no metadata.
        public static string GetSkin(ItemValue iv)
        {
            if (iv == null || iv.Metadata == null) return null;
            string s;
            if (iv.TryGetMetadata(KeySkin, out s) && !string.IsNullOrEmpty(s)) return s;
            return null;
        }

        public static bool HasSkin(ItemValue iv)
        {
            return GetSkin(iv) != null;
        }

        public static void SetSkin(ItemValue iv, string skinId)
        {
            iv.SetMetadata(KeySkin, skinId);
        }

        // Paint Removal Kit / "au skin none": the gun has no skin again (and can take a new one).
        public static bool ClearSkin(ItemValue iv)
        {
            if (iv == null || iv.Metadata == null || !HasSkin(iv)) return false;
            iv.RemoveMetaData(KeySkin);
            if (HasSkin(iv)) iv.SetMetadata(KeySkin, "");   // fallback: GetSkin treats an empty value as no skin
            return true;
        }

        // ---------------- perks ----------------

        // The raw "au.perks" string, or null.
        public static string GetPerksRaw(ItemValue iv)
        {
            if (iv == null || iv.Metadata == null) return null;
            string raw;
            if (!iv.TryGetMetadata(KeyPerks, out raw) || string.IsNullOrEmpty(raw)) return null;
            return raw;
        }

        // Parses "id:q,id:q". Duplicates are kept (each copy is its own perk). Tolerant so a bad value never crashes:
        // a missing or unreadable ":q" counts as quality 1, out-of-range qualities are clamped to 1..90.
        public static List<PerkEntry> ParsePerks(string raw)
        {
            List<PerkEntry> list = new List<PerkEntry>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string part in raw.Split(','))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                int colon = s.LastIndexOf(':');
                string id = colon < 0 ? s : s.Substring(0, colon).Trim();
                int q = MinQuality;
                if (colon >= 0)
                {
                    int parsed;
                    if (int.TryParse(s.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) q = parsed;
                }
                if (id.Length == 0) continue;
                list.Add(new PerkEntry(id, q));
            }
            return list;
        }

        public static string FormatPerks(List<PerkEntry> perks)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < perks.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(perks[i].Id).Append(':').Append(ClampQuality(perks[i].Quality).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static List<PerkEntry> GetPerks(ItemValue iv)
        {
            return ParsePerks(GetPerksRaw(iv));
        }

        private static void SetPerks(ItemValue iv, List<PerkEntry> perks)
        {
            if (perks.Count == 0)
            {
                iv.RemoveMetaData(KeyPerks);
                if (GetPerksRaw(iv) != null) iv.SetMetadata(KeyPerks, "");   // fallback: an empty value means no perks
                return;
            }
            iv.SetMetadata(KeyPerks, FormatPerks(perks));
        }

        // Perk slots = the gun's mod slots (Undead Legacy: 1..5 by quality band, follows UL's per-gun table and its
        // Maintenance Station upgrades, which rebuild Modifications at the new slot count).
        public static int PerkLimit(ItemValue iv)
        {
            return iv == null || iv.Modifications == null ? 0 : iv.Modifications.Length;
        }

        // Perk ids are item_modifier names: letters, digits, '_', '-', '.'; no commas, colons or spaces.
        public static bool IsValidPerkId(string perkId)
        {
            if (string.IsNullOrEmpty(perkId)) return false;
            foreach (char c in perkId)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }

        public static AddPerkResult AddPerk(ItemValue iv, string perkId, int quality)
        {
            if (iv == null || !IsValidPerkId(perkId)) return AddPerkResult.Invalid;
            List<PerkEntry> perks = GetPerks(iv);
            if (perks.Count >= PerkLimit(iv)) return AddPerkResult.Full;
            perks.Add(new PerkEntry(perkId, quality));
            SetPerks(iv, perks);
            return AddPerkResult.Added;
        }

        // Removes the perk at a position (0-based, as listed). Returns false if there is none.
        public static bool RemovePerkAt(ItemValue iv, int index, out PerkEntry removed)
        {
            removed = default(PerkEntry);
            if (iv == null) return false;
            List<PerkEntry> perks = GetPerks(iv);
            if (index < 0 || index >= perks.Count) return false;
            removed = perks[index];
            perks.RemoveAt(index);
            SetPerks(iv, perks);
            return true;
        }
    }
}
