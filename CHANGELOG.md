# Changelog

## 1.1.0 Beta - 2026-11-07

Melee weapons, tools and bows.

- The Gunsmith Bench now takes melee weapons (clubs, bats, blades, knuckles, spears, sledges, batons), tools (axes, pickaxes, shovels, hammers, wrenches, sickles, the prospector pick) and bows, not just guns. Anything with a quality level works; bare hands, the torch and the flashlight don't.
- Names, perks and skins work on them the same way as on guns.
- Perks that would do nothing are refused with a tip: Deep Well, Quickhands, Hungry Mag, Bloodprice, Overclock and Wildcat on melee weapons and tools; Deep Well and Hungry Mag on bows. Crossbows count as guns, as before.
- Skins on melee weapons and tools cover the head or body only: the handle, grip, cutting edge and add-ons (nails, spikes, wire, wraps) stay stock. Bows skip the string, cables and arrows. Every Undead Legacy and vanilla melee weapon and tool ships with its own mask (`Skins\Masks`), made from its 3D model. Guns keep the stock-parts word list.
- Masks are plain PNGs (white = skin, black = stock): `<item>.<texture>.png` for one item, `<texture>.png` for every item using that texture. Without a mask the whole texture is skinned. `au mask export` writes the held item's textures to `Skins\Masks\_export` to paint your own.
- First-person arms are never skinned, on any item.
- `au parts` shows which rule an item uses and which mask each texture uses.
- New console command: `au give <item> [quality] [count]` spawns any item (Undead Legacy blocks `giveself`).
- 11 tool perks, for tools only (axes, picks, shovels, hammers, wrenches, sickles, knives, machetes, sledgehammers):
  Prospector (harvest yield), Demolisher (block damage), Tireless (stamina per swing), Quick Swing (swing speed),
  Feller (damage to trees and wood), Butcher (meat, hide and bone), Steady Edge (wear per hit), Lucky Strike (chance of a
  double harvest), and three corrupted ones from hardened chests: Overdrive, Greedy and Earthshaker (hits also damage the
  blocks around the one you hit). Tool parts are crafted at the bench, found in toolboxes, garages, workbenches and POI
  chests, and sold by traders.
- Every perk part's tooltip now says what it works on.
- Modders: tag a perk `auToolOnly` for tools only, `auGunOnly` (guns only) or `auMagPerk` (magazine perk) in item_modifiers.xml to get the same refusal.

## 1.0.1 Beta - 2026-11-02

IZY (Izayo) weapon pack support, and functional parts stay stock.

- Skins no longer paint the first-person hands, gloves, forearm armour, wrist wraps and arm pouches that IZY builds into its weapon models.
- Triggers, hammers, safeties, fire selectors, catches and release buttons, bolts, charging handles, gas blocks and bipods now stay stock on every gun (EFTX, IZY and UL).
- Crossbow darts stay stock, like bullets.
- All of this is in `Skins\stock_parts.txt` (now 29 words). Tested with IZY 9mm pistols and SMGs (Makarov, dual MP9s, Scorpion EVO), the IZY Van Helsing crossbow, and EFTX.

If you edited `stock_parts.txt` yourself, compare it with the new one and copy across the words you're missing.

## 1.0.0 Beta - 2026-11-01

First public release. Undead Legacy 2.7 add-on for 7 Days to Die 2.6.

- Gunsmith Bench: researched in UL's tree and built at a tier 2 Workbench. It has a GUNSMITH tab and a CRAFTING tab.
- Engraved names, set once per gun.
- 12 perk parts, quality 1-90. The perk's strength comes from the part's quality. A gun gets one perk per mod slot, and duplicates stack.
- 8 corrupted perk parts with drawbacks, found only in tier 4-5 hardened chests.
- Perk parts are crafted at Q1 and upgraded at UL's Maintenance Station (tiers 2-4).
- A Repair Kit removes a perk and gives back its part at the same quality.
- 10 starter skins plus custom skins from your own images. A Paint Removal Kit takes a skin off.
- Perk parts and starter skins in loot and at every trader (not vending machines).
- "AU" prefix on every item name for easy searching.
- Exact perk values shown at the bench and in tooltips.
