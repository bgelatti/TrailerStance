# Trailer Stance

A BepInEx 5 mod for **Apocalypter** that lets you tune the **Small and Big trailers**, so they stop bouncing, swaying and rolling over on rough desert, and brake together with your car.

## Features

- **Wheel spacing** (1.0x - 1.5x): a wider track is much harder to roll over.
- **Ride height** (-0.15 to +0.15 m).
- **Centre of mass** (-0.6 to +0.3 m), with an on-screen marker (pink ball and a line to the ground) so you can see where it is.
- **Spring** and **damper** strength.
- **Anti-bounce**: guarantees a minimum amount of damping, calculated from the spring and the load on each wheel. 0.7 = almost no rebound.
- **Sway damping**: extra resistance to rocking side to side and pitching.
- **Trailer brakes**: the trailer brakes when you brake the car while rolling forward (never while reversing) and when you hold the handbrake. Strength is a share of each wheel's grip, so it never overpowers the load.
- Settings are saved per trailer type (Big / Small) and can be edited in game or in **Apocasetter**.

## Requirements

- Apocalypter
- BepInEx 5 (5.4.x)

## Installation

**With Apocasetter:** open the in-game Mods window, find Trailer Stance and click install. Updates show up there automatically.

**Manually:**
1. Download the release zip.
2. Put the `TrailerStance` folder in `Apocalypter\BepInEx\plugins`.
3. Start the game. The config file is `BepInEx\config\local.apocalypter.trailerstance.cfg`.

## How to use

Look at a trailer and press **Insert**.

| Key | Action |
|---|---|
| Up / Down | choose a setting |
| Left / Right | change it (hold **Left Alt** for 5x steps) |
| Delete | reset this trailer type |
| Insert | done |
| F9 | write the structure of the trailer you look at to `LogOutput.log` |

All keys can be changed in the config or in Apocasetter (`[Keys]` and `[Brakes]` sections).

### Suggested settings for dunes and rough ground

| Setting | Value |
|---|---|
| Width | 1.3 - 1.5x |
| Centre of mass | around axle height (watch the pink ball) |
| Spring | 1.0x |
| Damper | 1.0x |
| Anti-bounce | 0.5 - 0.7 |
| Sway damping | 1.0 |
| Brakes | 0.6 |

A very stiff spring on a light trailer acts like a trampoline: lower the spring before raising the damping.

## Compatibility

- Works alongside Ultimate Tools, ApocaGarage, ApocaGearbox, EngineSim and other BepInEx mods.
- The trailer's axle is part of the body model, so at wide settings the wheels sit outside the axle visually; the physics are correct.
- Some mods use numpad keys (NPC Overhaul, Apocabuilder, Ultimate Tools), which is why this mod uses Insert, the arrows and Delete by default.

## Building from source

Requires the .NET SDK (6 or newer).

```
dotnet build -c Release -p:GamePath="C:\path\to\Apocalypter"
```

Add `-p:CopyToGame=true` to copy the DLL and icon into `BepInEx\plugins\TrailerStance` after building.

### Releasing

The Apocasetter index reads GitHub releases. For every release:
1. Bump `VERSION` and the assembly version in `src/TrailerStance.cs`, `<Version>` in the csproj, and the changelog.
2. Tag it `vX.Y.Z` (same version as in `[BepInPlugin]`).
3. Attach `TrailerStance-X.Y.Z.zip` containing `TrailerStance/TrailerStance.dll` and `TrailerStance/icon.png`. The game and BepInEx DLLs are referenced from your install and are not included in this repository.

## How it works (for modders)

- The game removes the BepInEx plugin object, so the mod runs its updates from its own `DontDestroyOnLoad` runner and from `Application.onBeforeRender`.
- Trailers are found by the root object name (`Trailer_Big(Clone)...`, `Trailer_Small(Clone)...`); wheels are NWH `WheelController` components (Unity `WheelCollider` is supported as a fallback).
- Width and height move the wheel controllers in the trailer's local space; spring and damper values are scaled from the stock values, which are re-read if the game changes them (part swaps).
- NWH damper force is `rate x slow/fast multiplier x speed`; only the two rates are changed.
- Brakes: a Harmony prefix on `WheelController.Step` adds brake torque right before each physics step.
- The game does not forward Unity errors to `LogOutput.log`, so the mod catches and logs its own.

## License

All rights reserved, see [LICENSE](LICENSE). You can use the mod and read the code, but please do not re-upload it or publish modified versions; link to the [releases page](https://github.com/bgelatti/TrailerStance/releases) instead. Bug reports and ideas are welcome as [issues](https://github.com/bgelatti/TrailerStance/issues); pull requests are not accepted.
