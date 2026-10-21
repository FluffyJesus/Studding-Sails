# Studding Sails

A mod for **Sailwind** (BepInEx 5) that adds studding sails to square sails: extra canvas on either side of a sail for more speed in fair winds. They are rigged the way real ships did it.

<img width="1712" height="1219" alt="image" src="https://github.com/user-attachments/assets/9666ede5-e4cf-45e5-9390-a7cc4f5640ff" />


## How it works

- **Buy them at the shipyard.** Select a square sail and press **Studding: On**. The price grows with the studding sail width.
- **Set them with winches.** Every mast with studding sails gets two winches, one for each side. Crank a winch and:
  1. a boom runs out along the yard below;
  2. the studding sail is hoisted on its own small yard up to the end of the main yard, and the canvas opens.
- **Steer with them.** Set studding sails on one side only and the boat turns. Set them on both sides for straight-line speed. They work even when the main sail is furled.
- **Alive in the wind.** The studding sail yard swings on its rope, the canvas billows, the sheets lean with it.
- **Matches your boat.** The canvas takes the sail's cloth and colour, the spars the yard's wood.

## Controls

| Where | What |
|---|---|
| Shipyard: **Studding On / Off** | Add or remove studding sails on the selected sail |
| Shipyard: **Studding + / −** | Wider or narrower studding sails |
| Shipyard: **Yardarm + / −** | Longer or shorter yard ends that carry the studding sails |
| Shipyard: **Foot out / in** | Move the inner lower corner of a studding sail outwards or inwards |
| Winches at the mast | Set and take in the studding sails, port and starboard separately |
| **L** | Show or hide all studding sails on your boat |
| **F8** | Tuning window for the looks |

## Settings

In `BepInEx/config/fluffyjesus.sailwind.studdingsails.cfg`. The visual ones can also be changed live in the **F8** window.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | true | Offer studding sails at the shipyard |
| `PriceMultiplier` | 0.35 | Studding sail price as a share of the sail price |
| `ThrustMultiplier` | 1 | How hard studding sails pull (0 = looks only) |
| `TurnMultiplier` | 1 | How strongly one-sided studding sails turn the boat (0 = not at all) |
| `UseWinch` | true | Off: studding sails simply follow their sail, and **L** sets / stows them |
| `ToggleKey` / `WindowKey` | L / F8 | Keys |
| `VanillaBoats` | vanilla boat list | Boats whose winches stand with the mast's other winches; on all others they are pinned to the mast |
| `WinchOffsetX/Y/Z` | 0 | Nudge the winch if it overlaps something |
| `HeadOverlap` | 0.2 | How far a studding sail overlaps its sail (when the yard length is automatic) |
| `HalyardLength` | 0.3 | Length of the rope the studding sail yard hangs on: longer swings slower and further |
| `SwingStrength` | 5 | How much the wind swings the studding sail yard (0 = it hangs still) |
| `FootLift` | 0.5 | Gap between the studding sail's foot and the boom below |
| `LengthSlack` / `Belly` / `SideSlack` / `HemSlack` | 0.14 / 0.005 / 0.01 / 0.1 | Spare cloth: how loose the canvas hangs |
| `BoomOverlapOnYard` | 0.2 | How far the booms lie along the yard |
| `BoomOffsetZ` / `HeadOffsetZ` | 0 / 0 | Move booms or studding sail heads fore and aft |
| `UseSailTexture` | true | Use the sail's own cloth (off = plain canvas) |
| `ShowRopes` | true | Show the ropes |

## Installation

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx) for Sailwind.
2. Put `StuddingSails` into `Sailwind/BepInEx/plugins/StuddingSails/`.

## Compatibility

- **Topsail Crossyard** and **ShipyardExpansion**: work together.
- **Modded boats**: supported; the winches are pinned to the masts.
- **Saves**: older saves load fine. Without the mod, a save loads with plain sails.

## A bit of history

Studding sails (pronounced *"stuns'ls"*) were the light-weather sails of square-rigged ships from the 16th to the late 19th century. Booms were run out along the yards, and narrow sails, each on a small yard of its own, were hoisted beyond the edges of the main sails. They took a lot of work to set and were used only with the wind from behind, but they added a great deal of canvas. Clippers and men-of-war carried them to the end of the age of sail.
