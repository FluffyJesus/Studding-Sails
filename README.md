# Studding Sails

A mod for **Sailwind** that adds studding sails to square sails: extra canvas set on booms beyond the ends of the yards, for more sail area in fair winds.

<img width="1809" height="1233" alt="image" src="https://github.com/user-attachments/assets/5ce50b96-1d89-4f24-9c9d-974edb834b53" />

## Features

- **Bought at the shipyard.** Square sails get a **Studding: On/Off** button, plus **Studding +** / **Studding −** to change the width (50–200%). The price scales with the width.
- **Procedural shape.** Each studding sail is generated from the sail it belongs to:
  - the **lowest** one is a right triangle, its clew tied to the clew of the sail;
  - the **upper** ones are trapezoids, spread between their own boom and the studding boom of the yard below;
  - they grow with the width of the sail.
- **Real cloth.** They use Unity cloth with the same wind and damping as the vanilla sails, so they billow, sag and luff together with them.
- **Same canvas.** They copy the material, texture and colour of their sail, with the seams laid out to match.
- **Rigged.** Booms lie on top of the yards. Corners are lashed with blocks, the lower corners are sheeted to the yard below, and halyards lead to the yard above or to the masthead.
- **Reefing.** Studding sails are set and taken in together with their sail. When it is furled, a furled bundle stays on the boom.
- **Thrust.** Their area is added to the parent sail's power, so they do help in fair winds. Adjustable, or 0 for looks only.

## Controls

| Key | Action |
|---|---|
| **L** | Set / stow all studding sails and booms on the boat you are on |
| **F8** | Tuning window (boom overlap, gap, cloth fullness, edge slack, ropes) |

Both keys can be changed in the config.

## Installation

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx) for Sailwind.
2. Put `StuddingSails` into `Sailwind/BepInEx/plugins`.

## Configuration

`BepInEx/config/fluffyjesus.sailwind.studdingsails.cfg`:

- **[Studding]:** enable the shipyard option, price multiplier, thrust multiplier, keys.
- **[Visuals]:** the same settings as in the F8 window, plus the sail texture and ropes toggles.

## Compatibility

- **ShipyardExpansion:** sail textures chosen with it are picked up automatically.
- **Saves:** the studding sail flag and width are stored in a field of the sail save data that vanilla does not use. Without the mod, such a save loads as ordinary sails.

## A bit of history

Studding sails (pronounced *"stuns'ls"*) were the light-weather extensions of square-rigged ships from the 16th to the late 19th century. Light booms were run out along the yards, and narrow sails were hoisted on them beyond the leeches of the courses, topsails and topgallants. In a fair breeze this added a large share of extra canvas.

The lowest ones, set outside the courses, were often triangular or were spread by a boom swung out from the ship's side. The upper ones were named after the sail they extended: topmast and topgallant studding sails.

Setting and taking them in was laborious, and they were used only with the wind from astern or on the quarter. Clippers and men-of-war carried them to the end of the age of sail, and they disappeared with the coming of steam and of the simpler rigs of the last sailing ships.
