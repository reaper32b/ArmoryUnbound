# Armory Unbound

**Version 1.0.0 Beta** - an Undead Legacy add-on for 7 Days to Die.

Forge any gun into your own unique at the Gunsmith Bench. Engrave a name, add perks from perk parts, and give it a skin.
Every gun you make is one of a kind and stays that way.

## Features

- **Gunsmith Bench.** Researched in Undead Legacy's tree and built at a tier 2 Workbench.
- **Engraved names.** Type a name once and it's on that gun for good.
- **Perks.** A gun gets one perk per mod slot. Each perk comes from a perk part, and the part's quality (1-90) decides how strong the perk is. Duplicates stack.
- **Removable perks.** Put a Repair Kit in the part slot to take a perk off. Its part comes back at the same quality, so you can upgrade it and put it back on.
- **Perk parts.** Craft them at the bench (they start at quality 1) and upgrade them at Undead Legacy's Maintenance Station. You can also find them in loot or buy them from traders.
- **Corrupted perks.** 8 stronger perks with a drawback. Found only in hardened chests (tier 4 and 5). Higher quality means a bigger upside and a smaller drawback.
- **Skins.** 10 starter patterns, plus your own images. They show on the gun in your hands, first and third person. A Paint Removal Kit scrubs a skin off.
- **"AU" tag.** Every Armory Unbound item name starts with "AU", so typing `AU` in any search box finds them all.

## Requirements

- 7 Days to Die **2.6** (tested on b14).
- **Undead Legacy 2.7** (tested on 2.7.40).
- **EAC off.** This mod includes a DLL, so the game must be launched with Easy Anti-Cheat disabled.
- 0_TFP_Harmony, which ships with the game.

Version 1.0 is for Undead Legacy only; plain vanilla isn't supported. It was tested in single player. Multiplayer and dedicated servers are untested.

## Install

1. Close the game.
2. Extract the zip into your `7 Days To Die\Mods` folder. You should end up with two folders:
   - `Mods\0-ArmoryUnbound`
   - `Mods\zzz_ArmoryUnbound_UL`

   Both are required. Keep the folder names as they are: the order they load in depends on them.
3. Start the game with EAC off. Existing saves are fine.

**Back up your save first.** This is a beta. If you uninstall mid-save, every Armory Unbound item in that world (the bench, parts and skins) disappears.

## How to use it

1. Research **Gunsmith Bench**. It's in the Ranged tab under Basic Firearms, or in the Modded tab if you use ZZZ_UL_ResearchTree. It needs a tier 2 Research Station.
2. Craft the bench at a tier 2 Workbench and place it.
3. Open the bench's **GUNSMITH** tab and put a gun in the gun slot. Then, in any combination:
   - type a name (it can never be changed afterwards),
   - add a perk part (or a Repair Kit to remove a perk; the arrows pick which one),
   - add a skin item (or a Paint Removal Kit to remove the skin).
4. Click **Apply**, check the preview, then click **Confirm**.

The bench's **CRAFTING** tab makes perk parts, skins and Paint Removal Kits.

### Perk parts

| Perk | Effect (Q1 to Q90) | Extra ingredient |
|---|---|---|
| Hard Hitter | +15-35% damage | 50 gunpowder |
| Deep Well | +25-75% magazine size | 5 duct tape |
| Quickhands | +15-40% reload speed | 5 oil |
| Ironclad | +50-150% durability | 10 more forged steel |
| Executioner | +30-90% headshot damage | 5 electrical parts |
| Second Wind | kills restore 8-25 health | 3 bandages |
| Bloodletter | hits cause bleeding, 40-100% of the time | 50 buckshot |
| Kindling | hits set the target on fire, 40-100% of the time | 20 coal |
| Can Opener | ignores 20-50% of armour; hits shred armour | 2 acid |
| Sledge | 10-30% knockdown chance per hit | 20 forged iron |
| Tar Pit | hits slow the target, 40-100% of the time | 5 glue |
| Scholar | +10-35% XP while held | 20 paper |

Every part also costs 5 forged steel, 10 mechanical parts and 4 springs. The panel and tooltips show the exact number for a part's quality.

**Upgrading:** at a Maintenance Station, each upgrade raises a part's quality by 10. You need a tier 2 station up to Q40, tier 3 up to Q70, and tier 4 up to Q90. Undead Legacy's normal success chance applies.

### Corrupted perk parts

Loot only (hardened chests, tier 4 and 5). Upgrades cost double.

| Perk | Upside | Drawback (shrinks with quality) |
|---|---|---|
| Glass Cannon | +60-100% damage | -40 to -10% durability |
| Overclock | +40-70% fire rate | +50 to +15% wear per shot |
| Hungry Mag | +100-150% magazine size | -25 to -5% reload speed |
| Deadeye | +120-200% headshot damage | -20 to -5% damage elsewhere |
| Bloodprice | +50-80% damage, kills heal 20-35 | starting a reload costs 5 to 1 health |
| Leech | hits heal 10-20% of the damage dealt | -15 to -5% max stamina while held |
| Wildcat | +35-60% damage, +25-40% fire rate | +60 to +20% recoil |
| Hellfire | hits always burn and bleed | +100 to +30% noise |

### Where to find parts and skins

- **Loot:** weapon bags, gun racks, military footlockers, gun safes and POI loot chests. The better the container, the better the odds.
- **Traders:** every trader stocks a few normal perk parts (Q1-40) and starter skins; gun traders stock more. Vending machines don't. New stock appears at a trader's next restock.
- **Crafting:** skins cost 10 paint + 5 cloth; a Paint Removal Kit costs 2 acid + 3 cloth.

## Custom skins

Put `.png` or `.jpg` images in `Mods\0-ArmoryUnbound\Skins\Custom` and restart the game. Each image becomes a craftable skin, named after its file: `red_tiger.png` becomes "AU Skin: Red Tiger".

- Square, seamless (tileable) images look best. The pattern repeats about 4 times across each gun texture.
- 1024x1024 is plenty.
- Custom skins are craft-only. They never appear in loot or at traders.
- Your images stay on your PC.

Some parts stay stock instead of being skinned: barrels, sights, scopes, grips, lasers, lights, suppressors and magazines. The list is in `Skins\stock_parts.txt`. Delete a word there to skin that part, then run `au parts reload` in the console (F1).

## Console (F1)

Run these while holding a gun:

- `au info`: shows the gun's name, perks with their qualities, and skin.
- `au parts`: lists every part of the gun and whether it's skinned or kept stock.
- `au perk` / `au skin`: list the loaded perk and skin ids.

There are also admin and test commands (`au name`, `au perk <id> [quality]`, `au skin <id>`). Type `au` on its own for the full list.

## Known notes

- Compatibility is tested with Undead Legacy 2.7.40, EFTX weapon packs and ZZZ_UL_ResearchTree. Trader mods that only resize stock work alongside it.
- The first time a gun model gets a skin, building the textures can cause a short hitch. After that they come from a cache in `%APPDATA%\7DaysToDie\ArmoryUnbound\SkinCache`.
- Gun data is stored in the gun's own item data, so a skinned and perked gun keeps everything when stored, dropped or traded.

## Bugs

Please report bugs through the bug tab on the release page. Your game log helps a lot: `%APPDATA%\7DaysToDie\logs\output_log_client__<date>.txt`. Every Armory Unbound line starts with `[ArmoryUnbound]`.

## Building from source

The C# source is in `src` (C# 5, .NET Framework csc, no project file needed):

```powershell
.\src\build.ps1 -Game 'C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die'
```

This builds `Mods\0-ArmoryUnbound\ArmoryUnbound.dll` against the game's own DLLs and Harmony.

## License

MIT - see [LICENSE](LICENSE). Undead Legacy, 7 Days to Die and any other mods mentioned belong to their authors. This project doesn't include any of their code or assets.
