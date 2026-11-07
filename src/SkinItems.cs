using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using HarmonyLib;

namespace ArmoryUnbound
{
    // One skin item per registered skin image (scope item 3), generated into the game's own XML so ids,
    // saves and ConfigsDump work through the normal pipeline.
    //
    // Hook (decoded from Assembly-CSharp V2.6 b14): WorldStaticData.loadSingleXml calls
    //   XmlPatcher.LoadAndPatchConfig(name, callback) - loads Data/Config/<name>.xml, runs EVERY mod's
    //   Config/<name>.xml through PatchXml in load order, then calls callback(xmlFile) once;
    //   then ApplyConditionalXmlBlocks, DumpPatchedXml (ConfigsDump), cache, parse.
    // A prefix wraps that callback for items / recipes / loot, so our nodes go in after every mod's
    // patches (Undead Legacy's wholesale <set> on /recipes included) and before ConfigsDump and parsing.
    // (A postfix on PatchXml itself would run once per mod file, i.e. before later mods' patches.)
    //
    //   items.xml   : <item name="auSkin<Name>">  Extends partsMaster, AuSkin = id, tag auSkinItem,
    //                 our colourless skin-kit icon (auSkinKit) tinted with the image's average colour.
    //   recipes.xml : the same name at the Gunsmith Bench (craft_area auGunsmithBench): 10 paint + 5 cloth.
    //   loot.xml    : the static auSkins group (Config/loot.xml) loses entries whose starter image is gone,
    //                 so a deleted starter PNG can't break loot.
    // Names "Skin: Tiger Stripe" are added to the game's localization dictionary after every language
    // load/select (postfix on Localization.SelectLanguage); a key already in a Localization.txt wins.
    public static class SkinItems
    {
        public const string PaintItem = "resourcePaint";
        public const string ClothItem = "resourceCloth";
        // Our own colourless icon (UIAtlases\ItemIconAtlas\auSkinKit.png); CustomIconTint colours it per skin.
        public const string SkinIcon = "auSkinKit";
        public const int PaintCount = 10;
        public const int ClothCount = 5;
        public const string SkinTag = "auSkinItem";
        // Paint Removal Kit (AJ, 2026-10-29): goes in the skin slot and scrubs the gun's skin off (the skin is destroyed).
        // The item is static (Config/items.xml, property AuSkinRemover); its recipe is generated here like the skins'
        // (so Undead Legacy's XML cache can't drop it); it sits in the auSkins loot group.
        public const string RemoverItem = "auPaintRemover";
        public const string RemoverAcid = "resourceAcid";
        public const int RemoverAcidCount = 2;
        public const int RemoverClothCount = 3;
        public const string LootGroup = "auSkins";

        public static int ItemsAdded, RecipesAdded;

        public static void Inject(string which, XmlFile f)
        {
            if (f == null || f.XmlDoc == null || f.XmlDoc.Root == null) return;
            XElement root = f.XmlDoc.Root;
            List<SkinInfo> skins = SkinRegistry.All();
            if (which == "items") InjectItems(root, skins);
            else if (which == "recipes") InjectRecipes(root, skins);
            else if (which == "loot") TrimLoot(root);
            else if (which == "traders") TrimTraders(root);
        }

        // Trader stock (UL add-on 1.6.0, Config/traders.xml): the auTraderSkins group loses entries whose starter image is gone,
        // like TrimLoot. Missing items in a trader group would otherwise fail the trader load.
        public const string TraderGroup = "auTraderSkins";

        private static void TrimTraders(XElement root)
        {
            int removed = 0;
            XElement groups = root.Element("trader_item_groups");
            if (groups == null) return;
            foreach (XElement g in groups.Elements("trader_item_group"))
            {
                XAttribute gn = g.Attribute("name");
                if (gn == null || gn.Value != TraderGroup) continue;
                List<XElement> drop = new List<XElement>();
                foreach (XElement it in g.Elements("item"))
                {
                    XAttribute a = it.Attribute("name");
                    if (a != null && a.Value != RemoverItem && !SkinRegistry.IsSkin(a.Value)) drop.Add(it);
                }
                foreach (XElement it in drop)
                {
                    Log.Warning("[ArmoryUnbound] trader group " + TraderGroup + ": " + it.Attribute("name").Value + " has no skin image any more - removed from traders");
                    it.Remove();
                    removed++;
                }
                if (!g.HasElements)
                    g.Add(new XElement("item", new XAttribute("name", PaintItem)));   // never leave a group empty
            }
            if (removed > 0) Log.Out("[ArmoryUnbound] traders: removed " + removed + " missing starter skins from " + TraderGroup);
            TopUpTraders(root, groups);
        }

        // "Every trader" (AJ): a trader added by a mod that loads AFTER zzz_ArmoryUnbound_UL (e.g. ZZZ_BDUBS_UL's vehicle trader, id 75)
        // can't be reached by the add-on's XML. This runs after every mod's patches, so any real trader still missing our groups
        // gets 1 roll of each. Only when the add-on's groups exist (no add-on = no trader stock). Vending machines and Testing are skipped.
        public const string TraderPartGroup = "auTraderPerkParts";
        private static readonly HashSet<string> NotTraders = new HashSet<string> { "3", "4", "5", "9", "10" };

        // 1.1.0: the tool-parts group is topped up the same way.
        public const string TraderToolPartGroup = "auTraderToolParts";

        private static void TopUpTraders(XElement root, XElement groups)
        {
            string[] ours = { TraderPartGroup, TraderToolPartGroup, TraderGroup };
            List<string> present = new List<string>();
            foreach (XElement g in groups.Elements("trader_item_group"))
            {
                string n = (string)g.Attribute("name");
                if (Array.IndexOf(ours, n) >= 0 && !present.Contains(n)) present.Add(n);
            }
            if (present.Count == 0) return;
            List<string> added = new List<string>();
            foreach (XElement t in root.Elements("trader_info"))
            {
                string id = (string)t.Attribute("id");
                if (id == null || NotTraders.Contains(id)) continue;
                if ((string)t.Attribute("allow_sell") == "false" || t.Attribute("player_owned") != null || t.Attribute("rentable") != null) continue;   // vending-style
                XElement items = t.Element("trader_items");
                if (items == null) continue;
                List<string> has = new List<string>();
                foreach (XElement it in items.Elements("item"))
                {
                    string gr = (string)it.Attribute("group");
                    if (gr != null && !has.Contains(gr)) has.Add(gr);
                }
                bool any = false;
                foreach (string g in present)
                {
                    if (has.Contains(g)) continue;
                    items.Add(new XElement("item", new XAttribute("group", g), new XAttribute("count", "1")));
                    any = true;
                }
                if (any) added.Add(id);
            }
            if (added.Count > 0) Log.Out("[ArmoryUnbound] traders: added perk parts/tool parts/skins to trader(s) " + string.Join(",", added.ToArray()) + " (defined by a mod loading after the add-on)");
        }

        private static HashSet<string> Existing(XElement root, string element)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement e in root.Elements(element))
            {
                XAttribute a = e.Attribute("name");
                if (a != null) names.Add(a.Value);
            }
            return names;
        }

        private static XElement Prop(string name, string value)
        {
            return new XElement("property", new XAttribute("name", name), new XAttribute("value", value));
        }

        // Generated by us (any earlier run, e.g. from Undead Legacy's XML cache): an item with property AuSkin and Tags auSkinItem,
        // or a recipe tagged auSkinItem. These are removed and re-added fresh on every load, so a cached file never keeps a
        // skin whose image was removed or changed, and nothing is ever doubled.
        private static bool IsGeneratedItem(XElement e)
        {
            bool auSkin = false, tag = false;
            foreach (XElement p in e.Elements("property"))
            {
                XAttribute n = p.Attribute("name"), v = p.Attribute("value");
                if (n == null || v == null) continue;
                if (n.Value == "AuSkin") auSkin = true;
                else if (n.Value == "Tags" && v.Value == SkinTag) tag = true;
            }
            return auSkin && tag;
        }

        private static bool IsGeneratedRecipe(XElement e)
        {
            XAttribute t = e.Attribute("tags");
            return t != null && t.Value == SkinTag;
        }

        private static int RemoveGenerated(XElement root, string element, Func<XElement, bool> ours)
        {
            List<XElement> drop = new List<XElement>();
            foreach (XElement e in root.Elements(element)) if (ours(e)) drop.Add(e);
            foreach (XComment c in root.Nodes().OfType<XComment>().ToList())
                if (c.Value.Contains("Armory Unbound: ") && c.Value.Contains("generated skin")) c.Remove();
            foreach (XElement e in drop) e.Remove();
            return drop.Count;
        }

        private static void InjectItems(XElement root, List<SkinInfo> skins)
        {
            if (root.Name.LocalName != "items") { Log.Warning("[ArmoryUnbound] items.xml root is <" + root.Name.LocalName + ">, skin items not added"); return; }
            int old = RemoveGenerated(root, "item", IsGeneratedItem);
            HashSet<string> have = Existing(root, "item");
            if (!have.Contains("partsMaster") || !have.Contains(PaintItem))
                Log.Warning("[ArmoryUnbound] partsMaster or " + PaintItem + " missing from items.xml - skin items may not load right");
            int n = 0;
            root.Add(new XComment(" Armory Unbound: " + skins.Count + " generated skin items (one per image in Mods/0-ArmoryUnbound/Skins) "));
            foreach (SkinInfo s in skins)
            {
                if (have.Contains(s.Id)) { Log.Warning("[ArmoryUnbound] an item named " + s.Id + " already exists (not ours) - left as is"); continue; }
                root.Add(new XElement("item", new XAttribute("name", s.Id),
                    Prop("Extends", "partsMaster"),
                    Prop("CreativeMode", "Player"),
                    Prop("CustomIcon", SkinIcon),
                    Prop("CustomIconTint", s.AvgHex ?? "ffffff"),
                    Prop("DescriptionKey", "auSkinItemDesc"),
                    Prop("Tags", SkinTag),
                    Prop("AuSkin", s.Id),
                    Prop("Stacknumber", "10"),
                    Prop("Weight", "1"),
                    Prop("EconomicValue", "300"),
                    Prop("SoundPickup", "paint_grab"),
                    Prop("SoundPlace", "paint_place")));
                n++;
            }
            ItemsAdded = n;
            Log.Out("[ArmoryUnbound] skin items: added " + n + " to items.xml" + (old > 0 ? " (replaced " + old + " from an earlier/cached load)" : ""));
        }

        private static void InjectRecipes(XElement root, List<SkinInfo> skins)
        {
            if (root.Name.LocalName != "recipes") { Log.Warning("[ArmoryUnbound] recipes.xml root is <" + root.Name.LocalName + ">, skin recipes not added"); return; }
            int old = RemoveGenerated(root, "recipe", IsGeneratedRecipe);
            HashSet<string> have = Existing(root, "recipe");
            int n = 0;
            root.Add(new XComment(" Armory Unbound: generated skin recipes (Gunsmith Bench) "));
            foreach (SkinInfo s in skins)
            {
                if (have.Contains(s.Id)) continue;   // a hand-written recipe for this skin wins
                root.Add(new XElement("recipe", new XAttribute("name", s.Id), new XAttribute("count", "1"),
                    new XAttribute("craft_area", "auGunsmithBench"), new XAttribute("craft_time", "30"), new XAttribute("tags", SkinTag),
                    new XElement("ingredient", new XAttribute("name", PaintItem), new XAttribute("count", PaintCount.ToString())),
                    new XElement("ingredient", new XAttribute("name", ClothItem), new XAttribute("count", ClothCount.ToString()))));
                n++;
            }
            if (!have.Contains(RemoverItem))
            {
                root.Add(new XElement("recipe", new XAttribute("name", RemoverItem), new XAttribute("count", "1"),
                    new XAttribute("craft_area", "auGunsmithBench"), new XAttribute("craft_time", "20"), new XAttribute("tags", SkinTag),
                    new XElement("ingredient", new XAttribute("name", RemoverAcid), new XAttribute("count", RemoverAcidCount.ToString())),
                    new XElement("ingredient", new XAttribute("name", ClothItem), new XAttribute("count", RemoverClothCount.ToString()))));
                Log.Out("[ArmoryUnbound] paint removal kit recipe added (" + RemoverAcidCount + " " + RemoverAcid + " + " + RemoverClothCount + " " + ClothItem + ")");
            }
            RecipesAdded = n;
            Log.Out("[ArmoryUnbound] skin recipes: added " + n + " to recipes.xml" + (old > 0 ? " (replaced " + old + " from an earlier/cached load)" : ""));
        }

        private static void TrimLoot(XElement root)
        {
            int removed = 0;
            foreach (XElement g in root.Elements("lootgroup"))
            {
                XAttribute gn = g.Attribute("name");
                if (gn == null || gn.Value != LootGroup) continue;
                List<XElement> drop = new List<XElement>();
                foreach (XElement it in g.Elements("item"))
                {
                    XAttribute a = it.Attribute("name");
                    if (a != null && a.Value != RemoverItem && !SkinRegistry.IsSkin(a.Value)) drop.Add(it);
                }
                foreach (XElement it in drop)
                {
                    Log.Warning("[ArmoryUnbound] loot group " + LootGroup + ": " + it.Attribute("name").Value + " has no skin image any more - removed from loot");
                    it.Remove();
                    removed++;
                }
                if (!g.HasElements)
                    g.Add(new XElement("item", new XAttribute("name", PaintItem), new XAttribute("count", "1")));   // never leave a group empty
            }
            if (removed > 0) Log.Out("[ArmoryUnbound] loot: removed " + removed + " missing starter skins from " + LootGroup);
        }

        // Every Armory Unbound item name starts with this tag (Localization.txt does the same for parts, bench and kit). Not "[AU]":
        // square brackets are colour codes in the game's text.
        public const string NameTag = "AU ";

        // Adds "AU Skin: <name>" for every skin item that has no Localization.txt entry. Safe to call repeatedly.
        public static void AddLocalization()
        {
            Dictionary<string, string[]> d = Localization.mDictionary;
            if (d == null) return;
            string[] header;
            if (!d.TryGetValue(Localization.HeaderKey, out header) || header == null) return;
            int col = Localization.defaultLanguageIndex;
            if (col < 0 || col >= header.Length) return;
            Dictionary<string, string[]> ci = Localization.mDictionaryCaseInsensitive;
            foreach (SkinInfo s in SkinRegistry.All())
            {
                if (d.ContainsKey(s.Id)) continue;
                string[] row = new string[header.Length];
                row[col] = NameTag + "Skin: " + s.DisplayName;   // "AU Skin: Arctic Camo" - type AU in any search box
                d[s.Id] = row;
                if (ci != null && !ci.ContainsKey(s.Id)) ci[s.Id] = row;
            }
        }
    }

    // Priority.First: Undead Legacy has its own prefix here (H_XmlPatcher_by_SMiThaYe, XML cache). On a cache HIT it hands the
    // callback, as it is when its prefix runs, to its own enumerator and skips the game's patching; on a MISS it wraps the
    // callback (save to cache, then call on). Running first means our wrapper is the callback UL captures in BOTH cases, so
    // the skins are added to cached files too. (Found 2026-10-28: recipes.xml came from UL's cache with no skin recipes.)
    [HarmonyPatch(typeof(XmlPatcher), "LoadAndPatchConfig")]
    public static class Patch_XmlPatcher_LoadAndPatchConfig
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(string _configName, ref Action<XmlFile> _callback)
        {
            string n = (_configName ?? "").ToLowerInvariant();
            if (n.EndsWith(".xml")) n = n.Substring(0, n.Length - 4);
            if (n != "items" && n != "recipes" && n != "loot" && n != "traders") return;
            Action<XmlFile> orig = _callback;
            string which = n;
            _callback = delegate (XmlFile f)
            {
                try { SkinItems.Inject(which, f); }
                catch (Exception e) { Log.Error("[ArmoryUnbound] adding skin " + which + " failed: " + e); }
                if (orig != null) orig(f);
            };
        }
    }

    [HarmonyPatch(typeof(Localization), "SelectLanguage")]
    public static class Patch_Localization_SelectLanguage
    {
        public static void Postfix()
        {
            try { SkinItems.AddLocalization(); }
            catch (Exception e) { Log.Warning("[ArmoryUnbound] skin item names not added to localization: " + e.Message); }
        }
    }
}
