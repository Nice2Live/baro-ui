# Changelog

BaroUI follows semantic versioning (see [docs/API.md](docs/API.md)): a breaking change of the public API raises the major version.

## 0.2.0 — 2026-11-16 (first release)

The first public release of the engine that grew inside the *Barotrauma: Beyond the Europa* modpack (its working name was BBE UI).

- Namespace `BaroUI`, assembly `BaroUI`, package **Baro-UI**; entry points `BaroUi`, `BaroUiView`; console command `baroui` (the old `bbe_ui` still works).
- Version API: `BaroUi.Version`, `BaroUi.Requires(minVersion, modName)`, `baroui version`; the public API is listed in `docs/API.md`.
- Layout: block, inline, flex, grid, relative/absolute/sticky positioning, scrolling, `line-clamp`, natural size of `<img>` and `<video>`.
- Styling: cascade, variables, `:has()`, `::before`/`::after`, sibling combinators, gradients (linear at any angle, radial, layers), shadows, per-corner radii, analytic anti-aliasing of curved edges, transitions and keyframes.
- Elements: forms and controls, code editor, popups and context menus, drag and drop, game inventory cells with `-bbe-slot-frame`, `<video>` (WebM), `<chart>`, `<curve>`, `<gauge>`, a drawing kit for canvases.
- Input: focus and `tabindex`, `data-keys`, mouse buttons 4/5 and Alt+arrows as back/forward, `OnEscape`/`OnBack`/`OnForward` hooks.
- An example mod: `examples/BaroUI_Example`.
- References in English, Russian and Simplified Chinese.

Not verified in the game yet: video sound and looping, mouse buttons 4/5, the example mod, live charts, gauges, slot frames, rounded clipping (not enabled). See the reference, sections 10 and 11.
