# Changelog

## 1.2.0
Performance release; behaviour and settings are unchanged.
- Trailer discovery looks only at the root objects of the loaded scenes, every 5 s and right after a scene loads, instead of every rigidbody in the world every 2 s.
- Towing car: found through the hitch joint first, then with a 12 m physics overlap. Kept until the joint lets go or the car is more than 12 m away. No more world-wide `WheelController` searches.
- No memory allocated per frame: damper values live in fixed buffers per wheel, wheel lists are reused, and the physics-step callback no longer creates a delegate every step.
- Wheels, springs and dampers are applied at once when a setting changes, and checked for changes made by the game every 0.25 s instead of every frame.
- New `[Debug] PerfLog` option (off by default): every 10 s writes the average and peak time of the mod per frame, physics step, scan and car search to `LogOutput.log`, with the trailer and wheel counts.

## 1.1.1
- Plugin GUID and config file renamed to `com.gelatti.apocalypter.trailerstance` (`BepInEx\config\com.gelatti.apocalypter.trailerstance.cfg`). An existing `local.apocalypter.trailerstance.cfg` is moved to the new name on first start, so your settings are kept.

## 1.1.0
- Ready for the Apocasetter in-game Mods window: install and one-click updates straight from GitHub releases.
- Mod icon (`TrailerStance/icon.png`) and the `General > Apocasetter` toggle.
- DLL now carries its version number (1.1.0.0).

## 1.0.0
- Trailer brakes: the trailer brakes with the car (brake key while rolling forward, never while reversing) and with the handbrake. Strength is a share of each wheel's grip, ramped in smoothly.
- Brake torque is added right before each wheel's physics step (Harmony prefix on `WheelController.Step`), because the game resets it every step.

## 0.9.0
- Fixed: dampers only scale the two NWH damper rates; the four slow/fast multipliers are left as the game sets them. Previously Anti-bounce could launch the trailer.
- Safety cap keeps damping inside what the physics step can handle.

## 0.8.0
- Anti-bounce (minimum damping as a share of critical damping) and Sway damping (extra angular drag).
- Damper range up to 6x. Panel shows the real spring and damping of one wheel.

## 0.7.0
- Centre of mass marker: pink ball with a line to the ground, plus its height in the panel.

## 0.6.0
- Panel drawn with a UI canvas and a Windows font (the game's IMGUI font has no letters).
- Updates also run from `Application.onBeforeRender`; keys moved off the numpad (Insert, arrows, Delete).

## 0.4.0 - 0.5.0
- Own persistent runner object, error capture to the log, trailer found by its root object (`Trailer_Big` / `Trailer_Small`).

## 0.1.0 - 0.3.0
- First versions: wheel spacing, ride height, centre of mass, spring and damper per trailer type; diagnostics dump.
