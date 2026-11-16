# Baro-UI Example / Baro-UI Example

Шаблон для модов на движке Baro-UI (BaroUI). Открывается командой `baroui_example`; показывает панель состояния субмарины: шапка со значками Lucide, карточки с градиентами и тенями, график мощности, прибор глубины и корпуса, редактируемую кривую балласта, список с закреплённой шапкой, ползунок, флажки, радио и выпадающий список. Кнопка «Дозаправка» меняет данные через C#.

## Русский

### Что в папке

| Файл | Что это |
|---|---|
| `ModConfig.xml` | сборка C# (клиент), имя `BaroUI.Example` |
| `filelist.xml` | описание пакета, имя «Baro-UI Example» |
| `Xml/sync.xml` | пустой список, нужен, чтобы сервер перечислял пакет |
| `CSharp/Client/ExamplePlugin.cs` | вход мода: проверка версии движка, команда `baroui_example` |
| `CSharp/Client/ExampleView.cs` | вид окна: `[UiHandler]`-методы `refuel()` и `reset()` |
| `CSharp/Client/ExampleModel.cs` | модель страницы (простые свойства) |
| `CSharp/Client/ExampleSample.cs` | пример данных в коде (те же числа, что в `example.data.json`) |
| `UI/example.html` | страница: разметка, привязки `{{…}}`, `data-for`, `data-class`, `<gauge>`, `<chart>`, `<curve>` |
| `UI/example.css` | стили: переменные цвета в `:root`, градиенты, тени, переходы, `:hover`, `:has`, `::before`, `@keyframes` |
| `Fonts/Caveat-Bold.ttf`, `Fonts/OFL-Caveat.txt` | свой шрифт мода (OFL, текст лицензии рядом): заголовок берёт семейство `Example Hand` из `@font-face` в `example.css` |
| `UI/example.data.json` | те же данные в JSON: для `tools/ui_host --data` и для проверки; `example.en.data.json` и `example.zh.data.json` — то же на английском и китайском |
| `CSharp/Client/ExampleTexts.cs` | все надписи страницы на трёх языках (ru, en, zh); страница читает их как `t.title`, `t.depth`… Переключатель RU / EN / 中文 в шапке зовёт `lang(code)`. Китайская страница берёт шрифт `Noto Sans SC` (в Noto Sans иероглифов нет) |

### Как сделать свой мод из этого шаблона

1. Скопируйте папку `own/BaroUI_Example` в свой пакет, переименуйте папку и поля:
   - `filelist.xml`: `name` (видимое имя пакета) и `modversion` (ваша версия);
   - `ModConfig.xml`: `FriendlyName` (уникальное имя сборки, не совпадает с другими пакетами);
   - `ExamplePlugin.cs`: `ModName` (то же имя, что в `filelist.xml`: по нему находится папка пакета), `Command`, `EngineMin`.
2. Зависимость от движка. В `modpack.yaml` (для сборки в этом репозитории) — `requires: ["BaroUI"]`. Для Steam Workshop — добавьте Baro-UI в «Required items» вашего мода. Порядок пакетов: Baro-UI должен стоять **выше** вашего мода в списке (как в `deploy.py server_order`).
3. C# в LuaCs. Для пакетов с C# нужно, чтобы LuaCs был включён в игре (C#-скрипты пакетов). Сборка компилируется при загрузке против сборки `BaroUI`; если движок не найден, мод не загрузится.
4. Проверка версии. Первой строкой в `Initialize` стоит `BaroUi.Requires("0.2.0", ModName)`. Старый движок даёт одну строку в журнале и `false`; тогда команду не регистрируйте. Версия — `docs/ui/API.md` (раздел «Версионирование»).
5. Страницы живут в `UI/`: `UI/<имя>.html` и `UI/<имя>.css`. Пути к картинкам — от папки страницы. Данные для просмотра вне игры — `UI/<имя>.data.json`.
6. Вид — класс от `BaroUiView` в `CSharp/Client`. Его `[UiHandler]`-методы вызываются из разметки (`onclick="refuel()"`); модель — `Data` (объект C#), после изменения — `Refresh()`. Если действие меняет состояние игры, отправляйте запрос серверу через `Send(элемент, действие)` и обновляйте `Data` в ответе, а не сразу.
7. Открытие: `BaroUi.Open(путь, new ВашВид(), "имя")`. Закрытие: Esc, крестик (`onclick="close()"`) или `w.Close()`.
8. Консоль: команда регистрируется в `Initialize`, удаляется в `Dispose`. Для разработки страницы: `baroui watch` перезагружает сохранённую страницу (движок), `baroui problems` показывает замечания.

### Свой шрифт мода

Шрифт — файл `.ttf` или `.otf` в пакете мода (здесь `Fonts/Caveat-Bold.ttf`; лицензию OFL положите рядом). Есть два способа; семейство видно только окнам, которые его объявили.

- В CSS, в странице или в `.css`, которую страница подключает (пример: `UI/example.css`):

```css
@font-face {
  font-family: "Мой шрифт";
  src: url("../Fonts/MyFont-Regular.ttf") format("truetype");
  font-weight: 400;
  font-style: normal;
}
.title { font-family: "Мой шрифт", sans-serif; }
```

  Путь берётся от папки страницы (`UI/`) и должен остаться внутри пакета: `../` выше пакета, `http:`, `data:` и абсолютные пути не принимаются. В `src` можно перечислить несколько `url(...)` через запятую: берётся первый читаемый. Жирный и курсив — отдельные блоки `@font-face` с `font-weight` и `font-style`.
- В C#, до открытия окна: `BaroUi.RegisterFont("Мой шрифт", путьКФайлу, 400, false)`. Путь абсолютный (файл вашего пакета). Действует на окна, открытые после вызова.

Правила. Семейство из `@font-face` в окне перекрывает встроенное с тем же именем только в этом окне. Если файла нет или он не читается, список шрифтов идёт к следующему имени, потом к шрифту по умолчанию, и одна строка появляется в `baroui problems`. Высота строки берётся из таблиц файла. Шрифт освобождается вместе с окном.

Что не сделано в этом шаблоне: сетевой обмен (данные локальные), локализация через `TextManager` (`data-t`), сохранение состояния.

### Проверка

- Страница собрана и нарисована `tools/ui_host` (`--page own/BaroUI_Example/UI/example.html --data own/BaroUI_Example/UI/example.data.json --size 1600x900`), замечаний нет: снимок `docs/ui/example_shot.png`.
- `cscheck` (клиент и сервер) и тест `tools/ui_test` (`ExamplePageTests`: без замечаний, колонки сетки, закреплённая шапка, данные).
- Шрифт мода: снимок `tools/ui_host` показывает заголовок в шрифте файла (Caveat), а копия страницы с путём вне пакета даёт строку в `problems` и шрифт `sans-serif` (Noto Sans).
- В игре не запускалось: команда `baroui_example` и поведение в кадре не проверены в игре.

## English

### What is in this folder

| File | What it is |
|---|---|
| `ModConfig.xml` | the client C# assembly, name `BaroUI.Example` |
| `filelist.xml` | the package description, name "Baro-UI Example" |
| `Xml/sync.xml` | an empty list; it makes the server list the package |
| `CSharp/Client/ExamplePlugin.cs` | the mod entry: checks the engine version, the command `baroui_example` |
| `CSharp/Client/ExampleView.cs` | the window's view: the `[UiHandler]` methods `refuel()` and `reset()` |
| `CSharp/Client/ExampleModel.cs` | the page model (plain properties) |
| `CSharp/Client/ExampleSample.cs` | sample data in code (the same numbers as `example.data.json`) |
| `CSharp/Client/ExampleTexts.cs` | every text of the page in three languages (ru, en, zh); the page reads them as `t.title`, `t.depth`… The RU / EN / 中文 switch in the header calls `lang(code)`. A Chinese page uses the font `Noto Sans SC` (Noto Sans has no Chinese glyphs); `UI/example.en.data.json` and `UI/example.zh.data.json` are the English and Chinese sample data |
| `UI/example.html` | the page: markup, bindings `{{…}}`, `data-for`, `data-class`, `<gauge>`, `<chart>`, `<curve>` |
| `UI/example.css` | the styles: colour variables on `:root`, gradients, shadows, transitions, `:hover`, `:has`, `::before`, `@keyframes` |
| `Fonts/Caveat-Bold.ttf`, `Fonts/OFL-Caveat.txt` | the mod's own font (OFL, the licence text next to it): the title takes the family `Example Hand` from the `@font-face` in `example.css` |
| `UI/example.data.json` | the same data as JSON: for `tools/ui_host --data` and the page test |

### How to make your own mod from it

1. Copy `own/BaroUI_Example` into your package and rename the folder and the fields:
   - `filelist.xml`: `name` (the visible package name) and `modversion` (your version);
   - `ModConfig.xml`: `FriendlyName` (a unique assembly name, not used by another package);
   - `ExamplePlugin.cs`: `ModName` (the same name as in `filelist.xml`: the package folder is found by it), `Command`, `EngineMin`.
2. Dependency on the engine. In this repository's `modpack.yaml`: `requires: ["BaroUI"]`. On the Steam Workshop: add Baro-UI to the "Required items" of your mod. Baro-UI must come **before** your mod in the package order (as in `deploy.py server_order`).
3. C# in LuaCs. Packages with C# need LuaCs enabled in the game (C# scripts of packages). The assembly compiles at load against the `BaroUI` assembly; if the engine is missing, the mod does not load.
4. Version check. The first line of `Initialize` is `BaroUi.Requires("0.2.0", ModName)`. An older engine gives one log line and `false`; then do not register the command. The version rules are in `docs/ui/API.md` (section "Версионирование", versioning).
5. Pages live in `UI/`: `UI/<name>.html` and `UI/<name>.css`. Image paths are relative to the page folder. Data for viewing outside the game: `UI/<name>.data.json`.
6. The view is a class derived from `BaroUiView` in `CSharp/Client`. Its `[UiHandler]` methods are called from the markup (`onclick="refuel()"`); the model is `Data` (a C# object); after a change call `Refresh()`. If an action changes game state, send a request to the server with `Send(element, action)` and update `Data` from the answer, not at once.
7. Opening: `BaroUi.Open(path, new YourView(), "name")`. Closing: Esc, the close button (`onclick="close()"`), or `w.Close()`.
8. Console: register the command in `Initialize`, remove it in `Dispose`. While you work on a page, `baroui watch` reloads the saved page and `baroui problems` lists the page's notes.

### Your own font

A font is a `.ttf` or `.otf` file in the mod's package (here `Fonts/Caveat-Bold.ttf`; put its OFL licence text next to it). There are two ways; the family is visible only to the windows that declare it.

- In CSS, in the page or in a `.css` the page links (example: `UI/example.css`):

```css
@font-face {
  font-family: "My Font";
  src: url("../Fonts/MyFont-Regular.ttf") format("truetype");
  font-weight: 400;
  font-style: normal;
}
.title { font-family: "My Font", sans-serif; }
```

  The path is relative to the page folder (`UI/`) and must stay inside the package: `../` above the package, `http:`, `data:` and absolute paths are refused. Several `url(...)` separated by commas in `src`: the first readable one is used. Bold and italic are separate `@font-face` blocks with `font-weight` and `font-style`.
- In C#, before the window opens: `BaroUi.RegisterFont("My Font", pathToFile, 400, false)`. The path is absolute (a file of your package). It applies to the windows opened after the call.

Rules. A family from `@font-face` shadows a built-in family of the same name only in that window. If the file is missing or unreadable, the font list goes on to the next name, then to the default font, and one line appears in `baroui problems`. The line height comes from the file's own tables. The font is released with the window.

Not done in this template: network exchange (the data is local), localization through `TextManager` (`data-t`), saving the state.

### Checks

- The page was rendered with `tools/ui_host` (`--page own/BaroUI_Example/UI/example.html --data own/BaroUI_Example/UI/example.data.json --size 1600x900`) with no notes: the snapshot is `docs/ui/example_shot.png`.
- `cscheck` (client and server) and `tools/ui_test` (`ExamplePageTests`: no notes, grid columns, sticky list header, data).
- The mod's font: the `tools/ui_host` snapshot shows the title in the file's font (Caveat); a copy of the page whose path leaves the package gives one line in `problems` and the `sans-serif` font (Noto Sans).
- Not run in the game: the command `baroui_example` and the behaviour in a frame were not checked in the game.
