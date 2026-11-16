# Changelog

BaroUI follows semantic versioning (see [docs/API.md](docs/API.md)): a breaking change of the public API raises the major version.

## 0.3.0 — 2026-11-16

- Mod fonts: `@font-face { font-family; src: url(...); font-weight; font-style }` with the file in the mod's own package (paths like images, TTF and OTF; metrics read from the file; a family is local to the window that declares it), and `BaroUi.RegisterFont(family, file, weight, italic)` in C#.
- Rounded clipping: a block with `overflow: hidden|auto|scroll` and `border-radius` clips its children to the rounded shape (drawn at the start of the frame into its own texture, then masked; at most 32 such blocks per window).
- Font family `Noto Sans SC` (Simplified Chinese) from the game folder.
- Example mod: every text in three languages (RU / EN / 中文, one at a time), its title in a font shipped with the example (`examples/BaroUI_Example/Fonts`).
- Published on the Steam Workshop (item 3840968580).

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
