Translated from REFERENCE.ru.md (the Russian file is the source of truth).

The console command is `baroui`; the old name `bbe_ui` still works (the engine was renamed BaroUI on 2026-11-16).

# BBE UI (Baro-UI): full engine reference

Reference version: 2026-11-16. The Russian version is the master; the English and Simplified Chinese translations are made from this file.

Package: `own/BBE_UI` (namespace `BaroUI`, LuaCs mod via `ModConfig.xml`). Public engine name: Baro-UI (owner decision, 2026-11-15).

Sources: `docs/ui/AUTHORING.md` (author guide), `docs/ui/PERF.md`, `docs/ui/AUDIT_STAGE_C.md`, `docs/ui/AUDIT_STAGE_D.md`, `docs/HANDOFF.md` (section 0), `docs/ui/DESIGN_*.md`, code `own/BBE_UI/CSharp/Client` and `own/BBE_UI/CSharp/Shared`, pages `own/BBE_UI/UI/*.html`, tools `tools/ui_host`, `tools/ui_preview`, `tools/ui_test`.

## 0. How to read this reference

Markers:

- **(verified in code)** — the fact was checked against the source; the file and, where needed, the line are given in parentheses.
- **(per AUTHORING/tests)** — taken from `docs/ui/AUTHORING.md`, the audits, or `tools/ui_test` tests; not re-read line by line in this check.
- **(not verified in code)** — not found in the code or not checked; such items must not be treated as guaranteed.
- **(not verified in game)** — the code exists, but it has not been run in the game. As of 2026-11-16 the game has not been run for the engine after the last stages D, E, F; earlier changes were verified only by tests, build, and `ui_host`.

Tests: the full `tools/ui_test` run on 2026-11-16 — 8863 passed, 0 failed (`docs/HANDOFF.md`, section 0). The tests cover `Core/` (parsing, layout, styles, geometry); `Render/` is verified only by the build (`cscheck`), `ui_host`, and the game.

If a statement has no marker, the fact was verified in code or is a definition from this reference itself.

---

## 1. What it is and how it works

### 1.1. Essentials

BBE UI is a custom HTML/CSS engine for the game Barotrauma. The game has no browser: HTML parsing, CSS cascade, layout, and drawing are implemented in C#. Only what is listed in this reference is supported. Everything unknown is skipped with a note in the log and on the error banner (`baroui watch`, `baroui debug`, `baroui problems`).

Pipeline (verified in code, call order in `Render/UiWindow.cs`):

| Stage | Files | What it does |
|---|---|---|
| HTML parsing | `Core/HtmlParser.cs`, `Core/Dom.cs` | tree of `Element`/`TextNode`, void tags, raw text for `style`, `script`, `textarea`, entity table |
| CSS parsing | `Core/CssParser.cs`, `Core/CssValues.cs` | rules, selectors, declarations, `@keyframes`; `@media`, `@import`, `@font-face` are skipped with a note |
| data | `Core/Template.cs`, `Core/Expr.cs` | `{{…}}`, `data-for`, `data-if`, `data-class`, `data-model`, `data-t`, handlers |
| cascade | `Core/StyleEngine.cs` | values, inheritance, `var()`, `!important`, states (`:hover` etc.), style cache |
| layout | `Core/LayoutEngine.cs`, `Core/LayoutBox.cs` | block, inline, flex, grid, positioning, incremental layout |
| animation | `Core/Animation.cs`, `Core/Easing.cs` | transitions, `@keyframes`, `layout` transition, typewriter |
| drawing | `Render/Painter.cs`, `Render/UiDraw.cs`, `Render/UiCharts.cs`, `Render/UiControls.cs`, `Render/UiImages.cs`, `Render/UiFonts.cs` | the game's `SpriteBatch`, background, borders, shadows, gradients, text, fields, charts |
| input | `Core/PointerRouter.cs`, `Core/KeyRouter.cs`, `Core/DragDrop.cs`, `Core/Popup.cs`, `Core/FocusOrder.cs`, `Render/UiCanvasInput.cs`, `Render/UiFileInput.cs` | mouse, keys, focus, drag and drop, popups, files |
| coordinates | `Core/ScreenMap.cs`, `Core/UiTransform.cs` | the single place that computes where an element is on screen (drawing, mouse, IME, tooltips) |

The core (`Core/`) does not depend on the game: its tests run without the game (`tools/ui_test`). `Render/` and `Api/` work only inside the game.

### 1.2. Units and canvas

- `1u = min(screen width / 1600, screen height / 900) × game HUD scale` (verified in code: `Render/UiWindow.cs`, line 630, `UnitPx`).
- `px` — the value is used as written, without multiplying by `UnitPx` (verified in code: `Core/StyleEngine.cs`, `FontSizePx` calculation, `Unit.Px` branch). (not verified in code: whether 1 px equals one screen pixel at a non-integer HUD scale.)
- `em` — the element's font size; for `font-size` — the parent's font size.
- `%` — relative to whichever side or value the property defines (see the property table).
- The page canvas is **1600u × 900u**, centered on the screen. If the screen has a different aspect ratio, the remainder stays empty (verified in code: `UiWindow.cs`, lines 646–648).
- `<html data-viewport="screen">` (or `<body>`): the layout viewport is the whole screen; `position: fixed` and percentages are measured from the screen edges. The `u` unit is the same. The browser preview does not show this mode (per AUTHORING; verified in code: `Core/Dom.cs`, `Document.ScreenViewport`).
- The comment in `Core/CssValues.cs` (line 7) said `u` is 1/1600 of the window width. Fixed 2026-11-16: it now gives the formula above; the code behavior did not change.

### 1.3. Where it runs

- It is a **LuaCs C# mod**: `own/BBE_UI/ModConfig.xml` loads the `CSharp/Shared` assembly (`IsScript="true"`, `FriendlyName="BaroUI"`) and the `CSharp/Client` assembly (`Target="Client"`, same name) (verified in code: `ModConfig.xml`).
- The game and LuaCs are hooked through Harmony (verified in code: `Api/BaroUi.cs`, `Init`):
  - prefix on `DebugConsole.AddToGUIUpdateList` — here windows get the frame and input;
  - prefix on the pause menu — Esc (`PauseMenuPrefix`).
- **Where windows are visible**: in the main menu only if LuaCs keeps mods running in the menu. This is the BBE fork of LuaCs (`forks/LuaCs`, branches `luacs/bbe` and `moonsharp/bbe`; per HANDOFF). Stock LuaCs unloads mods in the menu, so windows do not work there; in the lobby, rounds, and editors, stock LuaCs works (per `docs/HANDOFF.md` and `docs/luacs/COMPARE_PLUGIN.md`; (not verified in game) for each scenario separately).
- No JavaScript. No network: `http(s)://`, `data:`, and `file:` addresses are not loaded (verified in code: `Core/ImageLayout.cs`, `ResolvePath`).

---

## 2. Installation and first screen

### 2.1. Package contents

```
own/BBE_UI/
  ModConfig.xml        LuaCs assemblies (Shared and Client, FriendlyName BaroUI)
  filelist.xml         contentpackage "BBE UI", modversion 0.0.1-bbe, corepackage false, gameversion 1.13.5.0
  Xml/sync.xml         empty: the package appears in the server list so the client keeps it above packages that use it
  CSharp/Shared/       PointCurve.cs, UiEcho.cs, UiPlugin.cs (shared by client and server)
  CSharp/Client/       Api/, Core/, Render/, Probe.cs (client part)
  Fonts/               JetBrainsMono-Regular/Bold.ttf, Caveat-Regular/Bold.ttf, PTSerif-Regular/Bold/Italic.ttf,
                       Lucide.ttf, lucide-codepoints.json (2379 names), OFL and ISC licenses
  UI/                  pages: *.html and *.css (controls, inputs, charts, anim_demo, pseudo_demo, test)
```

(verified in code: contents of the `own/BBE_UI` folders and files; the game version in `filelist.xml` is 1.13.5.0.)

The Noto Sans font is **not** included in the package: it is read read-only from the installed game folder (`Content/Fonts/NotoSans`) (verified in code: `Core/FontFamilies.cs`).

The package finds itself: `BaroUi.FindPackageDir` looks for an enabled package named `Baro-UI` with the file `Fonts/JetBrainsMono-Regular.ttf` (verified in code: `Api/BaroUi.cs`, `FindPackageDir`).

Build check without the game: `py -X utf8 tools/cscheck.py BBE_UI` (per `docs/HANDOFF.md`, section 3; verified by the presence of the script).

### 2.2. Minimal page

`own/BBE_UI/UI/fuel.html`:

```html
<!doctype html>
<html>
<head>
  <link rel="stylesheet" href="fuel.css">
</head>
<body>
  <div id="root" class="panel">
    <p class="title">Топливо</p>
    <div class="gauge"><div class="fill" style="width: {{level}}%"></div></div>
    <p>Уровень: {{level}}</p>
    <button onclick="refill(1)">Заправить</button>
  </div>
</body>
</html>
```

`own/BBE_UI/UI/fuel.css`:

```css
.panel { width: 400u; padding: 16u; background-color: #0b1418; border: 1u solid #4a6a6c; border-radius: 6u; }
.gauge { height: 14u; background-color: #1d3a3f; }
.fill { height: 100%; background-color: #40c8be; }
```

C# class:

```csharp
using BaroUI.Api;
using BaroUI.Core;

public sealed class FuelView : BaroUiView
{
    protected override void OnOpen() => Data = new { level = 40 };

    [UiHandler]
    private void refill(int id)
    {
        // message to the server; the server reply updates Data via Refresh()
        Send(Find("#root"), () => { /* send */ });
    }
}

// open:
BaroUi.Open<FuelView>(Path.Combine(pkgDir, "UI", "fuel.html"));
```

Notes on the example (verified in code):

- `[UiHandler]` handlers are methods of the window class; the method name is the name used in the `onclick` attribute; private methods work too (`Core/Template.cs`, `Handlers`, registration method: "public or not").
- `Data` is a C# object or dictionary; after a change, `Refresh()` is called; for a single field — `Set("level", 42)` (`BaroUiView.Set`, `UiWindow.Set`).
- A call to `Send(...)` blocks the element until a reply (5 s by default) and adds the class `pending` (verified in code: `Api/BaroUiView.cs`, `Send`/`Done`). The element must call `Done(source)`.
- (per AUTHORING) A window does not change game data by itself: changes go through the server, and the window waits for the reply.

Important: in the example, `Find("#root")` is `BaroUiView.Find(selector)` with a CSS selector; `UiWindow.Find(id)` accepts only an `id`.

### 2.3. Window class lifecycle

Methods of `BaroUiView` (verified in code: `Api/BaroUiView.cs`):

| Method | When it is called |
|---|---|
| `protected virtual void OnOpen()` | after the window is created and the first layout is done |
| `protected virtual void OnUpdate(float deltaTime)` | every frame |
| `protected virtual void OnAfterInput()` | after the frame's input is processed |
| `protected virtual void OnClose()` | on close |

(OnUpdate and OnAfterInput are called from `UiWindow`; the exact order relative to layout was not re-read in this check — (not verified in code).)

### 2.4. Opening a page

```csharp
// free window: the engine handles Esc and clicks outside the window (Esc goes to it if the window is on top)
UiWindow w = BaroUi.Open(path, view, "fuel");
var w2 = BaroUi.Open<FuelView>(path);              // with a new instance of class T
UiWindow w3 = BaroUi.OpenPage("controls");         // UI/controls.html of the current package (or from baroui src)
```

The `Open` functions add the window to the list of free windows (`windows`), which receive the frame and input (`Api/BaroUi.cs`, `Open`).

### 2.5. Item window (hosted window)

An item component (such as the terminal, `own/BBE_Terminal/CSharp/Client/TerminalUi.cs`) creates a window like this:

```csharp
// not a free window: the component adds it to the update list itself and closes it
window = BaroUi.Create(path, view, "term");
// in the component's AddToGUIUpdateList:
window.AddToGUIUpdateList(order);
// close when deselected:
window.Close();
```

Esc and clicks outside the window are handled by the game: the component's `GuiFrame` is an invisible `GUIFrame` that every frame is placed on the rectangle of the root block (per AUTHORING: `window.BoxOf(window.Find("term"))`; for fields and tooltips the exact screen position is given by `ScreenRect`, see section 4.9).

(per AUTHORING; verified in code: `Api/BaroUi.cs`, `Create` puts the window into the `hosted` list, which is served by `AddWindowsPrefix`.)

`baroui reload` and `watch` work for such windows. An owner that holds a reference to the window subscribes to `BaroUi.Reloaded` and replaces the reference (`Api/BaroUi.cs`, `public static event Action<UiWindow, UiWindow> Reloaded`; (order of arguments — old/new — not verified in code)).

`window.CaptureNextDraw("file.png", "id")` — snapshot of a block after the next draw (verified in code: `UiWindow.cs`, `CaptureNextDraw`).

---

## 3. HTML: tags, attributes, controls, templates, events

### 3.1. Markup parsing

- Parsing is tolerant: an extra closing tag closes the nearest matching ancestor or is ignored (`Core/HtmlParser.cs`, class comment; verified in code).
- Comments `<!-- … -->` are skipped (per AUTHORING; (not verified in code: separate branch for comments)). `<!doctype html>` is written in the examples; its handling was not verified (not verified in code).
- Raw text (not markup): `style`, `script`, `textarea` (verified in code: `RawText`, `HtmlParser.cs`). For `textarea` the content is the field value.
- Void tags (no closing tag, verified in code: `Void`, `Core/HtmlParser.cs`): `br`, `hr`, `img`, `input`, `meta`, `link`, `item`, `slot`.
- Named entities (verified in code: `Entities` table): `&amp; &lt; &gt; &quot; &apos; &nbsp; &times; &mdash; &ndash; &hellip; &rarr; &larr; &uarr; &darr; &middot; &deg; &laquo; &raquo; &numero; &minus; &plusmn;`. Numeric references `&#NNN;` (not verified in code). For characters outside the list, use the character itself in UTF-8.
- `<bar>` was removed from the engine (2026-11-16, decision on item 15 of the check): no page, package, or code used it, it was never drawn, and it appeared only in the void-tag list and in the UA sheet. It is now an unknown tag; there is none in the pages; the behavior of an unknown tag in layout was not verified in code.
- Encoding: page files are UTF-8 (per AUTHORING; (not verified in code: explicit encoding check)).

### 3.2. Tag table

The "default" column is the UA rules from `Core/StyleEngine.cs` (`UserAgentCss`, verified in code).

| Tag | Default | What it does and notes |
|---|---|---|
| `html`, `body` | block | page root; `data-viewport="screen"` on them (section 1.2) |
| `div`, `section`, `header`, `footer`, `nav`, `main`, `aside`, `p`, `form` | block | regular blocks |
| `h1` | block, `font-size: 2em`, bold | |
| `h2` | block, `font-size: 1.5em` | |
| `h3` | block, `font-size: 1.17em` | |
| `h4` | block, size not set | `h1`–`h4` and `th` are bold (UA) |
| `ul`, `ol`, `li` | block | no markers (verified in code: no `list-item` branch) |
| `table` | block | not a table: there is no `display: table` layout; a block with a note in the log (see section 4.6, `display`) |
| `tr` | flex, row | (verified in code: UA `tr { display: flex }`) |
| `td`, `th` | block, `flex: 1 1 0` | share the `tr` row equally |
| `span`, `b`, `strong`, `i`, `em`, `a`, `label` | inline | `b`, `strong` bold; `i`, `em` italic; `i[data-icon]` — icon (section 7) |
| `img` | inline-block | `src`, `alt`; size see below |
| `button` | inline-block | `onclick`, `data-default`, `data-item`; the button look is set only by CSS (there are no UA appearance rules) |
| `input` | inline-block, 12em × 1.8em | fields, checkboxes, radios, slider — section 3.5 |
| `select` | inline-block, 12em × 1.8em, cursor `pointer` | drop-down list |
| `textarea` | inline-block, 30em × 12em | code editor — section 5.6 |
| `progress` | inline-block, 10em × 0.5em, background `#1d3a3f`, 2px radius | `value`, `max` |
| `item` | inline-block, 40u × 40u | game item icon |
| `canvas` | inline-block, 300u × 150u | C# drawing — sections 6 and 8 |
| `slot` | inline-block | game inventory cell; size set by CSS |
| `dialog` | `display: none`; `dialog[open]` — block; absolute, `z-index: 1000` | modal window |
| `curve`, `chart`, `gauge` | block (UA rule `curve, chart, gauge { display: block }`, added 2026-11-16; before that the default was `inline`) | size set by CSS (`height`, `width`); the page `UI/charts.html` sets `display: block` itself, which matches; see section 6 |
| `head`, `title`, `meta`, `style`, `script`, `option`, `template` | `display: none` | not drawn |
| `link` | not drawn | `<link rel="stylesheet" href="…">` loads CSS (`UiWindow.cs`, line 175) |
| `br` | line break | |
| `hr` | (not verified in code: line drawing) | do not use without checking |

Separate notes (verified in code):

- **`<img>` size.** If CSS left a side as `auto`, the file's pixel size is used (`Core/LayoutEngine.cs`, `IImageSizes`). One side in `px`/`u`/`em` — the other side follows the image proportions. If the file is missing, the block is 0 × 0. Instead of a missing image, `alt` is drawn as one line (per AUTHORING).
- **`<option>`** is not drawn; list rows are taken from child `option` elements (`UiControls.OptionsOf`).
- **`<label>`** — a click calls `ClickLabel` (`Render/UiControls.cs`, lines 413 and 425). What exactly is passed to the linked field — (not verified in code).
- **`<a>`** becomes focusable and clickable only with `onclick` (`UiControls.cs`, line 255); without `onclick`, `a` is plain text.

### 3.3. Attributes

Common (verified in code unless stated otherwise):

| Attribute | Action |
|---|---|
| `id` | name for `Find(id)`, `focus('id')`, `showModal('id')`, `SetCanvas(id, …)`, `SetFileInput(id, …)` |
| `class` | classes for selectors; `data-class` changes them by conditions |
| `style` | inline declaration: stronger than any selector (per AUTHORING) |
| `tabindex` | `-1`, `0`, or a positive number: focus and Tab order (section 5.3) |
| `title` | tooltip; appears 0.4 s after hover (`UiControls.cs`, `TooltipDelay = 0.4f`); in the game, not in the preview |
| `accesskey` | one letter: Alt+letter activates the element or focuses the field (`UiControls.cs`) |
| `lang` | language; `ru` (or no `lang`) enables Russian word glue with `-bbe-ru-glue: auto` (section 4.6) |
| `data-*` | see the sections below; unknown `data-*` are ignored (per AUTHORING) |

Control attributes:

| Attribute | Where | Action |
|---|---|---|
| `type` | `input` | `text` (default), `number`, `password`, `search`, `checkbox`, `radio`, `range`. Other values (`email`, `date`, `color`, etc.) are not recognized as fields: `IsText`, `IsCheckbox`, `IsRadio`, `IsRange` do not match them (`UiControls.cs`, lines 128–135); how they are drawn — (not verified in code) |
| `name` | `input type=radio` | radio group within the window |
| `value` | `input`, `option`, `progress`, `textarea`, `curve`, `chart`, `gauge` | value; for `textarea` — the tag content |
| `checked` | `input` | checked (`:checked`) |
| `placeholder` | `input`, `textarea` | hint for an empty field (`UiControls.cs`, `UiCodeEdit.cs`) |
| `maxlength` | `input` text | length limit (`UiControls.cs`, line 161, `MaxLength`) |
| `disabled` | element with a control | does not respond, `:disabled` (`UiWindow.IsDisabled`) |
| `readonly` | `textarea` | read-only (`UiCodeEdit.cs`) |
| `autofocus` | field inside `dialog` | focus when the modal opens (`UiControls.cs`, lines 573–585) |
| `min`, `max`, `step` | `input type=number`, `range` | bounds and step; `step="any"` — no grid for the slider (per AUTHORING) |
| `on*` | any | see section 3.7 |
| `src`, `alt` | `img` | file and text instead of the picture |
| `href`, `rel` | `link` | `rel="stylesheet"`, path relative to the page folder |
| `open` | `dialog` | open (set by `showModal`) |
| `closedby="any"` | `dialog` | closes on a click outside the window (`UiControls.cs`, line 362) |
| `prefab`, `count` | `item` | game item by prefab name; `count > 1` — number in the corner (`UiControls.cs`, lines 1264 and 1282) |
| `sprite` | `item` | game sprite `kind:id` (section 7.3) |
| `inventory`, `index` | `slot` | inventory cell; name from `Window.SetInventory` (section 8) |
| `data-lang`, `tabsize`, `data-tabs`, `data-autoindent`, `data-gutter` | `textarea` | code editor (section 5.6) |
| `data-viewport` | `html`, `body` | screen instead of the canvas (section 1.2) |
| `data-picked` | `chart` | index of the point picked by a click (set by the engine before `onpick`) |

### 3.4. Game tags and values

- `<item prefab="oxygentank" count="3">` — game item icon; `prefab` is looked up by name; an unknown name gives a log line "no such item" (`UiControls.cs`, line 1270 and following).
- `<item sprite="item:oxygentank">`, `sprite="talent:…"`, etc. — game sprite (section 7.3).
- `<canvas id="scope">` — drawn by C#: `Window.SetCanvas("scope", (sb, rect) => …)` (section 8). Mouse — `SetCanvasMouse`, pictures from files — `Window.Images` (section 6).
- `<textarea id="code" data-lang="xml">` — code editor (section 5.6).
- `<slot inventory="in" index="0">` — game inventory cell (`Window.SetInventory("in", inventory)`). Dragging, tooltips, and stacks work while the game itself updates the inventory. The size is set by CSS; the game draws the status strip above the cell — leave room at the top. A cell scrolled past an edge or covered by a `dialog` does not take the mouse. After `SetInventory(name, null)` or closing the window, the cells return to the game (per AUTHORING).
  - The cell frame is `-bbe-slot-frame` (not inherited): `game` (default, the game's sprite), `none` (no frame: the `<slot>` element's own background, border and radius show; the game draws the item, its condition strip and the stack count), `url("frame.png")` (your picture, stretched to the cell). A picture that cannot be found falls back to the game frame with one log line (verified in code: `Render/UiControls.cs`, `SlotFrame`; not verified in game).
- `<dialog id="…">` — modal window (section 5.5).
- `<progress value max>` — without `value` — an indeterminate running bar (per AUTHORING; (not verified in code: running-bar animation)).
- `<curve>`, `<chart>`, `<gauge>` — section 6.

### 3.5. Fields, checkboxes, radios, slider, list, button

Common (verified in `Render/UiControls.cs` and `Core/TextEdit.cs` unless stated otherwise):

- A text field (`text`, `number`, `password`, `search`) receives the keyboard and IME; the character is not controlled by the game at that time.
- Focus on click: input fields and the code editor take focus themselves; any element with an explicit `tabindex` (including `-1`) does too. Buttons, checkboxes, radios, sliders, and lists **without** `tabindex` do not take focus on click — they take the keyboard only via Tab (per AUTHORING, section 5.3). The set of elements that Tab can stop on at all — `UiControls.cs`, line 255 (verified in code).

#### 3.5.1. Text field

Keys (verified in `TextEdit.cs` and `UiControls.cs` unless stated otherwise):

- Left and right arrows; Ctrl+arrows — by word (`TextEdit.WordLeft`, `WordRight`); Shift — selection.
- Home, End; Ctrl+A — select all (`SelectAll`).
- Backspace, Delete; Ctrl+Backspace and Ctrl+Delete — by word (`TextEdit.Backspace(word)`, `Delete(word)`).
- Enter: if the field has `onenter`, it is called; otherwise the button with `data-default` in the nearest `dialog` or window is pressed (`UiControls.cs`, lines 937 and 989–993).
- Ctrl+C, Ctrl+X, Ctrl+V — clipboard (per AUTHORING). For `password`, copy and cut are forbidden (`UiControls.cs`, lines 870 and 873).
- Esc: closes an open list, then a dialog, then the window (section 5.8).
- Tab and Shift+Tab — move through the focus order (section 5.3).
- Length limit — `maxlength` (`MaxLength`). Character filter: `number` accepts digits, `-`, `+`, `.`, `,`, `e`, `E` (`NumberField.Accept`, `Core/TextEdit.cs`).

Mouse:

- A click places the caret (`TextEdit.IndexAt`); dragging selects.
- Double and triple click — (not verified in code for fields; for the code editor see section 5.6).

IME: the engine gives the game the field's rectangle on screen (`UiControls.cs`, lines 59 and 1120), so the IME window sits at the caret. The composition itself (pre-edit input) runs in the game; (not verified in code and in game).

`number`:

- Up and Down — step `step` (default 1); the value is rounded to the grid and clamped to `min` and `max` (`UiControls.cs`, lines 693–714 and 932–933; `NumberField.Clamp`).
- Mouse wheel over a number field (verified in code, since 2026-11-16: `UiControls.Wheel` → `WheelNumber`, `Render/UiControls.cs`; step and limits — `NumberField.Stepped`, `Core/TextEdit.cs`, test `ControlsTests.cs`, group "controls"): changes the field under the pointer by one step per notch, up is larger. With focus — like the arrow: `oninput` immediately, `onchange` when leaving the field. Without focus — the value changes at once, `oninput` and `onchange` arrive at once, and the model is written. At the limit the wheel is not used, and the page under it scrolls. The wheel inside an open list and inside the code editor works as before. (not verified in game: the wheel mechanics in the game itself.)
- The value is written to the model when leaving the field (`UiControls.cs`, function `Commit`; per AUTHORING).

`password`: `*` on screen for each character (`UiControls.cs`, lines 1044–1045).

`search`: behaves like a text field; no separate behavior (clearing on Esc, etc.) was found (not verified in code).

#### 3.5.2. Checkbox

- `input type=checkbox`: click and Space toggle it (per AUTHORING; verified in code: `UiControls.cs`, `IsCheckbox` branch).
- A checked box draws an inset square inside (`UiControls.cs`, lines 1061–1069, color `Accent(s, opacity)`).
- Default look (UA): 1em × 1em, `cursor: pointer`; checked — background `#1d3a3f`. The inner square is colored by `accent-color` (default `#40c8be`).
- `onchange` arrives when the value changes (`UiControls.cs`, lines 772 and 795; per AUTHORING).

#### 3.5.3. Radio

- Radios with the same `name` in a window form one group; only one is checked. A radio without `name` is a group of one (per AUTHORING; `RadioGroup.cs`).
- Default value — `on` (`RadioGroup.ValueOf`).
- Click, Enter, or Space checks the radio and unchecks the others (per AUTHORING).
- Tab lands on the checked radio of the group; if none is checked, on the first one. Up/Left and Down/Right arrows move to the neighbor and check it (per AUTHORING; (not verified in code: full key set in the radio handler)).
- `data-model`: it is enough to put it on one radio of the group. The radio whose `value` equals the model value is checked (comparison as text: `3` and `"3"` match). A radio without its own `data-model` also follows the model (`Core/Template.cs`, lines 451–460, verified in code).
- `onchange` arrives only to the newly checked radio and only after a successful model write; on a failed write — a warning in the log and no `onchange` (per audit D, item 11; `UiControls.cs`, lines 274 and 283–284).
- Look (UA): `input[type=radio] { width: 1em; height: 1em; padding: 0; border-radius: 50%; cursor: pointer }`; checked — background `#1d3a3f`; the dot inside is colored by `accent-color`.

#### 3.5.4. Slider (`input type=range`)

- Attributes: `min` (default 0), `max` (100), `step` (1; `any` — no grid), `value` (without it, the middle), `data-model`, `oninput` (on every movement), `onchange` (on mouse release and on every key) (per AUTHORING; `UiControls.cs`, lines 742–751).
- The value snaps to the step grid from `min`; a `max` off the grid is unreachable (`RangeField.Snap`, as in HTML).
- Mouse: pressing the track moves the thumb under the cursor, then it follows the mouse; the keyboard is not handed to the game.
- Keys (focus by Tab): arrows — one step; PageUp and PageDown — ten steps; Home and End — the ends (per AUTHORING).
- The wheel does not move the slider: it scrolls the page (per AUTHORING; (not verified in code: wheel behavior over a slider)).
- The engine draws it: the track in the text color (22% opacity), the part before the thumb, and the round thumb in `accent-color`. The `height` sets the size: the thumb is 4/5 of the height, the track is 3/10 (per AUTHORING).

#### 3.5.5. `select` list

- `option value="…"`; `disabled` on an option.
- Expands in a window of 8 rows (`DropRows = 8`, `UiControls.cs`, line 20). The wheel over an expanded list scrolls it (`UiControls.Wheel`, lines 532–538). Arrows and Enter select (per AUTHORING).
- Inside a block with `scale`, list rows are drawn without the scale (section 4.9).
- Esc closes the list (section 5.8).

#### 3.5.6. Button

- A click calls `onclick`. On click the button does not take focus (section 5.3); Tab moves to it.
- Enter and Space on a focused button — (per AUTHORING; (not verified in code: button branch in `UiControls.cs`)).
- `data-default` — the button that Enter presses in a field without `onenter` (section 3.5.1).
- `disabled`: does not respond, `:disabled`. By UA, `input:disabled, select:disabled, textarea:disabled` — `opacity: 0.5`, cursor `auto`.

#### 3.5.7. Code editor (`textarea`)

See section 5.6.

### 3.6. Templates and data

All bindings read `Data` (object or dictionary). A template **only reads** data; only C# can write it (per AUTHORING; verified in code: `Core/Expr.cs`, the grammar has no assignments).

| Markup | What it does | Notes |
|---|---|---|
| `{{path}}` in text and attributes | inserts the value | `{{}}` in attributes whose names start with `on` is not substituted (`Core/Template.cs`, line 145) |
| `{{path \| format}}` | `.NET` format: `0`, `0.0`, `#`, `,`, `F1` | `{{x \| 000}}`, `{{v \| 0.0}}` (per AUTHORING; (not verified in code: the full format set is .NET `ToString`)) |
| `data-for="r in rows"` + `data-key="r.id"` | element repeat | with `data-key`, rows with the same key are reused; without a key — by row number |
| `data-virtual="on"` / `"off"` | virtual list | inside `overflow: auto/scroll` a list of 100 or more rows becomes virtual automatically (`Core/Template.cs`, `AutoVirtual`); row heights are uniform (per AUTHORING) |
| `data-if="condition"` | the element exists only if the condition is true | a hidden element is not built and takes no space (`Template.cs`, lines 386–392) |
| `data-class="name: condition; name2: condition"` | classes by conditions | an entry without a colon is a note (`Template.cs`, line 132) |
| `data-disabled="condition"` | `disabled` by condition | |
| `data-model="path"` | two-way field ↔ data | for `input`, `textarea`, `curve`, `chart`, `gauge`; writes to the model from the code editor happen at most once per frame (per AUTHORING) |
| `data-t="id"`, `data-t-title="id"`, `data-t-<attribute>="id"` | translation | the text for the id is substituted **when the page is read** (`Core/Localizer.cs`); if there is no text, the page gets a note and the element stays as written in the markup. Source — the game's `TextManager` (`Api/BaroUi.cs`, line 44). The `own/BBE_UI` package has no `Localization` folder (verified from the package contents). Package texts reach `TextManager` the same way as in neighboring packages: with the line `<Text file="%ModDir%/Localization/English.xml" />` in `filelist.xml` (verified in `own/BBE_Mods/filelist.xml` and `own/BBE_Terminal/filelist.xml`); whether the game loads them this way for this page — (not verified in game). The earlier AUTHORING phrase "package text files" was fixed 2026-11-16. Text with `{{…}}` is bound after substitution |

Built-in handlers (verified in code: `Render/UiWindow.cs`, lines 153–156):

| Call in an attribute | Action |
|---|---|
| `close()` | close the window |
| `showModal('id')` | open the `dialog` with this `id` |
| `closeDialog()`, `closeDialog('id')` | close the modal (current or with this `id`) |
| `focus('id')` | focus the element with this `id` |

Expressions (verified in code: `Core/Expr.cs`):

- Paths: `a.b[0].c`; name case does not matter (per AUTHORING). `.length` and `.count` for lists (`Expr.cs`, line 178).
- Literals: numbers, strings in `'single'` quotes, `true`, `false`, `null`.
- `$index` — row number in `data-for` (`Expr.cs`, line 224).
- Operators: `+ - * / %`, comparisons `== != < <= > >=`, `&&`, `||`, `!`, parentheses. Strings are concatenated with `+` (`Expr.cs`, line 270).
- No ternary `?:`, function calls, or assignments were found in the grammar (verified in code: `Expr.cs`).

Handler arguments are expressions from the attribute, for example `onclick="craft(r.id)"`. The `this` variable in an expression — (not verified in code; per AUTHORING — the element itself).

### 3.7. Events (`on*` attributes)

Names the engine calls (verified in code: calls to `Fire(e, "…")` and `Dispatch`):

| Attribute | When it arrives |
|---|---|
| `onclick` | left-click on the element; if the element has no `onclick`, the nearest ancestor with one is used (`Template.Dispatch`). After a drag farther than 4 px it does not arrive (section 5.2) |
| `oninput` | after every text edit; for a slider — on every movement; for `textarea` — one call per frame |
| `onchange` | when leaving a field, if the text changed; for a checkbox — on change; for a slider — on release; for a radio — after a successful model write |
| `onenter` | Enter in a text field (instead of pressing `data-default`) |
| `onfocus`, `onblur` | gaining and losing focus (`UiControls.cs`, lines 179 and 235) |
| `onclose` | closing a `data-popup` (popup) and a `dialog` (`UiControls.cs`, lines 473 and 654) |
| `onback`, `onforward` | mouse buttons 4 and 5 and Alt+Left / Alt+Right: the element under the pointer and its ancestors, then the focus and its ancestors (`Core/NavRoute.cs`; section 5.8a; checked in code) |
| `onsave` | Ctrl+S in a `textarea` (`UiCodeEdit.cs`, line 265) |
| `onpick` | click on a point or bar of a `chart`; the point index is in `data-picked`, passed as `$arg` (`UiCharts.cs`, lines 669–670; `onpick="pick($arg)"`) |

Notes (verified in code):

- The handler search goes from the target up through the ancestors: the first element with the needed attribute receives the event (`Core/Template.cs`, `Dispatch`, lines 682–687).
- An exception in a mouse handler closes the window and is written to the log (`UiWindow.Fail`). Keyboard handling works the same way (section 5.8; audit D, item 11).
- There are no `onkeydown` and `onkeyup` attributes; the engine does not receive key releases (in code: no `keyup` search in the sources). For keys use `data-keys` and `SetKeys` (section 5.3).
- There are no mouse attributes `onmousedown`, `onmouseup`, etc. For the mouse use `data-mouse` and `SetMouse` (section 5.1).

---

### 3.8. Video: `<video>`

See the «Видео» section of AUTHORING.md (Russian). Not verified in the game (the decoder works in ui_host).

| What | How | Status |
|---|---|---|
| Decoder | the game's `Barotrauma.Media.Video` (WebM); the fork is not changed | fact (code), not verified in the game |
| `src` | `.webm` only, path as for `<img>` (inside the package, from the page); otherwise a placeholder and a line in problems | fact |
| `autoplay`, `loop`, `muted`, `volume` 0..1 | an out-of-range `volume` is clamped, a non-number is 1, both said | fact |
| `poster` | a picture shown until the first frame and on failure | fact |
| Natural size | width and height of the video track from the WebM header, no decoding; without a file 320 x 180 | fact |
| `object-fit` | as `<img>`; no `object-position` | fact |
| `border-radius` | the background and border are rounded; the frame and poster keep square corners, as a picture does | fact |
| `play(id)`, `pause(id)`, `stop(id)`, `restart(id)` | built-in handlers | fact |
| `onended`, `onerror` | `onended` only without `loop`; `onerror` — no file, a failure, or no frame within 5 s | fact |
| `Window.Video(id)` | `Play`, `Pause`, `Stop`, `Restart`, `Volume`, `IsPlaying`, `Duration` (from the header) | fact |
| Decoding | only while the element is painted; off screen it unloads and starts again from the beginning (no seek) | fact |
| Pause and stop | unload the decoder; resume from the beginning | fact |
| Limit | 2 decoders per window; a third waits, one line in problems | fact |
| Texture | in the window budget (256 MB, 4096 entries); let go through a 0.75 s queue | fact |
| Audio | `muted` sets `AudioGain` to 0 (the decoder still runs); following the game's volume setting is not confirmed | not verified |
| Missing | `controls`, seek, pause with position, rounded frame, `object-position`, `<source>` | — |

## 4. CSS: values, selectors, cascade, properties

### 4.1. Units and values

| Kind | Supported | Notes |
|---|---|---|
| `u` | yes | the main canvas unit (section 1.2) |
| `px` | yes | value as written (section 1.2) |
| `em` | yes | the element's font size; for `font-size` — the parent's font; `letter-spacing`, `word-spacing`, `text-indent`, `text-shadow` are computed on the element that wrote them (per AUTHORING; verified in code: `StyleEngine.cs`, lines 1017–1019) |
| `%` | yes | meaning depends on the property: from the containing block, from free space (`background-position`), from the picture's side (`border-image`), from the font size (`text-decoration-thickness`), from the scroll height (`sticky`) |
| `fr` | yes | only in `grid-template-*` |
| `auto` | yes | everywhere the property allows it |
| `0` without a unit | yes | the number `0` is a length; other bare numbers are not lengths (`Core/CssValues.cs`, `Length.TryParse`). Bare numbers are for `line-height`, `flex-*`, `opacity`, `scale`, `layout-fps`, etc. |
| `rem`, `vw`, `vh`, `vmin`, `vmax`, `ch`, `ex`, `pt`, `cm`, `in` | no | `Length.TryParse` knows only `px fr em % u` (verified in code: `CssValues.cs`, line 24) |
| `calc()`, `min()`, `max()`, `clamp()` | no | not parsed (verified in code: there are no such functions in `Core/`) |

Numbers: `1.5`, `-2`, `.5` — with a dot, no comma (verified in code: `float.TryParse` with `InvariantCulture`).

### 4.2. Colors

- Named (verified in code: `CssValues.cs`, lines 60–66): `transparent`, `black`, `white`, `red`, `green` (#008000), `blue`, `yellow`, `orange`, `gray`/`grey` (#808080), `cyan`, `teal`, `magenta`, `lime`, `silver`, `navy`, `maroon`, `purple`. Case does not matter. (AUTHORING was brought in line 2026-11-16: it now lists all 18 names, including `silver`, `navy`, `maroon`, `purple`.)
- Hexadecimal: `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa` (verified in code: `TryHex`).
- Functions: `rgb(r, g, b)`, `rgba(r, g, b, a)`; channels — numbers 0–255 or percentages; alpha — 0–1 or a percentage; separators — comma or space, alpha after `/` (verified in code: `Rgba.TryParse`).
- `hsl()`, `hwb()`, `lab()`, `color-mix()` — not supported (verified in code).
- `currentcolor` — the text color of this element (verified in code: from the default values of `border-*-color`, `outline-color`, `text-decoration-color`, `text-shadow`).
- Transparency for transitions: from `transparent`, the color does not "darken" — the target color is used (per AUTHORING; `Core/Animation.cs`).

### 4.3. Variables and keywords

- Custom properties: `--name: value;`. They inherit. They can reference each other (`var(--a)` inside `--b`).
- `var(--name)` and `var(--name, fallback)` are substituted recursively (verified in code: `StyleEngine.cs`, `Replace var`, lines 1044–1060). If the variable is missing and there is no fallback, the declaration is dropped with a note (`StyleEngine.cs`, line 958).
- `var()` inside `@keyframes` is not substituted (verified in code: comment on `@keyframes`, `StyleEngine.cs`, line 846).
- The keywords `inherit`, `initial`, `unset` are supported (verified in code: `StyleEngine.cs`, branches `inherit`, `initial`, `unset`).
- `!important` is supported (per AUTHORING and section 4.7).

### 4.4. Selectors and combinators

Supported (verified in code: `Core/CssParser.cs`, `ParseSelector`, and `Core/StyleEngine.cs`):

- Type: `div`, `button`; `*` — any element.
- Identifier: `#id`; class: `.cls` (several classes in a row — `.a.b`).
- Attribute: `[name]` and `[name=value]` (the value may be quoted).
- Combinators: descendant (space), `>` (child), `+` (next sibling), `~` (any following sibling). They can be mixed: `.x > .y + .z ~ li`. Two combinators in a row are a rule error (`CssParser.cs`, line 455).
- Comma-separated list: `a, b, c { … }`.

Not supported (rule error with a note):

- Attribute operators `[a^=…]`, `[a$=…]`, `[a*=…]`, `[a~=…]`, `[a|=…]` (verified in code: `CssParser.cs`, line 478, "only [name] and [name=value] are supported").
- Pseudo-elements other than `::before` and `::after` (`::first-line`, `::marker`, `::selection`, `::placeholder` — error, verified in code: line 503).
- Namespaces, `@` rules inside a selector, escaping in selectors (not verified in code).

Specificity (verified in code: `CssParser.cs`, counting `ids`, `classes`, `tags`):

- id — 1 0 0; class, attribute, and pseudo-class — 0 1 0; type and pseudo-element — 0 0 1. Combinators add no weight (per AUTHORING).
- `:not(X)` — the weight of its argument. `:has(X)` — the weight of its argument (`CssParser.cs`, line 516).
- Order: the later declaration wins at equal specificity (per AUTHORING and section 4.7).

### 4.5. Pseudo-classes and pseudo-elements

Pseudo-classes (verified in code: `Compound.KnownPseudos`, `CssParser.cs`, line 135, and parsing of `:not`, `:has`, `:nth-child`):

| Pseudo-class | True when |
|---|---|
| `:hover` | the pointer is over the element or its descendant (per AUTHORING) |
| `:active` | the mouse button is pressed on the element |
| `:focus` | the element has focus (`NodeState.Focus`) |
| `:focus-within` | the element or its ancestor has focus (except the page root) |
| `:disabled` | the `disabled` attribute or the `Disabled` state (`CssParser.cs`, line 113) |
| `:checked` | the `checked` attribute is present (`CssParser.cs`, line 116) |
| `:root` | the page root |
| `:first-child`, `:last-child` | the first or last element among its sibling elements |
| `:nth-child(N)`, `:nth-child(odd)`, `:nth-child(even)` | position among sibling elements (verified in code: `CssParser.cs`, lines 121–131) |
| `:dragging` | the source of the current drag (`DragDrop`) |
| `:drag-over` | the drop target under the pointer during a drag, if it accepts the kind |
| `:not(X)` | X — one compound selector without combinators, without a list, without a nested `:not`, and without pseudo-elements (verified in code: lines 536–540) |
| `:has(X)` | X — one relative selector (`:has(.a)`, `:has(> .a)`, `:has(+ .a)`, `:has(~ .a)`, a chain with combinators); no lists, nested `:has`, or pseudo-elements (verified in code: lines 575–588) |

Limits (verified in code):

- `:nth-child(2n+1)` and similar `an+b` are not parsed: no match. (A note about it — (not verified in code).)
- `:link`, `:visited`, `:empty`, `:is()`, `:where()`, `:lang()`, `:first-of-type`, and the rest — rule error with a note (`CssParser.cs`, line 550: "pseudo-class :X is not supported").

State performance: `:hover`, `:active`, `:focus`, `:focus-within`, `:dragging`, `:drag-over`, `:has()` restyle only what the rule can see (section 10, `PERF.md`).

#### Pseudo-elements `::before` and `::after`

(per AUTHORING; verified in code: `CssParser.cs`, lines 496–512, `StyleEngine.cs`, `ResolveOne`, `PseudoTextKey`.)

- `.a::before { … }` draws a box before the element's content, `::after` — after it. The old single-colon form `:before` and `:after` is also accepted (comment in `CssParser.cs`, line 496).
- The box is in layout as the first or last child, but **it is not an element**: it is not in the tree, templates, handlers, and queries do not see it. Hover, press, and mouse on the box are events of the element itself; the box accepts no events of its own.
- One pseudo-element per selector and only at the end: `.a:hover::before` — allowed; `.a::before .b` and `::before::after` — error.
- The box is created only when `content` is not `none` or `normal`. Its style: `display` (default `inline`), `position: absolute` with `top`/`left`, `background`, `border`, `border-radius`, `box-shadow`, `width`, `height`, `padding`, `margin`, `transition`. Inherited properties are taken from the element.
- Element states also apply to the box: `.a:hover::before { color: #f88 }`.
- `content`: quoted strings with CSS escapes (`"\201C"`), `attr(name)` — the attribute value, `icon(name)` — a Lucide icon (like `data-icon`, section 7.2; if the rule did not set `font-family`, the font is `lucide`). Parts are concatenated in order: `content: "№ " attr(data-id)`. `none` and `normal` — no box. `counter()`, `counters()`, `url()` — no box and one note in the log (verified in code: `StyleEngine.cs`, line 757).
- Not supported: `::first-line`, `::first-letter`, `::marker`, `::selection`, `::placeholder`, `counter()`, `counters()`, `content: url(…)`.
- Example: `own/BBE_UI/UI/pseudo_demo.html` and `pseudo_demo.css`.

### 4.6. Properties

The full list of known properties — `StyleEngine.Initial` and `StyleEngine.Shorthands` (verified in code, `Core/StyleEngine.cs`, lines 196–251). Anything else gives the note `unknown property NAME` (`CssParser.cs`, lines 433–435) and has no effect.

Default values are from `Initial`. Whether a property inherits — section 4.7.

#### 4.6.1. Layout and box

| Property | Values | Notes |
|---|---|---|
| `display` | `block` (default for UA blocks), `inline` (default for everything else), `inline-block`, `inline-flex`, `inline-grid` (any `inline-*` — an atom of the line), `flex`, `grid`, `flow-root`, `none` | `table`, `table-row`, `table-cell`, `list-item` — block with a note (`LayoutEngine.cs`, lines 209–211). `flow-root` — a block whose children's margins do not collapse with its own margins: `BfcRoot` (set in `LayoutEngine.cs`, line 208) prevents margin collapsing at the top and bottom (lines 605–606). Unknown value — (not verified in code: whether a note is issued) |
| `position` | `static` (default), `relative`, `absolute`, `fixed`, `sticky` | `fixed` — like `absolute`, relative to the window (viewport). `sticky` — section 4.9 |
| `top`, `right`, `bottom`, `left` | length, `%`, `auto` | for `relative`, `absolute`, `fixed`; for `sticky` — only `top` and `bottom` |
| `width`, `height` | length, `%`, `auto` | `auto` — by content (for a picture — the file size, section 3.2) |
| `min-width`, `min-height` | length, `%`, `auto` (default) | |
| `max-width`, `max-height` | length, `%`, `none` (default) | |
| `margin` (+ `-top`, `-right`, `-bottom`, `-left`) | 1–4 values, `auto` | `auto` centers (per AUTHORING). Negative values — (not verified in code) |
| `padding` (+ sides) | 1–4 values, length and `%` | `%` — from the width of the containing block (per CSS; (not verified in code)) |
| `box-sizing` | `border-box` (default, **not as in the browser**: the browser uses `content-box`), `content-box` | `LayoutEngine.cs`, lines 450, 902, 1093 |
| `z-index` | integer, `auto` (default) | drawing order among siblings |
| `visibility` | `visible`, `hidden` | `hidden` keeps the space; inherited |
| `opacity` | number 0–1 | not inherited; affects the whole drawing group of the block and its descendants |
| `overflow` | 1–2 values | shorthand for `overflow-x` and `overflow-y`: `overflow: hidden auto` |
| `overflow-x`, `overflow-y` | `visible` (default), `hidden`, `auto`, `scroll` | any value other than `visible` clips to the rectangle (in code: `LayoutBox.ClipX`, `ClipY`, `LayoutEngine.cs`, lines 211–212). `clip` — like `hidden` (verified in code: `ClipX`/`ClipY` — any value other than `visible`; there is no scroll bar). Described in AUTHORING since 2026-11-16. `auto` and `scroll` give a scroll bar (section 4.6.3) |

Flex and grid (verified in code: `LayoutEngine.cs`; values per AUTHORING):

| Property | Values | Notes |
|---|---|---|
| `flex-direction` | `row` (default), `column`, `row-reverse`, `column-reverse` | |
| `flex-wrap` | `nowrap` (default), `wrap` | |
| `flex-flow` | shorthand `direction wrap` | |
| `flex` | shorthand `grow shrink basis` | |
| `flex-grow`, `flex-shrink`, `flex-basis` | defaults `0`, `1`, `auto` | |
| `order` | integer, `0` by default | |
| `align-items` | `stretch` (default), `flex-start`, `flex-end`, `center`, `baseline` | |
| `align-self` | `auto` (default), same as `align-items` | |
| `justify-content` | `flex-start` (default), `flex-end`, `center`, `space-between`, `space-around`, `space-evenly` | |
| `align-content` | `normal` (default), `stretch`; other values are read without effect | since 2026-11-16 in the list of known properties (`StyleEngine.Initial`); before that it gave `unknown property`. Works only with `flex-wrap: wrap`: `normal` and `stretch` stretch the lines over the full height (`LayoutEngine.cs`, line 1182). Described in AUTHORING |
| `gap`, `row-gap`, `column-gap` | length, `0` by default | for flex and grid; `gap` — shorthand |
| `grid-template-columns`, `grid-template-rows` | `none` (default), lengths, `fr`, `auto`, `min-content`/`max-content` (read as `auto`), `repeat(N, …)` | `LayoutEngine.cs`, line 1375 |
| `grid-column` | `auto` (default), `N`, `span N`, `N / M`, `N / span M` | |
| `grid-row` | — | not supported (decision 2026-11-16: we do not do it). Grid rows go in order, `grid-column` sets only the column; an explicit row number would require changing the auto-placement `Place`, and that is not "trivially next to grid-column" |
| `grid-area`, `justify-items`, `justify-self`, `place-*` | — | not known: a note |

Block buttons and sections: `tr` is a flex row, `td` and `th` are `flex: 1 1 0` (section 3.2).

#### 4.6.2. Picture sizes and `object-fit`

| Property | Values | Notes |
|---|---|---|
| `object-fit` (for `img`) | `fill` (default), `contain`, `cover`, `none`, `scale-down` | the picture is centered in the content box; there is no `object-position` (verified in code: not in `Initial`) |

#### 4.6.3. Scrolling

- `overflow: auto` — a thin bar over the right edge of the content. `overflow: scroll` with `padding-right` — a bar with its track in this field; the content does not extend onto it (per AUTHORING; (not verified in code: bar geometry)).
- The wheel scrolls, the thumb is dragged with the mouse, a click on the track moves it under the cursor (per AUTHORING).
- `scrollbar-color` — the thumb color; `scrollbar-width` — `thin`, `none`, `auto` (`Initial`, verified in code).
- `UiWindow.ScrollBy(e, dy)` and `GetScroll(e)` — scrolling from C# (section 8).

#### 4.6.4. Text and fonts

| Property | Values | Inherited | Notes |
|---|---|---|---|
| `color` | color | yes | default `#000000`; root (`:root` in UA) — `#ffffff` |
| `font-family` | name from the list | yes | see section 7.1 |
| `font-size` | `px`, `u`, `em`, `%` | yes | default `16px`. **Do not change it every frame**: each new size is a new game font (section 10) |
| `font-weight` | `normal` (400), `bold` (700), 100–900, `bolder`, `lighter` | yes | `bolder` and `lighter` are relative to 400; the parent's weight is not taken (per AUTHORING). 500 and 600 — only in Noto Sans; for others — the nearest (500 → 400, 600 → 700) |
| `font-style` | `normal`, `italic`, `oblique` | yes | a true italic exists only for PT Serif at regular weight; for the others — a slant (section 7.1) |
| `line-height` | `normal`, number (`1.3`), length | yes | `%`, `em`, `u` are inherited already in px (per AUTHORING; `StyleEngine.cs`, line 1019) |
| `text-align` | `left` (default), `center`, `right` | yes | no other values |
| `white-space` | `normal`, `nowrap`, `pre`, `pre-wrap`, `pre-line`, `break-spaces` | yes | `pre` keeps the line whole (no wrapping). `pre-wrap`, `pre-line`, and `break-spaces` wrap at spaces, and spaces in them collapse (`LayoutEngine.cs`, lines 321–327). Until 2026-11-16 `break-spaces` behaved like `pre` (did not wrap); fixed to behave like `pre-wrap`, test `TextWrapTests.cs` (group "text wrap"). Described in AUTHORING |
| `text-overflow` | `clip` (default), `ellipsis` | no | requires `overflow: hidden` and `white-space: nowrap` (per AUTHORING) |
| `text-transform` | `none`, `uppercase`, `lowercase`, `capitalize` | yes | |
| `letter-spacing` | `normal` or length | yes | added after each character, including the last one in the line (per AUTHORING); changes layout |
| `word-spacing` | `normal` or length | yes | added to each space (U+0020 and U+00A0) |
| `text-indent` | length or `%` | yes | first line of the block only; negative — hanging line |
| `line-clamp` (and `-webkit-line-clamp`) | integer greater than 0 or `none` | no | keeps n lines; the last one ends with `…` (per AUTHORING; `LayoutEngine.cs`, lines 625–628). `inline-block` items on the dropped lines are not hidden (audit C, item 12) |
| `-webkit-box-orient` | `horizontal`, `vertical` | no | only read, no action (verified in code: `Initial`) |
| `overflow-wrap` (and `word-wrap`) | `normal` (default), `break-word`, `anywhere` | yes | `break-word` breaks a word that does not fit even on an empty line; `anywhere` — also `min-content` down to one character; has no effect with `nowrap` and `pre` |
| `-bbe-ru-glue` | `auto` (default), `on`, `off` | yes | Russian non-breaking pairs, section 4.6.4a |
| `text-shadow` | `<x> <y> [<blur>] <color>` or `none` | yes | one **sharp** shadow; a `blur` greater than 0 is ignored (with a note); a comma-separated list — the first shadow is used (with a note); no `spread`; section 4.6.4b |
| `typewriter` | `none` or number of characters per second | yes | typewriter effect, section 4.8 |
| `vertical-align` | any | no | read, no effect (per AUTHORING) |
| `-webkit-line-clamp` | alias of `line-clamp` | | |

**4.6.4a. Russian glue (`-bbe-ru-glue`).** Verified in code: `Core/RuText.cs`; description per AUTHORING. In Cyrillic text, a line break never falls between two words if:

- the first word is a preposition or conjunction from the list `в к с у о и а я но на за по от до из не ни во ко со об да` and `без для под над при про что как или либо ибо чем` (any case; with no comma after it);
- the second is the particle `же бы ли ль ж б`;
- the second is a dash `—` or `–`;
- the first is a number, the second is a unit (`км м см мм кг г т л мл с сек мин ч шт % ₽ $ € руб г. тыс. млн` and others);
- the first is `№`;
- the first is an initial (`А.`) and the second starts with a capital letter.

(The word lists are Russian on purpose: they are the actual values the feature matches. The first list is prepositions and conjunctions, the second is short particles; the units are Russian units of measure and currency.)

A glued pair is one non-breaking unit; the text does not change (copy and selection see what is written). `auto`: the rules are on if the nearest `lang` on the element or an ancestor is `ru` or `ru-RU`, or if `lang` is nowhere; `lang="en"` and any other value turns them off. `on` enables them at any `lang`, `off` disables them.

Important: `auto` with no `lang` **enables** glue. This is an owner decision (audit C, item 4; `docs/HANDOFF.md`, question 1). Russian text line breaks in all windows are therefore slightly different.

**4.6.4b. Text shadow.** Verified in code: `Core/TextShadow.cs`, `StyleEngine.cs`, line 1257.

- Format: `<x> <y> [<blur>] <color>`; the color may be anywhere; without a color — `currentcolor` (the text color of the current frame, so a text color transition also changes the shadow).
- Lengths: `px`, `u`, `em`; `0` without a unit is allowed; `em` and `u` are converted to pixels on the element that wrote the shadow (`TextShadow.ResolveUnits`).
- The text is drawn a second time, offset by `(x, y)` in the shadow color, before the main text; the block's `opacity` and `scale` are taken into account.
- Not animated (per AUTHORING; `Core/Animation.cs`, animation property table).

#### 4.6.5. Background, pictures, and gradients

| Property | Values | Notes |
|---|---|---|
| `background-color` | color | default `transparent` |
| `background-image` | `none`, `url("file")`, `sprite(kind:id)`, `linear-gradient(…)`, `radial-gradient(…)`, comma-separated list of layers | gradients are drawn over the color; `url` and `sprite` — a picture; with several layers, the first is on top (per CSS) |
| `background-size` | `auto` (default), `cover`, `contain`, `<width> [<height>]` | `px`, `u`, `em`, `%` of the area; `auto` takes proportions from the other side |
| `background-position` | `left`/`center`/`right`, `top`/`center`/`bottom`, lengths, `%`, the form `right 10px bottom 4px` | default `0% 0%`; `%` — of free space (50 % — centered) |
| `background-repeat` | `repeat` (default), `no-repeat`, `repeat-x`, `repeat-y`, `space`, `round` | a separate word for each axis; limit of 1024 copies in a block (per AUTHORING) |
| `background` | shorthand: color, picture, repeat, position, size (`center / cover`) | the first layer from a comma-separated list; the rest are skipped with a note; the shorthand resets `background-size`, `-position`, `-repeat` |

Linear gradients (verified in code: `Core/Gradient.cs`; per AUTHORING):

- `linear-gradient(...)`: `to top`, `to right`, `to bottom`, `to left`, angles `0`, `90deg`, `180deg`, `270deg` (also `turn`, `rad`, `grad`); angles such as `to top right` — a diagonal line per CSS rules. Default `to bottom`.
- Stops: colors with alpha, positions in `%`, hard stops.
- The gradient follows the block's border box; the length of the band is set by `em`/`u` for stops in those units.

Radial gradients: `radial-gradient([circle|ellipse] [closest-side|farthest-side|closest-corner|farthest-corner|<length> [<length>]] [at <position>], stops)`. Default `ellipse farthest-corner at center`. Lengths — `px`, `u`, `em`; the position — `%`, `px`, `u`, `em` or `left/center/right/top/bottom`. Any number of stops.

Not supported: `conic-gradient`, `repeating-*`, color hints, a radius in `%` for a radial gradient. Such a layer gives a warning once, and the block's gradient is not drawn at all (per AUTHORING; `Core/Gradient.cs`, line 113).

Layers: `background-image: radial-gradient(...), linear-gradient(...)`. `url()` and `sprite()` in the list are skipped by the gradient and drawn as pictures (verified in code: `Gradient.cs`, line 122).

Rounded corners of a gradient — section 4.6.6 (corners are baked separately, each corner with its own ellipse; up to 11 sprites; radius up to 96 px of texture).

Limits (per AUTHORING and audit C): a gradient longer than 512 px of texture is stretched by the sampler; with smooth zoom the band and corners are taken from the nearest zoom step; no more than 64 baked bands, corners, and areas per frame, the rest go to the next frame without the gradient. Look in the game — (not verified in game).

A picture as background (`url()`, verified in code: `Render/Painter.cs`, `ImageCss`):

- A path from the page folder (`url(img/a.png)`, `url("../Images/a.png")`). The path cannot leave the folder with `filelist.xml` (`Core/ImageLayout.cs`, `ResolvePath`). Addresses `http(s)://`, `data:`, `file:`, and absolute paths are rejected with a note. `?` and `#` in the address are cut off.
- One layer. A picture has no rounded corners: the corners are square (section 4.6.6, limits).
- File not found or not a picture — a log line, and the block has no picture.
- The picture is read at first draw and kept in memory; one file — one texture per window; the file is re-read if it was saved (per AUTHORING; `UiImages.cs`, check once per second).
- `sprite(kind:id)` — game sprite (section 7.3).

#### 4.6.6. Borders, rounded corners, shadows, outline

| Property | Values | Notes |
|---|---|---|
| `border` | shorthand: `width style color` | |
| `border-width` | 1–4 values | shorthand |
| `border-color` | 1–4 values | shorthand |
| `border-style` | `solid` (default), `dashed`, `dotted`; others (`double`, `groove`, etc.) — as `solid` | `dashed` and `dotted` are drawn; one style for all four sides (the last entry with a style wins) (per AUTHORING) |
| `border-top`, `border-right`, `border-bottom`, `border-left` | shorthand: `width style color` | |
| `border-top-width` (and the three other sides) | integer number of screen pixels, at least 1 | a child flush against the border joins without a gap (per AUTHORING) |
| `border-top-color` (and the three other sides) | color; default `currentcolor` | on a rounded block — each side in its own color; the corner is split along the diagonal; a side with no width or a transparent color is not drawn (per AUTHORING) |
| `border-radius` | 1–4 values (order TL TR BR BL), ellipse via `/` | `%` — of the width for the horizontal radius and of the height for the vertical radius; adjacent corner radii that do not fit are reduced by the same factor (per AUTHORING) |
| `border-top-left-radius` (and the three other corners) | one value (circle) or two (ellipse) | |
| `border-image` | shorthand; see below | |
| `border-image-source` | `none` (default), `url(...)`, `sprite(...)` | the sprite is sliced whole (per AUTHORING) |
| `border-image-slice` | 1–4 numbers or `%`, `fill` | default `100%` |
| `border-image-width` | number (multiple of `border-width`), length, `%`, `auto` | default `1` |
| `border-image-repeat` | `stretch` (default), `repeat`, `round`, `space` | one word for both axes or two |
| `box-shadow` | `<x> <y> [<blur> [<spread>]] <color>` or `none` | one **outer** shadow; `inset` is not supported; limits: `blur` up to 48 px and radius up to 96 px of texture; does not change layout (per AUTHORING) |
| `outline` | shorthand: `width style color` in any order | `outline: 2px red` without a line style gives nothing (audit C, item 6) |
| `outline-width` | length (`px`, `u`, `em`) or `thin`/`medium`/`thick` (1/3/5 px) | integer number of screen pixels, at least 1 |
| `outline-style` | `none` (default); `solid`; `dashed`, `dotted` and others — a solid line; `none` and `hidden` (any case) — no outline | `dashed`, `dotted`, and others — a solid line with a warning once (per AUTHORING; `OutlineSpec.cs`) |
| `outline-color` | color; default `currentcolor` | |
| `outline-offset` | length, may be negative | |

Outline behavior (per AUTHORING; verified in code: `Core/OutlineSpec.cs`, `Render/Painter.cs`):

- The outline **takes no space**, is not inherited, and sits outside the border by `outline-offset + width`.
- It is not clipped by the block's own `overflow`, but it is clipped by the `overflow` of ancestors.
- On a rounded block the outline is rounded (the block's radius plus the offset and width).
- A neighboring block drawn later can cover the outline: there is no outline "on top of the whole stacking context", as in a browser.
- `:focus { outline: 2px solid …; outline-offset: 2px }` — a common pattern for fields and buttons.

Shadows and rounded corners (per AUTHORING and audit C):

- `box-shadow` and the outline use one radius: the largest of the smaller semi-axes of the corners. For different corners, the shadow and outline are rounded by the largest one.
- The shadow is drawn before the background; blur follows the block's shape with its radius; inside `scale` the shadow is sharp.
- A shadow that extends into a clip region is drawn even if the block nearly reaches the edge (audit C, item 13).
- Under a semi-transparent block the shadow shows through (the browser cuts it out under the block). This is a difference (per AUTHORING).

Rounding and clipping (per AUTHORING and `docs/ui/PERF.md`, section "Rounding by overflow"):

- `overflow: hidden` clips children **by the rectangle, not by the rounding**: children that reach into a corner of a rounded block are visible in the square corner.
- The clip geometry for rounding exists (`Core/RoundedClip.cs`), but drawing is not enabled: switching the draw target in the middle of a frame wipes the screen (verified with `ui_host`, `PERF.md`).
- A picture in a rounded card reaches into the corner. This is the same limitation.

- **Edge coverage (lane G, 2026-11-16; not verified in game).** The edges of rounded blocks, rings, corners, and circles (`UiDraw.Disc`, corner textures) are computed analytically: the distance from the pixel center to the edge gives coverage through one linear transition 1 px wide (`Core/Coverage.cs`, `Edge`). A straight side on the grid gives exactly 0 or 1, so an arc and a line of the same width match. The earlier 4×4 samples and the power correction of 0.55–0.7 were removed. Thin rings of 1–2 px: partly covered pixels are lifted with powers 0.8 (1 px) and 0.9 (2 px), `Coverage.ThinLift` (verified in code); the values were chosen by calculation, not fitted to a Chrome snapshot. There is no correction above 2 px; fully covered and empty pixels do not change. Limits: masks with a fractional radius are not made (the radius is rounded to an integer px, `Core/Corners.cs`, `CornerPx`); clipping by rounding is not enabled (section 11.1).

#### 4.6.7. Cursor, pointer, control colors

| Property | Values | Notes |
|---|---|---|
| `cursor` | `auto`, `default`, `pointer`, `grab`, `grabbing`, `text`, `vertical-text`, `move`, `all-scroll`, `*-resize`, `wait`, `progress`, `not-allowed`, `help`, `crosshair`, `zoom-in`, `zoom-out`, `context-menu`, `cell`, `alias`, `copy`, `no-drop`, `none` | inherited; a comma-separated list — the first known word is used; `url()` is not drawn. The game has seven cursors: hand (`pointer`, `grab`), `IBeam` (`text`, `vertical-text`), move arrows (`move`, `all-scroll`, `*-resize`), closed hand (`grabbing`), hourglass (`wait`), hourglass with a cursor (`progress`). The rest — the normal cursor (per AUTHORING; `CssCursor.cs`) |
| `pointer-events` | `auto` (default), `none` | `none` — the block is transparent to the mouse; inherited (verified in code: `Inherited`) |
| `accent-color` | color | the `progress` bar, checkbox, radio dot, slider thumb; inherited; default `#40c8be` |
| `caret-color` | `auto` or color | the field's caret; inherited |
| `scrollbar-color`, `scrollbar-width` | color; `thin`, `none`, `auto` | inherited (`scrollbar-color`) |

The cursor does not change over a `disabled` block (per AUTHORING). An unknown word — a warning once, and the normal cursor.

#### 4.6.8. Text: decoration

| Property | Values | Notes |
|---|---|---|
| `text-decoration` | shorthand: lines, style, color, thickness in any order | `none`, `solid`, `currentcolor`, `auto` reset. Not inherited as a value (per AUTHORING) |
| `text-decoration-line` | `none`, `underline`, `line-through`, `overline` (several separated by spaces) | |
| `text-decoration-color` | color; default `currentcolor` | animated in transitions |
| `text-decoration-style` | `solid` (drawn); `double`, `dotted`, `dashed`, `wavy` — drawn as a solid line with a warning | |
| `text-decoration-thickness` | `auto` (about 1/14 of the font size, at least 1 px), length, `%` of the font size | |

The line is drawn under/through all the text of inline content and nested elements, in the color of this element; it does not extend into `inline-block` and `position: absolute/fixed`. Text shadow does not get the decoration (per AUTHORING; `Core/TextDecor.cs`).

#### 4.6.9. Images and icons in CSS

| Property | Values |
|---|---|
| `content` | only `::before`/`::after`: strings, `attr()`, `icon()`, `none`, `normal` (section 4.5) |
| `font-family` for icons | `lucide` (section 7.2) |

#### 4.6.10. Shift, scale, other

| Property | Values | Notes |
|---|---|---|
| `translate` | `X Y` or `X` (lengths) | shift when drawing; layout does not change; the mouse zone moves with the block (per AUTHORING) |
| `scale` | one number (default `1`) | section 4.9; `0`, negative, and non-numeric values count as 1 (per AUTHORING) |
| `transform-origin` | any | **read, but has no effect**: the scale center is always the top-left corner of the border box (`StyleEngine.cs`, line 221). Recorded so a page written for the browser does not produce a warning |
| `layout-fps` | `auto` (default), number (times per second), `max` | how often layout is recomputed while lengths move; `auto` — the global value `BaroUi.LayoutFps` (default 30, `baroui anim fps N`), not the window frame rate (AUTHORING corrected 2026-11-16; the number is at least 1, `max` — every frame) |

Not supported at all (verified in code: these names are not in `Initial` or `Shorthands`): `float`, `transform` (rotation, skew, matrices), `filter`, `backdrop-filter`, `clip-path`, `mask`, `mix-blend-mode`, `will-change`, `user-select`, `inset`, `aspect-ratio`, `font` (shorthand), `grid-row`, `grid-area`, `justify-items`, `justify-self`, `place-*`, `object-position`, `background-clip`, `background-attachment`, `background-origin`, `word-break`, `hyphens`, `text-wrap`, `text-align-last`, `text-justify`, `direction`, `writing-mode`, `font-variant`, `font-stretch`, `list-style`, `counter-reset`, `counter-increment`, `border-top-style` and similar per-side variants, `outline-*` beyond those listed.

### 4.7. Cascade and inheritance

Verified in code: `Core/StyleEngine.cs`, `Core/CssParser.cs`.

1. The engine's built-in rules (UA sheet `UserAgentCss`) — lowest of all.
2. Page rules (`<style>`, `<link>`) — by specificity, then by order.
3. `!important` beats normal declarations; within one level — specificity, then order.
4. `style=""` — stronger than any selector (per AUTHORING).
5. States (`:hover` etc.) — ordinary pseudo-classes: they take part in the cascade when they match.

Inherited properties (verified in code: the `Inherited` set, `StyleEngine.cs`, lines 232–241):

`color`, `font-family`, `font-size`, `font-weight`, `font-style`, `text-align`, `line-height`, `white-space`, `visibility`, `text-transform`, `letter-spacing`, `cursor`, `pointer-events`, `scrollbar-color`, `accent-color`, `caret-color`, `typewriter`, `text-shadow`, `overflow-wrap`, `-bbe-ru-glue`, `word-spacing`, `text-indent`. Custom properties `--name` — inherited.

Not inherited (everything else): `opacity`, `text-overflow`, `line-clamp`, `text-decoration-*`, `box-shadow`, `outline-*`, `background-*`, `border-*`, `translate`, `scale`, `transition*`, `animation*`, `layout-fps`, `content`, layout properties.

Recalculation on state change (section 10): only what the rule can see is restyled.

---

### 4.8. Animations

(per AUTHORING; verified in code: `Core/Animation.cs`, `Core/Easing.cs`, `Core/CssParser.cs` (`@keyframes`)).

The engine computes animations itself, on every frame. Nothing needs to be called from code.

**What can change smoothly** (verified in code: the `Props` table, `Animation.cs`, lines 378–398):

| Group | Properties | How |
|---|---|---|
| opacity | `opacity` | number |
| colors | `color`, `background-color`, `border-top-color` and the three other sides, `outline-color`, `text-decoration-color` | color |
| shadow | `box-shadow` | offset, blur, spread, and color; only in `transition`, not in `@keyframes` (per AUTHORING) |
| shift and scale | `translate`, `scale` | without layout recalculation |
| lengths | `width`, `height`, `min-width`, `min-height`, `max-width`, `max-height`, `top`, `right`, `bottom`, `left`, `margin-*`, `padding-*`, `border-*-width`, `row-gap`, `column-gap` | move the layout; see below |

Not animated: `text-shadow`, `font-size`, `border-radius`, `background-size`, `visibility`, `z-index`, and the rest (in code: they are not in `Props`).

If one end is not a number (`auto`, `none`) or the units differ, the value changes at once (per AUTHORING).

**`transition`.** `transition: opacity 0.15s ease-out, background-color 0.15s;` or `transition: all 0.2s`.

- Easings: `linear`, `ease` (default), `ease-in`, `ease-out`, `ease-in-out`, `step-start`, `step-end`, `cubic-bezier(x1, y1, x2, y2)`, `steps(n)` (verified in code: `Easing.cs`).
- `transition: all` — all properties from the table above, including lengths (verified in code: `StartTransitions`, the `all` branch). Because of lengths, `all` can trigger layout; for list blocks an explicit list is better.
- A transition is started by the style the block moves into (as in CSS): write `transition` in the block's normal rule, not only in `:hover`, if both directions are needed.
- A new block appears at once, without a transition.
- An interrupted transition starts from the value where it stopped.
- Durations and delays — `s` and `ms` (per AUTHORING; (not verified in code: parsing of `ms`)).

Lengths and `layout-fps`: lengths move the block's layout. Values are taken on the `layout-fps` time grid (default 30 times per second), and layout runs only when the value has changed. Keep such animations on small blocks and short (up to 0.3 s).

**`transition: layout`** — smooth layout without recalculation per frame (verified in code: `Animation.cs`, line 120 — `spec.Flip`):

```css
.row { transition: layout 0.25s ease-out; }
.panel { transition: layout 0.2s ease-out, opacity 0.15s; }
```

- The block's position relative to its parent is animated (the block and everything inside it move together), and its size (the block is drawn at the intermediate size; the content is in the final layout, clipped by the border; text is not re-laid out during the motion).
- Turn on `layout` for the block that changes size and for neighbors that shift (usually for all list rows). A block without `layout` jumps at once.
- A frame costs the same as a normal draw: there is no layout recalculation.
- An interrupted motion continues from the place where the block is currently drawn.
- New blocks are not animated (for appearance use `opacity` and `@keyframes`). The first layout of a window and a change of screen size also have no transition.
- `transition: all` does not include layout blocks; `layout` is written explicitly.
- Do not put `layout` on rows of a long scrolling list with `data-for` (virtual): rows are recreated and may move to a new place.
- In the browser preview the motion is not visible: blocks jump at once.

**`animation` and `@keyframes`:**

```css
@keyframes rise { from { opacity: 0; translate: 0 12u } to { opacity: 1; translate: 0 0 } }
.panel { animation: rise 0.25s ease-out both; }
```

- Stops: `from`, `to`, `NN%`. Shorthands such as `margin` are expanded; `var()` is not substituted (verified in code: `StyleEngine.cs`, line 846).
- If `from` or `to` is not set, the value from the block's normal style is used.
- Repeats: a number or `infinite`. Direction: `normal`, `reverse`, `alternate`, `alternate-reverse`. `fill`: `none`, `forwards`, `backwards`, `both`. The words `running` and `paused` in the `animation` shorthand are accepted and do nothing; there is no separate `animation-play-state` property (verified in code: `Animation.cs`, line 270; `Initial`).
- The animation starts when the block appears (the page opens, a list row arrives, `data-if` turns the block on). It restarts if the block's `animation` value changes. A hidden `data-if` block starts again when it is shown.
- Shorthand: `animation: name time [easing] [delay] [repeats|infinite] [direction] [fill]`; by parts — `animation-name`, `animation-duration`, `animation-timing-function`, `animation-delay`, `animation-iteration-count`, `animation-direction`, `animation-fill-mode`.
- Staggered example: `.row:nth-child(2) { animation-delay: 0.05s }`.

**Typewriter.** `typewriter: 60` on a block with text shows the text at 60 characters per second. Line wrapping is already done on the full text, so the text does not jump. The window reports `IsTyping`; on the confirm key the owner calls `SkipTyping()` (verified in code: `UiWindow.IsTyping`, `SkipTyping`). It restarts when the text changes.

**Switch.** `baroui anim off` turns off all window animations: changes happen at once and the text is shown in full (section 9). Animation time follows the window's clock, not the world clock; (per AUTHORING: animations continue while the world is paused; `Timing.TotalTime`).

Not present: `transition` for `font-size` (each size is a new font); animation of `border-radius`; animation of `text-shadow`; `@keyframes` with `box-shadow`; `animation-play-state`; `animation-composition`; `transition-behavior`.

---

### 4.9. Scale, shift, and coordinates

(per AUTHORING; verified in code: `Core/UiTransform.cs`, `Core/ScreenMap.cs`, `Render/Painter.cs`, `EnterScale`/`LeaveScale`.)

**`translate`** — a shift in screen pixels when drawing; layout does not change; the block and everything inside it move together; the mouse zone moves too.

**`scale`** — scale of the block and everything inside it:

- The scale center is the top-left corner of the block's own border box **after** its `translate`: a point `p` inside the block is drawn at `corner + translate + N·(p − corner)`.
- Nested `scale` values multiply. Mouse, `overflow` clipping, and text stay consistent with the picture; text takes a font of the needed size and stays sharp.
- A block outside the screen is not drawn, as without scale.
- **This is not as in the browser.** In the browser the scale center is the middle. For the browser preview, add `transform-origin: 0 0` next to it.

Works inside a block with `scale` (per AUTHORING): background, borders, rounded corners, text (sharp, up to 160 px on screen, beyond that stretched), buttons, checkboxes, `range` sliders, input fields (caret and mouse selection), `textarea`, the `title` tooltip, hover, `:active`, clicks, `overflow: hidden` and scrollable blocks, `item`, `progress`, `canvas` (the callback draws in the same pixels, the result is scaled with the block; `SetCanvasMouse` reports positions in these same pixels).

Does not work inside a block with `scale` (per AUTHORING; what is not done does not pretend to work):

- `slot` (inventory cells): the game draws them by screen rectangle; they are not drawn, with a warning once per template line.
- `select`: list rows are drawn without scale; the list opens over the screen at the right place, with the row width and height as for the on-screen field; the row font is the normal size.
- `dialog`: opens over the screen without scale, without `translate` and without scrolling of ancestors; a warning once.
- Synthetic italic on a font without its own italic (JetBrains Mono, Caveat) is stretched by a matrix and looks blurry; PT Serif has a true italic.
- A `<canvas>` callback that reads the mouse itself (`PlayerInput`) or restarts the package computes in screen pixels: use `SetCanvasMouse`.
- `BoxOf(e)` returns the layout rectangle without `translate`, without animation shift, without scrolling, and without `scale`. The screen position is given by **`ScreenRect(e)`** (`Render/UiWindow.cs`, line 487): it accounts for `translate` (CSS or `SetTranslate`), the layout animation shift of the element and its ancestors, scrolling, and nested `scale` in the same order that `Painter` draws (`Core/ScreenMap.cs`). The caret and IME rectangle of a field, the `title` tooltip, the `select` list, the scroll bar, and the mouse zone of a slider are computed the same way.

**`position: sticky`** (per AUTHORING and `docs/ui/PERF.md`; verified in code: `ScreenMap.StickyOffset`, `LayoutBox.Sticky`):

- The block stays at `top` or `bottom` of the nearest scrollable ancestor (`overflow-y` not `visible`) and does not leave its parent block.
- Layout does not change: drawing and the mouse move (one formula in `ScreenMap`).
- Without a scrollable ancestor the block does not move.
- The `top`/`bottom` values — `px`, `u`, `em`, `%` of the scroll height.
- Example: `#head { position: sticky; top: 0 }` in a scrollable block.
- Not done: `left`/`right` (horizontal sticky); sticky inside elements with `transform: scale` (the shift is computed in unscaled coordinates) (`PERF.md`).

**`SetTranslate` and `SetScale` from C#** (section 8): a number replaces the CSS value of this element (like `element.style`), together with the transition and animation; no restyling and no layout recalculation. `NaN` and infinity are ignored; the number is not carried over to a copy (`Clone`). For everything that moves every frame, set `data-key` on `data-for` rows: without a key, the number lives at the row's position and will drift when rows are inserted or reordered.

---

## 5. Input: mouse, focus, keys, menus, drag and drop, files

General facts (verified in code: `Core/PointerRouter.cs`, `Core/KeyRouter.cs`, `Core/FocusOrder.cs`, `Core/Popup.cs`, `Core/DragDrop.cs`, `Core/FileInput.cs`, `Render/UiControls.cs`, `Render/UiWindow.cs`): a window's input goes through one router per window; game calls (`PlayerInput`, `DebugConsole`, `PauseMenuPrefix`) only pass events inside.

### 5.1. Mouse on elements: `data-mouse`, `SetMouse`

An element receives mouse events if it has `data-mouse="name"`. The owner sets the handler by name:

```csharp
window.SetMouse("card", e => { … });            // Func<UiMouseEvent, bool>
window.SetMouse("card", e => { … });            // Action<UiMouseEvent>: always answers true
window.ClearMouse("card");                       // remove; a capture it held ends with Cancel
```

(verified in code: `Render/UiWindow.cs`, lines 379–390.)

`data-mouse-events="down up click dblclick contextmenu wheel enter leave move hover"` — which events are needed. Without the attribute, all arrive except `move` and `hover` (`PointerRouter.cs`, lines 462–474; verified in code).

Kinds (`UiMouseKind`, verified in code: `Core/PointerEvents.cs`): `Down`, `Up`, `Move`, `Enter`, `Leave`, `Wheel`, `Click`, `DoubleClick`, `ContextMenu`, `Hover`, `Cancel`. What exactly `Hover` sends — (not verified in code).

Fields of `UiMouseEvent` (verified in code):

| Field | Meaning |
|---|---|
| `Kind` | kind of event |
| `Button`, `Held` | button of this event; all held buttons |
| `X`, `Y` | screen |
| `LocalX`, `LocalY` | point in the content of the `Current` element, in its pixels |
| `Width`, `Height` | size of the content of `Current` |
| `Target` | the deepest element under the pointer |
| `Under` | element under the pointer during a capture or drag (section 5.1, below); otherwise `null` |
| `Current` | whose handler is being called now |
| `Path` | chain of elements from `Target` upward |
| `Up(attr)` | nearest ancestor with attribute `attr` |
| `Data(name)` | value of `data-<name>` of the nearest element from `Target` |
| `Captured` | the event goes to the capture holder |
| `Inside` | the pointer is over the element |
| `Dragged` | the shift went beyond the threshold |
| `ClickCount` | 1, 2, 3 — for `Click`/`DoubleClick` |
| `StartX`, `StartY` | point where the press started |
| `Wheel` | wheel notches |
| `Ctrl`, `Shift`, `Alt` | modifier keys (field `Mods`) |
| `Related` | for `Enter`/`Leave` — the other element |
| `Time` | seconds |
| `ToLocal(el, out x, out y)` | point in the coordinate system of `el` |
| `AsCanvas()` | the same event as a `CanvasMouseEvent` |
| `Capture()` | capture the mouse manually |

Rules:

- **Bubbling.** The event starts at the deepest element under the pointer and goes up through ancestors with `data-mouse`. `true` stops the bubbling. On `Down`, `true` also **takes the capture**: `Move`, `Up`, and `Wheel` go to the holder everywhere (and outside the window) until all of its buttons are released.
- **`Esc`** cancels the capture: the holder receives `Cancel`. Nothing else is closed on that press.
- **`Enter` and `Leave`** do not bubble: each element entered gets its own event. Ancestors with `data-mouse` receive `Enter`/`Leave` even when the pointer is over a canvas (`CanvasMouse`).
- **`Move`** arrives only to those with `move` in `data-mouse-events`, and always to the capture holder.
- **`Click`** (and `DoubleClick`, `ContextMenu` for the right button) arrives when the button is released on the element (or inside it) where it was pressed, and without a drag beyond the threshold (section 5.2).
- **Capture and tooltips.** While the mouse is captured or a drag is in progress, `:hover` and `title` tooltips stay where they are (per AUTHORING).
- **`Under`.** The element under the pointer during a capture or drag. The capture holder and the ghost of the dragged element are shifted aside during the hit test, so they are not counted; `pointer-events: none` is skipped. Outside a capture and drag `Under = null`. `Target` does not change: it is the capture holder or the element under the ghost (per AUTHORING; `UiMouseEvent.Under` added 2026-11-15, audit lane C).
- **Not done:** the `onmousedown="…"` markup shorthand.

### 5.2. Drag threshold and `click`

- The threshold is 4 px (`PointerRouter.DragThreshold = 4f`, `PointerRouter.cs`, line 17).
- `Click` with `data-mouse` does not arrive if the pointer moved beyond the threshold between press and release. A card that was dragged is not counted as pressed (per AUTHORING).
- `onclick` (the old attribute) is a **separate** event; it does not depend on `data-mouse` and arrives even if the element was dragged: it is enough to release where the press started (per AUTHORING, section "Mouse on elements"). The rule "no click after a shift" does not apply to `onclick`.
- A right-click `ContextMenu` arrives when the shift is no more than the threshold (`PopupRules.ContextCarrier`).

### 5.3. Focus, Tab, and block keys: `tabindex`, `data-keys`, `SetKeys`

**Focus on click.**

- Pressing an element (or something inside it) with an explicit `tabindex` — any number, including `-1` — gives it the keyboard. The character is not controlled, as with a text field.
- Input fields and the code editor take focus on click themselves (section 3.5).
- Buttons, checkboxes, radios, sliders, and lists **without** `tabindex` do **not** take focus on click — this is deliberate: a click on a button must not stop the character. They take the keyboard only via Tab. An explicit `tabindex` enables focus on click too.
- A button inside a block with `tabindex` passes focus on click to the nearest ancestor with `tabindex`; the keys go to the block (per AUTHORING).

**Tab order** (`Core/FocusOrder.cs`, verified in code):

1. First, elements with a positive `tabindex`, ascending (for equal values — document order).
2. Then `tabindex="0"`, fields, buttons, etc., in document order.
3. `tabindex="-1"` is skipped by Tab, but it is focused by click and from code (`focus('id')`).
4. Shift+Tab goes backward, cyclically. The list of stops is rebuilt on each press; changing `tabindex` without a layout takes effect at once.
5. While a popup (`data-popup`) is open, Tab and Shift+Tab cycle through the stops inside it and do not leave for the page (section 5.9).

**Block keys.** `data-keys` lists keys, `data-key-name` gives the name to the handler:

```html
<div tabindex="0" data-keys="Enter Escape ArrowUp ArrowDown" data-key-name="list">…</div>
```

```csharp
window.SetKeys("list", e => { /* e.Key, e.Ctrl, e.Shift, e.Alt, e.Target, e.Current */ return true; });
window.SetKeys("list", e => { … });   // Action<UiKeyEvent>: always answers true
window.ClearKeys("list");
```

(verified in code: `Render/UiWindow.cs`, lines 407–414; `Core/KeyRouter.cs`.)

Names in `data-keys` (`KeyRouter.Parse`, verified in code; case does not matter):

| Name | Key | Name | Key |
|---|---|---|---|
| `Enter` (`return`) | Enter | `ArrowUp` (`Up`) | up arrow |
| `Space` | Space | `ArrowDown` (`Down`) | down arrow |
| `Escape` (`Esc`) | Esc | `ArrowLeft` (`Left`) | left arrow |
| `Delete` (`Del`) | Delete | `ArrowRight` (`Right`) | right arrow |
| `Backspace` (`back`) | Backspace | `Home`, `End`, `PageUp`, `PageDown`, `Tab` | |

The `UiKeyEvent` event (verified in code: `Core/KeyRouter.cs`): `Key` (`UiKey`), `Ctrl`, `Shift`, `Alt`, `Target` (the element in focus), `Current` (whose `data-keys` matched).

Rules:

- A press goes from the focused element up through the ancestors that asked for this key. `true` stops the bubbling and **cancels the default action** (Space on a checkbox, focus movement by Tab, closing by Esc).
- **Text-field editing keys do not go past `data-keys`** (`KeyRouter.EditsText`, verified in code): Delete, Backspace, Left, Right, Home, End — always to the field; in a `textarea` also Enter, Up, Down, PageUp, PageDown; in a number field Up and Down (step). Other keys (Esc, Tab, Space, Enter in a single-line field) go to `data-keys` first.
- **Only with focus.** Keys reach the window while one of its elements has focus — this is how the game works: a single subscriber takes the keyboard, and the character must not lose control while nothing is selected (per AUTHORING). Without focus a key block receives nothing. For a map and lists give the root `tabindex="0"` (or `-1`).
- There is no key release (`keyup`).

Mouse and keys in one block: keyboard focus and the mouse are independent; a click on an element without `tabindex` does not change focus (section 5.3, above).

### 5.4. Mouse on `<canvas>`

`Window.SetCanvasMouse("pad", e => { … })` — the canvas mouse handler. Drawing is still `SetCanvas`; the mouse can be attached without drawing (per AUTHORING; `Render/UiCanvasInput.cs`, `Core/CanvasMouse.cs`).

| `e.Kind` (`CanvasMouseKind`) | When |
|---|---|
| `Move` | the pointer moves over the canvas (the first one — entry); during a drag — anywhere on the screen |
| `Down` / `Up` | button pressed / released on the canvas (`e.Button`: `Left`, `Right`, `Middle`; `e.Left`, `e.Right`, `e.Middle`) |
| `Wheel` | wheel over the canvas or during a drag; `e.Wheel` — notches, up is positive |
| `Leave` | the pointer left the canvas with no buttons pressed (to clear a highlight) |
| `Cancel` | the drag was taken away: the window closed, the canvas disappeared, or a dialog covered it; `Up` will not come |

Fields of `CanvasMouseEvent`: `X`, `Y` — pixels from the top-left corner of the canvas **content** (the same as `Rectangle` in `SetCanvas`; during a drag they may go outside `0..Width`, `0..Height`); `Width`, `Height`; `Ctrl`, `Shift`, `Alt`; `Held`; `Captured`; `Inside`; `Time`.

- **Capture.** A button pressed on the canvas, with `true` from the handler, holds the mouse: `Move`, `Up`, `Wheel` go to the canvas until all such buttons are released — both outside the canvas and outside the window. While a drag is in progress, the whole screen's mouse belongs to the window; the character is not controlled. A second button pressed during a drag joins it.
- **Response** (`Func<CanvasMouseEvent, bool>`): for `Down`, `true` — the press is accepted (a drag starts), `false` — the canvas does not take it; for `Wheel`, `true` — the wheel was used, `false` — the page under the canvas scrolls. The `Action<CanvasMouseEvent>` overload answers `true` always.
- `ClearCanvasMouse(id)` removes the handler (a drag ends with `Cancel`).
- **Double press is counted by the owner.** `ClickCounter.Count(e)` gives 1, 2, 3 (and 1 again) by time (`Interval = 0.4` s) and place (`Distance = 6` px) for consecutive `Down` events of the same button (`CanvasMouse.cs`, lines 180–181).
- The canvas accepts the mouse only within its bounds; outside them the game receives the mouse as before. `cursor` does not change. The canvas does not get the keyboard: map keys are read by the owner through `PlayerInput`.
- An error in the handler removes it and writes a line to the log; the window does not close (per AUTHORING).

```csharp
Window.SetCanvas("map", Draw);
var clicks = new ClickCounter();
Window.SetCanvasMouse("map", e =>
{
    switch (e.Kind)
    {
        case CanvasMouseKind.Down when e.Left: if (clicks.Count(e) == 2) { Open(NodeAt(e.X, e.Y)); } else { Grab(e.X, e.Y); } return true;
        case CanvasMouseKind.Move when e.Captured: Drag(e.X, e.Y); return true;
        case CanvasMouseKind.Wheel: Zoom(e.Wheel, e.X, e.Y); return true;
    }
    return true;
});
```

**Shift and scale as numbers** (`SetTranslate`, `SetScale`) — for everything that moves every frame; see sections 4.9 and 8.

### 5.5. Modal `dialog`

- `<dialog id="filters" closedby="any">…</dialog>`: by default `display: none`; when opened (`showModal('filters')` or `Window.ShowModal`) — block, over the page (`z-index: 1000`) (verified in code: UA, `Render/UiControls.cs`, `ShowModal`).
- Without `top`/`left`/`right`/`bottom` — centered. With them — relative to the nearest `position: relative` ancestor (per AUTHORING).
- `closedby="any"` — a press outside the window closes it (`UiControls.cs`, lines 361–363; verified in code).
- Backdrop: a press outside the dialog does not reach the game or the page beneath it (`UiControls.HitOverlay`, verified in code). When a `dialog` is open, input goes to it.
- `Esc` closes the topmost dialog (section 5.8).
- `closeDialog()` and `closeDialog('id')` (built-in) and `Window.CloseDialog(id?)` close the dialog; `onclose` fires (`UiControls.cs`, line 654).
- Opening a `dialog` from code closes an open popup (per AUTHORING).
- `autofocus` inside an open dialog takes focus on opening. Focus inside an open `data-popup` menu is not taken by `autofocus` (audit D, item 3).
- Inside a block with `scale` the dialog opens without scale, without `translate`, and without scrolling of ancestors (per AUTHORING; section 4.9).

### 5.6. Code editor: `<textarea>`

A multi-line editor drawn by the engine (per AUTHORING; `Render/UiCodeEdit.cs`, `Core/CodeEdit.cs`, `Core/CodeLexer.cs`).

```html
<textarea id="code" data-lang="xml" data-model="text" oninput="edited()" onsave="save()" tabsize="4" placeholder="Пусто"></textarea>
```

The text is `value`: the tag content (as in HTML, the first line break is not counted), the `value` attribute, `data-model` (two-way; a write to the model happens at most once per frame), or `Window.GetCodeEdit("code").SetText(…)`.

| Attribute | What it does |
|---|---|
| `data-lang` | `xml`, `lua`, `cs` (or a file name, `x.xml`) — highlighting; without it, the text has no colors |
| `data-model`, `value`, `placeholder`, `readonly`, `disabled` | as for `input` |
| `tabsize` | tab width in characters (default 4; allowed 1–16) |
| `data-tabs="tab"` | Tab inserts a tab character, not spaces |
| `data-autoindent="off"` | Enter does not copy indentation |
| `data-gutter="off"` | no line numbers |
| `oninput` | after an edit (one call per frame) |
| `onchange` | when leaving the editor, if the text changed since entry (`UiCodeEdit.cs`, line 199) |
| `onsave` | Ctrl+S (`UiCodeEdit.cs`, line 265) |

- **Size and look.** Without CSS: `30em × 12em`, border, `overflow: hidden` (its own scrolling). The font is **monospace** (default `font-family: mono`): the character width is measured once, and columns, caret, and mouse are computed from it. Line height is `line-height`. There is no wrapping: a long line scrolls sideways (Shift+wheel, bar at the bottom).
- **Colors** — custom properties, inherited: `--code-keyword`, `--code-type`, `--code-constant`, `--code-string`, `--code-number`, `--code-comment`, `--code-punct`, `--code-tag`, `--code-attr`, `--code-entity`, `--code-directive`, `--code-gutter`, `--code-line` (the caret line), `--code-selection`. Plain text — `color`; the caret — `caret-color`.
- **Keys.** Arrows; Ctrl+arrows — by word; Home — first character of the line, again — start; End; Ctrl+Home/End; PageUp/PageDown; Shift with all of these — selection. Tab — to the next stop; with a selection — indents the lines; Shift+Tab — removes indentation. Ctrl+Tab goes to the next field. Backspace in indentation steps back to a stop. Enter copies indentation; after `{ ( [` — one level deeper; between `{}` it puts `}` on a separate line. Ctrl+A, C, X, V. Ctrl+Z — undo; Ctrl+Y and Ctrl+Shift+Z — redo (up to 1000 steps; typing and deleting one character at a time — one step).
- **Mouse.** Click — caret; drag — selection (scrolls past the edge); double-click — word; triple — line; click on a line number — the line. Scroll bars can be dragged; a click on the track jumps; wheel — 3 lines.
- **Thousands of lines.** Only the visible lines are drawn; text is stored by lines; an edit touches only them. Highlighting is computed on demand, line by line, with the state at the end of each line; after an edit, what is being looked at is recomputed. Measurements (`tools/ui_test`, not in game): 20 000 lines — load 3 ms; 200 presses in the middle of the text — 0.1 ms; highlighting of the lower lines after an edit at the top — 6–8 ms once (per AUTHORING).
- **Line endings.** The model remembers the file's line ending (`CRLF` or `LF`, by majority) and writes it back; mixed text is aligned to the majority (per AUTHORING).
- **From code.** `Window.GetCodeEdit("code")` — the `CodeEdit` model (per AUTHORING): `Text`, `Caret`, `Anchor`, `GoTo`, `Select`, `Replace(from, to, text)`, `Undo`, `Redo`, `LineCount`, `Line(i)`. Its edits reach `value`, the model, and `oninput` on the next frame. `Window.RevealCaret(id)` — shows the caret.
- **Highlighting** is simple, line-based, not a parser: XML (tags, attributes, values, entities, comments, CDATA, `<?…?>`), Lua (words, long `--[[ ]]` and `[==[ ]==]`, numbers), C# (words and built-in types, `@"…"`, `"""…"""`, `/* */`, `#region`; a string with `$` is one piece, braces inside are not parsed) (per AUTHORING; `CodeLexer.cs`).
- Separately, `CodeLexer.Scan(language, line, state, list)` can be called (per AUTHORING).

### 5.7. Files: drag from Explorer and Ctrl+V

The game has no file-picker dialog, so there are two ways: **drag a file** onto the game window, or **paste** from the clipboard (per AUTHORING; `Render/UiFileInput.cs`, `Core/FileInput.cs`).

```csharp
Window.SetFileInput("sprite", f => { Load(f.Path ?? SaveImage(f)); return true; }, accept: ".png,.jpg");
```

```html
<div id="sprite" tabindex="0">Перетащите файл сюда или нажмите здесь и Ctrl+V</div>
```

- The target is a block with an `id`. `accept`: extensions (`.png,.jpg`), `image/*`, or `null` (any file).
- The handler receives a `UiFileInput`: `Source` (`Drop`, `PasteFile`, `PastePath`, `PasteImage`), `Path` (`null` for a pasted picture), `ImageData` (PNG bytes; if the clipboard had only a bitmap — BMP), `Extension`, `IsImage`, `Name`, `ReadBytes()`, `Target`, `Position`.
- A `false` response passes the file to the next target (the ancestor). `true` — accepted.
- **Dragging.** The game receives the `FileDropped` event (path) from SDL. A window takes the file if, at the moment of drop, the target block of this window or its descendant is under the pointer. A window that has targets and covers the point takes the file from the windows beneath it. The topmost window is the most recently shown.
- **Pasting.** A target gets focus on click and by Tab (`tabindex="0"`). Ctrl+V reads the clipboard in this order: files copied in Explorer (`CF_HDROP`); a picture (`PNG` format or a `CF_DIB` bitmap, for example a screenshot); text with a path (in quotes from "Copy as path", one line per file, `file:///…`). Whatever fits `accept` is taken.
- **Not present:** target highlighting during a drag (SDL sends no events while the file is held); drop points from SDL (the mouse position at the moment of drop is used); dragging from the window to Explorer; several files at once to one target (each arrives as a separate call).
- **Windows limitation.** If the game runs as administrator and Explorer does not, the system does not hand it the dragged files. The clipboard always works (per AUTHORING).
- `Window.Images.Add(key, bytes)` puts a pasted picture into the window's memory (section 6).

### 5.8. Esc

Esc comes from the game's interception (`BaroUi.PauseMenuPrefix`, `Api/BaroUi.cs`, lines 52 and 186; verified in code).

- Esc goes to the **topmost** window on screen: the last one in the window list that is open and was shown within the last 0.5 s. A window left open where it is not drawn does not take Esc.
- The game menu opens only when no free windows are open on screen.
- Inside a window, Esc is handled as a chain (per AUTHORING; audit D, item 7):
  1. cancel the mouse capture (drag);
  2. a popup (`data-popup`); if a `select` is expanded inside it, Esc closes the whole menu;
  3. the `data-keys` of the focused element (if it has `Escape`);
  4. an expanded `select` list;
  5. a modal `dialog`;
  6. the page window (`onclose`, closing).
- If a handler closed the window, the chain ends there (checked with `IsOpen`, audit D, item 7).
- The view's own step back: `BaroUiView.OnEscape()` (default `false`) is called after items 1–5 and **before** the window's `Escape` event and the close. `true` — the view stepped back by itself and the window stays; `false` — the window closes (`Api/BaroUiView.cs`, `Render/UiWindow.cs`, `OnEscape`; checked in code). The hooks are open to any view; a mod manager may use them, but its code is not in the engine.

### 5.8a. Mouse side buttons 4 and 5, Alt+arrows

Buttons 4 (back) and 5 (forward), and Alt+Left / Alt+Right, are handled by `BaroUi.NavigateByInput` (`Api/BaroUi.cs`) once per frame from `AddWindowsPrefix` (checked in code; **not verified in game**).

- Button 4 / 5 goes to the top window under the pointer (`ElementAt`). Alt+arrow goes to the topmost window on screen, as Esc does.
- The window first looks for the attribute `onback` / `onforward`: the element under the pointer and its ancestors, then the focused element and its ancestors (`Core/NavRoute.cs`, `NavRoute.Target`). The handler found runs through `Template.Dispatch`, as `onclick` does. Then `BaroUiView.OnBack()` / `OnForward()` is called (`UiWindow.Navigate`).
- If the window answered `true`, the side button of that frame is not passed to the game: a Harmony prefix on `PlayerInput.Mouse4ButtonClicked` / `Mouse5ButtonClicked` answers `false` until the end of the frame (`SideBackPrefix`, `SideForwardPrefix`). A button with the pointer outside the windows stays the game's.
- Limits: side buttons do not reach `data-mouse` (`MouseButtons` and `UiMouseEvent` have no buttons 4 and 5); a focused field does not take Alt+arrow; if the game read the button before the window in the same frame, there is no suppression (**not verified in game**).
- No back/forward history is kept by the engine: that is the window's job. A mod manager may return `true` from `OnBack` / `OnForward` and decide itself what back means (its code is not in the engine).

### 5.9. Popups and context menus: `data-popup`, `data-contextmenu`, `ShowPopup`
- An exception in a keyboard handler is written to the log and closes the window, as with a mouse error (`UiWindow.Fail`; audit D, item 11). What happens to exceptions from the game's key dispatcher after `Fail` — (not verified in game).

### 5.9. Popups and context menus: `data-popup`, `data-contextmenu`, `ShowPopup`

```html
<div class="menu" data-popup id="cardMenu" style="width: 160px">
  <button onclick="rename()">Переименовать</button>
  <button data-item onclick="remove()">Удалить</button>
  <button data-keep-open onclick="pin()">Закрепить</button>
</div>
<div class="card" data-contextmenu="cardMenu">…</div>
```

- A block with `data-popup` is hidden until it is shown: an engine rule with `!important` (`PopupPlace.Css`: `[data-popup]:not([open]) { display: none !important }`) — `#cardMenu { display: flex }` will not open it. An open one lies `position: absolute` above the page (`z-index` is set in the same line of `PopupPlace.Css`). Set a width: the width by content is computed from the parent (per AUTHORING).
- `Window.ShowPopup(id, screenX, screenY, anchor = null)` — at a point (context menu); `anchor` — the element it was opened for (returned by `Window.PopupAnchor`).
- `Window.ShowPopup(id, anchor, "below")` — at an element: `below`, `above`, `right`, `left` relative to `ScreenRect(anchor)`.
- If it does not fit — flip to the other side, then shift inside the screen (for a point — left and up from the cursor). The calculation is in `Core/Popup.cs`, `PopupPlace` (verified in code).
- `Window.ClosePopup(id = null)`, `Window.IsPopupOpen(id)`, the event `Window.PopupClosed(popup, reason)`, and the block's `onclose` attribute.
- Close reasons (`PopupCloseReason`, verified in code: `Core/Popup.cs`, line 7): `Outside` (press outside), `Escape`, `Chosen` (an item was chosen), `Code` (`ClosePopup`, the window was removed by `data-if`, a modal opened), `Replaced` (another was opened).
- One popup at a time: opening a second closes the first (`Replaced`). Opening the open one again just moves it. There are no nested submenus.
- **Press outside.** The left button closes the popup and **does not reach** the page (neither a click on what is underneath nor a focus change). The right button closes the popup and passes on, so another card's menu opens at once. A press inside the popup itself works normally. A click on a field or text in the popup does not close it (per AUTHORING).
- **Item** — a `button`, an `a` with `onclick`, an element with `data-item`, or any element with `onclick` inside the popup. After its `onclick` the popup closes (`Chosen`). `data-keep-open` on the item or on an ancestor keeps the popup open (verified in code: `Core/Popup.cs`, line 130).
- **Keyboard in the menu.** When opened, focus goes to the first item (`button`, `a` with `onclick`, `data-item`; `data-item` takes focus even without `tabindex`). Up, Down, Home, End move through the items cyclically when focus is on an item; for a text field, `select`, slider, radio, and multi-line field inside the menu these keys remain their own (audit D, item 4). Enter and Space press the item (as a click; the popup closes by the rule above). Tab does not leave the menu (section 5.3).
- An item whose `onclick` closed the menu and opened it again leaves the new menu open (audit D, item 10; `PopupState.Serial`).
- **`data-contextmenu="id"`** on an element or an ancestor: a right button pressed and released on it (shift no more than the threshold) opens the window `id` at the cursor.
  - If a `SetMouse` handler returned `true` on `ContextMenu`, the menu does not open: the owner opens its own menu via `ShowPopup(id, e.X, e.Y, card)`.
  - The menu does not open if the right press was taken by the capture holder (`true` on `Down`), or if it fell on a canvas with a mouse handler (`PointerRouter.RightTaken`) (audit D, item 1).
- **Layer.** The window is drawn above the page and above modal `dialog`s (a menu inside a dialog is visible). Opening a `dialog` from code closes the open popup.
- Drawn from zero offset, without scale and without scrolling of ancestors: inside a block with `scale` the menu opens at normal size (per AUTHORING).

### 5.10. Drag between blocks: `data-drag`, `data-drop`, `SetDrop`

A card is taken with the left button and dropped on another block of the page. There is no clone: the element itself moves (per AUTHORING; `Core/DragDrop.cs`).

```html
<div class="card" data-drag="item" data-drag-value="{{c.id}}" data-drag-name="cards">…</div>
<div class="slot" data-drop="item note" data-drop-name="slots"></div>
```

```csharp
window.SetDrop("slots", e =>
{
    // e.Phase: Enter, Over, Leave, Drop, Cancel; e.Kind, e.Value (data-drag-value), e.Source (the card), e.Target (the block with data-drop),
    // e.X/e.Y (screen), e.LocalX/e.LocalY (point in the target block), e.Ctrl/e.Shift/e.Alt
    if (e.Phase != UiDropPhase.Drop) { return true; }
    return Put(e.Target.GetAttribute("data-slot"), e.Value);   // true: accepted
});
window.SetDrop("cards", e => { if (e.Phase == UiDropPhase.Cancel) { Reset(e.Source); } return true; });  // the source learns about the cancel
```

(verified in code: `Render/UiWindow.cs`, lines 395–402; `Core/DragDrop.cs`, enum `UiDropPhase`: `Enter`, `Over`, `Leave`, `Drop`, `Cancel`.)

- One handler per name (as with `SetMouse` and `SetKeys`). The drop target's name is `data-drop-name`; the source's name is `data-drag-name` (it receives only `Cancel`).
- `data-drop` lists kinds separated by spaces; `*` accepts any. The target under the pointer is searched upward through ancestors; a block that does not accept the kind is skipped (the outer one is checked).
- **Start:** a press on an element with `data-drag` (or inside it) and a shift beyond 4 px. A press on `input`, `textarea`, `select`, `slot`, and a disabled element does not start a drag. If the press was taken by a `data-mouse` handler (`true`) or by a scroll bar, there is no drag either.
- **States for CSS:** `:dragging` on the source; `:drag-over` on the target under the pointer, only if it accepts the kind. Example: `.slot:drag-over { border-color: #40c8be } .card:dragging { opacity: .7; z-index: 50 }`.
- **Ghost** — the source itself, shifted by a numeric `translate` (`SetTranslate`) behind the pointer. Its CSS `translate` is replaced for the duration of the gesture, then restored. Ancestors with `overflow: hidden` will clip it: raise `z-index` in `:dragging`. Inside a scaled block the pointer offset is divided by the ancestors' scale (`DragDrop.GhostShift`); the card's own CSS `translate` stays (audit D, item 2).
- **What is under the pointer:** an element without the source (the source is moved off-screen during the check). The real element is always under the cursor, not the ghost. `:hover` freezes; `:drag-over` is highlighted.
- **Values** of `data-drag` and `data-drag-value` are remembered at the start of the gesture: all events, including `Cancel`, carry them (audit D, item 8).
- **End:** releasing over a target that accepts the kind sends `Drop`; `true` accepts. Any other outcome (released outside, `false`, Esc, the source disappeared, a dialog closed the window) sends `Leave` (if it was over a target) and `Cancel` to the source. A target without a handler refuses.
- `Esc` cancels the drag, and only that (per AUTHORING).
- The element's `onclick` is not called after a drag, even if the button was released after Esc (per AUTHORING).
- An exception in a handler removes it and counts as a refusal. The response to `Enter`, `Over`, `Leave`, and `Cancel` is ignored (per AUTHORING).
- A target that disappeared, or was closed by a dialog in the middle of the gesture, receives `Leave` and loses `:drag-over`. If the handler closed the window or cancelled the gesture in the frame of release, `Drop` does not come.
- **Do not mix** with the game's `<slot>` cell: that gesture is run by the game. Dropping a game item onto an HTML block is not done (per AUTHORING).

### 5.11. Blocking character control

(verified by description in AUTHORING and audit D, section "Windows without new features"; (not verified in game).)

- Character control is turned off: during a drag (the whole screen's mouse belongs to the window) and while focus is in an input field (keyboard belongs to the window).
- A click on a button, checkbox, radio, or slider without `tabindex` does not take focus and does not stop the character.
- A window that has neither focus nor a drag does not block the character.

---

## 6. Charts and drawing: UiDraw, `<curve>`, `<chart>`, `<gauge>`

Geometry and rules are in `Core/` (tests `DrawKitTests`, `ChartTests`, `tools/ui_test`); drawing is in `Render/UiDraw.cs`, `Render/UiCharts.cs` (verified in code). Example page: `own/BBE_UI/UI/charts.html` with data `charts.data.json`; snapshot: `tools/ui_host --page own/BBE_UI/UI/charts.html --data own/BBE_UI/UI/charts.data.json --frames 90` (per AUTHORING).

Common to charts: `curve`, `chart`, and `gauge` are blocks by the UA rule `curve, chart, gauge { display: block }` (added 2026-11-16, `Core/StyleEngine.cs`, `UserAgentCss`; before that they were `inline`). The size is set by CSS (`height`, `width`). `UI/charts.html` sets `display: block` itself; with the new rule this matches (verified in code; (not verified in game)).

### 6.1. Drawing set: `Window.Drawing` in `SetCanvas`

`Window.Drawing` (type `UiDraw`, `Render/UiDraw.cs`) is the same thing the `<curve>`, `<chart>`, and `<gauge>` elements use to draw themselves. Textures (lines, circles, corners) count against the window budget (section 10) and are released through the "graveyard" (a deferred-release list).

| Method | What it draws |
|---|---|
| `Stroke(sb, path, width, color, corner, dashOn, dashOff, continuePhase)` | polyline; joints are round; right-angle corners are rounded with a quarter ring of radius `corner` (px); `dashOn`/`dashOff` in px — a dash with a moving phase; `continuePhase` continues the phase of the previous line |
| `Dots(sb, path, radius, color)` | dots at the path's vertices |
| `Disc(sb, center, r, color)` | circle (radius in whole pixels) |
| `Ring(sb, center, outer, inner, color)` | ring |
| `Arc(sb, path, cx, cy, r, a0, a1, width, color)` | arc of thickness `width` |
| `Sector(sb, cx, cy, r, a0, a1, color)` | sector |
| `Area(sb, curve, baseY, color)` | solid area under the curve down to `baseY` |
| `Fill(sb, outline, color)` | any polygon |
| `Text(sb, fonts, style, text, x, baseline, color, align)` | text in the engine's font; `align` — `'l'`, `'c'`, `'r'`; `fonts` — `Window.Fonts` (public property `UiWindow.Fonts` since 2026-11-16, previously `internal`), `style` — `Window.Find("id").Style` (the element's `ComputedStyle`) |
| `Line(sb, a, b, width, color, cap)`, `Segment(…)`, `Bent(…)` | segments and a polyline with a corner |
| `Outline(sb, rect, t, color, dashOn, dashOff)`, `FillRound(sb, rect, r, color)`, `RingRound(sb, rect, r, t, color, dashOn, dashOff)` | rectangles with radius, border, dashed |

Angles in `Arc` and `Sector` are in radians: 0 — up, clockwise.

Notes (verified in code and per AUTHORING):

- `UiDraw` has no gradient (per AUTHORING).
- `UiPath` — a path: fill `path.Raw` (a list of `DPt` in canvas pixels) and reuse the list. The geometry is recomputed only when points, width, or radius change; a settled frame with no changes allocates no memory (per AUTHORING).
- `UiDraw` receives the window's texture budget (`TextureLedger`); `BeginFrame` is called by the window (the call frequency was not verified in code).
- `DashPhase` — the dash phase (property `UiDraw.DashPhase`).

```csharp
var line = new UiPath();
Window.SetCanvas("pad", (sb, r) =>
{
    line.Raw.Clear();
    line.Raw.Add(new DPt(0, r.Height)); line.Raw.Add(new DPt(r.Width / 2, 0)); line.Raw.Add(new DPt(r.Width, r.Height));
    Window.Drawing.Stroke(sb, line, 2, new Color(63, 182, 168), corner: 8);
});
```

Pictures for the canvas — `Window.Images` (section 8.4; `Draw`, `DrawRegion`, `Get`, `Add`, `Peek`, `Release`, `Clear`; formats PNG, JPG, BMP, GIF, TGA via the game's loader — per AUTHORING). `ImageFit` — `Contain`, `Cover`, `Stretch`, `Actual`, `ScaleDown` (verified in code: `Core/ImageLayout.cs`, line 8; `ScaleDown` is the same as `object-fit: scale-down`, described in AUTHORING since 2026-11-16).

### 6.2. `<curve>`: editable curve

```html
<curve data-model="settings.damageByDepth" x-min="0" x-max="5000" y-min="0" y-max="3" snap="250 0.1" mode="smooth" monotone max-points="8"></curve>
```

(per AUTHORING; `Render/UiCharts.cs`, `Core/CurveEditor.cs`, `Shared/PointCurve.cs`.)

- The value is the text `x:y, x:y` (as `BBE.Curves.PointCurve`; the game reads it with the same parser). Without `data-model` — the `value` attribute.
- The value can be a list of points (since 2026-11-16, `CurveSource` in `Core/ChartModel.cs`, `Read`/`Write`; verified in code): a list or an array, elements — `DPt`, a pair `(x, y)`, an array or list of two numbers, a dictionary with keys `x`/`y` (or `X`/`Y`), an object with numeric properties `X`/`Y`. Edits are written back in the same shape: a list is edited in place (grows and shrinks), an array of the same length — in place, a different length — a new array of the same type. A model of another shape is not read: the curve is empty (a line is written to the log, `UiCharts.cs`, line 360). Shape tests — `tools/ui_test/ChartLaneTests.cs` (group "chart lane": list, array, pairs, dictionaries, objects with `X`/`Y`, bad elements, NaN); (not verified in game).
- `mode`: `linear` (default), `smooth` (monotone, no overshoot), `step` (the value holds until the next point). The mode is not stored in the text.
- Range attributes: `x-min`, `x-max`, `y-min`, `y-max`; `snap="sx sy"` — grid step; `monotone` — `y` does not decrease; `max-points` (default 16), minimum two points.
- **Mouse:** drag a point; double-click on empty space — a new point; right-click on a point — remove it.
- **Keyboard** (Tab lands on the curve): arrows move the selected point by one grid step (without `snap` — a hundredth of the range); Delete or Backspace removes the point.
- Rules: the first and last points keep their `x` (they move along `y`); points do not pass their neighbors; `y` is in `[y-min, y-max]`.
- Events: `oninput` — on every movement; `onchange` — on release, on adding, removing, and on an arrow step.
- Look (CSS): `--series-1` line color, `--stroke-width` thickness, `--point-size` point diameter, `--point-color` point color, `--series-2` ring of the selected point, `--grid-color` grid, `background-color`, `border`, `height`.
- Not verified: the curve's keys in the game (`ui_host` does not deliver keys to focus; the game's self-test checks them — (not verified in game)).

### 6.3. `<chart>`: chart from model data

```html
<chart data-series="доход: stats.income; расход: stats.loss" type="area" format="0" y-min="0"></chart>
```

(per AUTHORING; `Render/UiCharts.cs`, `Core/ChartScale.cs`.)

- `data-series` — series as `name: path` separated by `;`. The path is read as in `data-model` (a list of numbers). Values that are not numbers are skipped.
- `type`: `line` (default), `area`, `bar` (columns with rounded ends), `step` (verified in code: `ChartScale.cs`, line 120).
- Axes: ticks are "round" (1, 2, 5 × 10ⁿ); the axis extends to whole ticks. `y-min`, `y-max` set the bounds explicitly. `format` — a `.NET` format for labels (`0`, `0.0`, `0 %`).
- X axis (at the bottom, only if set; since 2026-11-16, `UiCharts.cs`, lines 470–475; verified in code): `x-labels="model.path"` — category labels, a list of strings (when crowded, every n-th is shown); or `x-min`/`x-max` — round numeric ticks from `x-min` to `x-max`, with categories spaced evenly between them (by default `x-min` = 0, `x-max` = number of points − 1). If both `x-labels` and `x-min`/`x-max` are set, the category labels win.
- Hover: a vertical line and a tooltip with the values of all series.
- A click on a point (bar) calls `onpick`; the point index is written to the `data-picked` attribute and passed to the handler as `$arg` (`onpick="pick($arg)"`; verified in code: `UiCharts.cs`, lines 669–670; since 2026-11-16).
- Data changes are smooth: series catch up to new values within a fraction of a second.
- Look (CSS): `--series-1`…`--series-6` series colors, `--stroke-width`, `--grid-color`; labels — `color` and the element's font.
- Not done: gradient fill, logarithmic axes, zoom with the wheel, keyboard focus on `chart` and `gauge` (per AUTHORING).

### 6.4. `<gauge>`: instrument

```html
<gauge data-model="sub.depth" min="0" max="700" zones="500" ticks="8" format="0"></gauge>
<gauge data-model="reactor.load" min="0" max="100" zones="60, 85" fill="sector"></gauge>
```

(per AUTHORING; `Render/UiCharts.cs`, `Core/GaugeMath.cs`.)

- The value is a number from `data-model` (without a model — the `value` attribute), clamped to `[min, max]`. The needle smoothly catches up to the value.
- `zones` — thresholds in ascending order, comma-separated: below the first threshold is zone 0, from it — the next zone. The needle color and the zone arc follow the zone of the value.
- `fill="sector"` — a filled sector up to the value instead of only the needle.
- `ticks` — number of tick marks (default 5); `format` — labels of the ticks and the value.
- Arc from −135° to +135° (from the top, clockwise); the element's height is divided between the arc and the value label.
- Look (CSS): `--zone-0`…`--zone-3` zone colors, `--needle` needle, `--track-color` and `--track-width` arc, `--tick-color` tick marks.

### 6.5. Verified and not verified

Per AUTHORING and `docs/HANDOFF.md`:

- Verified: build (`cscheck`); `tools/ui_test` (rules, geometry, tick selection); `tools/ui_host` (the page draws; dragging a point and focus by click — yes).
- Not verified: the curve's keys; the game itself.
- Allocations (since 2026-11-16, lane H): a frame with no data changes allocates no memory — values are read into buffers, labels and the tooltip are rebuilt only when the scale, the hover index, or the data changes; the byte check on the hot path — `tools/ui_test/ChartLaneTests.cs` (verified in code and tests; not measured in game).
- What else allocates memory (outside the hot frame path; `Render/UiCharts.cs`, verified in code): arrays and `PointCurve` of `<curve>` in `smooth` mode on every change of points (lines 210–211: at every drag step); a new `CurveEditor` when the model changes (line 136); the series array when the number of points changes (line 502); label and tooltip strings when the scale or hover index changes; the `data-picked` string on click (line 669); `UiPath` paths — once per series and when `data-series` changes (line 431).

---

## 7. Fonts and icons

### 7.1. Families (`font-family`)

Verified in code: `Core/FontFamilies.cs`; per AUTHORING. The first known name in the list wins; quotes and case do not matter. An unknown name — JetBrains Mono.

| Family (internal name) | Names in CSS | Files | Weights | Notes |
|---|---|---|---|---|
| JetBrains Mono (default) | `mono` (default), `monospace`, `jetbrains mono`, `ui-monospace`, `consolas`, `courier new`, `courier`, `menlo`, `monaco` | `Fonts/JetBrainsMono-Regular.ttf`, `-Bold.ttf` | 400, 700 | monospace; character width is constant |
| Caveat | `caveat`, `cursive`, `handwriting`, `hand` | `Fonts/Caveat-Regular.ttf`, `-Bold.ttf` | 400, 700 | handwritten; italic is synthetic |
| PT Serif | `pt serif`, `serif`, `ui-serif`, `georgia`, `times new roman`, `times`, `newspaper` | `Fonts/PTSerif-Regular.ttf`, `-Bold.ttf`, `-Italic.ttf` | 400, 700, italic 400 | true italic only at regular weight |
| Noto Sans | `noto sans`, `sans-serif`, `ui-sans-serif`, `system-ui`, `sans`, `proportional`, `arial`, `helvetica`, `segoe ui` | **from the game folder**: `Content/Fonts/NotoSans/NotoSans-Regular.ttf`, `-Medium`, `-SemiBold`, `-Bold` | 400, 500, 600, 700 | proportional, with Cyrillic; read-only; no italic (synthetic) |
| Lucide | `lucide`, `icons`, `icon` | `Fonts/Lucide.ttf` | 400 | icons, section 7.2 |

Notes:

- Noto Sans is not copied into the package: if the file is not in the game folder, text is drawn in the monospace font. The log gets one line per family (per AUTHORING; audit C, item 14, found up to 4 lines and fixed it).
- Weights 500 and 600 exist only in Noto Sans. For other families it is the nearest: 500 → 400, 600 → 700 (verified in `FontFace` and per AUTHORING).
- Line height is computed from the metrics of the chosen font. Lucide's descent below the line is 0 (from its hhea) (verified in code: `FontFamilies.cs`, `Icons`; test `FontFamilyTests`).
- Character width: FreeType rounds widths, so in the game lines are slightly wider than in `ui_preview` (per AUTHORING).
- Each font size is a new, separate game font (about 8 MiB per "file, size" pair, `FontCache`). Do not change `font-size` every frame (section 10).
- Italic of a font without its own italic is stretched by a matrix — blurry (section 4.9).

### 7.2. Lucide icons: `data-icon`, `icon()`

(per AUTHORING; `Core/Icons.cs`, `Fonts/lucide-codepoints.json` (2379 names, counted), UA: `[data-icon] { font-family: lucide; font-weight: normal; font-style: normal; letter-spacing: normal; text-decoration: none }`.)

- `<i data-icon="trash-2"></i>` — the engine puts the icon glyph with this name into the element; `font-family: lucide` is set by the UA rule.
- Names as on lucide.dev: `check`, `x`, `plus`, `trash-2`, `settings`, `search`, `star`, `alert-triangle`, `chevron-down`, etc. The full list — `Fonts/lucide-codepoints.json`.
- An icon is text: its size is `font-size`, its color is `color`; `text-shadow` and `scale` work. The icon sits on the baseline and is 1em tall.
- The name can be bound: `data-icon="{{n.icon}}"`.
- An unknown name — no icon, one line in the log.
- Manually: `font-family: lucide` and the code point from the table.
- In `::before`/`::after`: `content: icon(name)`; if the rule did not set `font-family`, the font is `lucide` (section 4.5).
- The font is ISC-licensed, license `Fonts/LICENSE-Lucide.txt`. Not verified in game (per AUTHORING; (not verified in game)).

### 7.3. Game sprites: `sprite(kind:id)`, `<item sprite>`

(verified in code: `Render/UiSprites.cs`.)

| Kind | What it shows | Lookup |
|---|---|---|
| `talent:` | talent icon | `TalentPrefab` |
| `faction:` | faction icon | `FactionPrefab` |
| `mission:` | mission icon | `MissionPrefab` |
| `job:` | job icon | `JobPrefab` |
| `item:` | item (inventory icon or sprite) | `ItemPrefab` |
| `affliction:` | affliction icon | `AfflictionPrefab` |
| `ui:` | game interface icon | a named sprite of the styles (`GUIStyle.Sprites`), otherwise the first picture of the component style (`GUIStyle.GetComponentStyle`) |

Examples: `ui:speechbubbleicon`, `ui:outposticon`, `ui:wreckicon`, `ui:enemyicon`, `ui:guibuttonsettings` (per AUTHORING). If no such sprite exists — one line in the log, no icon. A sliced sprite gives the whole picture, not nine parts (`UiSprites.cs`, comment).

Where to use: `<item sprite="item:oxygentank">`, `background-image: sprite(ui:name)`, `<img src="sprite(item:oxygentank)">`, `border-image-source: sprite(ui:name)` (the last one is sliced whole; the game's nine parts are not taken, per AUTHORING).

### 7.4. Unicode symbols in JetBrains Mono

Per AUTHORING (verified against the font's character table, `cmap`; not re-checked in this pass):

- Present: `▲ ▼ ► ◄ ▶ ◀ ✓ ✗ ✕ × ⚠ ● ○ ■ □ ◆ ◇ ← → ↑ ↓ ≡ … • –`.
- Missing (empty square or skipped): `★ ☆ ♥ ✔ ✘ ⚙ ☰ ⌕ ✖`.
- Size, color, `text-shadow`, and `scale` work as for text. For other families (`font-family: serif`, etc.) the set is smaller; the fallback font for missing symbols was not verified (per AUTHORING: what is drawn in their place is unknown).

### 7.5. Picture icons

Raster sheet: `background-image: url("icons.png")` with `background-position` and `background-size`, like a sprite sheet: a block the size of an icon, `background-position: -32px -64px`, `background-repeat: no-repeat`. Softens at scale (per AUTHORING).

Other icon fonts (Material Symbols, Phosphor) are not connected. Lucide covers the same need.

### 7.6. Russian glue

Non-breaking pairs for Russian text (`-bbe-ru-glue`) are described in section 4.6.4a.

---

## 8. C# API: public types and members

Namespaces: `BaroUI.Api` (`BaroUi`, `BaroUiView`), `BaroUI.Core` (parsing, styles, input, geometry), `BaroUI.Render` (`UiWindow`, drawing, controls). All listed members are verified in code (`own/BBE_UI/CSharp/Client`), unless stated otherwise.

The stable public API (one line per member), the engine version and the rules for breaking changes: [`API.md`](API.md) (in Russian). Version check in a mod: `BaroUi.Requires("0.2.0", name)`.

### 8.1. `BaroUi` (static, `Api/BaroUi.cs`)

| Member | What it does |
|---|---|
| `Open(string htmlPath, BaroUiView view = null, string name = null)` → `UiWindow` | open a free window; it is updated and receives input |
| `Open<T>(string htmlPath, string name = null)` → `T` | the same, with a new instance of `T : BaroUiView` |
| `Create(string htmlPath, BaroUiView view = null, string name = null)` → `UiWindow` | item window: the owner adds it to the update list itself and closes it |
| `OpenPage(string page)` → `UiWindow` | open `UI/<page>.html` of the current package (or of the folder from `baroui src`) |
| `Windows` → `IReadOnlyList<UiWindow>` | open free windows |
| `Version` → `System.Version` | engine version (0.2.0), one constant; the same number is in `filelist.xml` |
| `Requires(string minVersion, string modName)` → `bool` | version check at start: on an older engine one log line and `false` |
| `Reload(UiWindow w)` → `string` | re-read the HTML and CSS in place |
| `event Action<UiWindow, UiWindow> Reloaded` | the window was replaced by a reload (argument order — (not verified in code)) |
| `Watch` (bool) | reload on save, and the error banner |
| `Animations` (bool) | all window animations on/off |
| `LayoutFps` (float, default 30) | global sampling rate of lengths (`layout-fps: auto`); `0` — every frame (`baroui anim fps N`, `Api/BaroUi.cs`, line 23; verified in code) |
| `PackageDir`, `PageDir` | the package folder and the pages folder |
| `Fonts` → `UiFonts` | window fonts |

Internal (not for pages): `Init`, `Dispose`, `Forget`, `AfterDraw`, `PauseMenuPrefix`, `AddWindowsPrefix`, `NavigateByInput`, `SideBackPrefix`, `SideForwardPrefix`, `SourceOverride` (`internal`).

The page model from files: `BaroUi.OpenPage(page)` (`baroui open <page>`) takes the model from `UI/<page>.data.json` next to the page when the window's view gave no model (checked in code, `Api/BaroUi.cs`; the reader is `Core/JsonModel.cs`). For `inputs` the self-test `InputsView` gives the model, so the file is not used in game; `ui_host --data` reads it. `pseudo_demo.html` reads no data, there is no file.

### 8.2. `BaroUiView` (abstract, `Api/BaroUiView.cs`)

The base class for window logic. `[UiHandler]` methods are handlers for `on*` attributes.

| Member | What it does |
|---|---|
| `Window` (`UiWindow`), `Document` | the window and its document |
| `protected virtual OnOpen()`, `OnUpdate(float dt)`, `OnAfterInput()`, `OnClose()` | lifecycle (section 2.3) |
| `Data` (object) | page data |
| `Set(string path, object value)` → bool | write one field and update |
| `Refresh()` | re-read the data (after changing the object) |
| `Find(string selector)`, `FindAll(string selector)` | CSS selector over the tree (the same parser as for styles) |
| `Text(Element e)`, `Text(Element e, string text)` | element text / set the text |
| `ToggleClass(e, cls, on)`, `HasClass(e, cls)` | classes |
| `Style(e, property, value)` | set one style property of the element (verified in code: `Api/BaroUiView.cs`, line 69) |
| `Attr(e, name, value)` | set an attribute of the element |
| `Append(parent, html)` → `List<Element>` | add markup into the element |
| `Remove(e)` | remove the element |
| `Send(source, Action send, float timeoutSeconds = 5)` | send to the server: blocks `source`, class `pending` until `Done` or timeout |
| `Done(source)` | reply received, remove the block |
| `Close()` | close the window |

### 8.3. `UiWindow` (`Render/UiWindow.cs`)

Created by `BaroUi.Open`/`Create`; `FromFile(htmlPath, fonts, name)` — static.

**State and data**

| Member | What it does |
|---|---|
| `Name`, `Document`, `Root` (`LayoutBox`), `IsOpen` | name, document, layout root, whether it is open |
| `Template`, `Handlers`, `View` | template, handlers (`[UiHandler]`), window class |
| `Data` | data (get/set) |
| `Find(string id)` → `Element` | element by `id` (id only; CSS selector — in `BaroUiView.Find`) |
| `Set(string path, object value)` → bool | write a field |
| `Refresh()` | data changed: rebind |
| `SetText(e, text)` | set the text |
| `ToggleClass(e, cls, on)` | class |
| `Invalidate()`, `Relayout()`, `Relayout(e)`, `RestyleElement(e)` | force restyle or re-layout |
| `Problems` (`IReadOnlyList<Diagnostic>`), `SourcePath` | page notes; file path |
| `UnitPx` | pixels in 1u |
| `IsTyping`, `SkipTyping()`, `IsAnimating` | typewriter; whether an animation is running |
| `Close()` | close the window |
| `AddToGUIUpdateList(int order = 0)` | for an item window: called by the owner from its `AddToGUIUpdateList` |
| `CaptureNextDraw(string file, string id = null)` | snapshot of a block after the next draw |
| `ElementAt(Point p)` → `Element` | element under a screen point |
| `BoxOf(e)` → `LayoutBox` | layout rectangle (without `translate`, animation shift, scrolling, and `scale`) |
| `ScreenRect(e)` → `Rectangle` | screen position with everything included (section 4.9) |
| `GetScroll(e)`, `GetScrollAbove(e)`, `ScrollBy(e, dy)` | block scrolling |
| `Click(e)` | programmatic click (`onclick` and `Clicked`) |

**Coordinates (section 4.9)**

| Member | What it does |
|---|---|
| `SetTranslate(e, x, y)`, `ClearTranslate(e)` | shift by number; replaces the element's CSS `translate`; no restyling |
| `SetScale(e, s)`, `ClearScale(e)` | scale by number (`0`, negative, `NaN` — one) |

**Drawing and canvas**

| Member | What it does |
|---|---|
| `SetCanvas(string id, Action<SpriteBatch, Rectangle> draw)` | drawing callback for a `<canvas>` |
| `Drawing` (`UiDraw`) | drawing set (section 6.1) |
| `Images` (`UiImages`) | pictures from files for the canvas (section 8.4) |
| `GetCodeEdit(string id)` → `CodeEdit` | code editor model |
| `RevealCaret(string id)` | show the caret |
| `SetInventory(string name, Inventory inventory)` | bind the game inventory to `<slot inventory="name">`; `null` gives the cells back to the game |

**Input (section 5)**

| Member | What it does |
|---|---|
| `SetMouse(string name, Func<UiMouseEvent,bool>)` / `(…, Action<UiMouseEvent>)`, `ClearMouse(name)` | `data-mouse` handler by name |
| `SetCanvasMouse(string id, Func<CanvasMouseEvent,bool>)` / `(…, Action<…>)`, `ClearCanvasMouse(id)` | canvas mouse |
| `SetDrop(string name, Func<UiDropEvent,bool>)` / `(…, Action<…>)`, `ClearDrop(name)` | drop target and drag source |
| `SetKeys(string name, Func<UiKeyEvent,bool>)` / `(…, Action<…>)`, `ClearKeys(name)` | `data-keys` keys |
| `SetFileInput(string id, Func<UiFileInput,bool>, string accept = null)` / `(…, Action<…>, …)`, `ClearFileInput(id)` | files: drag and Ctrl+V |
| `FocusId(string id)` | focus an element (built-in `focus('id')`) |
| `ShowModal(string id)`, `CloseDialog(string id = null)` | modal `dialog` |
| `ShowPopup(string id, float screenX, float screenY, Element anchor = null)` | popup at a point |
| `ShowPopup(string id, Element anchor, string placement = "below")` | at an element: `below`, `above`, `right`, `left` |
| `ClosePopup(string id = null)`, `IsPopupOpen(string id)`, `PopupAnchor` | close, check, the owning element of the menu |
| `event Action<Element, PopupCloseReason> PopupClosed` | close reason (section 5.9) |
| `event Action<UiWindow> Escape` | Esc reached the window (after the view's `OnEscape`; without a handler the window closes) |
| `protected virtual bool OnEscape()`, `OnBack()`, `OnForward()` (`BaroUiView`) | the step back for Esc, buttons 4 and 5 and Alt+arrows; `true` — handled (sections 5.8, 5.8a) |
| `event Action<UiWindow, Element> Clicked` | click on an element |

**Measurements (`PERF.md`)**

| Member | What it does |
|---|---|
| `LayoutCount`, `LastFullLayout` | how many times layout ran; whether the last one was a full layout |
| `LastStyleMs`, `LastLayoutMs`, `LastUpdateMs` | time of the last frame by stage, ms |
| `ProbeHost(double ms)`, `ProbeGame(double ms)` | accounting of host and game time |

**Budget constants**: `WindowBudgetBytes = 256 MiB`, `WindowBudgetCount = 4096` (verified in code: `UiWindow.cs`, lines 90–91).

### 8.4. `Window.Images` (`UiImages`, `Render/UiImages.cs`)

| Member | What it does |
|---|---|
| `Get(string path)` → `Texture2D` | a picture from a file; once per file; no file or broken — `null` and one log line. The file is checked once per second (`RecheckEvery = 1.0`); if saved again, it is re-read |
| `Add(string key, byte[] encoded)` → `Texture2D` | a picture not from a file (pasted, section 5.7) |
| `Peek(keyOrPath)`, `Release(keyOrPath)`, `Clear()` | take without loading, free, clear |
| `Count`, `Bytes` | number and size of pictures |
| `Draw(sb, path or Texture2D, Rectangle box, ImageFit fit = Contain, Color? tint = null, bool pixelPerfect = false)` | draw a picture |
| `DrawRegion(sb, tex, Rectangle source, Rectangle box, …)` | part of a picture (a cell of a sheet) |

`ImageFit`: `Contain` (whole, centered), `Cover` (fill, edges cropped), `Stretch`, `Actual` (1:1, centered), `ScaleDown`.

`ImageLayout` (static, `Core/ImageLayout.cs`): `Place(...)` — compute the place without drawing; `Cell(picW, picH, cols, rows, index, …)` → `(X, Y, W, H)` of a sheet cell; `ImagePlacement.PixelAt(px, py, out picX, out picY)` — which picture pixel is under a canvas point.

`pixelPerfect`: integer scales without smoothing (pixel art). The drawing batch is reopened for the duration of the call: use a few large pictures rather than hundreds of small ones (per AUTHORING).

Memory: all textures of a window are in one budget (section 10).

### 8.5. Event types and enumerations

| Type | Where | Contents |
|---|---|---|
| `UiMouseEvent`, `UiMouseKind` | `Core/PointerEvents.cs` | section 5.1: `Down`, `Up`, `Move`, `Enter`, `Leave`, `Wheel`, `Click`, `DoubleClick`, `ContextMenu`, `Hover`, `Cancel` |
| `CanvasMouseEvent`, `CanvasMouseKind` | `Core/CanvasMouse.cs` | section 5.4: `Move`, `Down`, `Up`, `Wheel`, `Leave`, `Cancel` |
| `ClickCounter` | `Core/CanvasMouse.cs` | `Count(e)` — 1, 2, 3; `Interval = 0.4`, `Distance = 6`; `Reset()` |
| `MouseButtons`, `KeyMods` | `Core/CanvasMouse.cs` | `Left`, `Right`, `Middle`; `Ctrl`, `Shift`, `Alt` |
| `UiKeyEvent`, `UiKey` | `Core/KeyRouter.cs` | section 5.3 |
| `UiDropEvent`, `UiDropPhase` | `Core/DragDrop.cs` | section 5.10: `Enter`, `Over`, `Leave`, `Drop`, `Cancel` |
| `UiFileInput` | `Render/UiFileInput.cs` | section 5.7 |
| `PopupCloseReason` | `Core/Popup.cs` | `Outside`, `Escape`, `Chosen`, `Code`, `Replaced` |
| `Diagnostic` | `Core/Dom.cs` | note: `File`, `Line`, `Message` |
| `NodeState` | `Core/Dom.cs` | state flags: `Hover`, `Active`, `Focus`, `Disabled`, `FocusWithin`, `DragOver`, `Dragging` |

### 8.6. Parsing and DOM (for tests and advanced cases)

| Type | Members |
|---|---|
| `Document` | `Root` (`#root`), `Diagnostics`, `Styles`, `File`, `ScreenViewport`, `GetElementById(id)` |
| `Element` | `Tag` (lowercase), `Children`, `Attributes`, `Id`, `Value`, `Checked`, `GetAttribute`, `SetAttribute`, `RemoveAttribute`, `Classes`, `HasClass`, `ToggleClass`, `Append`, `Insert`, `Remove`, `Clone`, `ElementChildren()`, `Descendants()`, `ElementIndex`, `TextContent`, `Style` (`ComputedStyle`), `Parent`, `Line`, `Hidden`, `State`, `SetTranslate`, `ClearTranslate`, `SetScale`, `ClearScale` |
| `TextNode` | `Text` |
| `Query` (`Core/Query.cs`) | `First(root, selector)`, `All(root, selector)` — the same CSS parser; `First` skips hidden elements |
| `Localizer` (`Core/Localizer.cs`) | `Texts` (function `id → text`; set by the game through `TextManager`), `Apply(doc, texts)` |
| `StyleEngine` | cascade (public through `Element.Style`); `CssParser` is internal, for tests (`API.md`) |

### 8.7. Shared types (`CSharp/Shared`)

| Type | What |
|---|---|
| `UiPlugin : IAssemblyPlugin` | mod initialization and unloading (`Initialize`, `Dispose`) |
| `UiInfo` | `Version = 0` (a leftover of stage 0; the engine version is `BaroUi.Version`), `AssemblyName`, `Ping(caller)` |
| `UiEcho` | (`internal`) echo channel to verify the connection |
| `BBE.Curves.PointCurve` (`PointCurve.cs`) | parsing and writing of the curve text `x:y, x:y` (verified per AUTHORING; (namespace — not verified in code)) |

### 8.8. What the API does not have

- No direct access from pages to C#: only `[UiHandler]` methods and attributes (section 3.6).
- No JavaScript, `eval`, network requests, `iframe`.
- `UiWindow.Fonts` — public property (`UiFonts`) since 2026-11-16, because `Window.Drawing.Text(sb, fonts, …)` accepts it (AUTHORING, section "Charts"). Before that it was `internal`, and the guide pointed to a member that was not in the public API. The `UiFonts` type is public (`Render/UiFonts.cs`).
- `BaroUi.All` — `private` (used inside `Api/BaroUi.cs`).

---

## 9. Tools

### 9.1. Game console commands

(verified in code: `Api/BaroUi.cs`, `OnCommand`, lines 210–283; the `baroui` command.)

| Command | What it does |
|---|---|
| `baroui open <page>` | open `UI/<page>.html` |
| `baroui close [page]` | close the page's window, or all windows |
| `baroui reload [page]` | re-read the HTML and CSS in place: data, C# class, subscribers, and scroll are kept; C# is not reloaded |
| `baroui list` | open windows (free and item, marked `(item)`) |
| `baroui watch [on\|off]` | on file save — reload the window and show the error banner |
| `baroui debug [on\|off]` | outlines of all blocks; under the mouse — margins (orange), padding (green), content (blue), selector, sizes in u and px, the line in the HTML; error banner |
| `baroui problems [page]` | all window notes to the log: `file:line: text` |
| `baroui anim [on\|off]` | window animations on/off |
| `baroui anim fps N\|max` | default sampling rate of animated lengths (30); `max` — every frame; a block's `layout-fps` takes precedence |
| `baroui src <folder>\|off` | take pages from a folder (for example `own/BBE_UI/UI`) instead of the installed package |

Usage line: `Usage: baroui open <page> | close [page] | reload [page] | list | watch [on|off] | debug [on|off] | anim [on|off|fps N|max] | problems [page] | src [dir|off]`.

Switches: `on`, `1` — enable; `off`, `0` — disable; no argument — toggle (internal function `Toggle` in `Api/BaroUi.cs`).

### 9.2. Watch, debug, problems

- `watch` polls page files (`PollWatch`) and reloads changed ones; errors — on the banner. The polling frequency — (not verified in code).
- `debug` enables `UiDebug.Outlines` (section 9.1).
- `problems` prints `w.Problems` of all windows or of one (`Core/Dom.cs`, `Diagnostic.ToString()`: `file:line: text`).
- Notes: every unsupported thing in CSS is a note (`CssParser`, `StyleEngine`), an unknown `data-t`, an unknown `prefab`, a `background-repeat` over the limit, and so on.

### 9.3. `tools/ui_host` and reference snapshots

- `tools/ui_host` — a console host (`ui_host.csproj`, `Program.cs`, `ModsScenario.cs`, `Script.cs`; `--data` reads through `own/BBE_UI/CSharp/Client/Core/JsonModel.cs`, the same code as `OpenPage`). It draws pages with a real `UiWindow` and `Painter` into PNG; the Noto Sans font is taken from the game folder (read-only) (per AUTHORING; verified from the folder contents).
- Build: `cd tools/ui_host && dotnet build -c Release && cd ../..` (per AUTHORING).
- Keys: `--page <file>`, `--data <json>`, `--frames N` (per AUTHORING and `snap.py`).
- Limitation: `ui_host` does not deliver keys to focus (AUTHORING, section "Charts").
- `tools/ui_host/snap.py` — snapshot comparison:
  - `PAGES` — list of pages: `controls`, `inputs`, `anim_demo` (from `own/BBE_UI/UI`), `dialog` (`own/BBE_Dialog/UI/dialog.html`), and `toast` (`own/BBE_Toast/UI/toast.html`; these are other packages, not `BBE_UI`).
  - `SIZE = 960x540`, `FRAMES = 90` (1.5 s of game time).
  - Keys: `--only <name>`, `--update` (write references), `--selftest` (check of the comparison itself), `--tol 8` (per-channel threshold, 0–255), `--share 0.001` (share of differing pixels).
  - Result: exit code 1 on mismatch; `out/diff/<page>.png` (differing pixels in red).
  - Requires `numpy` and `PIL`; nothing is downloaded.
- **No references exist.** The `tools/ui_host/refs` folder is not in the repository as of 2026-11-16 (verified by the folder's absence); `out/` holds past snapshots. So `snap.py` will currently report "no reference" for all pages until `--update` is run and `out/diff` and `out/*.png` are reviewed by eye. `docs/HANDOFF.md` records: "references not created: needs `ui_host` with the game's fonts" (verified in HANDOFF and by the folder's absence).
- `manifest.json` in `refs/` (per AUTHORING) will record whether Noto Sans was present when the references were written.

### 9.4. `tools/ui_preview`

- Browser preview without the game: `py -X utf8 tools/ui_preview.py [--ui DIR] [--port 8766]`, then `http://127.0.0.1:8766/?page=controls` (per AUTHORING; `tools/ui_preview.py`, line 10 — `Usage`).
- Data: `tools/ui_preview/data/<page>.json` — edited on the page; the folder contains, for example, `box.json`, `build.json`, `build_disputes.json`, and `_en` variants.
- Parameters: `&data=<name>` — take `data/<name>.json` instead of `data/<page>.json` (for example `lobby_subs`, `lobby_load`); `&set=path:value` — change one field after loading; `&dialog=<id>` — open the `dialog` with that `id`.
- Another pages folder: `--ui own/BBE_Terminal/UI`.
- Game tags (`item`, `slot`, `canvas`, `curve`, `chart`, `gauge`, etc.) are shown as placeholders.
- `data-viewport="screen"` mode is not shown: the preview draws a 16:9 canvas (per AUTHORING).
- Saving files redraws the page.
- The browser does not know `transition: layout` and `scale` as in the game (sections 4.8, 4.9): motion and scale in the preview may differ from the game.

### 9.5. `tools/ui_test`

- Build and run (per `docs/HANDOFF.md`, section 3): `cd tools/ui_test && dotnet build -v q -o bin/<name>/net8.0 && ./bin/<name>/net8.0/ui_test.exe 2>&1 | grep -E "^FAIL|Unhandled|passed"`. The build folder must be exactly `bin/<name>/net8.0` deep (the tests find the repository root by depth).
- Full run on 2026-11-16: **8863 passed, 0 failed** (`HANDOFF.md`, section 0).
- Quick sets: `ui_test.exe style-state`, `hover-perf`, `map-perf`, `map-cards`, `icons`, `style-cache`, `images`, `screen-map`, `perf`, `alloc`, `talloc`, `p7alloc`, `trace` (per `PERF.md` and `HANDOFF.md`).
- Groups that AUTHORING refers to: `canvas mouse`, `code editor`, `pictures and files`, `rounded clip`, `scale` (`ScaleTests.cs`), `DrawKitTests`, `ChartTests`, `FocusKeysTests`, `PopupTests`, `DragDropTests`, `SiblingSelectorTests`, `ShadowGradientTests`, `RoundedClipTests` (per AUTHORING, `AUDIT_STAGE_C.md`, `AUDIT_STAGE_D.md`, `PERF.md`).
- Documentation check: `Program.cs`, `AuthoringGuide()` (lines 638–644): every known property and shorthand (`StyleEngine.KnownProperties`) must appear in `docs/ui/AUTHORING.md` in backticks, and every pseudo-class as `:name` anywhere in the text. The check is by names only: it does not know whether the property is described correctly, and it does not catch names that the engine does not have (for example `font`); such names are listed in AUTHORING by hand ("Not present at all"). The wording in AUTHORING was fixed 2026-11-16 (item 17 of the check).
- Other related checks: the `ui_test` groups "hover perf" and "map perf" — measurements (relative, Debug).

### 9.6. In-game self-test

- Works only when the file `bbe_ui_test.flag` is in the game folder (`Api/SelfTest.cs`, lines 14 and 24). The comment at the top of the class mentions running through `tools/test.py`.
- Report — `bbe_ui_test.txt`; snapshots — `bbe_ui_test.png` and `bbe_ui_controls.png` (`SelfTest.cs`, constants `Report`, `Shot`, `Shot5`).
- Parts: `SelfTest.cs` (stages 3–5), `SelfTestInput.cs` (input), `SelfTestPerf.cs` (measurements), `SelfTestTools.cs` (tools).
- The self-test for lanes D, E, F has not been run in the game (`HANDOFF.md`, section 0; audits D and C — "not verified in game").

---

## 10. Performance and limits

All numbers are from `docs/ui/PERF.md`, `docs/ui/AUDIT_STAGE_C.md`, `docs/ui/AUDIT_STAGE_D.md`, `docs/HANDOFF.md`; they were not re-checked by measurement in this pass. The conditions are given for each group. Offline measurements — `tools/ui_test`, Release or Debug, median; **the numbers are relative**, on the developer's machine. In-game measurements — only where stated.

### 10.1. Game: stage 7 self-check (2026-10-18, PASS)

Test page: 299 elements, 203 bindings (200 recipe tiles via `data-for` in a scrolling grid, a log of 100 rows).

| What | Budget | In game |
|---|---|---|
| opening a window, warm | ≤ 50 ms | 1.9 ms (cold 8.4 ms) |
| 200 values change every frame | ≤ 2.5 ms, no full layout | 0.39 ms, 51 KB/frame |
| one value per frame | ≤ 0.5 ms | 0.085 ms, 0 B |
| `Refresh` with no changes | 0 B | 0.088 ms, 0 B |
| page drawing | 0 B | 0.19 ms, 0 B |

Text width cache: 42,843 hits, 693 misses.

These measurements were made before lanes A–F; they have not been measured in game after them (per HANDOFF).

### 10.2. Offline: 500 tiles, 2310 elements (before → after, 2026-10)

| Scenario | Before | After |
|---|---|---|
| full layout | 23.0 ms, 25 MB | 4.9 ms, 3.2 MB |
| opening: parsing + styles + layout | 27.4 ms | 7.9 ms |
| text in one tile | 23 ms (full) | 0.71 ms, 52 KB (incremental) |
| 200 texts per frame | 23 ms (full) | 1.7 ms, 530 KB |
| update with no changes | — | 0.13 ms, 0 B |
| template, 200 rows, no changes | 142 KB | 0 B |

The full table — `PERF.md`, section "What has been done".

### 10.3. Hover: 300 cards, 6006 elements (before and after A4)

The sheet is `own/BBE_Mods/UI/build.css`. Numbers are for `ComputeState` and the full `Compute`, Debug, median, ms.

| Case | Before (full recompute of subtree) | After (state index) | After, with card rules |
|---|---|---|---|
| entering the window | 279.7 ms, 6006 elements | 0.017 ms, 0 elements | 0.97 ms, 20 elements |
| card 0 → card 1 | 2.2 ms, 40 | 0.011 ms, 0 | 1.88 ms, 40 |
| header → row inside a card | 0.51 ms, 9 | 0.008 ms, 0 | 0.32 ms, 7 |
| leaving the window | 277.0 ms, 6006 | 0.024 ms, 0 | 0.91 ms, 20 |

Conclusion of `PERF.md`: rules apply to the element's state, not to its subtree; `:hover` on the page's ancestors without need (`.page:hover …`) restyles the whole ancestor on every enter and leave (`AUTHORING`, section "Selectors").

### 10.4. Selector index and structural changes (after B3)

Debug, 300 cards, 22,932 elements, median:

| Case | Without index | With index |
|---|---|---|
| first print of the page | 1,670.7 ms | 1,106.9 ms |
| of which cascade | 1,481.1 ms | 886.1 ms |
| `resize` of one card | 60.2 ms | 55.6 ms |
| hover a (entering the window, full `Compute`) | 229.0 ms | 46.3 ms |

Structural change (add or remove a card): was 174.5 ms (whole document) → add 2.07 ms (re-cascade of 9 elements), remove 0.13 ms (0 elements).

Layout on structural change (stage A): adding a card ≈ 27 ms, of which the template 13 ms, restyle and layout ≈ 14 ms (`PERF.md`, stage A).

### 10.5. Map with 300 block cards (B3, Debug)

| Case | Median, ms | Maximum, ms | Re-cascaded elements | Boxes under recomputed roots |
|---|---|---|---|---|
| pan, 300 cards | 0.056 | 21.95 | 0 | 0 |
| zoom, 300 cards | 0.075 | 0.150 | 0 | 0 |
| dragging one card | 0.057 | 0.067 | 0 | 0 |
| `resize` of one card (full re-print) | 45.1 | 54.7 | 219 | 4,497 |
| first print of the page, 300 cards | 1,264.6 | 1,599.4 | 15,968 | 4,592 |

`resize` of one card re-lays out the whole layer: the parent `#world` falls into the dirty roots (`PERF.md`, B3). This has not been measured in game.

Stage A (2026-11-15, Debug, 300 cards): first opening of the page — cold cache 996 → 936 ms; reopening with the style cache — 419 ms; card resize 59 → 58 ms; pan/zoom/drag — 0.013–0.020 ms (`PERF.md`, stage A).

### 10.6. What has been verified only offline

Drawn pixels, GPU, draw calls, mouse hit testing via `Painter.Hit` (except in the test), font rasterization at scale, drawing allocations in the game — **not measured** (`PERF.md`, section B3, "What the test does not measure").

### 10.7. Limits (values in code)

| Limit | Value | Source |
|---|---|---|
| textures of one window (pictures, shadows, gradients, corner masks) | 256 MiB and 4096 entries, shared budget | `UiWindow.cs`, lines 90–91; `Core/TextureLedger.cs`; "one budget" not measured in game |
| texture release from the budget | up to three quarters of the budget in one pass; release with a delay of a fraction of a second | `TextureLedger.cs` (lines 87–88); `AUDIT_STAGE_C.md`, items 7–8 |
| heavy shadow bakes | at most 2 per frame | `Core/BoxShadow.cs`, `DecorBudget.HeavyPerFrame = 2` |
| light bakes (steps, corners, areas) | at most 64 per frame | `BoxShadow.cs`, `LightPerFrame = 64` |
| `background-repeat` repetition | 1024 copies in a block | `AUTHORING` (section "Properties", background) |
| blur of `box-shadow` and text | up to 48 px; shadow texture radius up to 96 px | `AUTHORING` (block shadow); `TextShadow` (text blur is not drawn) |
| gradient texture | longer than 512 px is stretched by the sampler | `AUTHORING` |
| gradient corner radius | up to 96 px of texture; larger — square | `AUTHORING`; `AUDIT_STAGE_C.md`, item 3 |
| style cache | 2048 entries of `style` strings and 8192 styles per generation (two generations) | `Core/StyleEngine.cs`, line 576 |
| fonts | about 8 MiB per each size (atlas and buffer) | `Core/FontCache.cs`; `HANDOFF.md`, A2 |
| virtual list | from 100 rows inside `overflow: auto/scroll` | `Core/Template.cs`, `AutoVirtual` |
| code editor | only visible lines are drawn; 20,000 lines — load 3 ms | `AUTHORING` (section "Code editor") |
| editor undo history | 1000 steps | `AUTHORING` |
| drop-down list | 8 rows | `UiControls.cs`, `DropRows = 8` |
| `title` tooltip | after 0.4 s | `UiControls.cs`, `TooltipDelay` |
| drag threshold | 4 px | `PointerRouter.DragThreshold` |
| `ClickCounter` | 0.4 s and 6 px | `Core/CanvasMouse.cs` |
| `Send` timeout | 5 s by default | `Api/BaroUiView.cs` |
| default `layout-fps` | 30 times per second | `Core/Animation.cs`; `BaroUi.LayoutFps` |
| `Esc` and "window shown" | the last 0.5 s | `Api/BaroUi.cs`, `PauseMenuPrefix` |
| `ui_host snap` | channel threshold 8 of 255; share of differing pixels 0.001 | `tools/ui_host/snap.py` |

The old limits from audit C (items 7–8: masks up to 256 and corner tiles up to 512 pieces of 16 MB each; decor cache up to 1500 entries and 48 MB) were described before the budgets were merged. The current limit is the window's shared budget (`TextureLedger`); (not verified in code that the old figures were removed from all places).

### 10.8. Allocations

- A settled frame with no changes: no memory allocation (`AUDIT_STAGE_D.md`: "a settled frame allocates no memory"; `PERF.md`: "0 B" for an update with no changes and for drawing in the offline measurement).
- Charts: a frame with no data changes allocates no memory (lane H, 2026-11-16; `ChartLaneTests`, bytes on the hot path). Allocations outside the hot path are listed in section 6.5.
- Text: in the text width cache (`UiFonts`, `MaxWidths = 10000`) — with the note that `letter-spacing` is drawn per character without per-frame allocations (`AUTHORING`).
- Re-layout in a container with `+` and `~`: quadratic growth was fixed (audit D, item 5).

### 10.9. What is expensive

| What | Why | What to do |
|---|---|---|
| `:has()` over a large subtree | on every restyle of the container, the subtree is checked; `.map:has(.x)` over 6000 elements — up to 6000 checks (`PERF.md`, "Pseudo-elements and :has()") | `:has(> .x)` (children only) or a rule on the element itself |
| `:hover` on a page ancestor (`.page:hover …`) | the whole ancestor is restyled on every enter and leave | a rule on the element or on its direct parent |
| structural change of a large list (first print, rebuild) | cascade of all elements (15,968 elements of the map — 1.1–1.8 s, Debug) | `data-for` with `data-key`; do not rebuild the whole list |
| `style="translate: {{x}}px"` every frame | a new style string and parsing every frame; the string cache is limited | `SetTranslate`/`SetScale` (section 4.9) |
| `font-size` every frame | each new size is a new game font (about 8 MiB) | `scale` for smooth enlargement (fonts for scale are unloaded) |
| length transitions (`width`, `height`, paddings) on large blocks | each length value is a block layout recalculation, on the `layout-fps` grid | `transition: layout` for expanding; `opacity` and `translate` for the rest |
| `layout` on rows of a virtual list | rows are recreated on scroll and may move (`AUTHORING`) | do not put `layout` on a virtual list |
| `transition: all` on a block with lengths | `all` includes lengths, each one triggers layout | list the properties |
| `box-shadow` and a rounded gradient during smooth zoom | per-frame bakes are limited (2 shadows, 64 bands/corners); what does not fit is drawn from the nearest step or skipped | do not animate many large shadows at once |
| `resize` of a card on the map (full re-print of the layer) | the parent `#world` falls into the dirty roots (45–65 ms, Debug) | a simplified view at small scale; whether it is needed is the owner's decision (`PERF.md`, B3; no decision yet) |
| `dialog`, `select` inside `scale` | open without scale, others take separate paths | open outside the scaled block |
| `canvas` with a large number of draw calls | not measured | (not verified) |

---

## 11. What is missing, limits, and what is not verified

### 11.1. "Not present" list

Verified in code: these names are not in `StyleEngine.Initial`, `Shorthands`, `CssValues`, `CssParser`, `HtmlParser`, `UserAgentCss`, or the behavior is explicitly not implemented. For every property — unknown, with a note.

**CSS functions and units**

- `calc()`, `min()`, `max()`, `clamp()`, `var()` inside functions (except simple substitution `var(--x)`).
- Units: `rem`, `vw`, `vh`, `vmin`, `vmax`, `ch`, `ex`, `pt`, `cm`, `in`, `Q`. Only `px`, `u`, `em`, `%`, `fr`, `auto`, `0` exist.
- Colors: `hsl()`, `hwb()`, `lab()`, `lch()`, `color-mix()`, `color()`. `rgb()`, `rgba()`, `#…`, and names exist.
- `counter()`, `counters()`, `attr()` outside `content`, `url()` in `content`.

**Rules and selectors**

- `@media`, `@import`, `@font-face`, `@supports`, `@layer`, `@container`, `@namespace`. `@keyframes` exists.
- Attribute operators: `[a^=…]`, `[a$=…]`, `[a*=…]`, `[a~=…]`, `[a|=…]`.
- Pseudo-elements other than `::before`/`::after`: `::marker`, `::selection`, `::placeholder`, `::first-line`, `::first-letter`.
- Pseudo-classes: `:link`, `:visited`, `:empty`, `:is()`, `:where()`, `:first-of-type`, `:nth-of-type`, `:lang()`, `:target`, `:scope`, `:required`, `:invalid`, `:placeholder-shown`, `:nth-child(2n+1)` (`an+b` is not parsed, no match).
- Lists in `:not(...)` and `:has(...)`, nested `:not` and `:has`, pseudo-elements in their arguments.
- Two combinators in a row.

**Properties that do not exist** (see also section 4.6.10)

- `float`, `transform` (except `translate` and `scale`), `filter`, `backdrop-filter`, `clip-path`, `mask`, `mix-blend-mode`, `will-change`, `user-select`, `inset`, `aspect-ratio`, `font` (shorthand), `grid-row`, `grid-area`, `justify-items`, `justify-self`, `place-*`, `object-position`, `background-clip`, `background-attachment`, `background-origin`, `word-break`, `hyphens`, `text-wrap`, `text-align-last`, `text-justify`, `direction`, `writing-mode`, `font-variant`, `font-stretch`, `list-style`, `counter-reset`, `counter-increment`, `border-top-style` (and per side), `transition-behavior`, `animation-composition`, `animation-play-state`.

**Values that are accepted but do not do what the browser does**

- `display: table`, `table-row`, `table-cell`, `list-item` — block with a note; `<table>` is not a table.
- `overflow: clip` — behaves like `hidden`.
- `border-style: double`, `groove`, `ridge`, `inset`, `outset` — solid line.
- `text-decoration-style` other than `solid` — solid line.
- `outline-style: dashed`/`dotted` — solid line.
- `text-shadow` with blur — sharp shadow; of a list of shadows — the first.
- `box-shadow`: only one outer; `inset` — no.
- `background-image`: `conic-gradient`, `repeating-*`, color hints, a radius in `%` for a radial gradient — no (the gradient is not drawn, with a warning).
- `vertical-align` and `-webkit-box-orient` — no action.
- `transform-origin` — no action (the scale center is the top-left corner).
- `text-align` — only `left`, `center`, `right`.
- `line-clamp` on `inline-block` blocks in dropped lines — not hidden (audit C, item 12).
- The `currentcolor` of a text shadow is taken per frame (this works), but the shadow is not animated.

**Drawing and pictures**

- Clipping children by `overflow` along the arc (rounded clipping) — no (geometry exists, drawing is not enabled).
- Rounding of a background picture and of an `<img>` picture — corners are square.
- `dashed`/`dotted` outline — solid line.
- Gradient and shadow on corners with different radii — the shadow and outline use one radius.
- `sprite()` with the game's nine parts — no (the whole sprite is taken).
- `image-rendering`, `object-position`, `background-clip` — no.

**Tags and attributes**

- `iframe`, `audio`, `svg` (as a drawing), `input type=color`, `input type=email/date/…` (not fields), `input type=file` (files — by drag and Ctrl+V, section 5.7), `details`, `summary`, `template` as a template, `<table>` as a table.
- Attributes `onmousedown`, `onmouseup`, `onmouseover`, `onkeydown`, `onkeyup`, `ondblclick` (no shorthand; `data-mouse` and `data-keys` are used instead).
- The `title` tooltip is not drawn in the browser preview (per AUTHORING — the game's tooltip; (not verified in code for the preview)).

**Input**

- `keyup` and key release — no (section 5.3).
- Highlighting of a file target during a drag from Explorer — no (section 5.7).
- Dragging from the window to Explorer — no.
- Dropping a game item onto an HTML block — no (section 5.10).
- The wheel over a `number` field exists since 2026-11-16 (section 3.5.1, `UiControls.WheelNumber`); not verified in game. The `range` slider has no wheel (it scrolls the page), as before.
- Multi-file drop in one call — no.
- Right button on the canvas — not verified (audit D).

**Other**

- JavaScript, `eval`, network, `http(s)://`, `data:`, `file:`, and absolute paths in picture and font addresses.
- Package fonts: no `@font-face`; only the families from section 7.1.
- Right-to-left directions, vertical writing — no.
- Printing, accessibility (screen reading) — no.
- Smoothing: the edges of arcs, diagonal lines, and corners look sharp (section 11.2).

### 11.2. Known limits and open items

From the audits and `docs/HANDOFF.md` (section 0, as of 2026-11-16):

| No. | What | Status |
|---|---|---|
| 1 | Clipping children by rounding (`overflow` and `border-radius`): geometry exists (`Core/RoundedClip.cs`), drawing is not enabled — switching the draw target in the middle of a frame wipes the screen (verified on `ui_host`) | not done; needs a pass at the start of the frame and `PreserveContents` (not verified) |
| 2 | Smoothing: roundings and edges that are not on the pixel grid look pixelated and sharp (owner, 2026-11-16) | lane G done 2026-11-16, not verified in game: edges of roundings, rings, and corners — analytic coverage with one linear transition of 1 px (`Core/Coverage.cs`, `Core/Corners.cs`, `Core/RoundedClip.cs`, `Render/Painter.cs`, `Render/UiDraw.cs`). The earlier 4×4 samples and the power correction of 0.55–0.7 were removed. Thin rings of 1–2 px lifted with powers 0.8 and 0.9 (`Coverage.ThinLift`): the values were chosen by calculation (a light line in sRGB at half coverage gives less light than in linear space), not fitted to a Chrome snapshot. Remaining: masks with a fractional radius are not made, the radius is rounded to whole px (`Corners.CornerPx`); page pictures are drawn with `PointClamp` (`UiImages.cs`, around line 180); compare the look with Chrome and in game |
| 3 | Shadow and gradient during zoom: baking is limited per frame; during fast zoom a shadow may jump or vanish for a frame | done (audit C, items 1–2), not measured in game |
| 4 | Block shadow and rounded gradient: one radius for the shadow and the outline | recorded (audit C, item 3 — across corners the gradient follows each corner; the shadow and outline do not) |
| 5 | Charts: a frame with no data changes allocates no memory (lane H, 2026-11-16, `ChartLaneTests`); outside the hot path allocations remain (section 6.5: smooth curve at every change of points, labels when the scale or hover changes) | partly done; not measured in game |
| 6 | Charts: no gradient fill, logarithmic axes, wheel zoom, keyboard focus on `chart`/`gauge` | not done (AUTHORING) |
| 7 | Canvas in `scale`: a callback that reads the mouse via `PlayerInput` computes in screen pixels | recorded |
| 8 | `slot`, `select` (rows), `dialog` inside `scale` | recorded (section 4.9) |
| 9 | `sticky` horizontally, `sticky` inside `scale` | not done |
| 10 | `line-clamp` and atomic blocks on dropped lines | recorded (audit C, item 12) |
| 11 | `:has()` over a large subtree — cost | recorded (`PERF.md`); not measured in game |
| 12 | Russian word glue enabled by default (`-bbe-ru-glue: auto`) — Russian text line breaks in all windows are slightly different | owner decision not made (`HANDOFF`, question 1); audit C, item 4 |
| 13 | `50%` on an elongated block gives an ellipse (before: a circle based on the smaller side); a rounded block with one border side — per side; fields change the cursor | owner decision, kept (audit C, item 4) |
| 14 | `ui_host` snapshots without references | references not created (section 9.3) |
| 15 | Test `shop page: 500 cards open within 50 ms` — on a quiet machine 53–59 ms, under load 54–74 ms | last run 2026-11-16 — 0 failed |
| 16 | Map (outside the engine): the card layer does not follow the camera between page updates; `Settle` at mod start | outside the engine, `HANDOFF` section 4.3, questions 2 and 5 |
| 17 | Items "recorded and not done" from `HANDOFF` (section 0): `UiMouseEvent.Under`, natural size of `<img>`, shared texture budget — per `HANDOFF` they are done (not verified in game) | see `HANDOFF` section 0 |

Audit D (interaction): all 14 items fixed by tests; in game — not verified (`AUDIT_STAGE_D.md`, section "Not verified"). Remaining entries: three meanings of the word `Over` and the public name `DragDrop.Swallow` were not renamed (so as not to change tests and pages; audit D, item 14).

Audit C (look): item 4 — owner decision; item 12 — recorded; the others were fixed by tests and `ui_host`; not verified in game.

### 11.3. What is not verified in game

Collected from `AUTHORING` ("Not verified in game"), `AUDIT_STAGE_C.md` and `AUDIT_STAGE_D.md` ("Not verified"), `PERF.md` ("Not verified"), `HANDOFF.md`:

- **Look.** Almost all new properties of stages C, D, E: shadows, gradients, radii, outlines, 9-slice borders, background pictures, Lucide icons, Noto Sans from the game folder, smoothing, the cursor in the game ("the look in the game has not been checked by eye"; AUTHORING).
- **Fonts.** Character width in the game (FreeType rounds); behavior when a character is missing and with a non-breaking space; behavior of the italic of synthetic fonts.
- **Input.** Mouse capture outside the window; SDL events during file drag; reading the clipboard through user32 and shell32; IME (composition); curve keys; the wheel over a `number` field (`WheelNumber`, since 2026-11-16); exceptions of the game's keyboard dispatcher after `Fail`; the right button on the canvas; `GhostShift` at real scale.
- **Menus and drag.** Scenarios of menus, context menus, card drag in the game; keys of fields in menus.
- **Self-test.** The self-test for lanes C, D, E, F has not been run in the game (`HANDOFF`).
- **Measurements in game.** Time of baking shadows and gradients; the cost of `:has()`; drawing allocations; the cost of charts per frame.
- **Map.** All scenarios of the mod manager in the game (`HANDOFF`, stage B6 waits for the game).
- **LuaCs.** Operation of windows on stock LuaCs — not checked (`HANDOFF`, section 0, line about stock LuaCs).
- **Package as is.** Build and run of `own/BBE_UI` on LuaCs 1.15.4 (the new plugin) — not verified (`docs/luacs/COMPARE_PLUGIN.md`).

---

## 12. Compatibility and versions

### 12.1. Game

- Version in the package's `filelist.xml` — `gameversion="1.13.5.0"` (1.13.4.0 before 2026-11-16).
- The patch of the LuaCs fork (`forks/LuaCs`, running mods in the menu) was built for game **1.13.5.0** (`docs/HANDOFF.md`, sections 4.1 and 0). The version in the package metadata (1.13.4.0) and the version the fork was built for (1.13.5.0) do not match. What of this matters for the game — not verified (the owner decides which version to write).
- **Decided by the owner on 2026-11-16:** the package `gameversion` was raised to 1.13.5.0, as for the fork. The earlier note: **Open owner decision** (item 19 of the check, 2026-11-16): do not change `gameversion` in `own/BBE_UI/filelist.xml` (1.13.4.0) until the owner decides which game version the package and the fork are for. Neither the code nor `filelist.xml` was touched during the check.
- The new LuaCs plugin (`LuaCsForBarotraumaPlugin`) targets game 1.15.4.0 (`docs/luacs/COMPARE_PLUGIN.md`, section 0).

### 12.2. LuaCs: BBE fork and stock

| Question | BBE fork (`forks/LuaCs`) | Stock LuaCs |
|---|---|---|
| mods in the main menu | work (running mods in the menu is a switchable setting of the fork; `docs/HANDOFF.md`, section 0) | unloaded on entering the menu (`HANDOFF`, section 0) — engine windows do not work in the menu |
| lobby, rounds, editors | work | work (not verified for this package — `HANDOFF`, section 0) |
| game patches | the fork patches game files: `py -X utf8 tools/engine.py save-patch` before `build client\|server` (the build wipes the patches), then `install-client` (`HANDOFF`, section 3) | not required |
| branches | `luacs/bbe`, `moonsharp/bbe` in `Nice2Live/Barotrauma-Beyond-the-Europa` (`HANDOFF`, section 0) | — |

The `own/BBE_UI` package sets Harmony prefixes only in `Api/BaroUi.cs` (`harmony.Patch`, lines 45–52): on `DebugConsole.AddToGUIUpdateList` and on the pause menu (section 1.3). No other patches were found in `CSharp/` (verified by searching for `harmony`). Whether these targets match the version the LuaCs runs on — (not verified in code for each version).

### 12.3. New LuaCs plugin

The new LuaCs plugin (`Luatrauma/LuaCsForBarotraumaPlugin`) is not a fork of the game: it connects through the plugin loader and Harmony, targets game 1.15.4.0, and therefore our fork for 1.13.5 does not carry over directly; `own/BBE_UI` probably loads by its `ModConfig.xml` format and Harmony targets, but it is not verified on 1.15.4 (`docs/luacs/COMPARE_PLUGIN.md`, section 0, items 1–6).

### 12.4. Versions and names

- Module version: `modversion="0.0.1-bbe"` (`filelist.xml`); `UiInfo.Version = 0` (`CSharp/Shared/UiPlugin.cs`).
- Public engine name — Baro-UI (owner decision, 2026-11-15); internal name of the package and assembly — `Baro-UI` / `BaroUI`. Renaming is not decided (`HANDOFF`, section 0).
- The repository `Nice2Live/baro-ui` was created private, empty as of 2026-11-15 with one local commit (`HANDOFF`, section 0). Visibility is not decided.
- UI texts: `data-t` takes text from the game's `TextManager` (the same as the game's texts in its language); the package has no text tables (section 3.6).
- Russian word glue is enabled by default (section 4.6.4a).

### 12.5. Browser preview and the game

Differences known from the documentation (`AUTHORING`, `PERF.md`):

- Scale center: in the game — the top-left corner of the border box; in the browser — the center. For the preview — `transform-origin: 0 0`.
- `transition: layout` is not visible in the browser (blocks jump).
- `data-viewport="screen"` does not work in the preview (a 16:9 canvas is drawn).
- Game tags in the preview are placeholders.
- Character width: in the game lines are slightly wider (FreeType rounds).
- Noto Sans font: in the game — from the game folder; which font the preview uses for the Noto Sans family, and whether the metrics match — (not verified in code).

---

## 13. For the translator

- This file is the master. The translations are `REFERENCE.en.md` and `REFERENCE.zh-CN.md` (per the owner's decision, 2026-11-16). Changes go here first, then into the translations.
- Identifiers, names of properties, selectors, tags, attributes, functions, and error text are carried over unchanged.
- The markers "(verified in code)", "(per AUTHORING)", "(not verified in code)", "(not verified in game)" are carried over as they are.
