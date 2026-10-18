# Changelog

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
