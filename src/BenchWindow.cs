using System;
using System.Collections.Generic;
using System.Text;

namespace ArmoryUnbound
{
    // Gunsmith window (scope item 9). Lives inside the bench's window group "workstation_auGunsmithBench",
    // next to the normal crafting windows (perk parts are crafted there). The group keeps the vanilla
    // XUiC_WorkstationWindowGroup controller: GameManager.workstationOpened casts to it, and it handles the
    // bench lock, sounds and closing when the bench is destroyed.
    //
    // Slots are temporary: nothing is stored in the bench. Whenever the window closes (Esc, hit, death,
    // bench destroyed, quit - GameManager.Disconnect closes all windows before the player is saved),
    // all three slots (gun, perk part, skin item) go back to the player's inventory, or drop at their feet if it's full.
    //
    // XML: controller="ArmoryUnbound.AuGunsmithWindow, ArmoryUnbound" - the game inserts "XUiC_" before the class name.
    public class XUiC_AuGunsmithWindow : XUiController
    {
        public const int MaxNameLength = 32;
        public const string RepairKitName = "resourceRepairKit";   // the standard kit (UL: RepairTools on 63 items); in the part slot = remove mode

        private XUiController btnPrev, btnNext;   // remove mode: pick the perk by position (duplicates are separate entries)
        private int removeIndex;
        private XUiC_AuSlotGrid gunGrid, partGrid, skinGrid;
        private XUiV_Texture swatch;
        private string swatchSkin;            // skin id the swatch currently shows
        private static readonly Dictionary<string, UnityEngine.Texture2D> swatches = new Dictionary<string, UnityEngine.Texture2D>();
        private XUiC_TextInput nameInput;
        private XUiController btnApply, btnConfirm, btnCancel;
        private bool refsReady, closing, confirming, suppressNameEvent;
        private ItemStack lastGunStack;       // to notice a different gun in the slot
        private bool nameLockedShown;
        private string status = "";
        private Plan pending;

        private sealed class Plan
        {
            public ItemStack GunStack;   // slot objects at plan time (a swap replaces them)
            public ItemStack PartStack;
            public int PartCount;
            public ItemStack SkinStack;
            public int SkinCount;
            public ItemValue Gun;
            public string ExistingName;
            public string NewName;       // null = name not changing
            public string PerkId;        // null = no perk being added
            public int PerkQuality;      // quality of the part in the slot (= the new perk's quality)
            public string PerkProblem;   // why the part / kit in the slot can't be used, or null
            public string PerksRaw;      // the gun's au.perks when planned (a change in between cancels the confirm)
            public int PerkCount, PerkLimit;
            public bool RemoveMode;      // a Repair Kit is in the part slot
            public int RemoveIndex = -1; // which perk the kit takes off (0-based), -1 = none
            public PerkEntry RemoveEntry;
            public ItemClass RemovePart; // the part that comes back, or null if the perk has none
            public string ExistingSkin;  // the gun's skin id, or null
            public string SkinId;        // null = no skin being applied
            public string SkinProblem;   // why the skin item in the slot can't be used, or null
            public string Problem;       // why nothing can be applied, or null
            public bool RemoveSkin;      // a Paint Removal Kit is in the skin slot and the gun has a skin
            public bool Any { get { return NewName != null || PerkId != null || SkinId != null || RemoveSkin || RemoveIndex >= 0; } }
        }

        public override void Init()
        {
            base.Init();
            EnsureRefs();
        }

        private void EnsureRefs()
        {
            if (refsReady) return;
            gunGrid = GetChildById("auGunGrid") as XUiC_AuSlotGrid;
            partGrid = GetChildById("auPartGrid") as XUiC_AuSlotGrid;
            skinGrid = GetChildById("auSkinGrid") as XUiC_AuSlotGrid;
            XUiController sw = GetChildById("auSkinSwatch");
            swatch = sw == null ? null : sw.ViewComponent as XUiV_Texture;
            if (skinGrid == null || swatch == null)
                Log.Warning("[ArmoryUnbound] Gunsmith window: skin slot or swatch missing (skinGrid=" + (skinGrid != null) + " swatch=" + (swatch != null) + ") - skins can't be applied at this bench");
            nameInput = GetChildById("auName") as XUiC_TextInput;
            btnApply = GetChildById("btnAuApply");
            btnConfirm = GetChildById("btnAuConfirm");
            btnCancel = GetChildById("btnAuCancel");
            if (gunGrid == null || partGrid == null || nameInput == null || btnApply == null || btnConfirm == null || btnCancel == null)
            {
                Log.Error("[ArmoryUnbound] Gunsmith window: missing controls (gunGrid=" + (gunGrid != null) + " partGrid=" + (partGrid != null)
                    + " name=" + (nameInput != null) + " apply=" + (btnApply != null) + " confirm=" + (btnConfirm != null) + " cancel=" + (btnCancel != null) + ")");
                return;
            }
            nameInput.SupportBbCode = false;
            nameInput.OnChangeHandler += OnNameChanged;
            btnApply.OnPress += OnApplyPressed;
            btnConfirm.OnPress += OnConfirmPressed;
            btnCancel.OnPress += OnCancelPressed;
            btnPrev = GetChildById("btnAuPrev");
            btnNext = GetChildById("btnAuNext");
            if (btnPrev != null) btnPrev.OnPress += (s, b) => StepRemove(-1);
            if (btnNext != null) btnNext.OnPress += (s, b) => StepRemove(1);
            if (btnPrev == null || btnNext == null)
                Log.Warning("[ArmoryUnbound] Gunsmith window: perk picker arrows missing (prev=" + (btnPrev != null) + " next=" + (btnNext != null) + ") - a Repair Kit removes the first perk only");
            refsReady = true;
        }

        private XUiC_ItemStack GunSlot { get { return gunGrid == null ? null : gunGrid.Slot; } }
        private XUiC_ItemStack PartSlot { get { return partGrid == null ? null : partGrid.Slot; } }
        private XUiC_ItemStack SkinSlot { get { return skinGrid == null ? null : skinGrid.Slot; } }

        // The gun currently in the gun slot, or null.
        public ItemValue CurrentGun()
        {
            XUiC_ItemStack s = GunSlot;
            if (s == null || s.ItemStack == null || s.ItemStack.IsEmpty()) return null;
            return GunData.IsGun(s.ItemStack.itemValue) ? s.ItemStack.itemValue : null;
        }

        public override void OnOpen()
        {
            base.OnOpen();
            EnsureRefs();
            closing = false;
            confirming = false;
            pending = null;
            status = "";
            ReturnAll(); // belt and braces: slots are always empty here unless a previous close failed
            lastGunStack = null;
            removeIndex = 0;
            nameLockedShown = false;
            SetNameText("");
            if (nameInput != null) nameInput.Enabled = true;
            ApplyMode();
            Refresh();
        }

        // ---------------- Craft / Gunsmith tabs (UL add-on 1.4.0) ----------------
        // Under Undead Legacy the bench's three columns fill the screen, so the Gunsmith panel sits over the middle
        // column (UL's recipe info) and a tab button in the header (XUiC_AuGunsmithTab) switches between the two.
        // Without a tab window in the group (vanilla look) the panel is always shown, as before.
        public static bool GunsmithMode;   // remembered for the session; starts in Craft mode

        public bool HasTab
        {
            get { return WindowGroup != null && WindowGroup.Controller != null && WindowGroup.Controller.GetChildByType<XUiC_AuGunsmithTab>() != null; }
        }

        public bool Shown
        {
            get { return !HasTab || GunsmithMode; }
        }

        public void ApplyMode()
        {
            bool show = Shown;
            if (ViewComponent != null) ViewComponent.IsVisible = show;
            if (!show)
            {
                confirming = false;
                pending = null;
                ReturnAll();   // nothing stays hidden in the slots while crafting
            }
        }

        public void ToggleMode()
        {
            GunsmithMode = !GunsmithMode;
            status = "";
            ApplyMode();
            Refresh();
        }

        public override void OnClose()
        {
            closing = true;
            confirming = false;
            pending = null;
            ReturnAll();
            SetNameText("");
            base.OnClose();
            closing = false;
        }

        // ---------------- returning items ----------------

        private void ReturnAll()
        {
            ReturnSlot(GunSlot);
            ReturnSlot(PartSlot);
            ReturnSlot(SkinSlot);
        }

        private void ReturnSlot(XUiC_ItemStack slot)
        {
            if (slot == null) return;
            ItemStack st = slot.ItemStack;
            if (st == null || st.IsEmpty()) return;
            ItemStack give = st.Clone();
            bool wasClosing = closing;
            closing = true; // no preview refresh while emptying
            slot.ItemStack = ItemStack.Empty.Clone();
            closing = wasClosing;
            Give(give);
        }

        private void Give(ItemStack stack)
        {
            string label = stack.itemValue != null && stack.itemValue.ItemClass != null ? stack.itemValue.ItemClass.Name + " x" + stack.count : "?";
            try
            {
                XUiM_PlayerInventory inv = xui.PlayerInventory;
                if (inv == null) throw new InvalidOperationException("no player inventory");
                inv.AddItem(stack); // leaves any part it couldn't place in stack.count
                if (stack.count > 0)
                {
                    inv.DropItem(stack);
                    Log.Out("[ArmoryUnbound] Gunsmith: inventory full, dropped " + label + " at the player's feet");
                }
            }
            catch (Exception e)
            {
                Log.Error("[ArmoryUnbound] Gunsmith: could not return " + label + " to the player: " + e);
            }
        }

        // ---------------- events ----------------

        public void OnSlotChanged()
        {
            if (closing) return;
            if (confirming) { confirming = false; pending = null; status = L("auUIStatusChanged"); }
            Refresh();
        }

        private void OnNameChanged(XUiController _sender, string _text, bool _changeFromCode)
        {
            if (suppressNameEvent || closing) return;
            string clean = StripBrackets(_text);
            if (clean != _text) { SetNameText(clean); }
            if (confirming) { confirming = false; pending = null; status = L("auUIStatusChanged"); }
            Refresh();
        }

        private void OnApplyPressed(XUiController _sender, int _mouseButton)
        {
            if (confirming) return;
            Plan p = MakePlan();
            if (!p.Any)
            {
                status = p.Problem ?? L("auUIStatusNothing");
                Refresh();
                return;
            }
            pending = p;
            confirming = true;
            status = "";
            Refresh();
        }

        private void OnCancelPressed(XUiController _sender, int _mouseButton)
        {
            if (!confirming) return;
            confirming = false;
            pending = null;
            status = L("auUIStatusCancelled");
            Refresh();
        }

        // Remove mode arrows: step through the gun's perks by position (wraps around), so two copies of one perk are separate picks.
        private void StepRemove(int delta)
        {
            if (confirming || closing) return;
            ItemValue gun = CurrentGun();
            int n = gun == null ? 0 : GunData.GetPerks(gun).Count;
            if (n == 0) return;
            removeIndex = (((removeIndex + delta) % n) + n) % n;
            status = "";
            Refresh();
        }

        private void OnConfirmPressed(XUiController _sender, int _mouseButton)
        {
            if (!confirming || pending == null) return;
            Plan was = pending;
            confirming = false;
            pending = null;
            Plan now = MakePlan();
            if (!SamePlan(was, now) || !now.Any)
            {
                status = L("auUIStatusChanged");
                Refresh();
                return;
            }
            try
            {
                Apply(now);
            }
            catch (Exception e)
            {
                status = L("auUIStatusChanged");
                Log.Error("[ArmoryUnbound] Gunsmith: apply failed, nothing changed: " + e);
            }
            Refresh();
        }

        // ---------------- the permanent change ----------------

        private void Apply(Plan p)
        {
            XUiC_ItemStack gunSlot = GunSlot, partSlot = PartSlot;
            ItemStack gun = gunSlot.ItemStack.Clone();
            ItemValue iv = gun.itemValue;

            // Check everything before writing anything.
            if (p.NewName != null && GunData.HasName(iv)) throw new InvalidOperationException("gun already named");
            ItemStack part = null;
            if (GunData.GetPerksRaw(iv) != p.PerksRaw) throw new InvalidOperationException("the gun's perks changed");
            if (p.PerkId != null)
            {
                List<PerkEntry> perks = GunData.GetPerks(iv);
                if (perks.Count >= GunData.PerkLimit(iv) || !Perks.IsPerk(p.PerkId))
                    throw new InvalidOperationException("perk no longer valid");
                part = partSlot.ItemStack.Clone();
                if (part.IsEmpty() || part.count < 1 || PerkOf(part.itemValue) != p.PerkId || PartQuality(part.itemValue) != p.PerkQuality)
                    throw new InvalidOperationException("perk part missing");
            }
            if (p.RemoveIndex >= 0)
            {
                List<PerkEntry> perks = GunData.GetPerks(iv);
                if (p.RemoveIndex >= perks.Count || perks[p.RemoveIndex].Id != p.RemoveEntry.Id || perks[p.RemoveIndex].Quality != p.RemoveEntry.Quality)
                    throw new InvalidOperationException("perk to remove changed");
                part = partSlot.ItemStack.Clone();
                if (part.IsEmpty() || part.count < 1 || !IsRepairKit(part.itemValue))
                    throw new InvalidOperationException("repair kit missing");
            }
            XUiC_ItemStack skinSlot = SkinSlot;
            ItemStack skinItem = null;
            if (p.SkinId != null)
            {
                if (GunData.HasSkin(iv) || !SkinRegistry.IsSkin(p.SkinId)) throw new InvalidOperationException("skin no longer valid");
                skinItem = skinSlot == null ? null : skinSlot.ItemStack.Clone();
                if (skinItem == null || skinItem.IsEmpty() || skinItem.count < 1 || SkinOf(skinItem.itemValue) != p.SkinId)
                    throw new InvalidOperationException("skin item missing");
            }
            string removedSkin = null;
            if (p.RemoveSkin)
            {
                removedSkin = GunData.GetSkin(iv);
                if (removedSkin == null) throw new InvalidOperationException("gun has no skin to remove");
                skinItem = skinSlot == null ? null : skinSlot.ItemStack.Clone();
                if (skinItem == null || skinItem.IsEmpty() || skinItem.count < 1 || !IsRemover(skinItem.itemValue))
                    throw new InvalidOperationException("paint removal kit missing");
            }

            string gunLabel = iv.ItemClass.Name;
            StringBuilder done = new StringBuilder();
            if (p.NewName != null)
            {
                GunData.SetName(iv, p.NewName);
                done.Append("engraved \"").Append(p.NewName).Append("\"");
            }
            if (p.PerkId != null)
            {
                GunData.AddPerk(iv, p.PerkId, p.PerkQuality); // checked above: a free perk slot, valid id; runs at the part's quality
                if (done.Length > 0) done.Append(", ");
                done.Append("added perk ").Append(p.PerkId).Append(" Q").Append(p.PerkQuality);
            }
            ItemStack giveBack = null;
            if (p.RemoveIndex >= 0)
            {
                PerkEntry gone;
                GunData.RemovePerkAt(iv, p.RemoveIndex, out gone); // checked above: same entry at that position
                if (done.Length > 0) done.Append(", ");
                done.Append("removed perk ").Append(gone.Id).Append(" Q").Append(gone.Quality).Append(" (#").Append(p.RemoveIndex + 1).Append(')');
                giveBack = MakePart(gone);
                done.Append(giveBack != null ? ", part " + giveBack.itemValue.ItemClass.Name + " Q" + giveBack.itemValue.Quality + " given back" : ", no part to give back");
            }
            if (p.SkinId != null)
            {
                GunData.SetSkin(iv, p.SkinId); // checked above: gun has no skin, skin is loaded
                if (done.Length > 0) done.Append(", ");
                done.Append("applied skin ").Append(p.SkinId);
            }
            if (p.RemoveSkin)
            {
                GunData.ClearSkin(iv); // checked above: gun has a skin, kit is in the slot
                if (done.Length > 0) done.Append(", ");
                done.Append("removed skin ").Append(removedSkin);
            }
            closing = true; // one refresh at the end, not one per slot write
            try
            {
                gunSlot.ForceSetItemStack(gun);
                if (part != null)
                {
                    part.count -= 1;
                    partSlot.ForceSetItemStack(part.count > 0 ? part : ItemStack.Empty.Clone());
                }
                if (skinItem != null)
                {
                    skinItem.count -= 1;
                    skinSlot.ForceSetItemStack(skinItem.count > 0 ? skinItem : ItemStack.Empty.Clone());
                }
            }
            finally { closing = false; }
            if (giveBack != null) Give(giveBack);   // the removed perk's part, at its quality: inventory, or at the player's feet if full

            Log.Out("[ArmoryUnbound] Gunsmith: " + done + " on " + gunLabel + " (perks now " + (GunData.GetPerksRaw(iv) ?? "none") + ", " + GunData.GetPerks(iv).Count + "/" + GunData.PerkLimit(iv) + ")"
                + (part != null ? (p.RemoveIndex >= 0 ? " (used 1 repair kit)" : " (used 1 perk part)") : "")
                + (skinItem != null ? (p.RemoveSkin ? " (used 1 paint removal kit)" : " (used 1 skin item)") : ""));
            if (p.RemoveIndex >= 0) removeIndex = 0;
            lastGunStack = null; // re-read the (now named) gun
            SetNameText("");
            status = L("auUIStatusApplied");
        }

        // ---------------- plan + preview ----------------

        private Plan MakePlan()
        {
            Plan p = new Plan();
            XUiC_ItemStack gs = GunSlot, ps = PartSlot, ss = SkinSlot;
            p.GunStack = gs == null ? null : gs.ItemStack;
            p.PartStack = ps == null ? null : ps.ItemStack;
            p.PartCount = p.PartStack == null ? 0 : p.PartStack.count;
            p.SkinStack = ss == null ? null : ss.ItemStack;
            p.SkinCount = p.SkinStack == null ? 0 : p.SkinStack.count;
            if (p.GunStack == null || p.GunStack.IsEmpty() || !GunData.IsGun(p.GunStack.itemValue))
            {
                p.Problem = L("auUIStatusNoGun");
                return p;
            }
            p.Gun = p.GunStack.itemValue;
            p.ExistingName = GunData.GetName(p.Gun);
            if (p.ExistingName == null)
            {
                string typed = CleanName(nameInput == null ? "" : nameInput.Text);
                if (typed.Length > 0) p.NewName = typed;
            }
            p.PerksRaw = GunData.GetPerksRaw(p.Gun);
            List<PerkEntry> have = GunData.GetPerks(p.Gun);
            p.PerkCount = have.Count;
            p.PerkLimit = GunData.PerkLimit(p.Gun);
            if (p.PartStack != null && !p.PartStack.IsEmpty())
            {
                if (IsRepairKit(p.PartStack.itemValue))
                {
                    // Remove mode: the kit takes off the perk picked with the arrows (by position).
                    p.RemoveMode = true;
                    if (have.Count == 0) p.PerkProblem = L("auUITipNoPerks");
                    else
                    {
                        int idx = removeIndex < 0 || removeIndex >= have.Count ? 0 : removeIndex;
                        p.RemoveIndex = idx;
                        p.RemoveEntry = have[idx];
                        p.RemovePart = Perks.PartFor(have[idx].Id);
                    }
                }
                else
                {
                    string perkId = PerkOf(p.PartStack.itemValue);
                    if (perkId == null) p.PerkProblem = L("auUITipOnlyParts");
                    else if (have.Count >= p.PerkLimit) p.PerkProblem = string.Format(L("auUIPerkFull"), have.Count, p.PerkLimit);
                    else
                    {
                        p.PerkId = perkId;
                        p.PerkQuality = PartQuality(p.PartStack.itemValue);
                    }
                }
            }
            p.ExistingSkin = GunData.GetSkin(p.Gun);
            if (p.SkinStack != null && !p.SkinStack.IsEmpty())
            {
                string skinId = SkinOf(p.SkinStack.itemValue);
                if (IsRemover(p.SkinStack.itemValue))
                {
                    if (p.ExistingSkin == null) p.SkinProblem = L("auUITipNoSkin");
                    else p.RemoveSkin = true;
                }
                else if (skinId == null) p.SkinProblem = L("auUITipOnlySkins");
                else if (p.ExistingSkin != null) p.SkinProblem = L("auUITipGunSkinned");
                else p.SkinId = skinId;
            }
            if (!p.Any) p.Problem = p.PerkProblem ?? p.SkinProblem ?? L("auUIStatusNothing");
            return p;
        }

        private static bool SamePlan(Plan a, Plan b)
        {
            return ReferenceEquals(a.GunStack, b.GunStack) && ReferenceEquals(a.PartStack, b.PartStack) && a.PartCount == b.PartCount
                && ReferenceEquals(a.SkinStack, b.SkinStack) && a.SkinCount == b.SkinCount
                && a.NewName == b.NewName && a.PerkId == b.PerkId && a.PerkQuality == b.PerkQuality && a.PerksRaw == b.PerksRaw
                && a.RemoveIndex == b.RemoveIndex && a.RemoveEntry.Id == b.RemoveEntry.Id && a.RemoveEntry.Quality == b.RemoveEntry.Quality
                && a.SkinId == b.SkinId && a.RemoveSkin == b.RemoveSkin;
        }

        private void Refresh()
        {
            if (!refsReady) return;
            XUiC_ItemStack gs = GunSlot;
            ItemStack gunStack = gs == null ? null : gs.ItemStack;
            if (!ReferenceEquals(gunStack, lastGunStack))
            {
                // A different gun (or none): lock the name box if it's already engraved.
                lastGunStack = gunStack;
                string existing = gunStack != null && !gunStack.IsEmpty() && GunData.IsGun(gunStack.itemValue) ? GunData.GetName(gunStack.itemValue) : null;
                removeIndex = 0;   // a different gun: the perk picker starts at its first perk
                if (existing != null)
                {
                    SetNameText(existing);
                    nameInput.Enabled = false;
                    nameLockedShown = true;
                }
                else
                {
                    if (nameLockedShown) SetNameText("");
                    nameInput.Enabled = true;
                    nameLockedShown = false;
                }
            }
            Plan p = MakePlan();
            if (btnApply.ViewComponent != null) btnApply.ViewComponent.Enabled = p.Any && !confirming;
            UpdateSwatch(p.SkinId ?? (p.SkinStack != null && !p.SkinStack.IsEmpty() ? SkinOf(p.SkinStack.itemValue) : null) ?? p.ExistingSkin);
            IsDirty = true;
            RefreshBindings(true);
        }

        public override bool GetBindingValueInternal(ref string _value, string _bindingName)
        {
            switch (_bindingName)
            {
                case "au_namelabel":
                    _value = L(nameLockedShown ? "auUINameLocked" : "auUIName");
                    return true;
                case "au_preview":
                    _value = refsReady ? PreviewText(MakePlan()) : "";
                    return true;
                case "au_status":
                    _value = status ?? "";
                    return true;
                case "au_confirm":
                    _value = confirming ? "true" : "false";
                    return true;
                case "au_confirmtext":
                    _value = confirming && pending != null ? ConfirmText(pending) : "";
                    return true;
                case "au_hasswatch":
                    _value = swatchSkin != null ? "true" : "false";
                    return true;
                case "au_removemode":
                    {
                        Plan rp = refsReady ? MakePlan() : null;
                        _value = rp != null && rp.RemoveIndex >= 0 && !confirming ? "true" : "false";
                    }
                    return true;
                case "au_removepick":
                    {
                        Plan rp = refsReady ? MakePlan() : null;
                        _value = rp != null && rp.RemoveIndex >= 0
                            ? Names.PerkLine(rp.RemoveEntry) + Names.GreyColor + "  " + (rp.RemoveIndex + 1) + "/" + rp.PerkCount + "[-]"
                            : "";
                    }
                    return true;
            }
            return base.GetBindingValueInternal(ref _value, _bindingName);
        }

        private string PreviewText(Plan p)
        {
            if (p.Gun == null) return Names.GreyColor + L("auUIStatusNoGun") + "[-]";
            StringBuilder sb = new StringBuilder();
            sb.Append(Names.GreyColor).Append(p.Gun.ItemClass.GetLocalizedItemName()).Append("[-]\n");
            sb.Append(L("auUIPreviewName")).Append(": ");
            if (p.ExistingName != null) sb.Append('"').Append(p.ExistingName).Append("\" ").Append(Names.GreyColor).Append(L("auUILocked")).Append("[-]");
            else if (p.NewName != null) sb.Append(Names.PerkColor).Append('"').Append(p.NewName).Append("\" (").Append(L("auUINew")).Append(")[-]");
            else sb.Append(Names.GreyColor).Append(L("auUINone")).Append("[-]");
            List<PerkEntry> perks = GunData.GetPerks(p.Gun);
            sb.Append('\n').Append(L("auUIPerks")).Append(" (").Append(perks.Count).Append('/').Append(p.PerkLimit).Append("):");
            if (perks.Count == 0) sb.Append(' ').Append(Names.GreyColor).Append(L("auUINone")).Append("[-]");
            for (int i = 0; i < perks.Count; i++)
            {
                sb.Append("\n  ").Append(i + 1).Append(". ").Append(Names.PerkLine(perks[i]));
                if (i == p.RemoveIndex) sb.Append(" [FF8080](").Append(L("auUIRemoveWill")).Append(")[-]");
            }
            if (p.PerkId != null)
                sb.Append('\n').Append("  + ").Append(Names.PerkLine(new PerkEntry(p.PerkId, p.PerkQuality))).Append(' ').Append(Names.PerkColor).Append('(').Append(L("auUINew")).Append(")[-]")
                  .Append('\n').Append(Names.GreyColor).Append("    ").Append(PerkText.At(p.PerkId, p.PerkQuality)).Append("[-]");
            if (p.RemoveIndex >= 0)
                sb.Append('\n').Append(Names.GreyColor).Append("  ").Append(p.RemovePart != null
                    ? string.Format(L("auUIConfirmRemovePerk"), PerkName(p.RemoveEntry.Id), p.RemoveEntry.Quality)
                    : string.Format(L("auUIConfirmRemoveNoPart"), PerkName(p.RemoveEntry.Id))).Append("[-]");
            if (p.PerkProblem != null) sb.Append('\n').Append("[FF8080]").Append(p.PerkProblem).Append("[-]");
            sb.Append('\n').Append(L("auUISkin")).Append(": ");
            if (p.RemoveSkin) sb.Append("[FF8080]").Append(SkinName(p.ExistingSkin)).Append(" (").Append(L("auUISkinRemoved")).Append(")[-]");
            else if (p.ExistingSkin != null) sb.Append(SkinName(p.ExistingSkin)).Append(' ').Append(Names.GreyColor).Append(L("auUILocked")).Append("[-]");
            else if (p.SkinId != null) sb.Append(Names.PerkColor).Append(SkinName(p.SkinId)).Append(" (").Append(L("auUINew")).Append(")[-]");
            else sb.Append(Names.GreyColor).Append(L("auUINone")).Append("[-]");
            if (p.SkinProblem != null) sb.Append('\n').Append("[FF8080]").Append(p.SkinProblem).Append("[-]");
            return sb.ToString();
        }

        private string ConfirmText(Plan p)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(L("auUIConfirm"));
            if (p.NewName != null) sb.Append("\n\n").Append(L("auUIPreviewName")).Append(": ").Append(Names.PerkColor).Append('"').Append(p.NewName).Append("\"[-]");
            if (p.PerkId != null)
            {
                sb.Append(p.NewName != null ? "\n" : "\n\n").Append(L("auUIPerk")).Append(": ").Append(Names.PerkLine(new PerkEntry(p.PerkId, p.PerkQuality)))
                  .Append('\n').Append(PerkText.At(p.PerkId, p.PerkQuality));
                if (p.PartStack != null && p.PartStack.itemValue != null && p.PartStack.itemValue.ItemClass != null)
                    sb.Append('\n').Append(Names.GreyColor).Append(string.Format(L("auUIUsesPart"), p.PartStack.itemValue.ItemClass.GetLocalizedItemName())).Append("[-]");
            }
            if (p.RemoveIndex >= 0)
            {
                sb.Append(p.NewName != null ? "\n" : "\n\n").Append(L("auUIPerk")).Append(": ").Append(Names.PerkLine(p.RemoveEntry)).Append(" [FF8080](").Append(L("auUIRemoveWill")).Append(")[-]");
                sb.Append('\n').Append(p.RemovePart != null
                    ? string.Format(L("auUIConfirmRemovePerk"), PerkName(p.RemoveEntry.Id), p.RemoveEntry.Quality)
                    : string.Format(L("auUIConfirmRemoveNoPart"), PerkName(p.RemoveEntry.Id)));
                if (p.PartStack != null && p.PartStack.itemValue != null && p.PartStack.itemValue.ItemClass != null)
                    sb.Append('\n').Append(Names.GreyColor).Append(string.Format(L("auUIUsesPart"), p.PartStack.itemValue.ItemClass.GetLocalizedItemName())).Append("[-]");
            }
            if (p.SkinId != null)
            {
                sb.Append(p.NewName != null || p.PerkId != null || p.RemoveIndex >= 0 ? "\n" : "\n\n").Append(L("auUISkin")).Append(": ").Append(Names.PerkColor).Append(SkinName(p.SkinId)).Append("[-]");
                if (p.SkinStack != null && p.SkinStack.itemValue != null && p.SkinStack.itemValue.ItemClass != null)
                    sb.Append('\n').Append(Names.GreyColor).Append(string.Format(L("auUIUsesPart"), p.SkinStack.itemValue.ItemClass.GetLocalizedItemName())).Append("[-]");
            }
            if (p.RemoveSkin)
            {
                sb.Append(p.NewName != null || p.PerkId != null || p.RemoveIndex >= 0 ? "\n" : "\n\n").Append(L("auUISkin")).Append(": ").Append("[FF8080]")
                  .Append(string.Format(L("auUIConfirmRemoveSkin"), SkinName(p.ExistingSkin))).Append("[-]");
                if (p.SkinStack != null && p.SkinStack.itemValue != null && p.SkinStack.itemValue.ItemClass != null)
                    sb.Append('\n').Append(Names.GreyColor).Append(string.Format(L("auUIUsesPart"), p.SkinStack.itemValue.ItemClass.GetLocalizedItemName())).Append("[-]");
            }
            return sb.ToString();
        }

        // ---------------- skins ----------------

        // Paint Removal Kit: an item with property AuSkinRemover (Config/items.xml).
        public static bool IsRemover(ItemValue iv)
        {
            ItemClass ic = iv == null ? null : iv.ItemClass;
            return ic != null && ic.Properties != null && ic.Properties.Contains("AuSkinRemover");
        }

        // Skin id carried by a skin item (property AuSkin), only if that skin is loaded.
        public static string SkinOf(ItemValue iv)
        {
            if (iv == null || iv.IsEmpty()) return null;
            ItemClass ic = iv.ItemClass;
            if (ic == null || ic.Properties == null || !ic.Properties.Contains("AuSkin")) return null;
            string id = ic.Properties.GetString("AuSkin");
            return SkinRegistry.IsSkin(id) ? id : null;
        }

        private static string SkinName(string id)
        {
            SkinInfo s = SkinRegistry.Get(id);
            return s != null ? s.DisplayName : id + " (" + L("auUISkinMissing") + ")";
        }

        // The pattern swatch next to the skin slot: the skin item in the slot, else the gun's own skin.
        private void UpdateSwatch(string skinId)
        {
            if (swatch == null) return;
            UnityEngine.Texture2D tex = skinId == null ? null : SwatchTexture(skinId);
            if (tex == null) skinId = null;
            if (skinId == swatchSkin) return;
            swatchSkin = skinId;
            swatch.Texture = tex;
            swatch.SetTextureDirty();
        }

        // The skin image, decoded once per session (mipmapped, GPU only) and kept for the next bench visit.
        private static UnityEngine.Texture2D SwatchTexture(string skinId)
        {
            UnityEngine.Texture2D t;
            if (swatches.TryGetValue(skinId, out t)) return t;
            t = null;
            SkinInfo s = SkinRegistry.Get(skinId);
            if (s != null && s.ImagePath != null)
            {
                try
                {
                    t = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, true, false);
                    if (!UnityEngine.ImageConversion.LoadImage(t, System.IO.File.ReadAllBytes(s.ImagePath), true))
                    {
                        UnityEngine.Object.Destroy(t);
                        t = null;
                    }
                    else
                    {
                        t.name = "auSwatch " + skinId;
                        t.wrapMode = UnityEngine.TextureWrapMode.Repeat;
                    }
                }
                catch (Exception e)
                {
                    Log.Warning("[ArmoryUnbound] Gunsmith: can't show the swatch for " + skinId + ": " + e.Message);
                    t = null;
                }
            }
            swatches[skinId] = t;   // null remembered too: no retry every refresh
            return t;
        }

        // ---------------- helpers ----------------

        private void SetNameText(string s)
        {
            if (nameInput == null) return;
            suppressNameEvent = true;
            try { nameInput.Text = s ?? ""; }
            finally { suppressNameEvent = false; }
        }

        // Perk id carried by a perk part (property AuPerk), only if it's a loaded perk.
        public static string PerkOf(ItemValue iv)
        {
            if (iv == null || iv.IsEmpty()) return null;
            ItemClass ic = iv.ItemClass;
            if (ic == null || ic.Properties == null || !ic.Properties.Contains("AuPerk")) return null;
            string id = ic.Properties.GetString("AuPerk");
            return Perks.IsPerk(id) ? id : null;
        }

        // The standard Repair Kit: in the part slot it switches the bench to remove mode.
        public static bool IsRepairKit(ItemValue iv)
        {
            ItemClass ic = iv == null || iv.IsEmpty() ? null : iv.ItemClass;
            return ic != null && ic.Name == RepairKitName;
        }

        // A part's quality is the perk's quality. A part without one (an old stackable part) counts as 1.
        public static int PartQuality(ItemValue iv)
        {
            return iv == null ? GunData.MinQuality : GunData.ClampQuality(iv.Quality);
        }

        // The part a removed perk comes back as, at the perk's quality (built like a crafted item:
        // new ItemValue(type, q, q, false, null, 1f), then Quality set again to be sure). Null if no item carries that perk.
        private static ItemStack MakePart(PerkEntry e)
        {
            ItemClass pc = Perks.PartFor(e.Id);
            if (pc == null) return null;
            int q = GunData.ClampQuality(e.Quality);
            ItemValue v = new ItemValue(pc.Id, q, q, false, null, 1f);
            v.Quality = (ushort)q;
            return new ItemStack(v, 1);
        }

        private static string PerkName(string id)
        {
            string s = Localization.Get(id);
            return string.IsNullOrEmpty(s) ? id : s;
        }

        // Square brackets would be read as colour codes wherever the name is shown.
        public static string StripBrackets(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOf('[') < 0 && s.IndexOf(']') < 0) return s;
            return s.Replace("[", "").Replace("]", "");
        }

        public static string CleanName(string s)
        {
            s = StripBrackets(s ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (s.Length > MaxNameLength) s = s.Substring(0, MaxNameLength).Trim();
            return s;
        }

        public static string L(string key)
        {
            string s = Localization.Get(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }

        public static void Tip(XUi xui, string key)
        {
            try
            {
                EntityPlayerLocal p = xui == null || xui.playerUI == null ? null : xui.playerUI.entityPlayer;
                if (p != null) GameManager.ShowTooltip(p, L(key), false, false, 0f);
            }
            catch (Exception) { }
        }
    }

    // Header tab button (UL add-on only): switches the middle column between UL's recipe info and the Gunsmith panel.
    // XML: controller="ArmoryUnbound.AuGunsmithTab, ArmoryUnbound", a button btnAuTab, label text {au_tablabel}.
    public class XUiC_AuGunsmithTab : XUiController
    {
        private XUiController button;

        public override void Init()
        {
            base.Init();
            button = GetChildById("btnAuTab");
            if (button != null) button.OnPress += OnTabPressed;
            else Log.Warning("[ArmoryUnbound] Gunsmith tab: button btnAuTab missing");
        }

        private XUiC_AuGunsmithWindow Gunsmith()
        {
            return WindowGroup == null || WindowGroup.Controller == null ? null : WindowGroup.Controller.GetChildByType<XUiC_AuGunsmithWindow>();
        }

        private void OnTabPressed(XUiController _sender, int _mouseButton)
        {
            XUiC_AuGunsmithWindow g = Gunsmith();
            if (g == null) return;
            g.ToggleMode();
            RefreshBindings(true);
        }

        public override void OnOpen()
        {
            base.OnOpen();
            XUiC_AuGunsmithWindow g = Gunsmith();
            if (g != null) g.ApplyMode();   // whichever window opens last, the mode ends up applied
            RefreshBindings(true);
        }

        public override bool GetBindingValueInternal(ref string _value, string _bindingName)
        {
            if (_bindingName == "au_tablabel")
            {
                _value = XUiC_AuGunsmithWindow.L(XUiC_AuGunsmithWindow.GunsmithMode ? "auUITabCrafting" : "auUITabGunsmith");
                return true;
            }
            return base.GetBindingValueInternal(ref _value, _bindingName);
        }
    }

    // One-slot grid for the gun and the perk part. Workstation location, so shift-click sends the item to the player.
    // XML: controller="ArmoryUnbound.AuSlotGrid, ArmoryUnbound"
    public class XUiC_AuSlotGrid : XUiC_ItemStackGrid
    {
        public override XUiC_ItemStack.StackLocationTypes StackLocation
        {
            get { return XUiC_ItemStack.StackLocationTypes.Workstation; }
        }

        public XUiC_ItemStack Slot
        {
            get { return itemControllers != null && itemControllers.Length > 0 ? itemControllers[0] : null; }
        }

        public override void OnOpen()
        {
            base.OnOpen();
            // Hook the slot-changed events the way vanilla grids do (SetStacks), keeping whatever is in the slots.
            if (itemControllers == null) return;
            ItemStack[] cur = new ItemStack[itemControllers.Length];
            for (int i = 0; i < cur.Length; i++)
            {
                ItemStack s = itemControllers[i].ItemStack;
                cur[i] = s == null ? ItemStack.Empty.Clone() : s;
            }
            SetStacks(cur);
        }

        public override void HandleSlotChangedEvent(int slotNumber, ItemStack stack)
        {
            base.HandleSlotChangedEvent(slotNumber, stack);
            XUiC_AuGunsmithWindow w = GetParentByType<XUiC_AuGunsmithWindow>();
            if (w != null) w.OnSlotChanged();
        }
    }

    // Gun slot: real guns only (same check as the "au" command).
    // XML: controller="ArmoryUnbound.AuGunStack, ArmoryUnbound"
    public class XUiC_AuGunStack : XUiC_ItemStack
    {
        public override bool CanSwap(ItemStack _stack)
        {
            if (_stack == null || _stack.IsEmpty()) return true;
            if (GunData.IsGun(_stack.itemValue)) return true;
            XUiC_AuGunsmithWindow.Tip(xui, "auUITipOnlyGuns");
            return false;
        }

        // Right-click "place one" doesn't go through CanSwap in vanilla; check it here.
        public override void HandleDropOne()
        {
            ItemStack cur = xui.dragAndDrop == null ? null : xui.dragAndDrop.CurrentStack;
            if (cur != null && !cur.IsEmpty() && !CanSwap(cur)) return;
            base.HandleDropOne();
        }
    }

    // Perk-part slot: parts whose perk is loaded (refused while every perk slot of the gun is used), or a Repair Kit
    // (remove mode; refused on a gun with no perks). XML: controller="ArmoryUnbound.AuPartStack, ArmoryUnbound"
    public class XUiC_AuPartStack : XUiC_ItemStack
    {
        public override bool CanSwap(ItemStack _stack)
        {
            if (_stack == null || _stack.IsEmpty()) return true;
            XUiC_AuGunsmithWindow w = GetParentByType<XUiC_AuGunsmithWindow>();
            ItemValue gun = w == null ? null : w.CurrentGun();
            if (XUiC_AuGunsmithWindow.IsRepairKit(_stack.itemValue))
            {
                if (gun != null && GunData.GetPerks(gun).Count == 0)
                {
                    XUiC_AuGunsmithWindow.Tip(xui, "auUITipNoPerks");
                    return false;
                }
                return true;
            }
            if (XUiC_AuGunsmithWindow.PerkOf(_stack.itemValue) == null)
            {
                XUiC_AuGunsmithWindow.Tip(xui, "auUITipOnlyParts");
                return false;
            }
            if (gun != null && GunData.GetPerks(gun).Count >= GunData.PerkLimit(gun))
            {
                XUiC_AuGunsmithWindow.Tip(xui, "auUITipGunFull");
                return false;
            }
            return true;
        }

        public override void HandleDropOne()
        {
            ItemStack cur = xui.dragAndDrop == null ? null : xui.dragAndDrop.CurrentStack;
            if (cur != null && !cur.IsEmpty() && !CanSwap(cur)) return;
            base.HandleDropOne();
        }
    }

    // Skin slot (Phase 2, scope item 7): only skin items whose skin is loaded, and never while the gun already
    // has a skin (a skin is set once). XML: controller="ArmoryUnbound.AuSkinStack, ArmoryUnbound"
    public class XUiC_AuSkinStack : XUiC_ItemStack
    {
        public override bool CanSwap(ItemStack _stack)
        {
            if (_stack == null || _stack.IsEmpty()) return true;
            XUiC_AuGunsmithWindow w = GetParentByType<XUiC_AuGunsmithWindow>();
            ItemValue gun = w == null ? null : w.CurrentGun();
            if (XUiC_AuGunsmithWindow.IsRemover(_stack.itemValue))
            {
                // the kit only makes sense on a skinned gun (no gun yet: allowed, the preview explains)
                if (gun != null && !GunData.HasSkin(gun))
                {
                    XUiC_AuGunsmithWindow.Tip(xui, "auUITipNoSkin");
                    return false;
                }
                return true;
            }
            if (XUiC_AuGunsmithWindow.SkinOf(_stack.itemValue) == null)
            {
                XUiC_AuGunsmithWindow.Tip(xui, "auUITipOnlySkins");
                return false;
            }
            if (gun != null && GunData.HasSkin(gun))
            {
                XUiC_AuGunsmithWindow.Tip(xui, "auUITipGunSkinned");
                return false;
            }
            return true;
        }

        public override void HandleDropOne()
        {
            ItemStack cur = xui.dragAndDrop == null ? null : xui.dragAndDrop.CurrentStack;
            if (cur != null && !cur.IsEmpty() && !CanSwap(cur)) return;
            base.HandleDropOne();
        }
    }
}
