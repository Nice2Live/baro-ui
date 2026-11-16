# BBE UI (Baro-UI): публичный API

Версия движка: `BaroUi.Version` = **0.2.0** (`own/BaroUI/CSharp/Client/Api/BaroUi.cs`); то же число в `own/BaroUI/filelist.xml` (`modversion`). Тест `tools/ui_test` (`EngineApiTests`) сверяет оба и список публичных типов ниже.
Пространство имён пока `BaroUI` (переименование в `BaroUI` не решено; до решения имя не меняем). Полная справка: `REFERENCE.ru.md`, раздел 8; как писать окна: `AUTHORING.md`; пример: `own/BaroUI_Example`.

## Проверка в моде

```csharp
public sealed class MyPlugin : IAssemblyPlugin
{
    public void Initialize()
    {
        if (!BaroUi.Requires("0.2.0", "My mod")) { return; }   // старый движок: одна строка в журнале, false
        // дальше окна
    }
}
```

`BaroUi.Version` — `System.Version`. Консоль: `baroui version`.

## Публичные типы

Всё, что не в этом списке, — внутреннее (`internal`). Тест падает, если публичный тип появился без записи здесь.

### Точка входа и окна

- `BaroUi` (статический класс, `Api/BaroUi.cs`):
  - `Version` — версия движка (`System.Version`), одна константа.
  - `Requires(string minVersion, string modName)` → `bool` — проверка при старте; при старой версии пишет строку в журнал.
  - `Open(string htmlPath, BaroUiView view = null, string name = null)` → `UiWindow` — открыть свободное окно.
  - `Open<T>(string htmlPath, string name = null)` → `T` — то же с новым видом `T`.
  - `Create(string htmlPath, BaroUiView view = null, string name = null)` → `UiWindow` — окно предмета (владелец сам добавляет его в список обновления).
  - `OpenPage(string page)` → `UiWindow` — `UI/<page>.html` пакета движка (или папки `baroui src`).
  - `Windows` → `IReadOnlyList<UiWindow>` — открытые свободные окна.
  - `Reload(UiWindow w)` → `string` — перечитать страницу на месте (`null` при успехе).
  - `Reloaded` — событие `Action<UiWindow, UiWindow>` после перезагрузки.
  - `Watch`, `Animations`, `LayoutFps` — свойства `baroui watch|anim` (чтение и запись).
  - `PackageDir`, `PageDir`, `Fonts` (`UiFonts`) — папка пакета, папка страниц, шрифты окон.
  - `RegisterFont(string family, string file, int weight = 400, bool italic = false)` — шрифт мода для окон, открытых после вызова; `file` — абсолютный путь файла в пакете мода (`.ttf`/`.otf`). Семейство видно окнам; файл читается при открытии окна: нет или не читается — строка в `problems`, семейства нет. Бросает `ArgumentException` для семейства не из одного имени (с запятой) и пути не абсолютного. Правила и CSS-вариант (`@font-face`): `docs/ui/AUTHORING.md`, «Свои шрифты»; `REFERENCE.ru.md` 7.7.
- `BaroUiView` (абстрактный класс, `Api/BaroUiView.cs`): вид окна; `[UiHandler]`-методы — обработчики `on*` разметки.
  - `Window` (`UiWindow`), `Document` (`Document`), `Data` (`object`), `Set(path, value)` → `bool`, `Refresh()`, `Close()`.
  - `Find(selector)` → `Element`, `FindAll(selector)` → `List<Element>`, `Text(e)`, `Text(e, text)`, `ToggleClass(e, cls, on)`, `HasClass(e, cls)`, `Style(e, prop, value)`, `Attr(e, name, value)`, `Append(parent, html)` → `List<Element>`, `Remove(e)`.
  - `Send(source, Action send, timeoutSeconds = 5)`, `Done(source)` — запрос серверу с блокировкой элемента до ответа.
  - Виртуальные: `OnOpen()`, `OnUpdate(dt)`, `OnAfterInput()`, `OnClose()`, `OnEscape()` → `bool`, `OnBack()`, `OnForward()`.
- `UiWindow` (`Render/UiWindow.cs`): окно, см. `REFERENCE.ru.md` 8.3. Используемые снаружи: `Name`, `Data`, `Document`, `Root` (`LayoutBox`), `IsOpen`, `Template`, `Handlers`, `View`, `Problems`, `SourcePath`, `Find(id)`, `Set`, `Refresh()`, `SetText`, `ToggleClass`, `Invalidate()`, `Relayout()`, `ElementAt(point)`, `BoxOf(e)`, `ScreenRect(e)`, `GetScroll(e)`, `ScrollBy(e, dy)`, `Click(e)`, `Close()`, `SetCanvas(id, draw)`, `Drawing` (`UiDraw`), `Images` (`UiImages`), `GetCodeEdit(id)` (`CodeEdit`), `SetInventory(name, inv)`, `SetMouse`, `ClearMouse`, `SetCanvasMouse`, `ClearCanvasMouse`, `SetDrop`, `ClearDrop`, `SetKeys`, `ClearKeys`, `SetFileInput`, `ClearFileInput`, `FocusId(id)`, `ShowModal(id)`, `CloseDialog(id)`, `ShowPopup(id, x, y, anchor)`, `ShowPopup(id, anchor, placement)`, `ClosePopup(id)`, `IsPopupOpen(id)`, `PopupAnchor`, `PopupClosed` (событие), `Escape` (событие), `Clicked` (событие), `SetTranslate`, `ClearTranslate`, `SetScale`, `ClearScale`, `LayoutCount`, `LastFullLayout`, `LastStyleMs`, `LastLayoutMs`, `LastUpdateMs`, `UnitPx`, `IsTyping`, `SkipTyping()`, `IsAnimating`, `AddToGUIUpdateList(order)`, `CaptureNextDraw(file, id)`, `FromFile(path, fonts, name)` (статический).
- `BaroUiVideo` (`Api/BaroUiVideo.cs`): `Window.Video(id)` — управление элементом `<video>`: `Id`, `Found`, `Play()`, `Pause()`, `Stop()`, `Restart()`, `Volume`, `IsPlaying`, `Duration` (см. `REFERENCE.ru.md` 3.8).
- `UiImages` (`Render/UiImages.cs`): `Window.Images`; `Get(path)`, `Add(key, bytes)`, `Peek`, `Release`, `Clear()`, `Count`, `Bytes`, `Draw(...)`, `DrawRegion(...)`.
- `UiDraw` (`Render/UiDraw.cs`): `Window.Drawing`; `Stroke`, `Dots`, `Disc`, `Ring`, `Arc`, `Sector`, `Area`, `Fill`, `Text`. `UiPath` — путь (`Raw`: список `DPt`).
- `UiFonts` (`Render/UiFonts.cs`): шрифты окна (`BaroUi.Fonts`, `Window.Fonts`); `ITextMeasurer` — интерфейс, который он реализует.
- `UiFileInput`, `FileInputSource` (`Render/UiFileInput.cs`): файлы, перетащенные в окно или вставленные (Ctrl+V).

### Значения, события, атрибут

- `UiHandlerAttribute` (`Core/Template.cs`): метка метода вида, который вызывает разметка (`onclick="f()"`).
- `UiMouseEvent`, `UiMouseKind` (`Core/PointerEvents.cs`): `data-mouse` и `SetMouse`.
- `CanvasMouseEvent`, `CanvasMouseKind`, `ClickCounter`, `MouseButtons`, `KeyMods` (`Core/CanvasMouse.cs`): мышь холста.
- `UiKey`, `UiKeyEvent` (`Core/KeyRouter.cs`): клавиши `data-keys` и `SetKeys`.
- `UiDropEvent`, `UiDropPhase` (`Core/DragDrop.cs`): перетаскивание.
- `PopupCloseReason` (`Core/Popup.cs`): причина закрытия всплывающего окна.
- `Diagnostic` (`Core/Dom.cs`): замечание страницы (`File`, `Line`, `Message`).
- `NodeState` (`Core/Dom.cs`): флаги состояния элемента.
- `ImageFit`, `ImageLayout`, `ImagePlacement` (`Core/ImageLayout.cs`): картинки на холсте и `object-fit`.
- `CodeEdit` (`Core/CodeEdit.cs`): модель редактора кода (`textarea`).
- `Localizer` (`Core/Localizer.cs`): тексты по `data-t`.
- `Query` (`Core/Query.cs`): `First(root, selector)`, `All(root, selector)`.

### Дерево и разбор (для продвинутых случаев и тестов)

- `Document`, `Node`, `Element`, `TextNode` (`Core/Dom.cs`): дерево страницы. `Element`: `Tag`, `Id`, `Children`, `Attributes`, `GetAttribute`, `SetAttribute`, `RemoveAttribute`, `Classes`, `HasClass`, `ToggleClass`, `Append`, `Insert`, `Remove`, `Clone`, `Parent`, `Style` (`ComputedStyle`), `State`, `TextContent`, `Value`, `Checked`, `Hidden`, `Line`.
- `LayoutBox`, `Edges`, `TextFragment` (`Core/LayoutBox.cs`): прямоугольники раскладки, которые отдаёт `UiWindow.BoxOf`; `TextureLedger`, `ITextureOwner` (`Core/TextureLedger.cs`): бюджет текстур (его создаёт `ModsMapCanvas` в BBE_Mods).
- `ComputedStyle` (`Core/StyleEngine.cs`): вычисленный стиль (`Get(property)`).
- `StyleEngine`: каскад; `Template`, `Handlers`, `TemplateChanges`, `IVirtualHost` (`Core/Template.cs`): шаблоны и обработчики.
- `Expr`, `LoopScope` (`Core/Expr.cs`): выражения шаблона, область цикла.
- `IPointerGeometry` (`Core/PointerEvents.cs`), `ITextMeasurer`, `UiControls` (`Render/UiControls.cs`): интерфейсы и контролы, которые возвращают публичные члены.
- Типы, которые публичны только потому, что встречаются в сигнатурах (CSS-разбор и значения): `CodePos`, `Combinator`, `Compound`, `Specificity`, `Selector`, `Rule`, `Declaration`, `KeyframesRule`, `KeyStop`, `StyleSheet`, `Length`, `Rgba`, `Unit`, `DPt`, `UiTransform`, `PopupRequest`. Пользоваться ими напрямую не нужно; работайте через `ComputedStyle` и `Element`.

### Общие (`CSharp/Shared`)

- `UiPlugin` — точка входа мода (`IAssemblyPlugin`); не вызывается из модов.
- `UiInfo` — `Version = 0`, `AssemblyName`, `Ping(caller)`: остаток стадии 0 (пакеты `BBE_UI_ProbeA/B` его используют). Не версия движка: версия — `BaroUi.Version`. Уйдёт вместе с пробами.
- `PointCurve`, `CurveMode` (`Shared/PointCurve.cs`): текст кривой `x:y, x:y`, режимы `Linear`, `Smooth`, `Step`. Тот же код есть в `own/BBE_LuaGates` (побайтно, проверяет `PointCurveTests`).

## Внутреннее (`internal`)

Всё остальное из `own/BaroUI/CSharp`: разбор HTML и CSS (`HtmlParser`, `CssParser`, `CssCursor`, `Length` и `Rgba` публичны, см. выше), раскладка (`LayoutEngine`), стили и анимации (`Animation`, `Ease`, `Animator`), значки (`Icons`), шрифты (`FontCache`, `FontFamilies`, `FontFace`), геометрия и рисование (`Coverage`, `Corners`, `Gradient`, `GradientArea`, `BoxShadow`, `OutlineSpec`, `NineSlice`, `DrawGeometry`, `GeometryCache`, `RoundedClip`, `ShadowRaster`, `ShadowSlices`, `DecorScale`, `DecorBudget`, `DecorPixels`, `CornerSpec`, `CornerRaster`), графики (`ChartModel`, `ChartScale`, `GaugeMath`, `CurveEditor`, `CanvasRegistry`), текст (`RuText`, `TextEdit`, `TextDecor`, `TextShadow`, `NumberField`, `RangeField`), редактор кода (`CodeLexer`, `CodeHighlighter`, `CodeEdit`), всплывающие окна (`Popup*`), фокус, навигация, ввод (`PointerRouter`, `KeyRouter`, `FocusOrder`, `NavRoute`, `RadioGroup`, `FileInput`, `DragDrop`), JSON-модель (`JsonModel`), видео (`UiVideos`, `VideoSpec`, `VideoSource`, `VideoLifecycle`, `VideoSlots`, `VideoPhase`, `VideoStep`, `VideoFacts`, `WebmHeader`), отладка (`UiDebug`), `UiEcho` (проверка связи), `SelfTest*`, `Probe`, `ScreenMap`, `TwoGenCache`, `CanvasRegistry`, `UiCharts`, `UiCodeEdit`, `Painter`, `TextureCache`, `FrameStats`, `UiCanvasInput`. Внутренние типы используются только внутри `BaroUI` и его тестов (`tools/ui_test`, `tools/ui_host` собирают тот же код в одну сборку).

Почему некоторые публичные: `UiWindow`, `Element`, `LayoutBox`, `TextureLedger`, `UiDraw`, `UiMouseEvent` и др. используют `BBE_Mods`, `BBE_Sonar`, `BBE_TabMenu` (их нельзя ломать); `Expr`/`LoopScope` — `BBE_TabMenu`; `PointCurve` — документирован. Остальные «технически публичные» — из-за сигнатур (см. выше).

## Версионирование (semver)

- Версия `MAJOR.MINOR.PATCH` в `BaroUi.Version` и `filelist.xml` меняется вместе.
- **PATCH** (0.2.0 → 0.2.1): исправления без изменения публичного API и поведения страниц, которые работали.
- **MINOR** (0.2 → 0.3): новые публичные члены, новые теги, свойства CSS, атрибуты и значения; старые моды компилируются и работают как раньше.
- **MAJOR** (0 → 1 и дальше): ломающие изменения.
- Пока MAJOR = 0, движок ещё в развитии: ломающее изменение допускается в MINOR, но записывается в этом файле с пометкой «ломает» и с версией, в которой оно сделано. Моды проверяют минимум через `BaroUi.Requires`.

Ломающие изменения (любое из этого — MAJOR, а в 0.x — MINOR с пометкой):
- удалить или переименовать публичный тип или член из списка выше; сменить тип параметра или возвращаемого значения; сделать метод невиртуальным или наоборот;
- сменить смысл: `Set`/`Send`/`Done`, порядок аргументов событий, поведение `OnEscape` (true/false);
- переименовать пространство имён (`BaroUI` → `BaroUI` — ломает, решение владельца не принято);
- убрать поддерживаемый тег, свойство CSS, атрибут или значение (например, `fill="sector"`), или изменить их отрисовку так, что страница выглядит иначе;
- изменить формат `.data.json` или текст кривой `x:y`;
- сделать публичным то, что было внутренним, и потом убрать — это тоже ломает.

Не ломает: новый тип или член, новая единица, новое свойство, исправление ошибки разбора, новый пункт в `baroui`.

Правило для авторов: новый публичный тип попадает в список выше в том же коммите, иначе падает `EngineApiTests`.
