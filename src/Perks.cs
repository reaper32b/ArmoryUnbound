using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ArmoryUnbound
{
    // Perks. A perk is an item_modifier tagged "auPerk" (modifier_tags) in item_modifiers.xml.
    // Each entry in a gun's "au.perks" metadata ("perkId:quality") is run as a cached ItemValue of that modifier whose
    // Quality is the stored part quality, using exactly the calls vanilla ItemValue.ModifyValue / FireEvent make for each
    // installed mod (decoded from Assembly-CSharp V2.6 b14 IL):
    //   ModifyValue: inside "if (_useMods)", for each mod whose ItemClass is ItemClassModifier:
    //       mod.ModifyValue(_entity, _originalItemValue, _passiveEffect, ref _originalValue, ref _perc_value, _tags, true, false)
    //     and the mod's own effects are evaluated with level = (float)mod.Quality (IL_0138..IL_0142), so tier="1,90"
    //     passives Lerp between their two values by the perk's quality (PassiveEffect.ModValue).
    //   FireEvent: after "if (!HasQuality) return;", for each mod: mod.FireEvent(_eventType, _eventParms)
    //     (modifier branch -> ItemClass.Effects.FireEvent). Triggered effects have no tier, so on-hit / on-kill perks
    //     step through quality bands with the AuPerkQuality requirement, which reads PerkContext.Current.
    // Perks never live in Modifications/CosmeticMods, so they never use a mod slot. Duplicates are allowed: every
    // entry runs on its own, so two copies stack.
    public static class Perks
    {
        public static readonly FastTags<TagGroup.Global> PerkTag = FastTags<TagGroup.Global>.Parse("auPerk");
        public static readonly FastTags<TagGroup.Global> CorruptedTag = FastTags<TagGroup.Global>.Parse("auCorrupted");
        public const int MaxEntries = 32;   // sanity cap on a hand-edited au.perks string; the real limit is the gun's mod slots

        private sealed class Entry
        {
            public object Generation;  // ItemClass.list at parse time; a new world load rebuilds it
            public string Raw;         // the au.perks string this entry was parsed from
            public ItemValue[] Perks;  // one cached modifier ItemValue per valid entry, Quality = the entry's quality
        }

        private static readonly ItemValue[] None = new ItemValue[0];
        // Per-ItemValue cache. Weak keys: an entry disappears with its gun.
        private static readonly ConditionalWeakTable<ItemValue, Entry> perGun = new ConditionalWeakTable<ItemValue, Entry>();
        // Shared by every gun with the same perk string (qualities are part of the string); guarded by sync.
        private static readonly Dictionary<string, ItemValue[]> byRaw = new Dictionary<string, ItemValue[]>();
        private static readonly HashSet<string> warned = new HashSet<string>();
        private static readonly object sync = new object();
        private static object byRawGeneration;

        // A loaded item_modifier tagged auPerk, or null.
        public static ItemClassModifier GetPerkClass(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            ItemClassModifier ic = ItemClass.GetItemClass(id, false) as ItemClassModifier;
            if (ic == null || !ic.HasAnyTags(PerkTag)) return null;
            return ic;
        }

        public static bool IsPerk(string id)
        {
            return GetPerkClass(id) != null;
        }

        public static bool IsCorrupted(string id)
        {
            ItemClassModifier ic = GetPerkClass(id);
            return ic != null && ic.HasAnyTags(CorruptedTag);
        }

        // Every loaded perk id, for "au perk" help text.
        public static List<string> AllPerkIds()
        {
            List<string> ids = new List<string>();
            ItemClass[] list = ItemClass.list;
            if (list == null) return ids;
            for (int i = 0; i < list.Length; i++)
            {
                ItemClassModifier m = list[i] as ItemClassModifier;
                if (m != null && m.HasAnyTags(PerkTag)) ids.Add(m.Name);
            }
            return ids;
        }

        // The part item that carries a perk (property AuPerk = perk id), for returning a removed perk. Null if none.
        private static readonly Dictionary<string, int> partByPerk = new Dictionary<string, int>();
        private static object partGeneration;

        public static ItemClass PartFor(string perkId)
        {
            if (string.IsNullOrEmpty(perkId)) return null;
            ItemClass[] list = ItemClass.list;
            if (list == null) return null;
            lock (sync)
            {
                if (!ReferenceEquals(partGeneration, list))
                {
                    partByPerk.Clear();
                    partGeneration = list;
                    for (int i = 0; i < list.Length; i++)
                    {
                        ItemClass ic = list[i];
                        if (ic == null || ic is ItemClassModifier || ic.Properties == null || !ic.Properties.Contains("AuPerk")) continue;
                        string p = ic.Properties.GetString("AuPerk");
                        if (!string.IsNullOrEmpty(p) && !partByPerk.ContainsKey(p)) partByPerk[p] = ic.Id;
                    }
                }
                int id;
                return partByPerk.TryGetValue(perkId, out id) ? ItemClass.GetForId(id) : null;
            }
        }

        // Hot path: called from every ItemValue.ModifyValue. Returns null when the item has no perks.
        public static ItemValue[] Get(ItemValue iv)
        {
            Dictionary<string, TypedMetadataValue> md = iv.Metadata;
            if (md == null || md.Count == 0) return null;
            string raw;
            if (!iv.TryGetMetadata(GunData.KeyPerks, out raw) || string.IsNullOrEmpty(raw)) return null;

            object gen = ItemClass.list;
            Entry e;
            if (perGun.TryGetValue(iv, out e) && ReferenceEquals(e.Generation, gen)
                && (ReferenceEquals(e.Raw, raw) || e.Raw == raw))
                return e.Perks;

            ItemValue[] perks = Parse(raw, gen);
            if (e == null)
            {
                e = new Entry();
                try { perGun.Add(iv, e); } catch (ArgumentException) { perGun.TryGetValue(iv, out e); }
            }
            e.Generation = gen;
            e.Raw = raw;
            e.Perks = perks;
            return perks;
        }

        private static ItemValue[] Parse(string raw, object gen)
        {
            lock (sync)
            {
                if (!ReferenceEquals(byRawGeneration, gen))
                {
                    byRaw.Clear();
                    byRawGeneration = gen;
                }
                ItemValue[] cached;
                if (byRaw.TryGetValue(raw, out cached)) return cached;

                List<ItemValue> list = new List<ItemValue>();
                foreach (PerkEntry pe in GunData.ParsePerks(raw))
                {
                    ItemClassModifier ic = GetPerkClass(pe.Id);
                    if (ic == null)
                    {
                        if (warned.Add(pe.Id))
                            Log.Warning("[ArmoryUnbound] perk '" + pe.Id + "' on a gun is not a loaded item_modifier tagged auPerk - it has no effect.");
                        continue;
                    }
                    if (list.Count >= MaxEntries) break;
                    ItemValue v = new ItemValue(ic.Id, false);
                    v.Quality = (ushort)GunData.ClampQuality(pe.Quality);   // the perk runs at its part's quality, never the gun's
                    list.Add(v);
                }
                ItemValue[] arr = list.Count == 0 ? None : list.ToArray();
                byRaw[raw] = arr;
                return arr;
            }
        }
    }

    // Quality of the perk whose effects are running right now on this thread (0 = none).
    // Set around each perk's ModifyValue / FireEvent; read by the AuPerkQuality requirement. Saved and restored, so a
    // nested event (a perk effect that fires another event) can't leave a stale value behind.
    public static class PerkContext
    {
        [ThreadStatic] public static int Current;
    }

    // Postfix on ItemValue.ModifyValue: runs after the gun's own effects and its installed mods.
    [HarmonyPatch(typeof(ItemValue), "ModifyValue")]
    public static class Patch_ItemValue_ModifyValue
    {
        public static void Postfix(ItemValue __instance, EntityAlive _entity, ItemValue _originalItemValue,
            PassiveEffects _passiveEffect, ref float _originalValue, ref float _perc_value,
            FastTags<TagGroup.Global> _tags, bool _useMods)
        {
            if (!_useMods || __instance.Metadata == null) return;
            // Vanilla returns at once when _originalItemValue equals this item (IL_0000..IL_000a); mirror it.
            if (_originalItemValue != null && _originalItemValue.Equals(__instance)) return;
            ItemValue[] perks = Perks.Get(__instance);
            if (perks == null || perks.Length == 0) return;
            int was = PerkContext.Current;
            try
            {
                for (int i = 0; i < perks.Length; i++)
                {
                    PerkContext.Current = perks[i].Quality;
                    perks[i].ModifyValue(_entity, _originalItemValue, _passiveEffect, ref _originalValue, ref _perc_value, _tags, true, false);
                }
            }
            finally { PerkContext.Current = was; }
        }
    }

    // Postfix on ItemValue.FireEvent: fires each perk's triggered effects like an installed mod's, with
    // PerkContext.Current = that perk's quality while its requirements are checked.
    [HarmonyPatch(typeof(ItemValue), "FireEvent")]
    public static class Patch_ItemValue_FireEvent
    {
        public static void Postfix(ItemValue __instance, MinEventTypes _eventType, MinEventParams _eventParms)
        {
            if (__instance.Metadata == null) return;
            // Vanilla only fires mods when the item HasQuality (IL_007b..IL_0083); mirror it.
            if (!__instance.HasQuality) return;
            ItemValue[] perks = Perks.Get(__instance);
            if (perks == null || perks.Length == 0) return;
            int was = PerkContext.Current;
            try
            {
                for (int i = 0; i < perks.Length; i++)
                {
                    PerkContext.Current = perks[i].Quality;
                    perks[i].FireEvent(_eventType, _eventParms);
                }
            }
            finally { PerkContext.Current = was; }
        }
    }
}
