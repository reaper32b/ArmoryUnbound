using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using HarmonyLib;
using UnityEngine;

namespace ArmoryUnbound
{
    // 1.1.0 tool perks (AJ 2026-11-02). Ten of the eleven are plain XML (item_modifiers.xml). Earthshaker needs this:
    // the block a hit lands on, which the onSelfDamagedBlock event doesn't carry (ItemActionAttack.Hit, V2.6 b14, only
    // sets MinEventContext.ItemValue / BlockValue / Tags before firing it). Hit is static and fires the event
    // synchronously, so a prefix that records hitInfo.hit.blockPos/blockFace is valid for the whole call.
    [HarmonyPatch(typeof(ItemActionAttack), "Hit")]
    public static class Patch_ItemActionAttack_Hit_BlockPos
    {
        [ThreadStatic] public static bool Valid;
        [ThreadStatic] public static Vector3i Pos;
        [ThreadStatic] public static BlockFace Face;

        public static void Prefix(WorldRayHitInfo hitInfo)
        {
            Valid = false;
            if (hitInfo == null || !hitInfo.bHitValid) return;
            Pos = hitInfo.hit.blockPos;
            Face = hitInfo.hit.blockFace;
            Valid = true;
        }

        public static void Postfix()
        {
            Valid = false;
        }
    }

    // Item tags that make an item a "tool" for tool-only perks (auToolOnly): UL tools (tool), butchering blades
    // (toolButcher: knives, cleavers, machetes) and sledgehammers (sledge).
    public static class ToolTags
    {
        public static readonly FastTags<TagGroup.Global> Any = FastTags<TagGroup.Global>.Parse("tool,toolButcher,sledge");

        public static bool IsTool(ItemValue iv)
        {
            return iv != null && iv.ItemClass != null && GunData.IsSupported(iv) && iv.ItemClass.HasAnyTags(Any);
        }
    }
}

// Earthshaker: on a block hit, the 4 blocks around it (in the plane of the face that was hit) take a share of the
// held item's own block damage against each of them. Share = from..to by the perk's quality (PerkContext.Current,
// set by Perks' FireEvent postfix). XML: <triggered_effect trigger="onSelfDamagedBlock" action="AuAreaBlockDamage, ArmoryUnbound" from="0.3" to="0.6"/>
// MinEventActionBase.ParseAction resolves action= with ReflectionHelpers.GetTypeWithPrefix("MinEventAction", name),
// so the class is MinEventActionAuAreaBlockDamage in the global namespace. Single player / local player only.
// Never inside trader areas; air is skipped; its own damage never re-triggers it (guard + DamageBlock fires no event).
public class MinEventActionAuAreaBlockDamage : MinEventActionBase
{
    public const string XmlName = "AuAreaBlockDamage, ArmoryUnbound";
    private float from = 0.3f, to = 0.6f;
    [ThreadStatic] private static bool busy;

    public override bool ParseXmlAttribute(XAttribute _attribute)
    {
        string n = _attribute.Name.LocalName;
        float v;
        if (n == "from" && float.TryParse(_attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { from = v; return true; }
        if (n == "to" && float.TryParse(_attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { to = v; return true; }
        return base.ParseXmlAttribute(_attribute);
    }

    public override void Execute(MinEventParams _params)
    {
        if (busy || !ArmoryUnbound.Patch_ItemActionAttack_Hit_BlockPos.Valid) return;
        EntityAlive self = _params == null ? null : _params.Self;
        World world = GameManager.Instance == null ? null : GameManager.Instance.World;
        if (self == null || world == null || !(self is EntityPlayerLocal)) return;
        int q = ArmoryUnbound.PerkContext.Current;
        if (q <= 0) return;
        ItemValue iv = _params.ItemValue != null && !_params.ItemValue.IsEmpty() ? _params.ItemValue : self.inventory.holdingItemItemValue;
        if (iv == null || iv.ItemClass == null || iv.ItemClass.Actions == null || iv.ItemClass.Actions.Length == 0) return;
        float share = Mathf.Lerp(from, to, Mathf.Clamp01((q - 1) / 89f));
        Vector3i c = ArmoryUnbound.Patch_ItemActionAttack_Hit_BlockPos.Pos;
        busy = true;
        try
        {
            foreach (Vector3i off in Neighbours(ArmoryUnbound.Patch_ItemActionAttack_Hit_BlockPos.Face))
            {
                Vector3i pos = c + off;
                if (world.IsWithinTraderArea(pos)) continue;
                BlockValue bv = world.GetBlock(pos);
                if (bv.isair || bv.Block == null) continue;
                float dmg = BlockDamageOf(iv, bv, self);
                int d = (int)(dmg * share);
                if (d < 1) continue;
                bv.Block.DamageBlock(world, 0, pos, bv, d, self.entityId, null, false, false);
            }
        }
        catch (Exception e)
        {
            Log.Warning("[ArmoryUnbound] Earthshaker: " + e.Message);
        }
        finally { busy = false; }
    }

    private static float BlockDamageOf(ItemValue iv, BlockValue bv, EntityAlive self)
    {
        ItemAction a = iv.ItemClass.Actions[0];
        ItemActionDynamic dyn = a as ItemActionDynamic;
        if (dyn != null) return dyn.GetDamageBlock(iv, bv, self, 0);
        ItemActionAttack atk = a as ItemActionAttack;
        if (atk != null) return atk.GetDamageBlock(iv, bv, self, 0);
        return 0f;
    }

    // The 4 blocks around the hit one, in the plane of the hit face (a wall hit spreads sideways and up/down;
    // a floor or ceiling hit spreads flat).
    private static readonly Vector3i[] FlatXZ = { new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0), new Vector3i(0, 0, 1), new Vector3i(0, 0, -1) };
    private static readonly Vector3i[] WallXY = { new Vector3i(1, 0, 0), new Vector3i(-1, 0, 0), new Vector3i(0, 1, 0), new Vector3i(0, -1, 0) };
    private static readonly Vector3i[] WallZY = { new Vector3i(0, 0, 1), new Vector3i(0, 0, -1), new Vector3i(0, 1, 0), new Vector3i(0, -1, 0) };

    private static Vector3i[] Neighbours(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.North:
            case BlockFace.South: return WallXY;
            case BlockFace.West:
            case BlockFace.East: return WallZY;
            default: return FlatXZ;
        }
    }
}
