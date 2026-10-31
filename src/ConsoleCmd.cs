using System;
using System.Collections.Generic;
using System.Text;

namespace ArmoryUnbound
{
    // "au" console command. Pattern copied from vanilla ConsoleCmdGiveQualityItem (giveself):
    // runs on the client, acts on GameManager.Instance.World.GetPrimaryPlayer().
    public class ConsoleCmdArmoryUnbound : ConsoleCmdAbstract
    {
        public override string[] getCommands()
        {
            return new string[] { "au" };
        }

        public override string getDescription()
        {
            return "Armory Unbound: name, perk, skin or inspect the gun in your hand";
        }

        public override string getHelp()
        {
            return "Usage:\n" +
                   "  au name <text>   - engrave a name on the held gun\n" +
                   "  au perk <perkId> [quality] - add a perk at that quality (1-90, default 1) to the held gun (limit = its mod slots; duplicates stack)\n" +
                   "  au perk remove <n> - remove the held gun's perk number n (as listed by 'au info'; test/admin, no part back)\n" +
                   "  au perk          - list the loaded perk ids\n" +
                   "  au skin <skinId> - put a skin on the held gun (test/admin: overwrites an existing skin)\n" +
                   "  au skin          - list the loaded skin ids\n" +
                   "  au skin none     - remove the held gun's skin (test/admin; players use a Paint Removal Kit)\n" +
                   "  au info          - show the held gun's Armory Unbound data\n" +
                   "  au parts         - list every part of the held gun: skinned or kept stock, and why (also written to the log)\n" +
                   "  au parts reload  - re-read Skins\\stock_parts.txt and re-skin the held gun";
        }

        public override bool IsExecuteOnClient
        {
            get { return true; }
        }

        public override Platform.DeviceFlag AllowedDeviceTypesClient
        {
            get { return (Platform.DeviceFlag)63; }
        }

        // Console output isn't written to the game log, so every reply is also logged (in-game tests can be checked from the log).
        private static void Out(string s)
        {
            SdtdConsole.Instance.Output("[AU] " + s);
            Log.Out("[ArmoryUnbound] console: " + s);
        }

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            try
            {
                Run(_params);
            }
            catch (Exception e)
            {
                Out("Error: " + e.Message);
                Log.Error("[ArmoryUnbound] au command failed: " + e);
            }
        }

        private void Run(List<string> _params)
        {
            if (GameManager.IsDedicatedServer)
            {
                Out("au acts on the gun in your hand - run it as a client, not on a dedicated server.");
                return;
            }
            if (_params.Count < 1)
            {
                Out(getHelp());
                return;
            }

            EntityPlayerLocal player = GameManager.Instance.World == null ? null : GameManager.Instance.World.GetPrimaryPlayer();
            if (player == null)
            {
                Out("No local player - load into a game first.");
                return;
            }

            Inventory inv = player.inventory;
            ItemValue iv = inv.holdingItemItemValue; // live slot object, not a copy
            string sub = _params[0].ToLowerInvariant();

            if (sub != "name" && sub != "perk" && sub != "skin" && sub != "info" && sub != "parts")
            {
                Out("Unknown subcommand '" + _params[0] + "'.\n" + getHelp());
                return;
            }
            if (sub == "parts" && _params.Count > 1 && _params[1].ToLowerInvariant() == "reload")
            {
                StockParts.Load(StockPartsModPath());
                SkinRuntime.Reskin(player, inv.GetHoldingItemTransform());
                Out("Reloaded " + StockParts.FilePath + ": " + StockParts.Words.Length + " words ("
                    + (StockParts.Words.Length > 0 ? string.Join(", ", StockParts.Words) : "none - every part is skinned")
                    + "). The held gun re-skins within a few seconds; run 'au parts' to check.");
                return;
            }
            if (sub == "skin" && _params.Count < 2)
            {
                Out("Usage: au skin <skinId>\n" + SkinList());
                return;
            }
            if (!GunData.IsGun(iv))
            {
                Out("Hold a gun first (melee weapons, tools and bows aren't supported).");
                return;
            }

            string gunLabel = iv.ItemClass.GetLocalizedItemName() + " (" + iv.ItemClass.Name + ")";

            if (sub == "name")
            {
                string text = _params.Count > 1 ? string.Join(" ", _params.GetRange(1, _params.Count - 1).ToArray()).Trim() : "";
                if (text.Length == 0)
                {
                    Out("Usage: au name <text>");
                    return;
                }
                string old = GunData.GetName(iv);
                GunData.SetName(iv, text);
                inv.Changed();
                Out("Named " + gunLabel + ": \"" + text + "\"" + (old != null ? " (was \"" + old + "\")" : ""));
                return;
            }

            if (sub == "perk")
            {
                if (_params.Count < 2)
                {
                    List<string> known = Perks.AllPerkIds();
                    Out("Usage: au perk <perkId> [quality 1-90] | au perk remove <n>\n  Loaded perks (" + known.Count + "): " + (known.Count > 0 ? string.Join(", ", known.ToArray()) : "(none)"));
                    return;
                }
                string id = _params[1].Trim();
                if (id.ToLowerInvariant() == "remove")
                {
                    List<PerkEntry> have = GunData.GetPerks(iv);
                    int n;
                    if (_params.Count < 3 || !int.TryParse(_params[2].Trim(), out n) || n < 1 || n > have.Count)
                    {
                        Out("Usage: au perk remove <n>, n = 1.." + have.Count + " (see 'au info')." + (have.Count == 0 ? " " + gunLabel + " has no perks." : ""));
                        return;
                    }
                    PerkEntry gone;
                    GunData.RemovePerkAt(iv, n - 1, out gone);
                    inv.Changed();
                    Out("Removed perk " + n + ": " + gone.Id + " Q" + gone.Quality + " (" + Localization.Get(gone.Id) + ") from " + gunLabel
                        + " (" + GunData.GetPerks(iv).Count + "/" + GunData.PerkLimit(iv) + "). Console removal gives no part back.");
                    return;
                }
                if (GunData.IsValidPerkId(id) && !Perks.IsPerk(id))
                {
                    List<string> known = Perks.AllPerkIds();
                    Out("Refused: '" + id + "' is not a loaded perk (an item_modifier tagged auPerk). Perk ids are case-sensitive.\n" +
                        "  Loaded perks (" + known.Count + "): " + (known.Count > 0 ? string.Join(", ", known.ToArray()) : "(none)"));
                    return;
                }
                int q = GunData.MinQuality;
                if (_params.Count > 2)
                {
                    if (!int.TryParse(_params[2].Trim(), out q) || q < GunData.MinQuality || q > GunData.MaxQuality)
                    {
                        Out("Refused: quality must be a whole number " + GunData.MinQuality + "-" + GunData.MaxQuality + ".");
                        return;
                    }
                }
                GunData.AddPerkResult r = GunData.AddPerk(iv, id, q);
                if (r == GunData.AddPerkResult.Added)
                {
                    inv.Changed();
                    Out("Added perk '" + id + "' Q" + q + " (" + Localization.Get(id) + ") to " + gunLabel + " (" + GunData.GetPerks(iv).Count + "/" + GunData.PerkLimit(iv) + "). Active now.");
                }
                else if (r == GunData.AddPerkResult.Full) Out("Refused: " + gunLabel + " has " + GunData.GetPerks(iv).Count + " perks and " + GunData.PerkLimit(iv) + " mod slots - every perk slot is used.");
                else Out("Refused: '" + id + "' is not a valid perk id (letters, digits, _ - . only).");
                return;
            }

            if (sub == "skin")
            {
                string id = _params[1].Trim();
                if (id.ToLowerInvariant() == "none")
                {
                    string had = GunData.GetSkin(iv);
                    if (!GunData.ClearSkin(iv)) { Out(gunLabel + " has no skin - nothing to remove."); return; }
                    inv.Changed();
                    SkinRuntime.ScheduleDrawPasses();
                    Out("Removed skin '" + had + "' from " + gunLabel + ". It shows stock again and can take a new skin.");
                    return;
                }
                SkinInfo skin = SkinRegistry.Get(id);
                if (skin == null)
                {
                    Out("Refused: '" + id + "' is not a loaded skin. Skin ids are case-sensitive.\n" + SkinList());
                    return;
                }
                string oldSkin = GunData.GetSkin(iv);
                GunData.SetSkin(iv, skin.Id);
                inv.Changed();
                SkinRuntime.ScheduleDrawPasses();
                Out("Skin '" + skin.Id + "' (" + skin.DisplayName + ") set on " + gunLabel
                    + (oldSkin != null ? " (was '" + oldSkin + "')" : "")
                    + ". The textures build in the background; the gun switches over within a few seconds.");
                return;
            }

            if (sub == "parts")
            {
                string report = PartsReport(player, inv, iv, gunLabel);
                Out(report);   // Out also writes it to the log
                return;
            }

            // info
            List<PerkEntry> perks = GunData.GetPerks(iv);
            string name = GunData.GetName(iv);
            StringBuilder sb = new StringBuilder();
            sb.Append("Gun: ").Append(gunLabel);
            sb.Append("\n  Quality: ").Append(iv.Quality).Append("  Mod slots: ").Append(GunData.PerkLimit(iv));
            sb.Append("  Durability used: ").Append(iv.UseTimes.ToString("0.#")).Append("/").Append(iv.MaxUseTimes);
            sb.Append("\n  Name: ").Append(name != null ? "\"" + name + "\"" : "(none)");
            sb.Append("\n  Perks (").Append(perks.Count).Append("/").Append(GunData.PerkLimit(iv)).Append("): ");
            if (perks.Count == 0) sb.Append("(none)");
            for (int i = 0; i < perks.Count; i++)
            {
                PerkEntry p = perks[i];
                sb.Append("\n    ").Append(i + 1).Append(". ").Append(p.Id).Append(" Q").Append(p.Quality);
                if (!Perks.IsPerk(p.Id)) sb.Append(" - NOT A LOADED PERK, no effect");
                else sb.Append(" - ").Append(Localization.Get(p.Id)).Append(Perks.IsCorrupted(p.Id) ? " (corrupted)" : "");
            }
            sb.Append("\n  au.perks = ").Append(GunData.GetPerksRaw(iv) ?? "(none)");
            string skinId = GunData.GetSkin(iv);
            SkinInfo si = SkinRegistry.Get(skinId);
            sb.Append("\n  Skin: ");
            if (skinId == null) sb.Append("(none)");
            else if (si == null) sb.Append(skinId).Append(" - NOT A LOADED SKIN, shows stock");
            else sb.Append(skinId).Append(" - ").Append(si.DisplayName).Append(si.Custom ? " (custom)" : si.Hidden ? " (hidden test skin)" : " (starter)");
            Out(sb.ToString());
        }

        private static string StockPartsModPath()
        {
            // FilePath = <mod>\Skins\stock_parts.txt, set at startup
            return System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(StockParts.FilePath));
        }

        // Every renderer of the held model and its first-person copies, with each material slot's state.
        private static string PartsReport(EntityPlayerLocal player, Inventory inv, ItemValue iv, string gunLabel)
        {
            string skinId = GunData.GetSkin(iv);
            StringBuilder sb = new StringBuilder();
            sb.Append("Parts of ").Append(gunLabel).Append(" - skin: ").Append(skinId ?? "(none - everything is stock)");
            sb.Append("\n  Stock-parts words: ").Append(StockParts.Words.Length > 0 ? string.Join(", ", StockParts.Words) : "(none)");
            List<UnityEngine.Transform> targets = SkinRuntime.Targets(player, inv.GetHoldingItemTransform());
            if (targets.Count == 0) sb.Append("\n  (no model in hand yet - draw the gun and try again)");
            int skinned = 0, stock = 0;
            foreach (UnityEngine.Transform root in targets)
            {
                sb.Append("\n  Model '").Append(root.name).Append("':");
                UnityEngine.Renderer[] rs = root.GetComponentsInChildren<UnityEngine.Renderer>(true);
                foreach (UnityEngine.Renderer r in rs)
                {
                    if (r == null) continue;
                    sb.Append("\n    ").Append(RelPath(r.transform, root));
                    if (!r.gameObject.activeInHierarchy) sb.Append(" [hidden]");
                    string why = SkinApply.KeepReason(r, root);
                    AuSkinMark mark = SkinApply.FindMark(r);
                    UnityEngine.Material[] orig = mark != null ? mark.Originals : r.sharedMaterials;
                    UnityEngine.Material[] cur = r.sharedMaterials;
                    if (why != null) { sb.Append(" -> STOCK: ").Append(why); stock++; }
                    for (int i = 0; i < orig.Length; i++)
                    {
                        UnityEngine.Material o = orig[i];
                        UnityEngine.Texture t = (o != null && o.HasProperty("_MainTex")) ? o.GetTexture("_MainTex") : null;
                        sb.Append("\n      slot ").Append(i).Append(" texture '").Append(t != null ? t.name : "-").Append("': ");
                        if (why != null) { sb.Append("stock"); continue; }
                        bool on = mark != null && mark.Made != null && i < mark.Made.Length && mark.Made[i] != null && i < cur.Length && cur[i] == mark.Made[i];
                        string slotWhy = SkinApply.SlotKeepReason(o);
                        if (on) { sb.Append("SKINNED"); skinned++; }
                        else if (!SkinApply.Eligible(o)) { sb.Append("stock (glass/transparent or no texture - never skinned)"); stock++; }
                        else if (slotWhy != null) { sb.Append("STOCK: ").Append(slotWhy); stock++; }
                        else if (skinId == null) { sb.Append("stock (gun has no skin)"); stock++; }
                        else { sb.Append("stock for now (texture still building?)"); stock++; }
                    }
                }
            }
            sb.Append("\n  Total: ").Append(skinned).Append(" skinned, ").Append(stock).Append(" stock. Edit Skins\\stock_parts.txt, then 'au parts reload'.");
            return sb.ToString();
        }

        private static string RelPath(UnityEngine.Transform t, UnityEngine.Transform root)
        {
            string p = t.name;
            for (UnityEngine.Transform x = t.parent; x != null && x != root; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        private static string SkinList()
        {
            List<SkinInfo> all = SkinRegistry.All();
            StringBuilder sb = new StringBuilder();
            sb.Append("  Loaded skins (").Append(SkinRegistry.StarterCount).Append(" starter, ").Append(SkinRegistry.CustomCount).Append(" custom):");
            if (all.Count == 0) sb.Append(" (none)");
            foreach (SkinInfo s in all)
                sb.Append("\n    ").Append(s.Id).Append(" - ").Append(s.DisplayName).Append(s.Custom ? " (custom)" : "");
            sb.Append("\n  Hidden test skin: ").Append(SkinRegistry.IdentityId).Append(" (copies the stock texture; should look exactly like stock)");
            return sb.ToString();
        }
    }
}
