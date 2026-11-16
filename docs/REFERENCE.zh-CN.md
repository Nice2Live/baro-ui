译自 REFERENCE.ru.md（以俄文文件为准）。

控制台命令为 `baroui`；旧名 `bbe_ui` 仍然可用（引擎于 2026-11-16 更名为 BaroUI）。

# BBE UI（Baro-UI）：引擎完整参考

参考手册版本：2026-11-16。主版本为俄文；英文及简体中文译文均以此文件为准翻译。

包：`own/BBE_UI`（命名空间 `BaroUI`，LuaCs 模组，通过 `ModConfig.xml` 加载）。引擎对外名称：Baro-UI（所有者决定，2026-11-15）。

来源：`docs/ui/AUTHORING.md`（作者指南）、`docs/ui/PERF.md`、`docs/ui/AUDIT_STAGE_C.md`、`docs/ui/AUDIT_STAGE_D.md`、`docs/HANDOFF.md`（第 0 节）、`docs/ui/DESIGN_*.md`、代码 `own/BBE_UI/CSharp/Client` 与 `own/BBE_UI/CSharp/Shared`、页面 `own/BBE_UI/UI/*.html`、工具 `tools/ui_host`、`tools/ui_preview`、`tools/ui_test`。

## 0. 如何阅读本参考

标记：

- **（「已核对代码」）**：事实已与源码核对；括号中给出文件，必要时给出行号。
- **（「依据 AUTHORING/测试」）**：取自 `docs/ui/AUTHORING.md`、审计报告或 `tools/ui_test` 测试，本次核对未逐行重读。
- **（「未核对代码」）**：未在代码中找到或未核对；这些条目不能视为保证。
- **（「未在游戏中验证」）**：代码存在，但未在游戏中运行。截至 2026-11-16，游戏尚未针对引擎在最近的 D、E、F 批次之后运行；此前的修改仅通过测试、构建和 `ui_host` 验证。

测试：2026-11-16 完整运行 `tools/ui_test`，结果为 8863 通过、0 失败（`docs/HANDOFF.md`，第 0 节）。测试覆盖 `Core/`（解析、布局、样式、几何）；`Render/` 仅通过构建（`cscheck`）、`ui_host` 和游戏验证。

若文中没有标记，则该事实已通过代码核对，或本身就是本参考的定义。

---

## 1. 是什么，如何工作

### 1.1. 概述

BBE UI 是为游戏 Barotrauma 开发的自有 HTML/CSS 引擎。游戏内没有浏览器：HTML 解析、CSS 层叠、布局和绘制均由 C# 实现。仅支持本参考中列出的内容。所有未知内容都会被跳过，并在日志和错误横幅中给出提示（`baroui watch`、`baroui debug`、`baroui problems`）。

流水线（「已核对代码」，调用顺序见 `Render/UiWindow.cs`）：

| 阶段 | 文件 | 作用 |
|---|---|---|
| HTML 解析 | `Core/HtmlParser.cs`、`Core/Dom.cs` | `Element`/`TextNode` 树，void 标签，`style`、`script`、`textarea` 的原始文本，实体表 |
| CSS 解析 | `Core/CssParser.cs`、`Core/CssValues.cs` | 规则、选择器、声明、`@keyframes`；`@media`、`@import`、`@font-face` 会被跳过并给出提示 |
| 数据 | `Core/Template.cs`、`Core/Expr.cs` | `{{…}}`、`data-for`、`data-if`、`data-class`、`data-model`、`data-t`、处理器 |
| 层叠 | `Core/StyleEngine.cs` | 值、继承、`var()`、`!important`、状态（`:hover` 等）、样式缓存 |
| 布局 | `Core/LayoutEngine.cs`、`Core/LayoutBox.cs` | 块级、行内、flex、grid、定位、增量布局 |
| 动画 | `Core/Animation.cs`、`Core/Easing.cs` | 过渡、`@keyframes`、`layout` 过渡、打字机效果 |
| 绘制 | `Render/Painter.cs`、`Render/UiDraw.cs`、`Render/UiCharts.cs`、`Render/UiControls.cs`、`Render/UiImages.cs`、`Render/UiFonts.cs` | 游戏的 `SpriteBatch`、背景、边框、阴影、渐变、文字、控件、图表 |
| 输入 | `Core/PointerRouter.cs`、`Core/KeyRouter.cs`、`Core/DragDrop.cs`、`Core/Popup.cs`、`Core/FocusOrder.cs`、`Render/UiCanvasInput.cs`、`Render/UiFileInput.cs` | 鼠标、键盘、焦点、拖放、弹出窗口、文件 |
| 坐标 | `Core/ScreenMap.cs`、`Core/UiTransform.cs` | 唯一计算「元素在屏幕上的位置」的地方（绘制、鼠标、IME、提示） |

`Core/` 不依赖游戏：其测试可脱离游戏运行（`tools/ui_test`）。`Render/` 与 `Api/` 仅在游戏内运行。

### 1.2. 单位与画布

- `1u = min(屏幕宽度 / 1600, 屏幕高度 / 900) × 游戏 HUD 缩放`（「已核对代码」：`Render/UiWindow.cs`，第 630 行，`UnitPx`）。
- `px`：数值按原样使用，不乘以 `UnitPx`（「已核对代码」：`Core/StyleEngine.cs`，`FontSizePx` 计算，`Unit.Px` 分支）。（「未核对代码」：在 HUD 缩放为非整数时，1 px 是否恰好等于一个屏幕像素。）
- `em`：元素的字体大小；对于 `font-size`，则指父元素的字体大小。
- `%`：相对于该属性指定的边或数值（见属性表）。
- 页面画布为 **1600u × 900u**，居中于屏幕。若屏幕比例不同，剩余部分留空（「已核对代码」：`UiWindow.cs`，第 646–648 行）。
- `<html data-viewport="screen">`（或 `<body>`）：布局视口为整个屏幕，`position: fixed` 和百分比均相对于屏幕边缘计算。单位 `u` 保持不变。浏览器预览不显示此模式（「依据 AUTHORING」；「已核对代码」：`Core/Dom.cs`，`Document.ScreenViewport`）。
- `Core/CssValues.cs` 第 7 行的注释曾写明 `u` 为窗口宽度的 1/1600。该注释已于 2026-11-16 修正：现在给出上述公式；代码行为未变。

### 1.3. 运行环境

- 这是 **LuaCs C# 模组**：`own/BBE_UI/ModConfig.xml` 引入 `CSharp/Shared`（`IsScript="true"`，`FriendlyName="BaroUI"`）和 `CSharp/Client`（`Target="Client"`，同名）（「已核对代码」：`ModConfig.xml`）。
- 游戏和 LuaCs 通过 Harmony 进行拦截（「已核对代码」：`Api/BaroUi.cs`，`Init`）：
  - `DebugConsole.AddToGUIUpdateList` 的前缀：窗口在这里获得帧与输入；
  - 暂停菜单的前缀：Esc（`PauseMenuPrefix`）。
- **窗口在哪里可见**：仅当 LuaCs 在主菜单中保持模组运行时，窗口才在主菜单中可见。这是 BBE 的分支（`forks/LuaCs`，分支 `luacs/bbe` 与 `moonsharp/bbe`；见 HANDOFF）。标准（stock）LuaCs 会在菜单中卸载模组，因此窗口在菜单中不工作；在大厅、回合和编辑器中，标准 LuaCs 可以工作（依据 `docs/HANDOFF.md` 与 `docs/luacs/COMPARE_PLUGIN.md`；每个场景「未在游戏中验证」，需逐一确认）。
- 没有 JavaScript。没有网络：`http(s)://`、`data:` 和 `file:` 地址不会被加载（「已核对代码」：`Core/ImageLayout.cs`，`ResolvePath`）。

---

## 2. 安装与第一个界面

### 2.1. 包的组成

```
own/BBE_UI/
  ModConfig.xml        LuaCs 程序集（Shared 与 Client，FriendlyName BaroUI）
  filelist.xml         contentpackage "BBE UI"，modversion 0.0.1-bbe，corepackage false，gameversion 1.13.5.0
  Xml/sync.xml         空：使本包进入服务器列表，使客户端将其排在依赖它的包之上
  CSharp/Shared/       PointCurve.cs、UiEcho.cs、UiPlugin.cs（客户端与服务器共用）
  CSharp/Client/       Api/、Core/、Render/、Probe.cs（客户端部分）
  Fonts/               JetBrainsMono-Regular/Bold.ttf、Caveat-Regular/Bold.ttf、PTSerif-Regular/Bold/Italic.ttf、
                       Lucide.ttf、lucide-codepoints.json（2379 个名称）、OFL 与 ISC 许可证
  UI/                  页面：*.html 与 *.css（controls、inputs、charts、anim_demo、pseudo_demo、test）
```

（「已核对代码」：`own/BBE_UI` 的目录与文件组成；`filelist.xml` 中的游戏版本为 1.13.5.0。）

Noto Sans 字体**不包含**在本包中：它从已安装游戏的文件夹（`Content/Fonts/NotoSans`）只读取（「已核对代码」：`Core/FontFamilies.cs`）。

包会自行定位：`BaroUi.FindPackageDir` 查找已启用、名称为 `Baro-UI` 且含有文件 `Fonts/JetBrainsMono-Regular.ttf` 的包（「已核对代码」：`Api/BaroUi.cs`，`FindPackageDir`）。

不启动游戏检查构建：`py -X utf8 tools/cscheck.py BBE_UI`（依据 `docs/HANDOFF.md`，第 3 节；已核对脚本存在）。

### 2.2. 最小页面

`own/BBE_UI/UI/fuel.html`：

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

`own/BBE_UI/UI/fuel.css`：

```css
.panel { width: 400u; padding: 16u; background-color: #0b1418; border: 1u solid #4a6a6c; border-radius: 6u; }
.gauge { height: 14u; background-color: #1d3a3f; }
.fill { height: 100%; background-color: #40c8be; }
```

C# 类：

```csharp
using BaroUI.Api;
using BaroUI.Core;

public sealed class FuelView : BaroUiView
{
    protected override void OnOpen() => Data = new { level = 40 };

    [UiHandler]
    private void refill(int id)
    {
        // 发送给服务器的消息；服务器的回复会通过 Refresh() 更新 Data
        Send(Find("#root"), () => { /* отправка */ });
    }
}

// 打开：
BaroUi.Open<FuelView>(Path.Combine(pkgDir, "UI", "fuel.html"));
```

示例说明（「已核对代码」）：

- `[UiHandler]` 处理器是窗口类的方法；方法名即 `onclick` 属性中的名称；私有方法同样可以（`Core/Template.cs`，`Handlers`，注册方法：「public or not」）。
- `Data` 是 C# 对象或字典；修改后调用 `Refresh()`；局部更新用 `Set("level", 42)`（`BaroUiView.Set`、`UiWindow.Set`）。
- 调用 `Send(...)` 会把元素锁定到收到回复为止（默认 5 秒），并添加类 `pending`（「已核对代码」：`Api/BaroUiView.cs`，`Send`/`Done`）。元素必须调用 `Done(source)`。
- （「依据 AUTHORING」）窗口不会自行修改游戏数据：修改通过服务器进行，窗口等待回复。

注意：示例中的 `Find("#root")` 是 `BaroUiView.Find(selector)`，按 CSS 选择器查找；`UiWindow.Find(id)` 只接受 `id`。

### 2.3. 窗口类的生命周期

`BaroUiView` 的方法（「已核对代码」：`Api/BaroUiView.cs`）：

| 方法 | 调用时机 |
|---|---|
| `protected virtual void OnOpen()` | 窗口创建并完成首次布局之后 |
| `protected virtual void OnUpdate(float deltaTime)` | 每帧 |
| `protected virtual void OnAfterInput()` | 处理完本帧输入之后 |
| `protected virtual void OnClose()` | 关闭时 |

（`OnUpdate` 与 `OnAfterInput` 由 `UiWindow` 调用；它们相对布局的精确顺序本次核对未重读，「未核对代码」。）

### 2.4. 打开页面

```csharp
// 自由窗口：Esc 与窗口外点击由引擎处理（若窗口位于最上层，Esc 交给它）
UiWindow w = BaroUi.Open(path, view, "fuel");
var w2 = BaroUi.Open<FuelView>(path);              // 使用类 T 的新实例
UiWindow w3 = BaroUi.OpenPage("controls");         // 当前包的 UI/controls.html（或来自 baroui src）
```

`Open` 函数会把窗口加入自由窗口列表（`windows`），由它们接收帧与输入（「已核对代码」：`Api/BaroUi.cs`，`Open`）。

### 2.5. 物品窗口（hosted window）

物品组件（例如终端，`own/BBE_Terminal/CSharp/Client/TerminalUi.cs`）按如下方式创建窗口：

```csharp
// 窗口不是自由窗口：组件自己将其加入更新列表并负责关闭
window = BaroUi.Create(path, view, "term");
// 在组件的 AddToGUIUpdateList 中：
window.AddToGUIUpdateList(order);
// 取消选择时关闭：
window.Close();
```

Esc 与窗口外点击由游戏处理：组件的 `GuiFrame` 是不可见的 `GUIFrame`，每帧被放置到根块的矩形上（依据 AUTHORING：`window.BoxOf(window.Find("term"))`；对于输入框和提示，精确的屏幕位置由 `ScreenRect` 给出，见第 4.9 节）。

（依据 AUTHORING；「已核对代码」：`Api/BaroUi.cs`，`Create` 将窗口放入 `hosted` 列表，由 `AddWindowsPrefix` 维护。）

这类窗口支持 `baroui reload` 与 `watch`。持有窗口引用的所有者订阅 `BaroUi.Reloaded` 并替换引用（「已核对代码」：`Api/BaroUi.cs`，`public static event Action<UiWindow, UiWindow> Reloaded`；（参数顺序——旧/新——「未核对代码」））。

`window.CaptureNextDraw("файл.png", "id")` 会在下一次绘制后截取块的快照（「已核对代码」：`UiWindow.cs`，`CaptureNextDraw`）。

---

## 3. HTML：标签、属性、控件、模板、事件

### 3.1. 解析标记

- 解析是容错的：多余的闭合标签会关闭最近的匹配祖先，或被忽略（「已核对代码」：`Core/HtmlParser.cs`，类的注释）。
- `<!-- … -->` 注释会被跳过（依据 AUTHORING；（「未核对代码」：注释的独立分支））。示例中写有 `<!doctype html>`；其处理未核对（「未核对代码」）。
- 原始文本（不是标记）：`style`、`script`、`textarea`（「已核对代码」：`RawText`，`HtmlParser.cs`）。对于 `textarea`，其内容即字段的值。
- Void 标签（无闭合标签；「已核对代码」：`Void`，`Core/HtmlParser.cs`）：`br`、`hr`、`img`、`input`、`meta`、`link`、`item`、`slot`。
- 具名实体（「已核对代码」：表 `Entities`）：`&amp; &lt; &gt; &quot; &apos; &nbsp; &times; &mdash; &ndash; &hellip; &rarr; &larr; &uarr; &darr; &middot; &deg; &laquo; &raquo; &numero; &minus; &plusmn;`。数字引用 `&#NNN;`（「未核对代码」）。对于列表之外的字符，直接使用 UTF-8 字符本身。
- `<bar>` 已从引擎中删除（2026-11-16，核对第 15 条的决定）：没有任何页面、包或代码使用它，也没有绘制逻辑，它只存在于 void 标签列表和 UA 中。现在它是未知标签；页面中不再使用；未知标签在布局中的行为未核对代码（「未核对代码」）。
- 编码：页面文件为 UTF-8（依据 AUTHORING；「未核对代码」：显式的编码检查）。

### 3.2. 标签表

「默认」列是 `Core/StyleEngine.cs`（`UserAgentCss`）中的 UA 规则（「已核对代码」）。

| 标签 | 默认 | 作用与说明 |
|---|---|---|
| `html`、`body` | block | 页面根；在它们上使用 `data-viewport="screen"`（第 1.2 节） |
| `div`、`section`、`header`、`footer`、`nav`、`main`、`aside`、`p`、`form` | block | 普通块 |
| `h1` | block，`font-size: 2em`，加粗 | |
| `h2` | block，`font-size: 1.5em` | |
| `h3` | block，`font-size: 1.17em` | |
| `h4` | block，未设置大小 | `h1`–`h4` 与 `th` 默认加粗（UA） |
| `ul`、`ol`、`li` | block | 没有列表标记（「已核对代码」：没有 `list-item` 分支） |
| `table` | block | 不是表格：没有 `display: table` 布局，为带日志提示的块（见第 4.6 节，`display`） |
| `tr` | flex，行 | （「已核对代码」：UA `tr { display: flex }`） |
| `td`、`th` | block，`flex: 1 1 0` | 在行 `tr` 中平均分配 |
| `span`、`b`、`strong`、`i`、`em`、`a`、`label` | inline | `b`、`strong` 加粗；`i`、`em` 斜体；`i[data-icon]` 为图标（第 7 节） |
| `img` | inline-block | `src`、`alt`；尺寸见下文 |
| `button` | inline-block | `onclick`、`data-default`、`data-item`；按钮外观只能由 CSS 设置（没有外观的 UA 规则） |
| `input` | inline-block，12em × 1.8em | 输入框、复选框、单选框、滑块 — 第 3.5 节 |
| `select` | inline-block，12em × 1.8em，光标 `pointer` | 下拉列表 |
| `textarea` | inline-block，30em × 12em | 代码编辑器 — 第 5.6 节 |
| `progress` | inline-block，10em × 0.5em，背景 `#1d3a3f`，圆角 2px | `value`、`max` |
| `item` | inline-block，40u × 40u | 游戏物品图标 |
| `canvas` | inline-block，300u × 150u | C# 绘制 — 第 6 与第 8 节 |
| `slot` | inline-block | 游戏的物品栏格子；尺寸由 CSS 设置 |
| `dialog` | `display: none`；`dialog[open]` 为 block；absolute，`z-index: 1000` | 模态窗口 |
| `curve`、`chart`、`gauge` | block（UA 规则 `curve, chart, gauge { display: block }`，2026-11-16 添加；此前默认为 `inline`） | 尺寸由 CSS 设置（`height`、`width`）；页面 `UI/charts.html` 自己设置了 `display: block`，与此一致；见第 6 节 |
| `head`、`title`、`meta`、`style`、`script`、`option`、`template` | `display: none` | 不绘制 |
| `link` | 不绘制 | `<link rel="stylesheet" href="…">` 引入 CSS（`UiWindow.cs`，第 175 行） |
| `br` | 换行 | |
| `hr` | （「未核对代码」：线条绘制） | 未经核对请勿使用 |

单独说明（「已核对代码」）：

- **`<img>` 的尺寸。** 若 CSS 让某一边为 `auto`，则取图片文件的像素尺寸（`Core/LayoutEngine.cs`，`IImageSizes`）。一边设为 `px`/`u`/`em` 时，另一边按图片比例计算。文件不存在时块为 0 × 0。找不到图片时，绘制一行 `alt` 文本（依据 AUTHORING）。
- **`<option>`** 不绘制；列表项取自子元素 `option`（`UiControls.OptionsOf`）。
- **`<label>`**：点击时调用 `ClickLabel`（`Render/UiControls.cs`，第 413 与 425 行）。具体传给关联字段的内容（「未核对代码」）。
- **`<a>`** 只有带 `onclick` 时才可聚焦、可点击（`UiControls.cs`，第 255 行）；不带 `onclick` 时 `a` 只是文本。

### 3.3. 属性

通用（「已核对代码」，除非另有说明）：

| 属性 | 作用 |
|---|---|
| `id` | 用于 `Find(id)`、`focus('id')`、`showModal('id')`、`SetCanvas(id, …)`、`SetFileInput(id, …)` 的名称 |
| `class` | 选择器的类；`data-class` 按条件修改它们 |
| `style` | 内联声明：强于任何选择器（依据 AUTHORING） |
| `tabindex` | `-1`、`0` 或正数：焦点与 Tab 顺序（第 5.3 节） |
| `title` | 提示；鼠标悬停 0.4 秒后出现（`UiControls.cs`，`TooltipDelay = 0.4f`）；在游戏中显示，预览中不显示 |
| `accesskey` | 单个字母：Alt+字母 激活元素或聚焦输入框（`UiControls.cs`） |
| `lang` | 语言；`ru`（或没有 `lang`）在 `-bbe-ru-glue: auto` 时启用俄语粘连（第 4.6 节） |
| `data-*` | 见下方各节；未知的 `data-*` 会被忽略（依据 AUTHORING） |

控件属性：

| 属性 | 位置 | 作用 |
|---|---|---|
| `type` | `input` | `text`（默认）、`number`、`password`、`search`、`checkbox`、`radio`、`range`。其他值（`email`、`date`、`color` 等）不被识别为字段：`IsText`、`IsCheckbox`、`IsRadio`、`IsRange` 都不认（`UiControls.cs`，第 128–135 行）；其渲染方式（「未核对代码」） |
| `name` | `input type=radio` | 窗口内单选框分组 |
| `value` | `input`、`option`、`progress`、`textarea`、`curve`、`chart`、`gauge` | 值；对于 `textarea`，为标签内容 |
| `checked` | `input` | 已选中（`:checked`） |
| `placeholder` | `input`、`textarea` | 空字段的提示（`UiControls.cs`、`UiCodeEdit.cs`） |
| `maxlength` | 文本 `input` | 长度限制（`UiControls.cs`，第 161 行，`MaxLength`） |
| `disabled` | 带控件的元素 | 不响应，`:disabled`（`UiWindow.IsDisabled`） |
| `readonly` | `textarea` | 只读（`UiCodeEdit.cs`） |
| `autofocus` | `dialog` 内的字段 | 模态窗口打开时获得焦点（`UiControls.cs`，第 573–585 行） |
| `min`、`max`、`step` | `input type=number`、`range` | 范围与步长；`step="any"` 使滑块无刻度网格（依据 AUTHORING） |
| `on*` | 任意 | 见第 3.7 节 |
| `src`、`alt` | `img` | 文件与替代文本 |
| `href`、`rel` | `link` | `rel="stylesheet"`，路径相对于页面所在文件夹 |
| `open` | `dialog` | 已打开（由 `showModal` 设置） |
| `closedby="any"` | `dialog` | 点击窗口外关闭（`UiControls.cs`，第 362 行） |
| `prefab`、`count` | `item` | 按预制体名称指定游戏物品；`count > 1` 时在角上显示数字（`UiControls.cs`，第 1264 与 1282 行） |
| `sprite` | `item` | 游戏精灵 `类别:id`（第 7.3 节） |
| `inventory`、`index` | `slot` | 物品栏格子；名称来自 `Window.SetInventory`（第 8 节） |
| `data-lang`、`tabsize`、`data-tabs`、`data-autoindent`、`data-gutter` | `textarea` | 代码编辑器（第 5.6 节） |
| `data-viewport` | `html`、`body` | 屏幕代替画布（第 1.2 节） |
| `data-picked` | `chart` | 被点击选中的点的索引（由引擎在 `onpick` 之前设置） |

### 3.4. 游戏专用标签与值

- `<item prefab="oxygentank" count="3">`：游戏物品图标；`prefab` 按名称查找，未知名称会在日志中写入一行「no such item」（`UiControls.cs`，第 1270 行起）。
- `<item sprite="item:oxygentank">`、`sprite="talent:…"` 等：游戏精灵（第 7.3 节）。
- `<canvas id="scope">`：由 C# 绘制：`Window.SetCanvas("scope", (sb, rect) => …)`（第 8 节）。鼠标使用 `SetCanvasMouse`，文件中的图片使用 `Window.Images`（第 6 节）。
- `<textarea id="code" data-lang="xml">`：代码编辑器（第 5.6 节）。
- `<slot inventory="in" index="0">`：游戏物品栏格子（`Window.SetInventory("in", inventory)`）。拖放、提示和堆叠在游戏自行更新物品栏时可用。尺寸由 CSS 设置；游戏在格子上方绘制状态条，请在上方留出空间。滚出边缘或被 `dialog` 覆盖的格子不接收鼠标。调用 `SetInventory(name, null)` 或关闭窗口后，格子归还游戏（依据 AUTHORING）。
  - 格子边框由 `-bbe-slot-frame` 设置（不继承）：`game`（默认，游戏的精灵图）、`none`（无边框：显示 `<slot>` 元素自身的背景、边框和圆角；游戏只绘制物品、状态条和堆叠数）、`url("frame.png")`（自定义图片，拉伸到格子）。找不到图片时回退为游戏边框，并在日志中写一行（「已核对代码」：`Render/UiControls.cs`，`SlotFrame`；「未在游戏中验证」）。
- `<dialog id="…">`：模态窗口（第 5.5 节）。
- `<progress value max>`：没有 `value` 时为滚动条（依据 AUTHORING；（「未核对代码」：滚动条动画））。
- `<curve>`、`<chart>`、`<gauge>`：第 6 节。

### 3.5. 输入框、复选框、单选框、滑块、列表、按钮

通用（「已核对代码」`Render/UiControls.cs` 与 `Core/TextEdit.cs`，除非另有说明）：

- 文本框（`text`、`number`、`password`、`search`）接收键盘与 IME；此时游戏中的角色不会被控制。
- 点击获得焦点：输入框与代码编辑器自行获得焦点；任何带显式 `tabindex`（包括 `-1`）的元素同样如此。按钮、复选框、单选框、滑块和列表**没有** `tabindex` 时点击不获得焦点——它们只能通过 Tab 获得键盘（依据 AUTHORING，第 5.3 节）。Tab 能停留的元素集合见 `UiControls.cs` 第 255 行（「已核对代码」）。

#### 3.5.1. 文本框

按键（「已核对代码」`TextEdit.cs` 与 `UiControls.cs`，除非另有说明）：

- 左右方向键；Ctrl+方向键 按单词移动（`TextEdit.WordLeft`、`WordRight`）；Shift 进行选择。
- Home、End；Ctrl+A 全选（`SelectAll`）。
- Backspace、Delete；Ctrl+Backspace 与 Ctrl+Delete 按单词删除（`TextEdit.Backspace(word)`、`Delete(word)`）。
- Enter：若输入框有 `onenter`，则调用它；否则按下最近的 `dialog` 或窗口中带 `data-default` 的按钮（`UiControls.cs`，第 937 行与第 989–993 行）。
- Ctrl+C、Ctrl+X、Ctrl+V：剪贴板（依据 AUTHORING）。对于 `password`，禁止复制与剪切（`UiControls.cs`，第 870 与 873 行）。
- Esc：依次关闭已打开的列表、对话框、窗口（第 5.8 节）。
- Tab 与 Shift+Tab：按焦点顺序切换（第 5.3 节）。
- 长度限制为 `maxlength`（`MaxLength`）。字符过滤：`number` 接受数字、`-`、`+`、`.`、`,`、`e`、`E`（`NumberField.Accept`，`Core/TextEdit.cs`）。

鼠标：

- 点击放置光标（`TextEdit.IndexAt`）；拖动进行选择。
- 双击与三击：（「未核对代码」：对输入框；对代码编辑器见第 5.6 节）。

IME：引擎把输入框在屏幕上的矩形交给游戏（`UiControls.cs`，第 59 与 1120 行），因此 IME 候选窗口位于光标处。组合输入（预输入）本身由游戏处理；（「未核对代码」及「未在游戏中验证」）。

`number`：

- 上下方向键：步长为 `step`（默认 1），数值按网格取整并限制在 `min` 与 `max` 之间（`UiControls.cs`，第 693–714 与 932–933 行；`NumberField.Clamp`）。
- 鼠标滚轮（「已核对代码」，自 2026-11-16 起：`UiControls.Wheel` → `WheelNumber`，`Render/UiControls.cs`；步长与范围由 `NumberField.Stepped` 给出，`Core/TextEdit.cs`，测试 `ControlsTests.cs`，分组「controls」）：在指针下的数字框中每格改变一步，向上增加。有焦点时与方向键相同：立即触发 `oninput`，离开时触发 `onchange`。无焦点时数值立即改变，`oninput` 与 `onchange` 立即触发，并写入模型。到达范围边界时滚轮不再生效，滚动页面。打开的列表与代码编辑器内的滚轮行为不变。（「未在游戏中验证」：滚轮机制本身在游戏中的表现。）
- 离开输入框时，数值写入模型（`UiControls.cs`，函数 `Commit`；依据 AUTHORING）。

`password`：每个字符在屏幕上显示为一个 `*`（`UiControls.cs`，第 1044–1045 行）。

`search`：行为与文本框相同；未找到单独的行为（例如按 Esc 清空）（「未核对代码」）。

#### 3.5.2. 复选框

- `input type=checkbox`：点击与空格键切换（依据 AUTHORING；「已核对代码」：`UiControls.cs`，`IsCheckbox` 分支）。
- 选中时，在内部绘制一个带内边距的方块（`UiControls.cs`，第 1061–1069 行，颜色 `Accent(s, opacity)`）。
- 默认外观（UA）：1em × 1em，`cursor: pointer`，选中时背景为 `#1d3a3f`。内部方块以 `accent-color` 颜色绘制（默认 `#40c8be`）。
- 变化时触发 `onchange`（`UiControls.cs`，第 772 与 795 行；依据 AUTHORING）。

#### 3.5.3. 单选框

- 窗口中具有相同 `name` 的单选框为一组；组内只能选中一个。没有 `name` 的单选框自成一组（依据 AUTHORING；`RadioGroup.cs`）。
- 默认值为 `on`（`RadioGroup.ValueOf`）。
- 点击、Enter 或空格键会选中该单选框并取消其他选中（依据 AUTHORING）。
- Tab 会落在组中已选中的单选框上；若尚无选中项，则落在第一个上。上、左、下、右方向键会移动到相邻项并选中它（依据 AUTHORING；（「未核对代码」：单选框处理程序中的完整按键集合））。
- `data-model`：只需设置在组中的某一个单选框上。`value` 与模型值相等的那个单选框被选中（按文本比较：`3` 与 `"3"` 相等）。没有自身 `data-model` 的单选框同样跟随模型（`Core/Template.cs`，第 451–460 行，「已核对代码」）。
- `onchange` 只发给新选中的单选框，且只在成功写入模型之后触发；写入失败时在日志中给出警告，且不触发 `onchange`（依据审计 D，第 11 条；`UiControls.cs`，第 274 与 283–284 行）。
- 外观（UA）：`input[type=radio] { width: 1em; height: 1em; padding: 0; border-radius: 50%; cursor: pointer }`；选中时背景为 `#1d3a3f`；内部的圆点以 `accent-color` 颜色绘制。

#### 3.5.4. 滑块（`input type=range`）

- 属性：`min`（默认 0）、`max`（100）、`step`（1；`any` 表示无网格）、`value`（未设置时取中点）、`data-model`、`oninput`（每次移动时）、`onchange`（鼠标松开时以及每次按键时）（依据 AUTHORING；`UiControls.cs`，第 742–751 行）。
- 数值吸附到从 `min` 开始的步长网格上；网格之外的 `max` 不可达（`RangeField.Snap`，与 HTML 一致）。
- 鼠标：在轨道上按下会把滑块拖到指针位置，之后跟随鼠标；不会把键盘交给游戏。
- 按键（Tab 聚焦后）：方向键为一步；PageUp 与 PageDown 为十步；Home 与 End 为两端（依据 AUTHORING）。
- 滚轮不移动滑块：它滚动页面（依据 AUTHORING；（「未核对代码」：滑块上的滚轮行为））。
- 引擎绘制：轨道使用文字颜色（透明度 22%），滑块左侧部分与圆形滑块使用 `accent-color` 颜色。尺寸由 `height` 决定：滑块为高度的 4/5，轨道为 3/10（依据 AUTHORING）。

#### 3.5.5. 列表 `select`

- `option value="…"`；选项可设置 `disabled`。
- 展开为一个 8 行的窗口（`DropRows = 8`，`UiControls.cs`，第 20 行）。在展开的列表上滚动滚轮会滚动它（`UiControls.Wheel`，第 532–538 行）。方向键与 Enter 用于选择（依据 AUTHORING）。
- 在带 `scale` 的块内，列表项不按缩放绘制（第 4.9 节）。
- Esc 关闭列表（第 5.8 节）。

#### 3.5.6. 按钮

- 点击调用 `onclick`。点击时按钮不获得焦点（第 5.3 节）；Tab 可以切换到它。
- 在获得焦点的按钮上按 Enter 与空格键——（依据 AUTHORING；（「未核对代码」：`UiControls.cs` 中按钮的分支））。
- `data-default`：在没有 `onenter` 的输入框中按 Enter 时被按下的按钮（第 3.5.1 节）。
- `disabled`：不响应，`:disabled`。UA 规则 `input:disabled, select:disabled, textarea:disabled` 设置 `opacity: 0.5`，光标 `auto`。

#### 3.5.7. 代码编辑器（`textarea`）

见第 5.6 节。

### 3.6. 模板与数据

所有绑定都读取 `Data`（对象或字典）。模板**只读**数据；只有 C# 可以写入（依据 AUTHORING；「已核对代码」：`Core/Expr.cs`，语法中没有赋值）。

| 标记 | 作用 | 说明 |
|---|---|---|
| `{{路径}}` 位于文本与属性中 | 替换为值 | 名称以 `on` 开头的属性中的 `{{}}` 不会被替换（`Core/Template.cs`，第 145 行） |
| `{{路径 \| 格式}}` | `.NET` 格式：`0`、`0.0`、`#`、`,`、`F1` | `{{x \| 000}}`、`{{v \| 0.0}}`（依据 AUTHORING；（「未核对代码」：完整格式集合即 .NET 的 `ToString`）） |
| `data-for="r in rows"` + `data-key="r.id"` | 重复元素 | 带 `data-key` 时，相同键的行会被复用；没有键时按行号复用 |
| `data-virtual="on"` / `"off"` | 虚拟列表 | 在 `overflow: auto/scroll` 内，超过 100 行时自动启用虚拟列表（`Core/Template.cs`，`AutoVirtual`）；行高统一（依据 AUTHORING） |
| `data-if="条件"` | 仅当条件为真时元素存在 | 隐藏元素不构建，也不占位（`Template.cs`，第 386–392 行） |
| `data-class="名称: 条件; 名称2: 条件"` | 按条件设置类 | 缺少冒号的写法会给出提示（`Template.cs`，第 132 行） |
| `data-disabled="条件"` | 按条件设置 `disabled` | |
| `data-model="路径"` | 字段与数据双向绑定 | 用于 `input`、`textarea`、`curve`、`chart`、`gauge`；代码编辑器写入模型的频率不超过每帧一次（依据 AUTHORING） |
| `data-t="id"`、`data-t-title="id"`、`data-t-<属性>="id"` | 翻译 | 文本按 id 在**读取页面时**替换（`Core/Localizer.cs`）；无文本时页面给出提示，元素保持标记中的样子。来源为游戏的 `TextManager`（`Api/BaroUi.cs`，第 44 行）。`own/BBE_UI` 包中没有 `Localization` 文件夹（依据包的组成）。包的文本与相邻包一样进入 `TextManager`：在 `filelist.xml` 中加入 `<Text file="%ModDir%/Localization/English.xml" />`（依据 `own/BBE_Mods/filelist.xml` 与 `own/BBE_Terminal/filelist.xml`）；游戏是否对本页面如此加载，未在游戏中验证。AUTHORING 中原有的「包的文本文件」一句已于 2026-11-16 修正。含 `{{…}}` 的文本在替换之后再绑定 |

内置处理器（「已核对代码」：`Render/UiWindow.cs`，第 153–156 行）：

| 属性中的调用 | 作用 |
|---|---|
| `close()` | 关闭窗口 |
| `showModal('id')` | 打开具有该 `id` 的 `dialog` |
| `closeDialog()`、`closeDialog('id')` | 关闭模态窗口（当前的或具有该 `id` 的） |
| `focus('id')` | 将焦点移到具有该 `id` 的元素 |

表达式（「已核对代码」：`Core/Expr.cs`）：

- 路径：`a.b[0].c`；名称不区分大小写（依据 AUTHORING）。列表可用 `.length` 与 `.count`（`Expr.cs`，第 178 行）。
- 字面量：数字、用 `'单引号'` 括起的字符串、`true`、`false`、`null`。
- `$index`：`data-for` 中的行号（`Expr.cs`，第 224 行）。
- 运算符：`+ - * / %`，比较 `== != < <= > >=`，`&&`、`||`、`!`，括号。字符串用 `+` 拼接（`Expr.cs`，第 270 行）。
- 语法中没有三元运算符 `?:`、函数调用和赋值（「已核对代码」：`Expr.cs`）。

处理器的参数是属性中的表达式，例如 `onclick="craft(r.id)"`。表达式中的 `this` 变量（「未核对代码」；依据 AUTHORING 为元素本身）。

### 3.7. 事件（`on*` 属性）

引擎调用的名称（「已核对代码」：`Fire(e, "…")` 与 `Dispatch` 的调用）：

| 属性 | 触发时机 |
|---|---|
| `onclick` | 左键点击元素；若元素没有 `onclick`，则查找最近的带该属性的祖先（`Template.Dispatch`）。拖动超过 4 px 后不触发（第 5.2 节） |
| `oninput` | 每次修改文本后；滑块为每次移动；`textarea` 每帧最多一次 |
| `onchange` | 离开字段且文本已改变时；复选框为值改变时；滑块为松开时；单选框为模型写入成功之后 |
| `onenter` | 文本框中按 Enter（代替 `data-default` 按钮的点击） |
| `onfocus`、`onblur` | 获得与失去焦点（`UiControls.cs`，第 179 与 235 行） |
| `onclose` | `data-popup`（弹出窗口）与 `dialog` 关闭时（`UiControls.cs`，第 473 与 654 行） |
| `onback`、`onforward` | 鼠标侧键 4、5 以及 Alt+Left / Alt+Right：指针下元素及其祖先，然后是焦点及其祖先（`Core/NavRoute.cs`；第 5.8a 节；已核对代码） |
| `onsave` | `textarea` 中按 Ctrl+S（`UiCodeEdit.cs`，第 265 行） |
| `onpick` | 点击 `chart` 的点或柱；点的索引写入 `data-picked`，并以 `$arg` 传入（`UiCharts.cs`，第 669–670 行；`onpick="pick($arg)"`） |

说明（「已核对代码」）：

- 处理器查找从目标向上遍历祖先：第一个带有对应属性的元素接收事件（`Core/Template.cs`，`Dispatch`，第 682–687 行）。
- 鼠标处理器中的异常会关闭窗口并写入日志（`UiWindow.Fail`）。键盘同理（第 5.8 节；审计 D，第 11 条）。
- 没有 `onkeydown` 与 `onkeyup` 属性；引擎不接收按键释放（代码中没有 `keyup` 的查找）。键盘请使用 `data-keys` 与 `SetKeys`（第 5.3 节）。
- 没有 `onmousedown`、`onmouseup` 等鼠标属性。鼠标请使用 `data-mouse` 与 `SetMouse`（第 5.1 节）。

---

### 3.8. 视频：`<video>`

见 AUTHORING.md 的「视频」一节（俄文）。未在游戏中验证（在 ui_host 中解码可用）。

| 内容 | 方式 | 状态 |
|---|---|---|
| 解码器 | 游戏的 `Barotrauma.Media.Video`（WebM）；不修改分支 | 事实（代码），未在游戏中验证 |
| `src` | 仅 `.webm`，路径同 `<img>`（包内、相对页面）；否则占位并在 problems 中一行 | 事实 |
| `autoplay`、`loop`、`muted`、`volume` 0..1 | 超出范围的 `volume` 被截断，非数字视为 1，均有提示 | 事实 |
| `poster` | 首帧前及失败时显示的图片 | 事实 |
| 自然尺寸 | 从 WebM 头读取视频轨道的宽高，不解码；无文件时 320 x 180 | 事实 |
| `object-fit` | 同 `<img>`；无 `object-position` | 事实 |
| `border-radius` | 背景和边框圆角；画面和 poster 为直角，同图片 | 事实 |
| `play(id)`、`pause(id)`、`stop(id)`、`restart(id)` | 内置处理程序 | 事实 |
| `onended`、`onerror` | `onended` 仅在无 `loop` 时；`onerror` — 无文件、失败或 5 秒内无帧 | 事实 |
| `Window.Video(id)` | `Play`、`Pause`、`Stop`、`Restart`、`Volume`、`IsPlaying`、`Duration`（来自头部） | 事实 |
| 解码 | 仅在元素被绘制时；移出屏幕则卸载，返回时从头开始（无快进） | 事实 |
| 暂停与停止 | 卸载解码器；恢复时从头开始 | 事实 |
| 上限 | 每窗口 2 个解码器；第三个等待，problems 中一行 | 事实 |
| 纹理 | 计入窗口预算（256 MB，4096 项）；经 0.75 秒队列释放 | 事实 |
| 声音 | `muted` 使 `AudioGain` 为 0（解码仍运行）；是否跟随游戏音量设置未确认 | 未验证 |
| 缺失 | `controls`、快进、保留位置的暂停、圆角画面、`object-position`、`<source>` | — |

## 4. CSS：值、选择器、层叠、属性

### 4.1. 单位与值

| 类型 | 是否支持 | 说明 |
|---|---|---|
| `u` | 是 | 画布的主单位（第 1.2 节） |
| `px` | 是 | 数值按原样使用（第 1.2 节） |
| `em` | 是 | 元素的字体大小；对于 `font-size`，为父元素的字体。`letter-spacing`、`word-spacing`、`text-indent`、`text-shadow` 在写下它们的元素上计算（依据 AUTHORING；「已核对代码」：`StyleEngine.cs`，第 1017–1019 行） |
| `%` | 是 | 含义取决于属性：相对于包含块、相对于剩余空间（`background-position`）、相对于图片边（`border-image`）、相对于字体大小（`text-decoration-thickness`）、相对于滚动高度（`sticky`） |
| `fr` | 是 | 仅用于 `grid-template-*` |
| `auto` | 是 | 在属性允许的任何地方 |
| 不带单位的 `0` | 是 | 数字 `0` 即长度；其他不带单位的数字不是长度（`Core/CssValues.cs`，`Length.TryParse`）。不带单位的数字用于 `line-height`、`flex-*`、`opacity`、`scale`、`layout-fps` 等 |
| `rem`、`vw`、`vh`、`vmin`、`vmax`、`ch`、`ex`、`pt`、`cm`、`in` | 否 | `Length.TryParse` 只识别 `px fr em % u`（「已核对代码」：`CssValues.cs`，第 24 行） |
| `calc()`、`min()`、`max()`、`clamp()` | 否 | 不解析（「已核对代码」：`Core/` 中没有这些函数） |

数字：`1.5`、`-2`、`.5`，使用小数点，不用逗号（「已核对代码」：使用 `InvariantCulture` 的 `float.TryParse`）。

### 4.2. 颜色

- 命名颜色（「已核对代码」：`CssValues.cs`，第 60–66 行）：`transparent`、`black`、`white`、`red`、`green`（#008000）、`blue`、`yellow`、`orange`、`gray`/`grey`（#808080）、`cyan`、`teal`、`magenta`、`lime`、`silver`、`navy`、`maroon`、`purple`。不区分大小写。（AUTHORING 已于 2026-11-16 与此对齐：其中现在包含全部 18 个名称，包括 `silver`、`navy`、`maroon`、`purple`。）
- 十六进制：`#rgb`、`#rgba`、`#rrggbb`、`#rrggbbaa`（「已核对代码」：`TryHex`）。
- 函数：`rgb(r, g, b)`、`rgba(r, g, b, a)`；通道为 0–255 的数字或百分比；透明度为 0–1 或百分比；分隔符为逗号或空格，透明度可用 `/` 书写（「已核对代码」：`Rgba.TryParse`）。
- 没有 `hsl()`、`hwb()`、`lab()`、`color-mix()`（「已核对代码」）。
- `currentcolor`：该元素的文字颜色（「已核对代码」：用于 `border-*-color`、`outline-color`、`text-decoration-color`、`text-shadow` 的默认值）。
- 过渡中的透明度：从 `transparent` 过渡时颜色不会「变暗」，而是取目标颜色（依据 AUTHORING；`Core/Animation.cs`）。

### 4.3. 变量与关键字

- 自定义属性：`--名称: 值;`。会继承。可以相互引用（在 `--b` 中使用 `var(--a)`）。
- `var(--名称)` 与 `var(--名称, 备用值)` 会被递归替换（「已核对代码」：`StyleEngine.cs`，`Replace var`，第 1044–1060 行）。若变量不存在且没有备用值，则该声明被丢弃并给出提示（`StyleEngine.cs`，第 958 行）。
- `@keyframes` 内不替换 `var()`（「已核对代码」：`StyleEngine.cs`，第 846 行的 `@keyframes` 注释）。
- 关键字 `inherit`、`initial`、`unset` 受支持（「已核对代码」：`StyleEngine.cs`，`inherit`、`initial`、`unset` 分支）。
- 支持 `!important`（依据 AUTHORING 与第 4.7 节）。

### 4.4. 选择器与组合器

支持（「已核对代码」：`Core/CssParser.cs`，`ParseSelector`，以及 `Core/StyleEngine.cs`）：

- 类型：`div`、`button`；`*` 匹配任何元素。
- 标识符：`#id`；类：`.cls`（连续多个类：`.a.b`）。
- 属性：`[name]` 与 `[name=value]`（值可加引号）。
- 组合器：后代（空格）、`>`（子元素）、`+`（相邻兄弟）、`~`（后续任意兄弟）。它们可以混用：`.x > .y + .z ~ li`。两个组合器相连是规则错误（`CssParser.cs`，第 455 行）。
- 逗号分隔的列表：`a, b, c { … }`。

不支持（规则错误并给出提示）：

- 属性操作符 `[a^=…]`、`[a$=…]`、`[a*=…]`、`[a~=…]`、`[a|=…]`（「已核对代码」：`CssParser.cs`，第 478 行，「only [name] and [name=value] are supported」）。
- 除 `::before` 与 `::after` 之外的伪元素（`::first-line`、`::marker`、`::selection`、`::placeholder` 为错误，「已核对代码」：第 503 行）。
- 命名空间、选择器内的 `@` 规则、选择器中的转义（「未核对代码」）。

特异性（「已核对代码」：`CssParser.cs`，统计 `ids`、`classes`、`tags`）：

- id 为 1 0 0；类、属性与伪类为 0 1 0；类型与伪元素为 0 0 1。组合器不增加权重（依据 AUTHORING）。
- `:not(X)` 取参数的权重。`:has(X)` 取参数的权重（`CssParser.cs`，第 516 行）。
- 顺序：特异性相同时，后声明者胜出（依据 AUTHORING 与第 4.7 节）。

### 4.5. 伪类与伪元素

伪类（「已核对代码」：`Compound.KnownPseudos`，`CssParser.cs`，第 135 行，以及 `:not`、`:has`、`:nth-child` 的解析）：

| 伪类 | 何时为真 |
|---|---|
| `:hover` | 指针位于元素或其后代之上（依据 AUTHORING） |
| `:active` | 鼠标按键在元素上按下 |
| `:focus` | 元素获得焦点（`NodeState.Focus`） |
| `:focus-within` | 元素或其祖先获得焦点（页面根除外） |
| `:disabled` | 具有 `disabled` 属性或处于 `Disabled` 状态（`CssParser.cs`，第 113 行） |
| `:checked` | 具有 `checked` 属性（`CssParser.cs`，第 116 行） |
| `:root` | 页面根 |
| `:first-child`、`:last-child` | 在兄弟元素中为第一个或最后一个元素 |
| `:nth-child(N)`、`:nth-child(odd)`、`:nth-child(even)` | 在兄弟元素中的序号（「已核对代码」：`CssParser.cs`，第 121–131 行） |
| `:dragging` | 当前拖动的源（`DragDrop`） |
| `:drag-over` | 拖动时指针下的接收者，且它接受该类型 |
| `:not(X)` | X 为单个复合选择器，不含组合器、不含列表、不含嵌套的 `:not`，且不含伪元素（「已核对代码」：第 536–540 行） |
| `:has(X)` | X 为单个相对选择器（`:has(.a)`、`:has(> .a)`、`:has(+ .a)`、`:has(~ .a)`，或含组合器的链）；不支持列表、嵌套 `:has` 与伪元素（「已核对代码」：第 575–588 行） |

限制（「已核对代码」）：

- `:nth-child(2n+1)` 及类似的 `an+b` 不被解析：不会匹配。（关于它的提示：（「未核对代码」）。）
- `:link`、`:visited`、`:empty`、`:is()`、`:where()`、`:lang()`、`:first-of-type` 及其他伪类为规则错误并给出提示（`CssParser.cs`，第 550 行：「pseudo-class :X is not supported」）。

状态性能：`:hover`、`:active`、`:focus`、`:focus-within`、`:dragging`、`:drag-over`、`:has()` 只会重新计算规则可能影响的部分（第 10 节，`PERF.md`）。

#### 伪元素 `::before` 与 `::after`

（依据 AUTHORING；「已核对代码」：`CssParser.cs`，第 496–512 行，`StyleEngine.cs`，`ResolveOne`，`PseudoTextKey`。）

- `.a::before { … }` 在元素内容之前绘制一个框，`::after` 则在之后绘制。旧写法 `:before` 与 `:after`（单冒号）同样被接受（`CssParser.cs`，第 496 行的注释）。
- 该框在布局中作为第一个或最后一个子元素存在，但**不是元素**：它不在树中，模板、处理器和查询都看不到它。悬停、按下和鼠标事件作用于元素本身；框不接收自己的事件。
- 每个选择器只能有一个伪元素，且必须位于末尾：`.a:hover::before` 可以；`.a::before .b` 与 `::before::after` 是错误。
- 只有当 `content` 不是 `none` 与 `normal` 时才会创建框。其样式：`display`（默认 `inline`）、`position: absolute` 与 `top`/`left`、`background`、`border`、`border-radius`、`box-shadow`、`width`、`height`、`padding`、`margin`、`transition`。可继承属性取自元素。
- 元素的状态也作用于该框：`.a:hover::before { color: #f88 }`。
- `content`：带引号的字符串，可使用 CSS 转义（`"\201C"`）；`attr(名称)`，即属性值；`icon(名称)`，即 Lucide 图标（与 `data-icon` 相同，第 7.2 节；若规则未设置 `font-family`，则字体为 `lucide`）。各部分连续拼接：`content: "№ " attr(data-id)`。`none` 与 `normal` 表示没有框。`counter()`、`counters()`、`url()` 不生成框，并在日志中给出一条提示（「已核对代码」：`StyleEngine.cs`，第 757 行）。
- 不支持：`::first-line`、`::first-letter`、`::marker`、`::selection`、`::placeholder`、`counter()`、`counters()`、`content: url(…)`。
- 示例：`own/BBE_UI/UI/pseudo_demo.html` 与 `pseudo_demo.css`。

### 4.6. 属性

已知属性的完整列表见 `StyleEngine.Initial` 与 `StyleEngine.Shorthands`（「已核对代码」，`Core/StyleEngine.cs`，第 196–251 行）。其他一切都会产生 `unknown property NAME` 提示（`CssParser.cs`，第 433–435 行），且不生效。

默认值取自 `Initial`。属性是否继承见第 4.7 节。

#### 4.6.1. 布局与盒子

| 属性 | 值 | 说明 |
|---|---|---|
| `display` | `block`（UA 中块的默认值）、`inline`（其余元素的默认值）、`inline-block`、`inline-flex`、`inline-grid`（任何 `inline-*` 都是行内原子）、`flex`、`grid`、`flow-root`、`none` | `table`、`table-row`、`table-cell`、`list-item` 为带提示的块（`LayoutEngine.cs`，第 209–211 行）。`flow-root` 为一种块，其子元素的外边距不与它自己的外边距合并：`BfcRoot`（在 `LayoutEngine.cs` 第 208 行设置）禁止上下外边距合并（第 605–606 行）。未知值：（「未核对代码」：是否给出提示） |
| `position` | `static`（默认）、`relative`、`absolute`、`fixed`、`sticky` | `fixed` 相对于窗口（视口）的 `absolute`。`sticky` 见第 4.9 节 |
| `top`、`right`、`bottom`、`left` | 长度、`%`、`auto` | 用于 `relative`、`absolute`、`fixed`；对 `sticky` 只有 `top` 与 `bottom` |
| `width`、`height` | 长度、`%`、`auto` | `auto` 按内容计算（图片为文件尺寸，第 3.2 节） |
| `min-width`、`min-height` | 长度、`%`、`auto`（默认） | |
| `max-width`、`max-height` | 长度、`%`、`none`（默认） | |
| `margin`（+ `-top`、`-right`、`-bottom`、`-left`） | 1–4 个值，`auto` | `auto` 居中（依据 AUTHORING）。负值（「未核对代码」） |
| `padding`（+ 各边） | 1–4 个值，长度与 `%` | `%` 相对于包含块的宽度（按 CSS；（「未核对代码」）） |
| `box-sizing` | `border-box`（默认，**与浏览器不同**：浏览器为 `content-box`）、`content-box` | `LayoutEngine.cs`，第 450、902、1093 行 |
| `z-index` | 整数，`auto`（默认） | 同级元素之间的绘制顺序 |
| `visibility` | `visible`、`hidden` | `hidden` 保留空间；可继承 |
| `opacity` | 0–1 的数字 | 不继承；作用于整个块及其后代的绘制组 |
| `overflow` | 1–2 个值 | `overflow-x` 与 `overflow-y` 的简写：`overflow: hidden auto` |
| `overflow-x`、`overflow-y` | `visible`（默认）、`hidden`、`auto`、`scroll` | 除 `visible` 外的任何值都按矩形裁剪（「已核对代码」：`LayoutBox.ClipX`、`ClipY`，`LayoutEngine.cs`，第 211–212 行）。`clip` 与 `hidden` 相同（「已核对代码」：`ClipX`/`ClipY` 对除 `visible` 外的任何值生效；没有滚动条）。自 2026-11-16 起在 AUTHORING 中描述。`auto` 与 `scroll` 会显示滚动条（第 4.6.3 节） |

弹性布局与网格（「已核对代码」：`LayoutEngine.cs`；值依据 AUTHORING）：

| 属性 | 值 | 说明 |
|---|---|---|
| `flex-direction` | `row`（默认）、`column`、`row-reverse`、`column-reverse` | |
| `flex-wrap` | `nowrap`（默认）、`wrap` | |
| `flex-flow` | 简写：`方向 换行` | |
| `flex` | 简写：`grow shrink basis` | |
| `flex-grow`、`flex-shrink`、`flex-basis` | 默认 `0`、`1`、`auto` | |
| `order` | 整数，默认 `0` | |
| `align-items` | `stretch`（默认）、`flex-start`、`flex-end`、`center`、`baseline` | |
| `align-self` | `auto`（默认），同 `align-items` | |
| `justify-content` | `flex-start`（默认）、`flex-end`、`center`、`space-between`、`space-around`、`space-evenly` | |
| `align-content` | `normal`（默认）、`stretch`；其余值读取但无效果 | 自 2026-11-16 起列入已知属性（`StyleEngine.Initial`）；此前会报 `unknown property`。仅对 `flex-wrap: wrap` 有效：`normal` 与 `stretch` 将行拉伸到整个高度（`LayoutEngine.cs`，第 1182 行）。在 AUTHORING 中有描述 |
| `gap`、`row-gap`、`column-gap` | 长度，默认 `0` | 用于 flex 与 grid；`gap` 为简写 |
| `grid-template-columns`、`grid-template-rows` | `none`（默认）、长度、`fr`、`auto`、`min-content`/`max-content`（按 `auto` 读取）、`repeat(N, …)` | `LayoutEngine.cs`，第 1375 行 |
| `grid-column` | `auto`（默认）、`N`、`span N`、`N / M`、`N / span M` | |
| `grid-row` | — | 不支持（2026-11-16 决定：不做）。网格行按顺序排列，`grid-column` 只设置列；显式行号需要改动自动放置 `Place`，这不是「与 grid-column 相邻的小改动」 |
| `grid-area`、`justify-items`、`justify-self`、`place-*` | — | 未知：给出提示 |

块级按钮与分区：`tr` 为 flex 行，`td` 与 `th` 为 `flex: 1 1 0`（第 3.2 节）。

#### 4.6.2. 图片尺寸与 `object-fit`

| 属性 | 值 | 说明 |
|---|---|---|
| `object-fit`（用于 `img`） | `fill`（默认）、`contain`、`cover`、`none`、`scale-down` | 图片在内容区域中居中；没有 `object-position`（「已核对代码」：`Initial` 中没有） |

#### 4.6.3. 滚动

- `overflow: auto`：在内容右边缘上的细滚动条。`overflow: scroll` 加 `padding-right`：滚动条位于该内边距中，内容不会进入其中（依据 AUTHORING；（「未核对代码」：滚动条几何））。
- 滚轮滚动，滚动条可用鼠标拖动，点击轨道会把滚动条移到光标处（依据 AUTHORING）。
- `scrollbar-color`：滚动条颜色；`scrollbar-width`：`thin`、`none`、`auto`（`Initial`，「已核对代码」）。
- `UiWindow.ScrollBy(e, dy)` 与 `GetScroll(e)`：C# 中的滚动（第 8 节）。

#### 4.6.4. 文本与字体

| 属性 | 值 | 继承 | 说明 |
|---|---|---|---|
| `color` | 颜色 | 是 | 默认 `#000000`；根（UA 中的 `:root`）为 `#ffffff` |
| `font-family` | 列表中的名称 | 是 | 见第 7.1 节 |
| `font-size` | `px`、`u`、`em`、`%` | 是 | 默认 `16px`。**不要每帧修改**：每个新尺寸都是一种新的游戏字体（第 10 节） |
| `font-weight` | `normal`（400）、`bold`（700）、100–900、`bolder`、`lighter` | 是 | `bolder` 与 `lighter` 相对于 400；不取父元素的字重（依据 AUTHORING）。500 与 600 仅 Noto Sans 有；其他字体取最接近的（500 → 400，600 → 700） |
| `font-style` | `normal`、`italic`、`oblique` | 是 | 真正的斜体仅 PT Serif 常规字重有；其他为倾斜（第 7.1 节） |
| `line-height` | `normal`、数字（`1.3`）、长度 | 是 | `%`、`em`、`u` 在继承前已转换为 px（依据 AUTHORING；`StyleEngine.cs`，第 1019 行） |
| `text-align` | `left`（默认）、`center`、`right` | 是 | 没有其他值 |
| `white-space` | `normal`、`nowrap`、`pre`、`pre-wrap`、`pre-line`、`break-spaces` | 是 | `pre` 保持整行不换行。`pre-wrap`、`pre-line` 与 `break-spaces` 按空格换行，其中的空格会折叠（`LayoutEngine.cs`，第 321–327 行）。截至 2026-11-16，`break-spaces` 的行为与 `pre` 相同（不换行）；现已修正为与 `pre-wrap` 相同，测试 `TextWrapTests.cs`（分组「text wrap」）。在 AUTHORING 中有描述 |
| `text-overflow` | `clip`（默认）、`ellipsis` | 否 | 需要 `overflow: hidden` 与 `white-space: nowrap`（依据 AUTHORING） |
| `text-transform` | `none`、`uppercase`、`lowercase`、`capitalize` | 是 | |
| `letter-spacing` | `normal` 或长度 | 是 | 加在每个字符之后，包括行末字符（依据 AUTHORING）；影响布局 |
| `word-spacing` | `normal` 或长度 | 是 | 加在每个空格上（U+0020 与 U+00A0） |
| `text-indent` | 长度或 `%` | 是 | 仅块的首行；负值为悬挂行 |
| `line-clamp`（及 `-webkit-line-clamp`） | 大于 0 的整数或 `none` | 否 | 保留 n 行；最后一行以 `…` 结尾（依据 AUTHORING；`LayoutEngine.cs`，第 625–628 行）。被舍弃行上的 `inline-block` 块不会被隐藏（审计 C，第 12 条） |
| `-webkit-box-orient` | `horizontal`、`vertical` | 否 | 仅读取，无作用（「已核对代码」：`Initial`） |
| `overflow-wrap`（及 `word-wrap`） | `normal`（默认）、`break-word`、`anywhere` | 是 | `break-word` 会拆开即使在空行中也放不下的单词；`anywhere` 还会使 `min-content` 缩到一个字符；与 `nowrap` 和 `pre` 一起不生效 |
| `-bbe-ru-glue` | `auto`（默认）、`on`、`off` | 是 | 俄语不间断词组，见第 4.6.4a 节 |
| `text-shadow` | `<x> <y> [<blur>] <颜色>` 或 `none` | 是 | 一个**锐利**阴影；`blur` 大于 0 时被忽略（并给出提示）；逗号分隔的列表只取第一个阴影（并给出提示）；没有 `spread`；见第 4.6.4б 节 |
| `typewriter` | `none` 或每秒字符数 | 是 | 打字机效果，见第 4.8 节 |
| `vertical-align` | 任意 | 否 | 仅读取，无效果（依据 AUTHORING） |
| `-webkit-line-clamp` | `line-clamp` 的别名 | | |

**4.6.4a. 俄语粘连（`-bbe-ru-glue`）。** 已核对代码：`Core/RuText.cs`；描述依据 AUTHORING。这是俄语文本的功能：在西里尔字母文本中，以下两个单词之间不会发生换行：

- 第一个词为介词或连词，来自列表 `в к с у о и а я но на за по от до из не ни во ко со об да` 以及 `без для под над при про что как или либо ибо чем`（任意大小写；其后没有逗号）；
- 第二个词为语气词 `же бы ли ль ж б`；
- 第二个词为破折号 `—` 或 `–`；
- 第一个词为数字，第二个词为计量单位（`км м см мм кг г т л мл с сек мин ч шт % ₽ $ € руб г. тыс. млн` 等）；
- 第一个词为 `№`；
- 第一个词为姓名首字母（`А.`），第二个词以大写字母开头。

粘连的词对是一个不可分割的元素；文本本身不变（复制与选择看到的是原文）。`auto`：当元素或其祖先最近的 `lang` 为 `ru` 或 `ru-RU`，或者根本没有 `lang` 时，规则启用；`lang="en"` 及任何其他值都会关闭。`on` 在任何 `lang` 下启用，`off` 关闭。

注意：`auto` 在没有 `lang` 时**会启用**粘连。这是所有者的决定（审计 C，第 4 条；`docs/HANDOFF.md`，问题 1）。因此所有窗口中俄语文本的换行会略有不同。

**4.6.4б. 文字阴影。** 已核对代码：`Core/TextShadow.cs`，`StyleEngine.cs`，第 1257 行。

- 格式：`<x> <y> [<blur>] <颜色>`；颜色可放在任何位置；未给出颜色时取 `currentcolor`（即本帧的文字颜色，因此文字颜色的过渡也会改变阴影）。
- 长度：`px`、`u`、`em`；允许不带单位的 `0`；`em` 与 `u` 在写下阴影的元素上换算为像素（`TextShadow.ResolveUnits`）。
- 文字会以偏移 `(x, y)` 用阴影颜色再绘制一次，位于主文字之下；`opacity` 的块与 `scale` 都会计入。
- 不产生动画（依据 AUTHORING；`Core/Animation.cs`，动画属性表）。

#### 4.6.5. 背景、图片与渐变

| 属性 | 值 | 说明 |
|---|---|---|
| `background-color` | 颜色 | 默认 `transparent` |
| `background-image` | `none`、`url("文件")`、`sprite(类别:id)`、`linear-gradient(…)`、`radial-gradient(…)`、逗号分隔的多层列表 | 渐变绘制在颜色之上；`url` 与 `sprite` 为图片；多层时第一层在最上面（按 CSS） |
| `background-size` | `auto`（默认）、`cover`、`contain`、`<宽度> [<高度>]` | `px`、`u`、`em`、`%` 相对于区域；`auto` 按另一边的比例计算 |
| `background-position` | `left`/`center`/`right`、`top`/`center`/`bottom`、长度、`%`、形式 `right 10px bottom 4px` | 默认 `0% 0%`；`%` 相对于剩余空间（50% 为居中） |
| `background-repeat` | `repeat`（默认）、`no-repeat`、`repeat-x`、`repeat-y`、`space`、`round` | 每个轴可单独指定一个词；块内最多 1024 个副本（依据 AUTHORING） |
| `background` | 简写：颜色、图片、重复、位置、尺寸（`center / cover`） | 取逗号分隔列表的第一层；其余层被跳过并给出提示；简写会重置 `background-size`、`-position`、`-repeat` |

线性渐变（「已核对代码」：`Core/Gradient.cs`；依据 AUTHORING）：

- `linear-gradient(...)`：`to top`、`to right`、`to bottom`、`to left`，角度 `0`、`90deg`、`180deg`、`270deg`（也可用 `turn`、`rad`、`grad`）；`to top right` 等对角方向按 CSS 规则为斜线。默认 `to bottom`。
- 色标：带透明度的颜色、`%` 位置、硬色标。
- 渐变沿块的边框进行；对于色标使用 `em`/`u` 的情况，渐变条的长度按这些单位计算。

径向渐变：`radial-gradient([circle|ellipse] [closest-side|farthest-side|closest-corner|farthest-corner|<长度> [<长度>]] [at <位置>], 色标)`。默认 `ellipse farthest-corner at center`。长度为 `px`、`u`、`em`；位置为 `%`、`px`、`u`、`em` 或 `left/center/right/top/bottom`。色标数量不限。

不支持：`conic-gradient`、`repeating-*`、颜色提示（color hints）、径向渐变中以 `%` 表示的半径。这样的层会给出一次警告，且该块的渐变根本不绘制（依据 AUTHORING；`Core/Gradient.cs`，第 113 行）。

层：`background-image: radial-gradient(...), linear-gradient(...)`。列表中的 `url()` 与 `sprite()` 会被渐变跳过，并以图片绘制（「已核对代码」：`Gradient.cs`，第 122 行）。

渐变的圆角见第 4.6.6 节（各角单独烘焙，每个角用其自己的椭圆；最多 11 个精灵；纹理半径最大 96 px）。

限制（依据 AUTHORING 与审计 C）：长度超过 512 px 纹理的渐变会被采样器拉伸；平滑缩放时，条带与角取最近的缩放级别；每帧烘焙的条带、角与区域不超过 64 个，其余推迟到下一帧并暂时没有渐变。游戏中的外观：（「未在游戏中验证」）。

背景图片（`url()`，「已核对代码」：`Render/Painter.cs`，`ImageCss`）：

- 路径相对于页面所在文件夹（`url(img/a.png)`、`url("../Images/a.png")`）。路径不能超出包含 `filelist.xml` 的文件夹（`Core/ImageLayout.cs`，`ResolvePath`）。`http(s)://`、`data:`、`file:` 地址和绝对路径会被拒绝并给出提示。地址中的 `?` 与 `#` 会被截掉。
- 仅一层。图片没有圆角：角是直的（第 4.6.6 节，限制）。
- 文件不存在或不是图片：日志中写入一行，块无图片。
- 图片在首次绘制时读取并保存在内存中；每个窗口每个文件一个纹理；文件被保存后会重新读取（依据 AUTHORING；`UiImages.cs`，每秒检查一次）。
- `sprite(类别:id)`：游戏精灵（第 7.3 节）。

#### 4.6.6. 边框、圆角、阴影、轮廓

| 属性 | 值 | 说明 |
|---|---|---|
| `border` | 简写：`宽度 样式 颜色` | |
| `border-width` | 1–4 个值 | 简写 |
| `border-color` | 1–4 个值 | 简写 |
| `border-style` | `solid`（默认）、`dashed`、`dotted`；其他（`double`、`groove` 等）按 `solid` 处理 | `dashed` 与 `dotted` 会被绘制；四边使用同一样式（最后写入的带样式声明优先）（依据 AUTHORING） |
| `border-top`、`border-right`、`border-bottom`、`border-left` | 简写：`宽度 样式 颜色` | |
| `border-top-width`（及其他三边） | 不小于 1 的整数像素（屏幕像素） | 紧贴边框的子元素之间没有间隙（依据 AUTHORING） |
| `border-top-color`（及其他三边） | 颜色；默认 `currentcolor` | 圆角块的每一边使用各自的颜色；角按对角线分割；宽度为零或颜色透明的边不绘制（依据 AUTHORING） |
| `border-radius` | 1–4 个值（顺序 TL TR BR BL），用 `/` 表示椭圆 | `%` 对水平半径相对于宽度，对垂直半径相对于高度；相邻角的半径放不下时，按同一倍数缩小（依据 AUTHORING） |
| `border-top-left-radius`（及其他三角） | 一个值（圆）或两个值（椭圆） | |
| `border-image` | 简写；见下文 | |
| `border-image-source` | `none`（默认）、`url(...)`、`sprite(...)` | 精灵整体按切片裁剪（依据 AUTHORING） |
| `border-image-slice` | 1–4 个数字或 `%`，`fill` | 默认 `100%` |
| `border-image-width` | 数字（为 `border-width` 的倍数）、长度、`%`、`auto` | 默认 `1` |
| `border-image-repeat` | `stretch`（默认）、`repeat`、`round`、`space` | 两个轴用一个词，或两个词 |
| `box-shadow` | `<x> <y> [<blur> [<spread>]] <颜色>` 或 `none` | 一个**外部**阴影；不支持 `inset`；上限：`blur` 至 48 px，半径至 96 px 纹理；不改变布局（依据 AUTHORING） |
| `outline` | 简写：`宽度 样式 颜色`，顺序任意 | `outline: 2px red` 没有线型样式时不生效（审计 C，第 6 条） |
| `outline-width` | 长度（`px`、`u`、`em`）或 `thin`/`medium`/`thick`（1/3/5 px） | 不小于 1 的整数屏幕像素 |
| `outline-style` | `none`（默认）；`solid`；`dashed`、`dotted` 等为实线；`none` 与 `hidden`（任意大小写）为无轮廓 | `dashed`、`dotted` 等为实线，并且仅警告一次（依据 AUTHORING；`OutlineSpec.cs`） |
| `outline-color` | 颜色；默认 `currentcolor` | |
| `outline-offset` | 长度，可为负 | |

轮廓行为（依据 AUTHORING；「已核对代码」：`Core/OutlineSpec.cs`，`Render/Painter.cs`）：

- 轮廓**不占空间**，不继承，位于边框外侧 `outline-offset + 宽度` 处。
- 不被块自身的 `overflow` 裁剪，但会被祖先的 `overflow` 裁剪。
- 圆角块的轮廓也是圆角（半径为块的半径加上偏移与宽度）。
- 后绘制的相邻块可能遮住轮廓：轮廓不会「置于整个层叠上下文之上」，这与浏览器不同。
- `:focus { outline: 2px solid …; outline-offset: 2px }` 是输入框与按钮的常用写法。

阴影与圆角（依据 AUTHORING 与审计 C）：

- `box-shadow` 与轮廓使用同一个半径：各角椭圆半径中较小半轴的最大值。角不同时，阴影与轮廓按最大的那个圆角处理。
- 阴影在背景之前绘制；模糊依据块的形状及其半径进行；在 `scale` 内阴影是清晰的。
- 延伸到裁剪区域内部的阴影会被绘制，即使块离边缘很近（审计 C，第 13 条）。
- 半透明块下的阴影是透视的（浏览器会在块下挖空阴影）。这是差异（依据 AUTHORING）。

圆角与裁剪（依据 AUTHORING 以及 `docs/ui/PERF.md`，「按 overflow 圆角裁剪」一节）：

- `overflow: hidden` 按**矩形**裁剪子元素，不按圆角：伸入圆角块角落的子元素会在直角中可见。
- 圆角裁剪的几何已存在（`Core/RoundedClip.cs`），但绘制未启用：在一帧中途切换绘制目标会清空屏幕（已在 `ui_host` 上核对，`PERF.md`）。
- 位于圆角卡片中的图片会伸入角落。这是同一限制。

- **边缘覆盖（通道 G，2026-11-16；未在游戏中验证）。** 圆角块、圆环、角与圆的边缘（`UiDraw.Disc`、角纹理）以解析方式计算：像素中心到边缘的距离经由宽 1 px 的线性过渡得到覆盖率（`Core/Coverage.cs`，`Edge`）。网格上的直边恰好得到 0 或 1，因此圆弧与直线的同宽度一致。原先的 4×4 采样与 0.55–0.7 的幂次修正已移除。1–2 px 的细圆环：部分覆盖的像素以指数 0.8（1 px）与 0.9（2 px）提升，即 `Coverage.ThinLift`（「已核对代码」）；这些数值是通过计算选定的，并非依据 Chrome 截图拟合。超过 2 px 的不做修正；完全覆盖与完全空白的像素不变。限制：不生成分数半径的遮罩（半径会取整为整数像素，`Core/Corners.cs`，`CornerPx`）；圆角裁剪未启用（第 11.1 节）。

#### 4.6.7. 光标、指针与控件颜色

| 属性 | 值 | 说明 |
|---|---|---|
| `cursor` | `auto`、`default`、`pointer`、`grab`、`grabbing`、`text`、`vertical-text`、`move`、`all-scroll`、`*-resize`、`wait`、`progress`、`not-allowed`、`help`、`crosshair`、`zoom-in`、`zoom-out`、`context-menu`、`cell`、`alias`、`copy`、`no-drop`、`none` | 可继承；列表用逗号分隔，取第一个已知关键字；不绘制 `url()`。游戏只有七种光标：手形（`pointer`、`grab`）、`IBeam`（`text`、`vertical-text`）、移动箭头（`move`、`all-scroll`、`*-resize`）、握紧的手（`grabbing`）、沙漏（`wait`）、带光标的时钟（`progress`）。其余为普通光标（依据 AUTHORING；`CssCursor.cs`） |
| `pointer-events` | `auto`（默认）、`none` | `none` 使块对鼠标透明；可继承（「已核对代码」：`Inherited`） |
| `accent-color` | 颜色 | `progress` 的条、复选框、单选框的圆点、滑块的滑块；可继承；默认 `#40c8be` |
| `caret-color` | `auto` 或颜色 | 输入框的光标；可继承 |
| `scrollbar-color`、`scrollbar-width` | 颜色；`thin`、`none`、`auto` | 可继承（`scrollbar-color`） |

`disabled` 块的光标不变（依据 AUTHORING）。未知关键字：警告一次，使用普通光标。

#### 4.6.8. 文字：装饰

| 属性 | 值 | 说明 |
|---|---|---|
| `text-decoration` | 简写：线条、样式、颜色、粗细，顺序任意 | `none`、`solid`、`currentcolor`、`auto` 会重置。不作为值继承（依据 AUTHORING） |
| `text-decoration-line` | `none`、`underline`、`line-through`、`overline`（多个用空格分隔） | |
| `text-decoration-color` | 颜色；默认 `currentcolor` | 在过渡中产生动画 |
| `text-decoration-style` | `solid`（会绘制）；`double`、`dotted`、`dashed`、`wavy` 绘制为实线并给出警告 | |
| `text-decoration-thickness` | `auto`（约为字体大小的 1/14，不小于 1 px）、长度、相对于字体大小的 `%` | |

线条绘制在行内内容及嵌套元素的整个文字下方或穿过文字，颜色取该元素的颜色；不会进入 `inline-block`，也不会进入 `position: absolute/fixed`。文字阴影不会应用于装饰线（依据 AUTHORING；`Core/TextDecor.cs`）。

#### 4.6.9. CSS 中的图片与图标

| 属性 | 值 |
|---|---|
| `content` | 仅用于 `::before`/`::after`：字符串、`attr()`、`icon()`、`none`、`normal`（第 4.5 节） |
| `font-family` 用于图标 | `lucide`（第 7.2 节） |

#### 4.6.10. 位移、缩放与其他

| 属性 | 值 | 说明 |
|---|---|---|
| `translate` | `X Y` 或 `X`（长度） | 绘制时位移；布局不变；鼠标区域随块移动（依据 AUTHORING） |
| `scale` | 单个数字（默认 `1`） | 第 4.9 节；`0`、负数及非数字均视为 1（依据 AUTHORING） |
| `transform-origin` | 任意 | **可读取但不生效**：缩放中心始终是边框的左上角（`StyleEngine.cs`，第 221 行）。保留它是为了让面向浏览器的页面不产生警告 |
| `layout-fps` | `auto`（默认）、数字（每秒次数）、`max` | 长度变化时布局重新计算的频率；`auto` 使用全局值 `BaroUi.LayoutFps`（默认 30，`baroui anim fps N`），而非窗口的帧率（AUTHORING 于 2026-11-16 修正；数字不小于 1，`max` 为每帧） |

完全不存在（「已核对代码」：这些名称不在 `Initial` 与 `Shorthands` 中）：`float`、`transform`（旋转、倾斜、矩阵）、`filter`、`backdrop-filter`、`clip-path`、`mask`、`mix-blend-mode`、`will-change`、`user-select`、`inset`、`aspect-ratio`、`font`（简写）、`grid-row`、`grid-area`、`justify-items`、`justify-self`、`place-*`、`object-position`、`background-clip`、`background-attachment`、`background-origin`、`word-break`、`hyphens`、`text-wrap`、`text-align-last`、`text-justify`、`direction`、`writing-mode`、`font-variant`、`font-stretch`、`list-style`、`counter-reset`、`counter-increment`、`border-top-style` 及类似的各边样式属性、除上述以外的 `outline-*`。

### 4.7. 层叠与继承

已核对代码：`Core/StyleEngine.cs`，`Core/CssParser.cs`。

1. 引擎内置规则（UA 样式表 `UserAgentCss`）优先级最低。
2. 页面规则（`<style>`、`<link>`）：按特异性，再按顺序。
3. `!important` 胜过普通声明；在同一层级内，先比特异性，再比顺序。
4. `style=""` 强于任何选择器（依据 AUTHORING）。
5. 状态（`:hover` 等）是普通伪类：匹配时参与层叠。

可继承属性（「已核对代码」：`Inherited` 集合，`StyleEngine.cs`，第 232–241 行）：

`color`、`font-family`、`font-size`、`font-weight`、`font-style`、`text-align`、`line-height`、`white-space`、`visibility`、`text-transform`、`letter-spacing`、`cursor`、`pointer-events`、`scrollbar-color`、`accent-color`、`caret-color`、`typewriter`、`text-shadow`、`overflow-wrap`、`-bbe-ru-glue`、`word-spacing`、`text-indent`。自定义属性 `--名称` 同样继承。

不继承（其余全部）：`opacity`、`text-overflow`、`line-clamp`、`text-decoration-*`、`box-shadow`、`outline-*`、`background-*`、`border-*`、`translate`、`scale`、`transition*`、`animation*`、`layout-fps`、`content`、布局属性。

状态改变时的重新计算（第 10 节）：只重新计算规则能够看到的部分。

---

### 4.8. 动画

（依据 AUTHORING；「已核对代码」：`Core/Animation.cs`、`Core/Easing.cs`、`Core/CssParser.cs`（`@keyframes`）。）

引擎自行在每一帧计算动画。无需从代码中调用任何方法。

**可以平滑变化的属性**（「已核对代码」：`Props` 表，`Animation.cs`，第 378–398 行）：

| 分组 | 属性 | 方式 |
|---|---|---|
| 透明度 | `opacity` | 数字 |
| 颜色 | `color`、`background-color`、`border-top-color` 及其他三边、`outline-color`、`text-decoration-color` | 颜色 |
| 阴影 | `box-shadow` | 偏移、模糊、扩散与颜色；仅在 `transition` 中，不在 `@keyframes` 中（依据 AUTHORING） |
| 位移与缩放 | `translate`、`scale` | 不重新计算布局 |
| 长度 | `width`、`height`、`min-width`、`min-height`、`max-width`、`max-height`、`top`、`right`、`bottom`、`left`、`margin-*`、`padding-*`、`border-*-width`、`row-gap`、`column-gap` | 驱动布局；见下文 |

不产生动画：`text-shadow`、`font-size`、`border-radius`、`background-size`、`visibility`、`z-index` 及其余（「已核对代码」：它们不在 `Props` 中）。

若一端不是数字（`auto`、`none`），或单位不同，则值立即改变（依据 AUTHORING）。

**`transition`。** `transition: opacity 0.15s ease-out, background-color 0.15s;` 或 `transition: all 0.2s`。

- 缓动：`linear`、`ease`（默认）、`ease-in`、`ease-out`、`ease-in-out`、`step-start`、`step-end`、`cubic-bezier(x1, y1, x2, y2)`、`steps(n)`（「已核对代码」：`Easing.cs`）。
- `transition: all`：上表中的全部属性，包括长度（「已核对代码」：`StartTransitions`，`all` 分支）。由于长度的存在，`all` 可能触发布局；对于列表块，最好显式列出属性。
- 过渡由块所进入的样式触发（与 CSS 一致）：若两个方向都需要，请把 `transition` 写在块的普通规则中，而不只写在 `:hover` 中。
- 新出现的块立即显示，没有过渡。
- 被打断的过渡从其停止时的值开始。
- 时长与延迟使用 `s` 与 `ms`（依据 AUTHORING；（「未核对代码」：`ms` 的解析））。

长度与 `layout-fps`：长度会驱动块的布局。取值按 `layout-fps` 的时间网格（默认每秒 30 次）进行，且只有当值发生变化时才重新布局。这类动画请用于小块，并保持较短（不超过 0.3 秒）。

**`transition: layout`**：不逐帧重算布局的平滑布局（「已核对代码」：`Animation.cs`，第 120 行 — `spec.Flip`）：

```css
.row { transition: layout 0.25s ease-out; }
.panel { transition: layout 0.2s ease-out, opacity 0.15s; }
```

- 块相对于父元素的位置会产生动画（块及其内容一起移动），尺寸也会（块以中间尺寸绘制；内容按最终布局绘制，并被边框裁剪；文字在过程中不会重新排版）。
- 对改变尺寸的块，以及需要移动的相邻块（通常是列表的所有行）开启 `layout`。没有 `layout` 的块会立即跳变。
- 此类帧与普通绘制的开销相同：不重新计算布局。
- 被打断的运动从块当前绘制的位置继续。
- 新块不做动画（出现请用 `opacity` 与 `@keyframes`）。窗口的首次布局与屏幕尺寸变化同样没有过渡。
- `transition: all` 不包含布局块；`layout` 需要显式书写。
- 不要在带 `data-for` 的长滚动列表（虚拟列表）行上使用 `layout`：行会被重新创建，可能跳到新位置。
- 在浏览器预览中看不到运动：块会直接跳变。

**`animation` 与 `@keyframes`：**

```css
@keyframes rise { from { opacity: 0; translate: 0 12u } to { opacity: 1; translate: 0 0 } }
.panel { animation: rise 0.25s ease-out both; }
```

- 关键帧：`from`、`to`、`NN%`。简写（如 `margin`）会展开；`var()` 不替换（「已核对代码」：`StyleEngine.cs`，第 846 行）。
- 若未设置 `from` 或 `to`，则取块普通样式中的值。
- 重复：数字或 `infinite`。方向：`normal`、`reverse`、`alternate`、`alternate-reverse`。`fill`：`none`、`forwards`、`backwards`、`both`。`animation` 简写中的 `running` 与 `paused` 会被接受但不起作用；没有单独的 `animation-play-state` 属性（「已核对代码」：`Animation.cs`，第 270 行；`Initial`）。
- 动画在块出现时开始（页面打开、列表行到达、`data-if` 使块显示）。若块的 `animation` 值改变，则从头开始。被 `data-if` 隐藏后再次显示的块会重新开始。
- 简写：`animation: 名称 时长 [缓动] [延迟] [次数|infinite] [方向] [fill]`；分开写则为 `animation-name`、`animation-duration`、`animation-timing-function`、`animation-delay`、`animation-iteration-count`、`animation-direction`、`animation-fill-mode`。
- 阶梯示例：`.row:nth-child(2) { animation-delay: 0.05s }`。

**打字机。** 对含文字的块设置 `typewriter: 60`，文字将以每秒 60 个字符显示。换行已基于完整文字计算，因此文字不会跳动。窗口报告 `IsTyping`；宿主在按下确认键时调用 `SkipTyping()`（「已核对代码」：`UiWindow.IsTyping`、`SkipTyping`）。当文字改变时重新开始。

**开关。** `baroui anim off` 关闭所有窗口动画：变化立即发生，文字完整显示（第 9 节）。动画时间以窗口时钟计算，而不是世界时钟；（依据 AUTHORING：世界暂停时动画仍继续；`Timing.TotalTime`）。

不存在的功能：对 `font-size` 的 `transition`（每个尺寸都是新字体）；`border-radius` 动画；`text-shadow` 动画；含 `box-shadow` 的 `@keyframes`；`animation-play-state`；`animation-composition`；`transition-behavior`。

---

### 4.9. 缩放、位移与坐标

（依据 AUTHORING；「已核对代码」：`Core/UiTransform.cs`、`Core/ScreenMap.cs`、`Render/Painter.cs`，`EnterScale`/`LeaveScale`。）

**`translate`**：绘制时以屏幕像素为单位位移；布局不变；块及其内容一起移动；鼠标区域同样移动。

**`scale`**：块及其内部所有内容的缩放：

- 缩放中心是块**自身 `translate` 之后**的边框左上角：块内的点 `p` 绘制在 `角 + translate + N·(p − 角)` 处。
- 嵌套的 `scale` 相乘。鼠标、`overflow` 裁剪与文字都与图像一致；文字使用对应大小的字体，并保持清晰。
- 位于屏幕之外的块不绘制，与无缩放时相同。
- **这与浏览器不同。** 在浏览器中，缩放中心在中间。对于面向浏览器的预览，请在旁边写上 `transform-origin: 0 0`。

以下内容在带 `scale` 的块内有效（依据 AUTHORING）：背景、边框、圆角、文字（清晰，屏幕上最大 160 px，超出则拉伸）、按钮、复选框、`range` 滑块、输入框（光标与鼠标选择）、`textarea`、`title` 提示、悬停、`:active`、点击、`overflow: hidden` 与可滚动块、`item`、`progress`、`canvas`（回调在相同像素中绘制，结果随块一起缩放；`SetCanvasMouse` 在这些相同像素中给出位置）。

以下内容在带 `scale` 的块内**不起作用**（依据 AUTHORING；未实现的内容不假装可用）：

- `slot`（物品栏格子）：游戏按屏幕矩形绘制它们；不绘制，每个模板行给出一次警告。
- `select`：列表项不按缩放绘制；列表在屏幕上正确位置打开，列表项宽高与屏幕上输入框一致；列表字体为正常大小。
- `dialog`：在屏幕上打开，不缩放，不带 `translate`，也不带祖先的滚动；给出一次警告。
- 没有自身斜体的字体（JetBrains Mono、Caveat）的合成斜体通过矩阵拉伸，显得模糊；PT Serif 的斜体是真正的斜体。
- 自行读取鼠标（`PlayerInput`）或重新启动包的 `<canvas>` 回调，以屏幕像素计算：请使用 `SetCanvasMouse`。
- `BoxOf(e)` 返回布局矩形，不含 `translate`、动画位移、滚动或 `scale`。屏幕上的位置由 **`ScreenRect(e)`** 给出（`Render/UiWindow.cs`，第 487 行）：它考虑了 `translate`（CSS 或 `SetTranslate`）、元素及祖先的布局动画位移、滚动，以及嵌套的 `scale`，顺序与 `Painter` 绘制时相同（`Core/ScreenMap.cs`）。输入框的光标与 IME 矩形、`title` 提示、`select` 列表、滚动条与滑块的鼠标区域同样以此计算。

**`position: sticky`**（依据 AUTHORING 与 `docs/ui/PERF.md`；「已核对代码」：`ScreenMap.StickyOffset`、`LayoutBox.Sticky`）：

- 块停留在最近的可滚动祖先（`overflow-y` 不为 `visible`）的 `top` 或 `bottom` 处，且不会超出其父块。
- 布局不变：绘制与鼠标都会移动（同一公式位于 `ScreenMap`）。
- 没有可滚动祖先时，块不移动。
- `top`/`bottom` 的值为 `px`、`u`、`em`、`%`，`%` 相对于滚动高度。
- 示例：在可滚动块中使用 `#head { position: sticky; top: 0 }`。
- 未实现：`left`/`right`（水平 sticky）；在带 `transform: scale` 的元素内的 sticky（偏移按未变换的坐标计算）（`PERF.md`）。

**C# 中的 `SetTranslate` 与 `SetScale`**（第 8 节）：数字会替换该元素的 CSS 值（类似 `element.style`），并同时作用于过渡与动画；无需重新应用样式，也不重新计算布局。`NaN` 与无穷大会被忽略；数字不会复制到副本（`Clone`）上。对于每帧移动的所有内容，请为 `data-for` 的行设置 `data-key`：没有键时，数字绑定在行的位置上，插入或重排时会跑偏。

---

## 5. 输入：鼠标、焦点、键盘、菜单、拖放、文件

通用事实（「已核对代码」：`Core/PointerRouter.cs`、`Core/KeyRouter.cs`、`Core/FocusOrder.cs`、`Core/Popup.cs`、`Core/DragDrop.cs`、`Core/FileInput.cs`、`Render/UiControls.cs`、`Render/UiWindow.cs`）：窗口的输入经过每个窗口一个路由器；游戏侧的调用（`PlayerInput`、`DebugConsole`、`PauseMenuPrefix`）只负责把事件传入。

### 5.1. 元素上的鼠标：`data-mouse`、`SetMouse`

元素带有 `data-mouse="名称"` 时会接收鼠标事件。所有者按名称设置处理器：

```csharp
window.SetMouse("card", e => { … });            // Func<UiMouseEvent, bool>
window.SetMouse("card", e => { … });            // Action<UiMouseEvent>：始终返回 true
window.ClearMouse("card");                       // 移除；它持有的捕获将以 Cancel 结束
```

（「已核对代码」：`Render/UiWindow.cs`，第 379–390 行。）

`data-mouse-events="down up click dblclick contextmenu wheel enter leave move hover"`：指定需要哪些事件。没有该属性时接收全部事件，`move` 与 `hover` 除外（`PointerRouter.cs`，第 462–474 行；「已核对代码」）。

类型（`UiMouseKind`，「已核对代码」：`Core/PointerEvents.cs`）：`Down`、`Up`、`Move`、`Enter`、`Leave`、`Wheel`、`Click`、`DoubleClick`、`ContextMenu`、`Hover`、`Cancel`。`Hover` 具体发送什么（「未核对代码」）。

`UiMouseEvent` 的字段（「已核对代码」）：

| 字段 | 含义 |
|---|---|
| `Kind` | 事件类型 |
| `Button`、`Held` | 本事件的按键；所有按住的按键 |
| `X`、`Y` | 屏幕坐标 |
| `LocalX`、`LocalY` | `Current` 元素内容中的点，以其像素计 |
| `Width`、`Height` | `Current` 内容的尺寸 |
| `Target` | 指针下最深的元素 |
| `Under` | 捕获或拖动期间指针下的元素（见下文）；否则为 `null` |
| `Current` | 当前调用的是谁的处理器 |
| `Path` | 从 `Target` 向上的元素链 |
| `Up(attr)` | 带有属性 `attr` 的最近祖先 |
| `Data(name)` | 从 `Target` 起最近元素的 `data-<name>` 值 |
| `Captured` | 事件发给捕获的持有者 |
| `Inside` | 指针位于元素之上 |
| `Dragged` | 位移超过阈值 |
| `ClickCount` | 1、2、3 — 用于 `Click`/`DoubleClick` |
| `StartX`、`StartY` | 按下的起点 |
| `Wheel` | 滚轮格数 |
| `Ctrl`、`Shift`、`Alt` | 修饰键（字段 `Mods`） |
| `Related` | 对于 `Enter`/`Leave` — 另一个元素 |
| `Time` | 秒 |
| `ToLocal(el, out x, out y)` | 转换为 `el` 坐标系中的点 |
| `AsCanvas()` | 同一事件，转换为 `CanvasMouseEvent` |
| `Capture()` | 手动捕获鼠标 |

规则：

- **冒泡。** 事件从指针下最深的元素开始，沿带 `data-mouse` 的祖先向上传递。`true` 停止冒泡。在 `Down` 上返回 `true` 还会**获取捕获**：`Move`、`Up` 与 `Wheel` 会发给持有者（即使在窗口之外），直到其所有按键都松开。
- **`Esc`** 取消捕获：持有者收到 `Cancel`。该次按下的其余操作不会因此关闭内容。
- **`Enter` 与 `Leave`** 不冒泡：每个进入的元素各自收到自己的事件。即使指针位于画布（`CanvasMouse`）上，带 `data-mouse` 的祖先仍会收到 `Enter`/`Leave`。
- **`Move`** 只发给在 `data-mouse-events` 中含有 `move` 的元素，并且总是发给捕获持有者。
- **`Click`**（以及 `DoubleClick`；右键为 `ContextMenu`）在按键松开时发生于按下时的同一元素（或其内部），且没有超过拖动阈值（第 5.2 节）。
- **捕获与提示。** 鼠标被捕获或正在拖动时，`:hover` 与 `title` 提示保持不变（依据 AUTHORING）。
- **`Under`。** 捕获或拖动时指针下的元素。在命中测试中，捕获持有者与被拖动元素的幽灵会被让开，因此不计入；`pointer-events: none` 会被跳过。非捕获、非拖动时 `Under = null`。`Target` 不变：它是捕获持有者或幽灵下方的元素（依据 AUTHORING；`UiMouseEvent.Under` 于 2026-11-15 添加，审计 C 通道）。
- **未实现：** 标记中的 `onmousedown="…"` 语法糖。

### 5.2. 拖动阈值与 `click`

- 阈值为 4 px（`PointerRouter.DragThreshold = 4f`，`PointerRouter.cs`，第 17 行）。
- 若在按下与松开之间指针移动超过阈值，带 `data-mouse` 的 `Click` 不会发生。被拖动的卡片不视为被点击（依据 AUTHORING）。
- `onclick`（旧属性）是**独立**事件，它不依赖 `data-mouse`，即使元素被拖动也会触发：只需在按下的同一位置松开即可（依据 AUTHORING，「鼠标在元素上」一节）。「拖动后不再有点击」的规则不适用于 `onclick`。
- 右键 `ContextMenu` 在位移不超过阈值时发生（`PopupRules.ContextCarrier`）。

### 5.3. 焦点、Tab 与块的键：`tabindex`、`data-keys`、`SetKeys`

**点击获得焦点。**

- 按下带显式 `tabindex`（任意数字，包括 `-1`）的元素（或其内部内容）会把键盘交给它。此时角色不受控制，与文本框相同。
- 输入框与代码编辑器自行通过点击获得焦点（第 3.5 节）。
- 按钮、复选框、单选框、滑块与列表**没有** `tabindex` 时，点击**不**获得焦点——这是有意设计：点击按钮不应使角色停止。它们只能通过 Tab 获得键盘。显式 `tabindex` 使其也能通过点击获得焦点。
- 带 `tabindex` 的块内的按钮，点击时把焦点交给最近的带 `tabindex` 的祖先；按键由该块处理（依据 AUTHORING）。

**Tab 顺序**（`Core/FocusOrder.cs`，「已核对代码」）：

1. 先是正 `tabindex` 的元素，按数值升序（相同时按文档顺序）。
2. 然后是 `tabindex="0"`、输入框、按钮等，按文档顺序。
3. `tabindex="-1"` 会被 Tab 跳过，但可通过点击和代码（`focus('id')`）获得焦点。
4. Shift+Tab 反向循环。停靠列表在每次按键时重新收集；不经布局更改 `tabindex` 会立即生效。
5. 弹出窗口（`data-popup`）打开期间，Tab 与 Shift+Tab 只在其内部的停靠点之间循环，不会离开到页面（第 5.9 节）。

**块的按键。** `data-keys` 列出按键，`data-key-name` 为处理器命名：

```html
<div tabindex="0" data-keys="Enter Escape ArrowUp ArrowDown" data-key-name="list">…</div>
```

```csharp
window.SetKeys("list", e => { /* e.Key, e.Ctrl, e.Shift, e.Alt, e.Target, e.Current */ return true; });
window.SetKeys("list", e => { … });   // Action<UiKeyEvent>：始终返回 true
window.ClearKeys("list");
```

（「已核对代码」：`Render/UiWindow.cs`，第 407–414 行；`Core/KeyRouter.cs`。）

`data-keys` 中的名称（`KeyRouter.Parse`，「已核对代码」；不区分大小写）：

| 名称 | 按键 | 名称 | 按键 |
|---|---|---|---|
| `Enter`（`return`） | Enter | `ArrowUp`（`Up`） | 上方向键 |
| `Space` | 空格 | `ArrowDown`（`Down`） | 下方向键 |
| `Escape`（`Esc`） | Esc | `ArrowLeft`（`Left`） | 左方向键 |
| `Delete`（`Del`） | Delete | `ArrowRight`（`Right`） | 右方向键 |
| `Backspace`（`back`） | Backspace | `Home`、`End`、`PageUp`、`PageDown`、`Tab` | |

事件 `UiKeyEvent`（「已核对代码」：`Core/KeyRouter.cs`）：`Key`（`UiKey`）、`Ctrl`、`Shift`、`Alt`、`Target`（获得焦点的元素）、`Current`（其 `data-keys` 触发的元素）。

规则：

- 按键从获得焦点的元素开始，沿请求该按键的祖先向上传递。`true` 停止冒泡，并且**取消默认行为**（复选框上的空格、Tab 切换焦点、Esc 关闭）。
- **文本框的编辑键不会进入 `data-keys`**（`KeyRouter.EditsText`，「已核对代码」）：Delete、Backspace、左、右、Home、End 总是给输入框；在 `textarea` 中还包括 Enter、上、下、PageUp、PageDown；在数字框中包括上与下（步进）。其余按键（Esc、Tab、空格、单行框中的 Enter）先进入 `data-keys`。
- **仅在有焦点时。** 窗口只有在其某个元素获得焦点时才会收到按键——这是游戏的设计：键盘由单一订阅者持有，角色在未选中任何内容时不应失去控制（依据 AUTHORING）。没有焦点时，按键块收不到事件。对于地图与列表，请为根元素设置 `tabindex="0"`（或 `-1`）。
- 没有按键释放（`keyup`）。

鼠标与键盘在同一块中：键盘焦点与鼠标互不影响；点击没有 `tabindex` 的元素不改变焦点（见上文）。

### 5.4. `<canvas>` 上的鼠标

`Window.SetCanvasMouse("pad", e => { … })`：画布的鼠标处理器。绘制仍使用 `SetCanvas`；也可以在不绘制的情况下接入鼠标（依据 AUTHORING；`Render/UiCanvasInput.cs`，`Core/CanvasMouse.cs`）。

| `e.Kind`（`CanvasMouseKind`） | 何时 |
|---|---|
| `Move` | 指针在画布上移动（第一次为进入）；拖动期间——屏幕上任何位置 |
| `Down` / `Up` | 按键在画布上按下 / 松开（`e.Button`：`Left`、`Right`、`Middle`；`e.Left`、`e.Right`、`e.Middle`） |
| `Wheel` | 画布上或拖动期间的滚轮；`e.Wheel` 为格数，向上为正 |
| `Leave` | 指针离开画布且无按键按下（用于取消高亮） |
| `Cancel` | 拖动被夺走：窗口关闭、画布消失或被对话框遮挡；不会再有 `Up` |

`CanvasMouseEvent` 的字段：`X`、`Y`——相对于画布**内容**左上角的像素（与 `SetCanvas` 中的 `Rectangle` 相同；拖动期间可能超出 `0..Width`、`0..Height`）；`Width`、`Height`；`Ctrl`、`Shift`、`Alt`；`Held`；`Captured`；`Inside`；`Time`。

- **捕获。** 在画布上按下的按键，若处理器返回 `true`，则捕获鼠标：`Move`、`Up`、`Wheel` 发给画布，直到所有此类按键都松开——无论在画布之内还是之外，甚至窗口之外。拖动期间，整个屏幕的鼠标都属于该窗口；角色不受控制。拖动期间按下的第二个按键会加入该拖动。
- **返回值**（`Func<CanvasMouseEvent, bool>`）：对于 `Down`，`true` 表示接受按下（开始拖动），`false` 表示画布不接受；对于 `Wheel`，`true` 表示滚轮已被使用，`false` 表示画布下方的页面滚动。`Action<CanvasMouseEvent>` 重载始终返回 `true`。
- `ClearCanvasMouse(id)` 移除处理器（拖动以 `Cancel` 结束）。
- **双击由所有者计算。** `ClickCounter.Count(e)` 根据同一按键的连续 `Down` 的时间（`Interval = 0.4` 秒）与位置（`Distance = 6` px），返回 1、2、3（然后再次为 1）（`CanvasMouse.cs`，第 180–181 行）。
- 画布只在其边界内接收鼠标；在边界之外，游戏照常接收鼠标。`cursor` 不改变。画布不接收键盘：地图的按键由所有者通过 `PlayerInput` 读取。
- 处理器中的错误会移除该处理器并在日志中写入一行；窗口不关闭（依据 AUTHORING）。

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

**用数字进行位移与缩放**（`SetTranslate`、`SetScale`）：适用于每帧都在移动的内容；见第 4.9 与第 8 节。

### 5.5. 模态窗口 `dialog`

- `<dialog id="filters" closedby="any">…</dialog>`：默认 `display: none`；打开时（`showModal('filters')` 或 `Window.ShowModal`）为 block，叠在页面之上（`z-index: 1000`）（「已核对代码」：UA、`Render/UiControls.cs`、`ShowModal`）。
- 没有 `top`/`left`/`right`/`bottom` 时居中。有它们时，相对于最近的 `position: relative` 祖先（依据 AUTHORING）。
- `closedby="any"`：点击窗口外部会关闭它（`UiControls.cs`，第 361–363 行；「已核对代码」）。
- 遮罩：点击对话框之外的区域不会传到游戏和其下方的页面（`UiControls.HitOverlay`，「已核对代码」）。`dialog` 打开时，输入发给它。
- `Esc` 关闭最上层的对话框（第 5.8 节）。
- `closeDialog()` 与 `closeDialog('id')`（内置）以及 `Window.CloseDialog(id?)` 关闭对话框；`onclose` 会触发（`UiControls.cs`，第 654 行）。
- 用代码打开 `dialog` 会关闭弹出窗口（依据 AUTHORING）。
- 在已打开的对话框内，`autofocus` 在打开时获得焦点。在已打开的 `data-popup` 菜单内，`autofocus` 不会抢走焦点（审计 D，第 3 条）。
- 在带 `scale` 的块内，对话框以无缩放、无 `translate`、无祖先滚动的方式打开（依据 AUTHORING；第 4.9 节）。

### 5.6. 代码编辑器：`<textarea>`

多行编辑器，由引擎绘制（依据 AUTHORING；`Render/UiCodeEdit.cs`，`Core/CodeEdit.cs`，`Core/CodeLexer.cs`）。

```html
<textarea id="code" data-lang="xml" data-model="text" oninput="edited()" onsave="save()" tabsize="4" placeholder="Пусто"></textarea>
```

文本来自 `value`：标签内容（与 HTML 相同，第一个换行不计入）、`value` 属性、`data-model`（双向；写入模型的频率不超过每帧一次），或 `Window.GetCodeEdit("code").SetText(…)`。

| 属性 | 作用 |
|---|---|
| `data-lang` | `xml`、`lua`、`cs`（或文件名，如 `x.xml`）——语法高亮；没有它则文本无颜色 |
| `data-model`、`value`、`placeholder`、`readonly`、`disabled` | 与 `input` 相同 |
| `tabsize` | 制表符宽度（字符数；默认 4；允许 1–16） |
| `data-tabs="tab"` | Tab 输入制表符，而不是空格 |
| `data-autoindent="off"` | Enter 不复制缩进 |
| `data-gutter="off"` | 不显示行号 |
| `oninput` | 编辑之后（每帧最多一次调用） |
| `onchange` | 离开编辑器时，若自进入以来文本已改变（`UiCodeEdit.cs`，第 199 行） |
| `onsave` | Ctrl+S（`UiCodeEdit.cs`，第 265 行） |

- **尺寸与外观。** 无 CSS 时：`30em × 12em`，有边框，`overflow: hidden`（滚动由它自己处理）。字体为**等宽**（默认 `font-family: mono`）：字符宽度只测量一次，列、光标与鼠标都以此计算。行高为 `line-height`。没有自动换行：长行横向滚动（Shift+滚轮，底部滚动条）。
- **颜色**为自定义属性，可继承：`--code-keyword`、`--code-type`、`--code-constant`、`--code-string`、`--code-number`、`--code-comment`、`--code-punct`、`--code-tag`、`--code-attr`、`--code-entity`、`--code-directive`、`--code-gutter`、`--code-line`（带光标的行）、`--code-selection`。普通文字使用 `color`，光标使用 `caret-color`。
- **按键。** 方向键；Ctrl+方向键按单词；Home——行首的第一个字符，再按一次到行首；End；Ctrl+Home/End；PageUp/PageDown；与 Shift 组合即为选择。Tab 移到下一个停靠点；有选择时对各行加缩进；Shift+Tab 取消缩进。Ctrl+Tab 跳到下一个字段。在缩进处按 Backspace 回退到上一个停靠点。Enter 复制缩进；在 `{ ( [` 之后增加一级；在 `{}` 之间时，把 `}` 放到单独一行。Ctrl+A、C、X、V。Ctrl+Z 撤销，Ctrl+Y 与 Ctrl+Shift+Z 重做（最多 1000 步；连续输入或删除单个字符算一步）。
- **鼠标。** 点击——放置光标；拖动——选择（越过边缘时滚动）；双击——选中单词；三击——选中整行；点击行号——选中该行。滚动条可拖动；点击轨道会跳转；滚轮——每次 3 行。
- **数千行。** 只绘制可见的行；文本按行存储；编辑只影响相应的行。高亮按需逐行计算，附带行末状态；编辑之后，正在查看的部分会被重新计算。测量（`tools/ui_test`，非游戏内）：20 000 行——加载 3 ms；文本中间 200 次按键——0.1 ms；编辑顶部之后下方行的高亮——一次 6–8 ms（依据 AUTHORING）。
- **行尾。** 模型记住文件的行尾（`CRLF` 或 `LF`，以多数为准）并写回；混合文本按多数统一（依据 AUTHORING）。
- **从代码访问。** `Window.GetCodeEdit("code")` 返回模型 `CodeEdit`（依据 AUTHORING）：`Text`、`Caret`、`Anchor`、`GoTo`、`Select`、`Replace(从, 到, 文本)`、`Undo`、`Redo`、`LineCount`、`Line(i)`。其编辑会在下一帧传到 `value`、模型与 `oninput`。`Window.RevealCaret(id)`——显示光标。
- **高亮**简单，逐行进行，不是完整解析：XML（标签、属性、值、实体、注释、CDATA、`<?…?>`）、Lua（关键字、长注释 `--[[ ]]` 与 `[==[ ]==]`、数字）、C#（关键字与内置类型、`@"…"`、`"""…"""`、`/* */`、`#region`；带 `$` 的字符串整体处理，其中的括号不解析）（依据 AUTHORING；`CodeLexer.cs`）。
- 可以单独调用 `CodeLexer.Scan(语言, 字符串, 状态, 列表)`（依据 AUTHORING）。

### 5.7. 文件：从资源管理器拖入与 Ctrl+V

游戏中没有文件选择对话框，因此有两种方式：**把文件拖到**游戏窗口上，或从剪贴板**粘贴**（依据 AUTHORING；`Render/UiFileInput.cs`，`Core/FileInput.cs`）。

```csharp
Window.SetFileInput("sprite", f => { Load(f.Path ?? SaveImage(f)); return true; }, accept: ".png,.jpg");
```

```html
<div id="sprite" tabindex="0">Перетащите файл сюда или нажмите здесь и Ctrl+V</div>
```

- 目标为带 `id` 的块。`accept`：扩展名（`.png,.jpg`）、`image/*`，或 `null`（任意文件）。
- 处理器接收 `UiFileInput`：`Source`（`Drop`、`PasteFile`、`PastePath`、`PasteImage`）、`Path`（粘贴的图片为 `null`）、`ImageData`（PNG 字节；若剪贴板中只有位图，则为 BMP）、`Extension`、`IsImage`、`Name`、`ReadBytes()`、`Target`、`Position`。
- 返回 `false` 会把文件交给下一个目标（祖先）；`true` 表示已接收。
- **拖放。** 游戏从 SDL 接收 `FileDropped` 事件（路径）。若在放下的瞬间，指针下是该窗口的目标块或其后代，则窗口接收该文件。拥有目标并覆盖该点的窗口，会从其下方的窗口接管文件。最上层的窗口为最新显示的窗口。
- **粘贴。** 目标通过点击与 Tab（`tabindex="0"`）获得焦点。Ctrl+V 按以下顺序读取剪贴板：在资源管理器中复制的文件（`CF_HDROP`）；图片（`PNG` 格式或位图 `CF_DIB`，例如截图）；带路径的文本（「复制为路径」得到的带引号路径，每行一个文件，或 `file:///…`）。取符合 `accept` 的内容。
- **不支持：** 拖动时对目标的高亮（文件在手中时 SDL 不发送事件）；SDL 提供的放下点（使用放下时鼠标的位置）；从窗口拖到资源管理器；同一目标一次接收多个文件（每个文件单独调用一次）。
- **Windows 限制。** 若游戏以管理员身份运行而资源管理器没有，系统不会把被拖动的文件交给它。剪贴板始终可用（依据 AUTHORING）。
- `Window.Images.Add(键, 字节)` 将粘贴的图片放入窗口内存（第 6 节）。

### 5.8. Esc

Esc 来自游戏的拦截（`BaroUi.PauseMenuPrefix`，`Api/BaroUi.cs`，第 52 与 186 行；「已核对代码」）。

- Esc 由屏幕上**最上层**的窗口接收：即窗口列表中最后一个仍打开、且在过去 0.5 秒内显示过的窗口。若窗口保持打开但未绘制，则 Esc 不会被它接收。
- 只有当屏幕上没有打开的自由窗口时，游戏菜单才会打开。
- 在窗口内，Esc 按以下链条处理（依据 AUTHORING；审计 D，第 7 条）：
  1. 取消鼠标捕获（拖动）；
  2. 弹出窗口（`data-popup`）；若其中展开了 `select`，Esc 关闭整个菜单；
  3. 获得焦点元素的 `data-keys`（若其中有 `Escape`）；
  4. 展开的 `select` 列表；
  5. 模态 `dialog`；
  6. 页面窗口（`onclose`，关闭）。
- 若某个处理器已关闭窗口，链条到此结束（通过 `IsOpen` 检查，审计 D，第 7 条）。
- 键盘处理器中的异常会写入日志，并像鼠标错误一样关闭窗口（`UiWindow.Fail`；审计 D，第 11 条）。`Fail` 之后游戏键盘分发器的异常会怎样（「未在游戏中验证」）。
- 视图自己的后退一步：`BaroUiView.OnEscape()`（默认 `false`）在第 1–5 项之后、窗口的 `Escape` 事件与关闭之前调用。`true` — 视图自己退了一步，窗口保留；`false` — 窗口关闭（`Api/BaroUiView.cs`、`Render/UiWindow.cs`、`OnEscape`；已核对代码）。这些钩子对任何视图开放；模组管理器可以使用它们，但其代码不在引擎中。

### 5.8a. 鼠标侧键 4 和 5，Alt+方向键

侧键 4（后退）、5（前进）以及 Alt+Left / Alt+Right 由 `BaroUi.NavigateByInput`（`Api/BaroUi.cs`）每帧处理，从 `AddWindowsPrefix` 调用（已核对代码；「未在游戏中验证」）。

- 侧键 4 / 5 交给指针下方最上层的窗口（`ElementAt`）。Alt+方向键交给屏幕上最上层的窗口，与 Esc 相同。
- 窗口先查找属性 `onback` / `onforward`：指针下的元素及其祖先，然后是焦点元素及其祖先（`Core/NavRoute.cs`、`NavRoute.Target`）。找到的处理器通过 `Template.Dispatch` 运行，与 `onclick` 相同。之后调用 `BaroUiView.OnBack()` / `OnForward()`（`UiWindow.Navigate`）。
- 若窗口返回 `true`，本帧的该侧键不再传给游戏：对 `PlayerInput.Mouse4ButtonClicked` / `Mouse5ButtonClicked` 的 Harmony 前缀在本帧结束前返回 `false`（`SideBackPrefix`、`SideForwardPrefix`）。指针在窗口之外时，侧键仍归游戏。
- 限制：侧键不会到达 `data-mouse`（`MouseButtons` 与 `UiMouseEvent` 没有 4、5 键）；聚焦的输入框不会拦截 Alt+方向键；若游戏在同一帧先于窗口读取了侧键，则不会被抑制（「未在游戏中验证」）。
- 引擎不保存「后退 / 前进」的历史（分区、层级）：这是窗口自己的工作。模组管理器可以从 `OnBack` / `OnForward` 返回 `true`，并自行决定「后退」的含义（其代码不在引擎中）。

### 5.9. 弹出窗口与右键菜单：`data-popup`、`data-contextmenu`、`ShowPopup`

```html
<div class="menu" data-popup id="cardMenu" style="width: 160px">
  <button onclick="rename()">Переименовать</button>
  <button data-item onclick="remove()">Удалить</button>
  <button data-keep-open onclick="pin()">Закрепить</button>
</div>
<div class="card" data-contextmenu="cardMenu">…</div>
```

- 带 `data-popup` 的块在被显示之前隐藏：引擎带 `!important` 的规则（`PopupPlace.Css`：`[data-popup]:not([open]) { display: none !important }`）使 `#cardMenu { display: flex }` 也无法将其打开。打开的弹出窗口以 `position: absolute` 位于页面之上（`z-index` 在 `PopupPlace.Css` 的同一行中设置）。请设置宽度：按内容计算的宽度以父元素为基准（依据 AUTHORING）。
- `Window.ShowPopup(id, screenX, screenY, anchor = null)`——在某点处打开（右键菜单）；`anchor` 为触发打开的元素（由 `Window.PopupAnchor` 返回）。
- `Window.ShowPopup(id, anchor, "below")`——在元素旁打开：`below`（下方）、`above`（上方）、`right`（右侧）、`left`（左侧），相对于 `ScreenRect(anchor)`。
- 放不下时：先翻转到另一侧，再向屏幕内平移（对于点，即向光标的左侧与上方）。计算位于 `Core/Popup.cs`、`PopupPlace`（「已核对代码」）。
- `Window.ClosePopup(id = null)`、`Window.IsPopupOpen(id)`、事件 `Window.PopupClosed(popup, reason)`，以及块上的 `onclose` 属性。
- 关闭原因（`PopupCloseReason`，「已核对代码」：`Core/Popup.cs`，第 7 行）：`Outside`（点击外部）、`Escape`、`Chosen`（选中了某一项）、`Code`（`ClosePopup`；删除了带 `data-if` 的窗口；打开了模态窗口）、`Replaced`（打开了另一个）。
- 同一时间只有一个弹出窗口：打开第二个会关闭第一个（`Replaced`）。重复打开已打开的窗口只会将其移动。没有嵌套子菜单。
- **点击外部。** 左键关闭弹出窗口，并且**不会**传到页面（既不会点击其下方的内容，也不会改变焦点）。右键关闭弹出窗口后会继续传递，因此在另一张卡片上会立即打开它的菜单。点击窗口自身正常工作。点击窗口中的字段与文字不会关闭它（依据 AUTHORING）。
- **项目**为 `button`、带 `onclick` 的 `a`、带 `data-item` 的元素，或窗口内任何带 `onclick` 的元素。其 `onclick` 执行之后窗口关闭（`Chosen`）。项目上或其祖先上的 `data-keep-open` 使窗口保持打开（「已核对代码」：`Core/Popup.cs`，第 130 行）。
- **菜单中的键盘。** 打开时，焦点落在第一个项目上（`button`、带 `onclick` 的 `a`、`data-item`；`data-item` 即使没有 `tabindex` 也能获得焦点）。若焦点在项目上，上、下、Home、End 在项目之间循环切换；但若焦点在菜单内的输入框、`select`、滑块、单选框或多行编辑器上，这些键仍属于它们自身（审计 D，第 4 条）。Enter 与空格键按下项目（与点击相同；窗口按上述规则关闭）。Tab 不会离开菜单（第 5.3 节）。
- 点击了某项、其 `onclick` 关闭了菜单又重新打开的项目，会使新菜单保持打开（审计 D，第 10 条；`PopupState.Serial`）。
- **`data-contextmenu="id"`** 位于元素或其祖先上：在其上按下并松开的右键（位移不超过阈值）会在光标处打开窗口 `id`。
  - 若 `SetMouse` 处理器对 `ContextMenu` 返回了 `true`，则菜单不会打开：所有者自己用 `ShowPopup(id, e.X, e.Y, card)` 打开其菜单。
  - 若右键按下被捕获持有者拿走（`Down` 上返回 `true`），或它落在带鼠标处理器的画布上（`PointerRouter.RightTaken`），则菜单不会打开（审计 D，第 1 条）。
- **层级。** 窗口绘制在页面之上，也在模态 `dialog` 之上（对话框内的菜单可见）。用代码打开 `dialog` 会关闭弹出窗口。
- 绘制从零偏移开始，不带缩放与祖先滚动：在带 `scale` 的块内，菜单以正常大小打开（依据 AUTHORING）。

### 5.10. 块之间的拖放：`data-drag`、`data-drop`、`SetDrop`

卡片用左键拿起，放到页面上的另一个块上。不克隆：移动的是元素本身（依据 AUTHORING；`Core/DragDrop.cs`）。

```html
<div class="card" data-drag="item" data-drag-value="{{c.id}}" data-drag-name="cards">…</div>
<div class="slot" data-drop="item note" data-drop-name="slots"></div>
```

```csharp
window.SetDrop("slots", e =>
{
    // e.Phase：Enter、Over、Leave、Drop、Cancel；e.Kind、e.Value（data-drag-value）、e.Source（卡片）、e.Target（带 data-drop 的块）、
    // e.X/e.Y（屏幕）、e.LocalX/e.LocalY（接收块内的点）、e.Ctrl/e.Shift/e.Alt
    if (e.Phase != UiDropPhase.Drop) { return true; }
    return Put(e.Target.GetAttribute("data-slot"), e.Value);   // true：已接受
});
window.SetDrop("cards", e => { if (e.Phase == UiDropPhase.Cancel) { Reset(e.Source); } return true; });  // 源知道被取消
```

（「已核对代码」：`Render/UiWindow.cs`，第 395–402 行；`Core/DragDrop.cs`，枚举 `UiDropPhase`：`Enter`、`Over`、`Leave`、`Drop`、`Cancel`。）

- 与 `SetMouse`、`SetKeys` 一样，每个名称只有一个处理器。接收方名称为 `data-drop-name`；源名称为 `data-drag-name`（只接收 `Cancel`）。
- `data-drop` 以空格列出类型；`*` 表示接受任何类型。指针下的接收方向上查找祖先；不接受该类型的块会被跳过（看外层）。
- **开始：** 在带 `data-drag` 的元素（或其内部）上按下，并移动超过 4 px。在 `input`、`textarea`、`select`、`slot` 以及被禁用的元素上按下不会开始拖动。若按下被 `data-mouse` 处理器（`true`）或滚动条接收，也不会拖动。
- **CSS 状态：** 源上为 `:dragging`；指针下的接收方，且它接受该类型时为 `:drag-over`。示例：`.slot:drag-over { border-color: #40c8be } .card:dragging { opacity: .7; z-index: 50 }`。
- **幽灵**——源本身，由数字 `translate`（`SetTranslate`）跟随指针移动。其 CSS `translate` 在手势期间被替换，之后恢复。带 `overflow: hidden` 的祖先会裁剪它：请在 `:dragging` 中提高 `z-index`。在带缩放的块内，指针的偏移会除以祖先的缩放（`DragDrop.GhostShift`）；卡片自身的 CSS `translate` 保持不变（审计 D，第 2 条）。
- **指针下的是什么：** 没有源的元素（源在检查期间会移出屏幕）。光标下始终是真实元素，而不是幽灵。`:hover` 冻结，`:drag-over` 高亮。
- **值**（`data-drag` 与 `data-drag-value`）在手势开始时记录：所有事件，包括 `Cancel`，都携带它们（审计 D，第 8 条）。
- **结束：** 在接受该类型的接收方上松开，发送 `Drop`；`true` 表示接受。其他任何结果（松开在外部、`false`、Esc、源消失、对话框关闭了窗口）都会发送 `Leave`（若曾在接收方上方）以及对源的 `Cancel`。没有处理器的接收方会拒绝。
- `Esc` 仅取消拖动（依据 AUTHORING）。
- 拖动之后，元素的 `onclick` 不会调用，即使按键是在 Esc 之后松开的（依据 AUTHORING）。
- 处理器中的异常会移除它，并视为拒绝。对 `Enter`、`Over`、`Leave`、`Cancel` 的返回值被忽略（依据 AUTHORING）。
- 在手势中途消失或被对话框关闭的接收方会收到 `Leave`，并失去 `:drag-over`。若处理器在松开的那一帧关闭了窗口或取消了手势，则不会发出 `Drop`。
- **不要与游戏的 `<slot>` 格子混用：** 那种手势由游戏处理。将游戏物品拖到 HTML 块上尚未实现（依据 AUTHORING）。

### 5.11. 角色控制锁定

（依据 AUTHORING 与审计 D 的「不含新功能的窗口」一节；「未在游戏中验证」。）

- 角色控制会被禁用：拖动期间（整个屏幕的鼠标属于该窗口），以及输入框获得焦点时（键盘属于该窗口）。
- 点击没有 `tabindex` 的按钮、复选框、单选框、滑块不会获得焦点，也不会使角色停止。
- 没有焦点且未拖动的窗口不会阻止角色。

---

## 6. 图表与绘制：UiDraw、`<curve>`、`<chart>`、`<gauge>`

几何与规则位于 `Core/`（测试 `DrawKitTests`、`ChartTests`、`tools/ui_test`）；绘制位于 `Render/UiDraw.cs`、`Render/UiCharts.cs`（「已核对代码」）。示例页面：`own/BBE_UI/UI/charts.html`，数据 `charts.data.json`；截图：`tools/ui_host --page own/BBE_UI/UI/charts.html --data own/BBE_UI/UI/charts.data.json --frames 90`（依据 AUTHORING）。

图表的通用说明：`curve`、`chart` 与 `gauge` 是 UA 规则 `curve, chart, gauge { display: block }` 下的块（2026-11-16 添加，`Core/StyleEngine.cs`，`UserAgentCss`；此前它们是 `inline`）。尺寸由 CSS 设置（`height`、`width`）。`UI/charts.html` 自己设置了 `display: block`，与新规则一致（「已核对代码」；「未在游戏中验证」）。

### 6.1. 绘制集：`SetCanvas` 中的 `Window.Drawing`

`Window.Drawing`（类型 `UiDraw`，`Render/UiDraw.cs`）是 `<curve>`、`<chart>`、`<gauge>` 自身使用的绘制工具。纹理（线条、圆、角）计入窗口预算（第 10 节），并通过「墓地」释放。

| 方法 | 绘制内容 |
|---|---|
| `Stroke(sb, path, width, color, corner, dashOn, dashOff, continuePhase)` | 折线；连接处为圆角；直角以半径 `corner`（px）的四分之一圆环倒角；`dashOn`/`dashOff` 以 px 计——带滚动相位的虚线；`continuePhase` 延续上一条线的相位 |
| `Dots(sb, path, radius, color)` | 路径顶点处的点 |
| `Disc(sb, center, r, color)` | 圆（半径为整数像素） |
| `Ring(sb, center, outer, inner, color)` | 圆环 |
| `Arc(sb, path, cx, cy, r, a0, a1, width, color)` | 厚度为 `width` 的圆弧 |
| `Sector(sb, cx, cy, r, a0, a1, color)` | 扇形 |
| `Area(sb, curve, baseY, color)` | 曲线下方直至 `baseY` 的实心区域 |
| `Fill(sb, outline, color)` | 任意多边形 |
| `Text(sb, fonts, style, text, x, baseline, color, align)` | 使用引擎字体的文字；`align`——`'l'`、`'c'`、`'r'`；`fonts`——`Window.Fonts`（公开属性 `UiWindow.Fonts`，自 2026-11-16 起，此前为 `internal`），`style`——`Window.Find("id").Style`（元素的 `ComputedStyle`） |
| `Line(sb, a, b, width, color, cap)`、`Segment(…)`、`Bent(…)` | 线段与带角折线 |
| `Outline(sb, rect, t, color, dashOn, dashOff)`、`FillRound(sb, rect, r, color)`、`RingRound(sb, rect, r, t, color, dashOn, dashOff)` | 圆角矩形、边框、虚线 |

`Arc` 与 `Sector` 中的角度以弧度计：0 为向上，顺时针。

说明（「已核对代码」与依据 AUTHORING）：

- `UiDraw` 没有渐变（依据 AUTHORING）。
- `UiPath`——路径：填充 `path.Raw`（以像素计的 `DPt` 列表），并复用该列表。只有当点、宽度或半径改变时才重新计算几何；稳定的无变化帧不分配内存（依据 AUTHORING）。
- `UiDraw` 获得窗口的纹理预算（`TextureLedger`）；`BeginFrame` 由窗口调用（调用频率未核对代码）。
- `DashPhase`——虚线相位（属性 `UiDraw.DashPhase`）。

```csharp
var line = new UiPath();
Window.SetCanvas("pad", (sb, r) =>
{
    line.Raw.Clear();
    line.Raw.Add(new DPt(0, r.Height)); line.Raw.Add(new DPt(r.Width / 2, 0)); line.Raw.Add(new DPt(r.Width, r.Height));
    Window.Drawing.Stroke(sb, line, 2, new Color(63, 182, 168), corner: 8);
});
```

画布图片——`Window.Images`（第 8.4 节；`Draw`、`DrawRegion`、`Get`、`Add`、`Peek`、`Release`、`Clear`；格式 PNG、JPG、BMP、GIF、TGA，通过游戏的加载器——依据 AUTHORING）。`ImageFit`——`Contain`、`Cover`、`Stretch`、`Actual`、`ScaleDown`（「已核对代码」：`Core/ImageLayout.cs`，第 8 行；`ScaleDown` 等同于 `object-fit: scale-down`，自 2026-11-16 起在 AUTHORING 中有描述）。

### 6.2. `<curve>`：可编辑曲线

```html
<curve data-model="settings.damageByDepth" x-min="0" x-max="5000" y-min="0" y-max="3" snap="250 0.1" mode="smooth" monotone max-points="8"></curve>
```

（依据 AUTHORING；`Render/UiCharts.cs`，`Core/CurveEditor.cs`，`Shared/PointCurve.cs`。）

- 值为文本 `x:y, x:y`（与 `BBE.Curves.PointCurve` 相同；游戏用相同的解析读取它）。没有 `data-model` 时使用 `value` 属性。
- 值可以是点的列表（自 2026-11-16 起，`Core/ChartModel.cs` 中的 `CurveSource`，`Read`/`Write`；「已核对代码」）：列表或数组，元素为 `DPt`、`(x, y)` 对、由两个数字组成的数组或列表、带 `x`/`y`（或 `X`/`Y`）键的字典，或带数字属性 `X`/`Y` 的对象。写回采用相同的形式：列表原地编辑（增长与缩短），等长数组原地编辑，不等长的数组则生成相同类型的新数组。其他形状的模型不会被读取：曲线为空（日志中写入一行，`UiCharts.cs`，第 360 行）。形状测试位于 `tools/ui_test/ChartLaneTests.cs`（分组「chart lane」：列表、数组、对、字典、带 `X`/`Y` 的对象、错误元素、NaN）；（「未在游戏中验证」）。
- `mode`：`linear`（默认）、`smooth`（单调，无过冲）、`step`（值保持到下一个点）。模式不保存在文本中。
- 范围属性：`x-min`、`x-max`、`y-min`、`y-max`；`snap="sx sy"`——网格步长；`monotone`——`y` 不减少；`max-points`（默认 16），至少两个点。
- **鼠标：** 拖动点；在空白处双击——新增一个点；右键点击某个点——删除它。
- **键盘**（Tab 落在曲线上）：方向键将所选点移动一个网格步长（没有 `snap` 时为范围的百分之一）；Delete 或 Backspace 删除该点。
- 规则：第一个与最后一个点保持其 `x`（只沿 `y` 移动）；点不越过相邻点；`y` 在 `[y-min, y-max]` 内。
- 事件：`oninput`——每次移动时；`onchange`——松开时、添加、删除以及用方向键步进时。
- 外观（CSS）：`--series-1` 线条颜色，`--stroke-width` 线宽，`--point-size` 点的直径，`--point-color` 点的颜色，`--series-2` 所选点的圆环，`--grid-color` 网格，`background-color`、`border`、`height`。
- 未核对：曲线的按键在游戏中（`ui_host` 不会把按键传给焦点；游戏的自检会检查它们——「未在游戏中验证」）。

### 6.3. `<chart>`：基于模型数据的图表

```html
<chart data-series="доход: stats.income; расход: stats.loss" type="area" format="0" y-min="0"></chart>
```

（依据 AUTHORING；`Render/UiCharts.cs`，`Core/ChartScale.cs`。）

- `data-series`——以 `;` 分隔的系列 `名称: 路径`。路径的读取方式与 `data-model` 相同（数字列表）。不是数字的项会被跳过。
- `type`：`line`（默认）、`area`、`bar`（带圆头的柱）、`step`（「已核对代码」：`ChartScale.cs`，第 120 行）。
- 坐标轴：「整齐」的刻度（1、2、5 × 10ⁿ）；坐标轴扩展到整数刻度。`y-min`、`y-max` 显式设置边界。`format`——标签的 `.NET` 格式（`0`、`0.0`、`0 %`）。
- X 轴（在底部，仅在设置时显示；自 2026-11-16 起，`UiCharts.cs`，第 470–475 行；「已核对代码」）：`x-labels="model.path"`——类别标签，字符串列表（拥挤时每 n 个显示一个）；或 `x-min`/`x-max`——从 `x-min` 到 `x-max` 的整齐数值刻度，类别在其间均匀分布（默认 `x-min` = 0，`x-max` = 点数 − 1）。若同时设置了 `x-labels` 与 `x-min`/`x-max`，则以类别标签为准。
- 悬停：竖线以及显示所有系列数值的提示框。
- 点击某个点（柱）会调用 `onpick`；点的索引写入属性 `data-picked`，并以 `$arg` 传给处理器（`onpick="pick($arg)"`；「已核对代码」：`UiCharts.cs`，第 669–670 行；自 2026-11-16 起）。
- 数据变化是平滑的：系列在几分之一秒内追上新值。
- 外观（CSS）：`--series-1`…`--series-6` 系列颜色，`--stroke-width`，`--grid-color`；标签使用元素的 `color` 与字体。
- 未实现：渐变填充、对数坐标轴、滚轮缩放、`chart` 与 `gauge` 的键盘焦点（依据 AUTHORING）。

### 6.4. `<gauge>`：仪表

```html
<gauge data-model="sub.depth" min="0" max="700" zones="500" ticks="8" format="0"></gauge>
<gauge data-model="reactor.load" min="0" max="100" zones="60, 85" fill="sector"></gauge>
```

（依据 AUTHORING；`Render/UiCharts.cs`，`Core/GaugeMath.cs`。）

- 值为 `data-model` 中的数字（没有模型时使用 `value` 属性），被限制在 `[min, max]` 之内。指针平滑地追上该值。
- `zones`——以逗号分隔的递增阈值：低于第一个阈值为区域 0，达到阈值则进入下一个区域。指针颜色与弧形区域的颜色取决于数值所在的区域。
- `fill="sector"`——填充到数值的扇形，而不只是指针。
- `ticks`——刻度数量（默认 5）；`format`——刻度标签与数值的格式。
- 弧形从 −135° 到 +135°（从顶点顺时针）；元素的高度会分配给弧形与数值标签。
- 外观（CSS）：`--zone-0`…`--zone-3` 区域颜色，`--needle` 指针，`--track-color` 与 `--track-width` 弧形，`--tick-color` 刻度。

### 6.5. 已验证与未验证

依据 AUTHORING 与 `docs/HANDOFF.md`：

- 已验证：构建（`cscheck`）；`tools/ui_test`（规则、几何、刻度选取）；`tools/ui_host`（页面可以绘制；拖动点与点击获得焦点——是）。
- 未验证：曲线的按键；游戏本身。
- 分配（自 2026-11-16 起，通道 H）：数据未变化的帧不分配内存——值读入缓冲区，标签与提示仅在刻度、悬停索引或数据改变时重建；热路径上的字节检查——`tools/ui_test/ChartLaneTests.cs`（「已核对代码」与测试；未在游戏中测量）。
- 仍会分配内存的情况（在绘制热路径之外；`Render/UiCharts.cs`，「已核对代码」）：`<curve>` 在 `smooth` 模式下每次点改变时的数组与 `PointCurve`（第 210–211 行：拖动的每一步）；更换模型时的新 `CurveEditor`（第 136 行）；点数改变时系列的数组（第 502 行）；刻度或悬停索引改变时的标签字符串与提示；点击时的 `data-picked` 字符串（第 669 行）；`UiPath`——每个系列一次，以及 `data-series` 改变时（第 431 行）。

---

## 7. 字体与图标

### 7.1. 字体族（`font-family`）

「已核对代码」：`Core/FontFamilies.cs`；依据 AUTHORING。列表中第一个已知名称生效；引号与大小写无关。未知名称使用 JetBrains Mono。

| 字体族（内部名称） | CSS 中的名称 | 文件 | 字重 | 说明 |
|---|---|---|---|---|
| JetBrains Mono（默认） | `mono`（默认）、`monospace`、`jetbrains mono`、`ui-monospace`、`consolas`、`courier new`、`courier`、`menlo`、`monaco` | `Fonts/JetBrainsMono-Regular.ttf`、`-Bold.ttf` | 400、700 | 等宽；字符宽度恒定 |
| Caveat | `caveat`、`cursive`、`handwriting`、`hand` | `Fonts/Caveat-Regular.ttf`、`-Bold.ttf` | 400、700 | 手写体；斜体为合成 |
| PT Serif | `pt serif`、`serif`、`ui-serif`、`georgia`、`times new roman`、`times`、`newspaper` | `Fonts/PTSerif-Regular.ttf`、`-Bold.ttf`、`-Italic.ttf` | 400、700，斜体 400 | 真正的斜体仅限常规字重 |
| Noto Sans | `noto sans`、`sans-serif`、`ui-sans-serif`、`system-ui`、`sans`、`proportional`、`arial`、`helvetica`、`segoe ui` | **来自游戏文件夹**：`Content/Fonts/NotoSans/NotoSans-Regular.ttf`、`-Medium`、`-SemiBold`、`-Bold` | 400、500、600、700 | 比例字体，含西里尔字母；只读；没有斜体（合成） |
| Lucide | `lucide`、`icons`、`icon` | `Fonts/Lucide.ttf` | 400 | 图标，第 7.2 节 |

说明：

- Noto Sans 没有复制到包中：若游戏文件夹中没有该文件，文字以等宽字体绘制。日志中每个字体族写入一行（依据 AUTHORING；审计 C，第 14 条，最多发现 4 行并已修正）。
- 500 与 600 字重只有 Noto Sans 具备。其他字体族取最接近的：500 → 400，600 → 700（依据 `FontFace` 与 AUTHORING）。
- 行高按所选字体的度量计算。Lucide 的行下延伸为 0（依据其 hhea）（「已核对代码」：`FontFamilies.cs`，`Icons`；测试 `FontFamilyTests`）。
- 字符宽度：FreeType 会对宽度取整，因此游戏中的文字行比 `ui_preview` 中略宽（依据 AUTHORING）。
- 每种字体大小都是一个单独的游戏字体（每对「文件、大小」约 8 MiB，`FontCache`）。不要每帧修改 `font-size`（第 10 节）。
- 没有自身斜体的字体，其斜体通过矩阵拉伸——显得模糊（第 4.9 节）。

### 7.2. Lucide 图标：`data-icon`、`icon()`

（依据 AUTHORING；`Core/Icons.cs`，`Fonts/lucide-codepoints.json`（2379 个名称，已计数核实），UA：`[data-icon] { font-family: lucide; font-weight: normal; font-style: normal; letter-spacing: normal; text-decoration: none }`。）

- `<i data-icon="trash-2"></i>`——引擎在元素中放入该名称对应的图标字符；`font-family: lucide` 由 UA 规则设定。
- 名称与 lucide.dev 相同：`check`、`x`、`plus`、`trash-2`、`settings`、`search`、`star`、`alert-triangle`、`chevron-down` 等。完整列表见 `Fonts/lucide-codepoints.json`。
- 图标是文字：大小由 `font-size` 决定，颜色由 `color` 决定；`text-shadow` 与 `scale` 同样适用。图标位于基线上，高度为 1em。
- 名称可以绑定：`data-icon="{{n.icon}}"`。
- 未知名称——没有图标，日志中写入一行。
- 手动：`font-family: lucide` 加上码表中的字符代码。
- 在 `::before`/`::after` 中：`content: icon(名称)`；若规则未设置 `font-family`，字体为 `lucide`（第 4.5 节）。
- 字体为 ISC 许可证，见 `Fonts/LICENSE-Lucide.txt`。游戏中未验证（依据 AUTHORING；（「未在游戏中验证」））。

### 7.3. 游戏精灵：`sprite(类别:id)`、`<item sprite>`

（「已核对代码」：`Render/UiSprites.cs`。）

| 类别 | 显示内容 | 查找 |
|---|---|---|
| `talent:` | 天赋图标 | `TalentPrefab` |
| `faction:` | 派系图标 | `FactionPrefab` |
| `mission:` | 任务图标 | `MissionPrefab` |
| `job:` | 职业图标 | `JobPrefab` |
| `item:` | 物品（物品栏图标或精灵） | `ItemPrefab` |
| `affliction:` | 病症图标 | `AfflictionPrefab` |
| `ui:` | 游戏界面图标 | 命名精灵样式（`GUIStyle.Sprites`），否则为组件样式的第一张图片（`GUIStyle.GetComponentStyle`） |

示例：`ui:speechbubbleicon`、`ui:outposticon`、`ui:wreckicon`、`ui:enemyicon`、`ui:guibuttonsettings`（依据 AUTHORING）。没有这样的精灵——日志中写入一行，没有图标。切片精灵会给出整张图片，而不是九个部分（`UiSprites.cs`，注释）。

用途：`<item sprite="item:oxygentank">`、`background-image: sprite(ui:名称)`、`<img src="sprite(item:oxygentank)">`、`border-image-source: sprite(ui:名称)`（后者整体按切片裁剪；游戏的九个部分不会被取用，依据 AUTHORING）。

### 7.4. JetBrains Mono 中的 Unicode 符号

依据 AUTHORING（「已核对代码」：字体字符表 `cmap`；本次核对未重新检查）：

- 有：`▲ ▼ ► ◄ ▶ ◀ ✓ ✗ ✕ × ⚠ ● ○ ■ □ ◆ ◇ ← → ↑ ↓ ≡ … • –`。
- 没有（显示为空方块或被跳过）：`★ ☆ ♥ ✔ ✘ ⚙ ☰ ⌕ ✖`。
- 大小、颜色、`text-shadow` 与 `scale` 对符号的作用与对文字相同。对于其他字体族（如 `font-family: serif` 等），符号集合更小；对缺失符号的后备字体未核对（依据 AUTHORING：缺失时画什么未知）。

### 7.5. 图片图标

位图图集：`background-image: url("icons.png")` 配合 `background-position` 与 `background-size`，与精灵图集相同：块的大小与图标相同，`background-position: -32px -64px`，`background-repeat: no-repeat`。缩放时会变软（依据 AUTHORING）。

其他图标字体（Material Symbols、Phosphor）未接入。Lucide 已能满足相同需求。

### 7.6. 俄语粘连

俄语文本的不间断词对（`-bbe-ru-glue`）见第 4.6.4a 节。

---

## 8. C# API：公开类型与成员

命名空间：`BaroUI.Api`（`BaroUi`、`BaroUiView`）、`BaroUI.Core`（解析、样式、输入、几何）、`BaroUI.Render`（`UiWindow`、绘制、控件）。除非另有说明，列出的所有成员都已通过代码核对（`own/BBE_UI/CSharp/Client`）。

稳定的公开 API（每个成员一行）、引擎版本和破坏性变更规则见：[`API.md`](API.md)（俄文）。模组中检查版本：`BaroUi.Requires("0.2.0", 名称)`。

### 8.1. `BaroUi`（静态，`Api/BaroUi.cs`）

| 成员 | 作用 |
|---|---|
| `Open(string htmlPath, BaroUiView view = null, string name = null)` → `UiWindow` | 打开自由窗口；会更新并接收输入 |
| `Open<T>(string htmlPath, string name = null)` → `T` | 同上，使用新的 `T : BaroUiView` 实例 |
| `Create(string htmlPath, BaroUiView view = null, string name = null)` → `UiWindow` | 物品窗口：所有者自己将其加入更新列表并关闭 |
| `OpenPage(string page)` → `UiWindow` | 打开当前包（或 `baroui src` 指定文件夹）中的 `UI/<page>.html` |
| `Windows` → `IReadOnlyList<UiWindow>` | 已打开的自由窗口 |
| `Version` → `System.Version` | 引擎版本（0.2.0），唯一常量；`filelist.xml` 中是同一个数字 |
| `Requires(string minVersion, string modName)` → `bool` | 启动时的版本检查：引擎较旧时在日志中写一行并返回 `false` |
| `Reload(UiWindow w)` → `string` | 原地重新读取 HTML 与 CSS |
| `event Action<UiWindow, UiWindow> Reloaded` | 窗口因重新加载而被替换（参数顺序——「未核对代码」） |
| `Watch`（bool） | 保存时重新加载，并显示错误横幅 |
| `Animations`（bool） | 开关所有窗口动画 |
| `LayoutFps`（float，默认 30） | 长度采样的全局频率（`layout-fps: auto`）；`0`——每帧（`baroui anim fps N`，`Api/BaroUi.cs`，第 23 行；「已核对代码」） |
| `PackageDir`、`PageDir` | 包的文件夹与页面文件夹 |
| `Fonts` → `UiFonts` | 窗口字体 |

内部成员（不用于页面）：`Init`、`Dispose`、`Forget`、`AfterDraw`、`PauseMenuPrefix`、`AddWindowsPrefix`、`NavigateByInput`、`SideBackPrefix`、`SideForwardPrefix`、`SourceOverride`（`internal`）。

页面模型来自文件：`BaroUi.OpenPage(page)`（`baroui open <page>`）在窗口的视图没有给出模型时，从页面旁的 `UI/<page>.data.json` 取模型（已核对代码，`Api/BaroUi.cs`；读取器是 `Core/JsonModel.cs`）。`inputs` 的模型由自测 `InputsView` 提供，因此游戏中不使用该文件；`ui_host --data` 会读取它。`pseudo_demo.html` 不读取数据，没有对应文件。

### 8.2. `BaroUiView`（抽象，`Api/BaroUiView.cs`）

窗口逻辑的基类。`[UiHandler]` 方法是 `on*` 属性的处理器。

| 成员 | 作用 |
|---|---|
| `Window`（`UiWindow`）、`Document` | 窗口及其文档 |
| `protected virtual OnOpen()`、`OnUpdate(float dt)`、`OnAfterInput()`、`OnClose()` | 生命周期（第 2.3 节） |
| `Data`（object） | 页面数据 |
| `Set(string path, object value)` → bool | 写入一个字段并更新 |
| `Refresh()` | 重新读取数据（对象改变之后） |
| `Find(string selector)`、`FindAll(string selector)` | 在树中按 CSS 选择器查找（与样式使用相同的解析） |
| `Text(Element e)`、`Text(Element e, string text)` | 读取元素文字 / 设置文字 |
| `ToggleClass(e, cls, on)`、`HasClass(e, cls)` | 类 |
| `Style(e, property, value)` | 设置元素的一个样式属性（「已核对代码」：`Api/BaroUiView.cs`，第 69 行） |
| `Attr(e, name, value)` | 设置元素属性 |
| `Append(parent, html)` → `List<Element>` | 向元素添加标记 |
| `Remove(e)` | 删除元素 |
| `Send(source, Action send, float timeoutSeconds = 5)` | 发送给服务器：锁定 `source`，直到 `Done` 或超时之前带 `pending` 类 |
| `Done(source)` | 已收到回复，解除锁定 |
| `Close()` | 关闭窗口 |

### 8.3. `UiWindow`（`Render/UiWindow.cs`）

由 `BaroUi.Open`/`Create` 创建；`FromFile(htmlPath, fonts, name)` 为静态方法。

**状态与数据**

| 成员 | 作用 |
|---|---|
| `Name`、`Document`、`Root`（`LayoutBox`）、`IsOpen` | 名称、文档、布局根、是否打开 |
| `Template`、`Handlers`、`View` | 模板、处理器（`[UiHandler]`）、窗口类 |
| `Data` | 数据（读/写） |
| `Find(string id)` → `Element` | 按 `id` 查找元素（只能用 id；CSS 选择器见 `BaroUiView.Find`） |
| `Set(string path, object value)` → bool | 写入字段 |
| `Refresh()` | 数据已改变：重新绑定 |
| `SetText(e, text)` | 设置文字 |
| `ToggleClass(e, cls, on)` | 类 |
| `Invalidate()`、`Relayout()`、`Relayout(e)`、`RestyleElement(e)` | 强制重新计算样式或布局 |
| `Problems`（`IReadOnlyList<Diagnostic>`）、`SourcePath` | 页面的提示；文件路径 |
| `UnitPx` | 每个 1u 的像素数 |
| `IsTyping`、`SkipTyping()`、`IsAnimating` | 打字机效果，是否正在动画 |
| `Close()` | 关闭窗口 |
| `AddToGUIUpdateList(int order = 0)` | 物品窗口：由所有者的 `AddToGUIUpdateList` 调用 |
| `CaptureNextDraw(string file, string id = null)` | 下一次绘制后的块快照 |
| `ElementAt(Point p)` → `Element` | 屏幕点处的元素 |
| `BoxOf(e)` → `LayoutBox` | 布局矩形（不含 `translate`、动画位移、滚动与 `scale`） |
| `ScreenRect(e)` → `Rectangle` | 考虑所有因素后的屏幕位置（第 4.9 节） |
| `GetScroll(e)`、`GetScrollAbove(e)`、`ScrollBy(e, dy)` | 块的滚动 |
| `Click(e)` | 程序化点击（`onclick` 与 `Clicked`） |

**坐标（第 4.9 节）**

| 成员 | 作用 |
|---|---|
| `SetTranslate(e, x, y)`、`ClearTranslate(e)` | 以数字位移；替换元素的 CSS `translate`；无需重新计算样式 |
| `SetScale(e, s)`、`ClearScale(e)` | 以数字缩放（`0`、负数、`NaN`——视为 1） |

**绘制与画布**

| 成员 | 作用 |
|---|---|
| `SetCanvas(string id, Action<SpriteBatch, Rectangle> draw)` | `<canvas>` 的绘制回调 |
| `Drawing`（`UiDraw`） | 绘制集（第 6.1 节） |
| `Images`（`UiImages`） | 画布的文件图片（第 8.4 节） |
| `GetCodeEdit(string id)` → `CodeEdit` | 代码编辑器模型 |
| `RevealCaret(string id)` | 显示光标 |
| `SetInventory(string name, Inventory inventory)` | 将游戏物品栏绑定到 `<slot inventory="name">`；`null` 将格子交还游戏 |

**输入（第 5 节）**

| 成员 | 作用 |
|---|---|
| `SetMouse(string name, Func<UiMouseEvent,bool>)` / `(…, Action<UiMouseEvent>)`、`ClearMouse(name)` | 按名称的 `data-mouse` 处理器 |
| `SetCanvasMouse(string id, Func<CanvasMouseEvent,bool>)` / `(…, Action<…>)`、`ClearCanvasMouse(id)` | 画布鼠标 |
| `SetDrop(string name, Func<UiDropEvent,bool>)` / `(…, Action<…>)`、`ClearDrop(name)` | 拖放的接收方与源 |
| `SetKeys(string name, Func<UiKeyEvent,bool>)` / `(…, Action<…>)`、`ClearKeys(name)` | `data-keys` 按键 |
| `SetFileInput(string id, Func<UiFileInput,bool>, string accept = null)` / `(…, Action<…>, …)`、`ClearFileInput(id)` | 文件：拖放与 Ctrl+V |
| `FocusId(string id)` | 将焦点移到元素（内置 `focus('id')`） |
| `ShowModal(string id)`、`CloseDialog(string id = null)` | 模态 `dialog` |
| `ShowPopup(string id, float screenX, float screenY, Element anchor = null)` | 在某点处打开弹出窗口 |
| `ShowPopup(string id, Element anchor, string placement = "below")` | 在元素旁：`below`、`above`、`right`、`left` |
| `ClosePopup(string id = null)`、`IsPopupOpen(string id)`、`PopupAnchor` | 关闭、检查、菜单的所有者元素 |
| `event Action<Element, PopupCloseReason> PopupClosed` | 关闭原因（第 5.9 节） |
| `event Action<UiWindow> Escape` | Esc 已到达窗口（在视图的 `OnEscape` 之后；没有处理器则窗口关闭） |
| `protected virtual bool OnEscape()`、`OnBack()`、`OnForward()`（`BaroUiView`） | Esc、侧键 4 和 5 以及 Alt+方向键的后退一步；`true` — 已处理（第 5.8、5.8a 节） |
| `event Action<UiWindow, Element> Clicked` | 对元素的点击 |

**测量（`PERF.md`）**

| 成员 | 作用 |
|---|---|
| `LayoutCount`、`LastFullLayout` | 布局计算次数；上一次是否为完整布局 |
| `LastStyleMs`、`LastLayoutMs`、`LastUpdateMs` | 上一帧各阶段的时间（毫秒） |
| `ProbeHost(double ms)`、`ProbeGame(double ms)` | 宿主与游戏的时间统计 |

**预算常量**：`WindowBudgetBytes = 256 MiB`，`WindowBudgetCount = 4096`（「已核对代码」：`UiWindow.cs`，第 90–91 行）。

### 8.4. `Window.Images`（`UiImages`，`Render/UiImages.cs`）

| 成员 | 作用 |
|---|---|
| `Get(string path)` → `Texture2D` | 文件中的图片；每个文件只加载一次；文件不存在或已损坏——返回 `null` 并在日志中写入一行。文件每秒检查一次（`RecheckEvery = 1.0`），重新保存后会重新读取 |
| `Add(string key, byte[] encoded)` → `Texture2D` | 非文件图片（粘贴的，第 5.7 节） |
| `Peek(keyOrPath)`、`Release(keyOrPath)`、`Clear()` | 不加载而获取、释放、清空 |
| `Count`、`Bytes` | 图片数量与占用空间 |
| `Draw(sb, path 或 Texture2D, Rectangle box, ImageFit fit = Contain, Color? tint = null, bool pixelPerfect = false)` | 绘制图片 |
| `DrawRegion(sb, tex, Rectangle source, Rectangle box, …)` | 图片的一部分（图集的一格） |

`ImageFit`：`Contain`（全部，居中）、`Cover`（填满，裁掉边缘）、`Stretch`、`Actual`（1:1，居中）、`ScaleDown`。

`ImageLayout`（静态，`Core/ImageLayout.cs`）：`Place(...)`——计算位置而不绘制；`Cell(picW, picH, cols, rows, index, …)` → `(X, Y, W, H)`，图集的格子；`ImagePlacement.PixelAt(px, py, out picX, out picY)`——画布点下是图片的哪个像素。

`pixelPerfect`：整数缩放且不平滑（像素艺术）。绘制批次在调用期间重新打开：请绘制几张大图，而不是数百张小图（依据 AUTHORING）。

内存：窗口的所有纹理共用一个预算（第 10 节）。

### 8.5. 事件类型与枚举

| 类型 | 位置 | 内容 |
|---|---|---|
| `UiMouseEvent`、`UiMouseKind` | `Core/PointerEvents.cs` | 第 5.1 节：`Down`、`Up`、`Move`、`Enter`、`Leave`、`Wheel`、`Click`、`DoubleClick`、`ContextMenu`、`Hover`、`Cancel` |
| `CanvasMouseEvent`、`CanvasMouseKind` | `Core/CanvasMouse.cs` | 第 5.4 节：`Move`、`Down`、`Up`、`Wheel`、`Leave`、`Cancel` |
| `ClickCounter` | `Core/CanvasMouse.cs` | `Count(e)`——1、2、3；`Interval = 0.4`，`Distance = 6`；`Reset()` |
| `MouseButtons`、`KeyMods` | `Core/CanvasMouse.cs` | `Left`、`Right`、`Middle`；`Ctrl`、`Shift`、`Alt` |
| `UiKeyEvent`、`UiKey` | `Core/KeyRouter.cs` | 第 5.3 节 |
| `UiDropEvent`、`UiDropPhase` | `Core/DragDrop.cs` | 第 5.10 节：`Enter`、`Over`、`Leave`、`Drop`、`Cancel` |
| `UiFileInput` | `Render/UiFileInput.cs` | 第 5.7 节 |
| `PopupCloseReason` | `Core/Popup.cs` | `Outside`、`Escape`、`Chosen`、`Code`、`Replaced` |
| `Diagnostic` | `Core/Dom.cs` | 提示：`File`、`Line`、`Message` |
| `NodeState` | `Core/Dom.cs` | 状态标志：`Hover`、`Active`、`Focus`、`Disabled`、`FocusWithin`、`DragOver`、`Dragging` |

### 8.6. 解析与 DOM（用于测试与高级场景）

| 类型 | 成员 |
|---|---|
| `Document` | `Root`（`#root`）、`Diagnostics`、`Styles`、`File`、`ScreenViewport`、`GetElementById(id)` |
| `Element` | `Tag`（小写）、`Children`、`Attributes`、`Id`、`Value`、`Checked`、`GetAttribute`、`SetAttribute`、`RemoveAttribute`、`Classes`、`HasClass`、`ToggleClass`、`Append`、`Insert`、`Remove`、`Clone`、`ElementChildren()`、`Descendants()`、`ElementIndex`、`TextContent`、`Style`（`ComputedStyle`）、`Parent`、`Line`、`Hidden`、`State`、`SetTranslate`、`ClearTranslate`、`SetScale`、`ClearScale` |
| `TextNode` | `Text` |
| `Query`（`Core/Query.cs`） | `First(root, selector)`、`All(root, selector)`——相同的 CSS 解析；`First` 跳过隐藏元素 |
| `Localizer`（`Core/Localizer.cs`） | `Texts`（函数 `id → 文字`；由游戏通过 `TextManager` 设置）、`Apply(doc, texts)` |
| `StyleEngine` | 层叠（通过 `Element.Style` 公开）；`CssParser` 为内部类型，仅供测试（`API.md`） |

### 8.7. 共用类型（`CSharp/Shared`）

| 类型 | 内容 |
|---|---|
| `UiPlugin : IAssemblyPlugin` | 模组的初始化与卸载（`Initialize`、`Dispose`） |
| `UiInfo` | `Version = 0`（阶段 0 探针的遗留；引擎版本是 `BaroUi.Version`）、`AssemblyName`、`Ping(caller)` |
| `UiEcho` | （`internal`）用于检查连接的回显通道 |
| `BBE.Curves.PointCurve`（`PointCurve.cs`） | 曲线文本 `x:y, x:y` 的解析与写入（依据 AUTHORING；（命名空间——「未核对代码」））|

### 8.8. API 中没有的内容

- 页面不能直接访问 C#：只有 `[UiHandler]` 方法与属性（第 3.6 节）。
- 没有 JavaScript、`eval`、网络请求、`iframe`。
- `UiWindow.Fonts` 自 2026-11-16 起为公开属性（`UiFonts`），因为 `Window.Drawing.Text(sb, fonts, …)` 需要接收它（AUTHORING，「图表」一节）。此前它是 `internal`，指南引用了公开 API 中不存在的成员。类型 `UiFonts` 是公开的（`Render/UiFonts.cs`）。
- `BaroUi.All`——`private`（仅在 `Api/BaroUi.cs` 内部使用）。

---

## 9. 工具

### 9.1. 游戏控制台命令

（「已核对代码」：`Api/BaroUi.cs`，`OnCommand`，第 210–283 行；命令 `baroui`。）

| 命令 | 作用 |
|---|---|
| `baroui open <page>` | 打开 `UI/<page>.html` |
| `baroui close [page]` | 关闭页面窗口或所有窗口 |
| `baroui reload [page]` | 原地重新读取 HTML 与 CSS：数据、C# 类、订阅者与滚动位置保留；C# 不重新加载 |
| `baroui list` | 已打开的窗口（自由窗口与物品窗口，物品窗口标注 `(item)`） |
| `baroui watch [on\|off]` | 保存文件时重新加载窗口并显示错误横幅 |
| `baroui debug [on\|off]` | 所有块的轮廓；鼠标下显示字段（橙色）、内边距（绿色）、内容（蓝色）、选择器、以 u 与 px 表示的尺寸，以及 HTML 中的行；错误横幅 |
| `baroui problems [page]` | 将所有窗口的提示写入日志：`文件:行号: 文本` |
| `baroui anim [on\|off]` | 开关窗口动画 |
| `baroui anim fps N\|max` | 动画长度的默认采样频率（30）；`max`——每帧；块的 `layout-fps` 优先 |
| `baroui src <文件夹>\|off` | 从文件夹（例如 `own/BBE_UI/UI`）读取页面，而不是从已安装的包读取 |

提示行：`Usage: baroui open <page> | close [page] | reload [page] | list | watch [on|off] | debug [on|off] | anim [on|off|fps N|max] | problems [page] | src [dir|off]`。

开关：`on`、`1`——开启；`off`、`0`——关闭；无参数——切换（内部函数 `Toggle`，`Api/BaroUi.cs`）。

### 9.2. Watch、debug、problems

- `watch` 轮询页面文件（`PollWatch`）并重新加载已更改的文件；错误以横幅显示。轮询频率（「未核对代码」）。
- `debug` 启用 `UiDebug.Outlines`（第 9.1 节）。
- `problems` 打印所有窗口或某一个窗口的 `w.Problems`（`Core/Dom.cs`，`Diagnostic.ToString()`：`文件:行号: 文本`）。
- 提示：CSS 中每个不支持的内容都会产生提示（`CssParser`、`StyleEngine`），未知的 `data-t`、未知的 `prefab`、超出限制的 `background-repeat` 等同样如此。

### 9.3. `tools/ui_host` 与参考快照

- `tools/ui_host`——控制台宿主（`ui_host.csproj`、`Program.cs`、`ModsScenario.cs`、`Script.cs`；`--data` 通过 `own/BBE_UI/CSharp/Client/Core/JsonModel.cs` 读取，与 `OpenPage` 相同的代码）。使用真正的 `UiWindow` 与 `Painter` 将页面绘制为 PNG；Noto Sans 字体取自游戏文件夹（只读）（依据 AUTHORING；依据文件夹内容核对）。
- 构建：`cd tools/ui_host && dotnet build -c Release && cd ../..`（依据 AUTHORING）。
- 参数：`--page <文件>`、`--data <json>`、`--frames N`（依据 AUTHORING 与 `snap.py`）。
- 限制：`ui_host` 不会把按键传给焦点（AUTHORING，「图表」一节）。
- `tools/ui_host/snap.py`——快照对比：
  - `PAGES`——页面列表：`controls`、`inputs`、`anim_demo`（来自 `own/BBE_UI/UI`）、`dialog`（`own/BBE_Dialog/UI/dialog.html`）以及 `toast`（`own/BBE_Toast/UI/toast.html`；这些是其他包，不是 `BBE_UI`）。
  - `SIZE = 960x540`，`FRAMES = 90`（游戏时间 1.5 秒）。
  - 参数：`--only <名称>`、`--update`（写入参考）、`--selftest`（检查比较本身）、`--tol 8`（每通道阈值，0–255）、`--share 0.001`（不同像素的比例）。
  - 结果：不匹配时退出码为 1；`out/diff/<页面>.png`（不同的像素标为红色）。
  - 需要 `numpy` 与 `PIL`；不下载任何内容。
- **没有参考。** 截至 2026-11-16，仓库中没有 `tools/ui_host/refs` 目录（依据文件夹核对）；`out/` 中是以前的快照。因此 `snap.py` 目前会对所有页面报告「无参考」，直到运行 `--update` 并人工检查 `out/diff` 与 `out/*.png`。`docs/HANDOFF.md` 记录：「参考未创建：需要带游戏字体的 `ui_host`」（依据 HANDOFF 与文件夹核对）。
- 写入 `refs/` 的 `manifest.json`（依据 AUTHORING）会记录写入参考时是否存在 Noto Sans。

### 9.4. `tools/ui_preview`

- 无需游戏的浏览器预览：`py -X utf8 tools/ui_preview.py [--ui DIR] [--port 8766]`，然后打开 `http://127.0.0.1:8766/?page=controls`（依据 AUTHORING；`tools/ui_preview.py`，第 10 行 — `Usage`）。
- 数据：`tools/ui_preview/data/<page>.json`——可在页面上编辑；该目录中有例如 `box.json`、`build.json`、`build_disputes.json`，以及 `_en` 变体。
- 参数：`&data=<名称>`——使用 `data/<名称>.json` 代替 `data/<page>.json`（例如 `lobby_subs`、`lobby_load`）；`&set=路径:值`——加载后修改一个字段；`&dialog=<id>`——打开 `dialog` 窗口。
- 其他页面文件夹：`--ui own/BBE_Terminal/UI`。
- 游戏专用标签（`item`、`slot`、`canvas`、`curve`、`chart`、`gauge` 等）以占位符显示。
- 不显示 `data-viewport="screen"` 模式：预览绘制 16:9 画布（依据 AUTHORING）。
- 保存文件后页面重绘。
- 浏览器不像游戏那样理解 `transition: layout` 与 `scale`（第 4.8、4.9 节）：预览中的运动与缩放可能与游戏不同。

### 9.5. `tools/ui_test`

- 构建与运行（依据 `docs/HANDOFF.md`，第 3 节）：`cd tools/ui_test && dotnet build -v q -o bin/<名称>/net8.0 && ./bin/<名称>/net8.0/ui_test.exe 2>&1 | grep -E "^FAIL|Unhandled|passed"`。构建文件夹的深度必须为 `bin/<名称>/net8.0`（测试按深度查找仓库根目录）。
- 2026-11-16 的完整运行：**8863 passed, 0 failed**（`HANDOFF.md`，第 0 节）。
- 快速测试集：`ui_test.exe style-state`、`hover-perf`、`map-perf`、`map-cards`、`icons`、`style-cache`、`images`、`screen-map`、`perf`、`alloc`、`talloc`、`p7alloc`、`trace`（依据 `PERF.md` 与 `HANDOFF.md`）。
- AUTHORING 引用的分组：`canvas mouse`、`code editor`、`pictures and files`、`rounded clip`、`scale`（`ScaleTests.cs`）、`DrawKitTests`、`ChartTests`、`FocusKeysTests`、`PopupTests`、`DragDropTests`、`SiblingSelectorTests`、`ShadowGradientTests`、`RoundedClipTests`（依据 AUTHORING、`AUDIT_STAGE_C.md`、`AUDIT_STAGE_D.md`、`PERF.md`）。
- 文档检查：`Program.cs`，`AuthoringGuide()`（第 638–644 行）：每个已知属性与简写（`StyleEngine.KnownProperties`）都必须以反引号形式出现在 `docs/ui/AUTHORING.md` 中，每个伪类都必须以 `:名称` 形式出现在文本中的某处。该检查只按名称进行：它不知道描述是否正确，也不会捕捉引擎中不存在的名称（例如 `font`）；这些名称在 AUTHORING 中手动列出（「完全没有」）。AUTHORING 中的措辞已于 2026-11-16 修正（核对第 17 条）。
- 其他相关检查：`ui_test` 的「hover perf」与「map perf」分组——相对测量（Debug）。

### 9.6. 游戏内自检

- 仅当游戏文件夹中存在文件 `bbe_ui_test.flag` 时运行（`Api/SelfTest.cs`，第 14 与 24 行）。类开头的注释提到通过 `tools/test.py` 运行。
- 报告——`bbe_ui_test.txt`；快照——`bbe_ui_test.png` 与 `bbe_ui_controls.png`（`SelfTest.cs`，常量 `Report`、`Shot`、`Shot5`）。
- 组成部分：`SelfTest.cs`（阶段 3–5）、`SelfTestInput.cs`（输入）、`SelfTestPerf.cs`（测量）、`SelfTestTools.cs`（工具）。
- D、E、F 批次的自检未在游戏中运行（`HANDOFF.md`，第 0 节；审计 D 与 C——「未在游戏中验证」）。

---

## 10. 性能与上限

所有数字均来自 `docs/ui/PERF.md`、`docs/ui/AUDIT_STAGE_C.md`、`docs/ui/AUDIT_STAGE_D.md`、`docs/HANDOFF.md`；本次核对未重新测量。每组的条件都已标注。离线测量——`tools/ui_test`，Release 或 Debug，取中位数；**数字是相对的**，来自开发者的机器。游戏中的测量仅在标注处进行。

### 10.1. 游戏：阶段 7 自检（2026-10-18，PASS）

测试页面：299 个元素，203 个绑定（网格中带滚动的 200 个配方瓦片，通过 `data-for` 生成，日志为 100 行）。

| 项目 | 预算 | 游戏中 |
|---|---|---|
| 打开窗口，热启动 | ≤ 50 ms | 1.9 ms（冷启动 8.4 ms） |
| 200 个值每帧变化 | ≤ 2.5 ms，无完整布局 | 0.39 ms，51 KB/帧 |
| 每帧一个值 | ≤ 0.5 ms | 0.085 ms，0 B |
| 无变化的 `Refresh` | 0 B | 0.088 ms，0 B |
| 页面绘制 | 0 B | 0.19 ms，0 B |

文字宽度缓存：42 843 次命中，693 次未命中。

这些测量在 A–F 批次之前完成；此后在游戏中未测量（依据 HANDOFF）。

### 10.2. 离线：500 个瓦片，2310 个元素（修改前 → 修改后，2026-10）

| 场景 | 之前 | 之后 |
|---|---|---|
| 完整布局 | 23.0 ms，25 MB | 4.9 ms，3.2 MB |
| 打开：解析 + 样式 + 布局 | 27.4 ms | 7.9 ms |
| 单个瓦片的文字 | 23 ms（完整） | 0.71 ms，52 KB（增量） |
| 每帧 200 段文字 | 23 ms（完整） | 1.7 ms，530 KB |
| 无变化的更新 | — | 0.13 ms，0 B |
| 模板，200 行，无变化 | 142 KB | 0 B |

完整表格见 `PERF.md`，「已完成内容」一节。

### 10.3. 悬停：300 张卡片，6006 个元素（A4 前后）

样式表为 `own/BBE_Mods/UI/build.css`。数字为 `ComputeState` 与完整 `Compute`，Debug，中位数，单位 ms。

| 情况 | 之前（完整重算子树） | 之后（状态索引） | 之后，含卡片规则 |
|---|---|---|---|
| 进入窗口 | 279.7 ms，6006 个元素 | 0.017 ms，0 个元素 | 0.97 ms，20 个元素 |
| 卡片 0 → 卡片 1 | 2.2 ms，40 | 0.011 ms，0 | 1.88 ms，40 |
| 标题 → 卡片内的行 | 0.51 ms，9 | 0.008 ms，0 | 0.32 ms，7 |
| 离开窗口 | 277.0 ms，6006 | 0.024 ms，0 | 0.91 ms，20 |

`PERF.md` 的结论：规则依附于元素的状态，而不是子树；页面祖先上不必要的 `:hover`（`.page:hover …`）在每次进入和离开时都会重新计算整个祖先（AUTHORING，「选择器」一节）。

### 10.4. 选择器索引与结构变化（B3 之后）

Debug，300 张卡片，22 932 个元素，中位数：

| 情况 | 无索引 | 有索引 |
|---|---|---|
| 页面首次打印 | 1670.7 ms | 1106.9 ms |
| 其中样式层叠 | 1481.1 ms | 886.1 ms |
| 单张卡片 `resize` | 60.2 ms | 55.6 ms |
| hover a（进入窗口，完整 `Compute`） | 229.0 ms | 46.3 ms |

结构变化（添加或删除一张卡片）：之前 174.5 ms（整个文档）→ 添加 2.07 ms（重新层叠 9 个元素），删除 0.13 ms（0 个元素）。

结构变化时的布局（阶段 A）：添加一张卡片约 27 ms，其中模板 13 ms，重新样式与布局约 14 ms（`PERF.md`，阶段 A）。

### 10.5. 含 300 个卡片块的地图（B3，Debug）

| 情况 | 中位数，ms | 最大值，ms | 重新层叠的元素 | 重新计算根下的盒子 |
|---|---|---|---|---|
| 平移，300 张卡片 | 0.056 | 21.95 | 0 | 0 |
| 缩放，300 张卡片 | 0.075 | 0.150 | 0 | 0 |
| 拖动一张卡片 | 0.057 | 0.067 | 0 | 0 |
| 单张卡片 `resize`（完整重绘） | 45.1 | 54.7 | 219 | 4 497 |
| 页面首次打印，300 张卡片 | 1264.6 | 1599.4 | 15 968 | 4 592 |

单张卡片的 `resize` 会重新布局整个图层：父级 `#world` 进入脏根（`PERF.md`，B3）。此项未在游戏中测量。

阶段 A（2026-11-15，Debug，300 张卡片）：首次打开页面——冷缓存 996 → 936 ms；使用样式缓存再次打开——419 ms；卡片 resize 59 → 58 ms；平移/缩放/拖动——0.013–0.020 ms（`PERF.md`，阶段 A）。

### 10.6. 仅离线验证的内容

渲染的像素、GPU、draw calls、通过 `Painter.Hit` 的鼠标命中（测试之外）、缩放时的字体光栅化、游戏中的绘制分配——**未测量**（`PERF.md`，B3 一节，「测试未测量的内容」）。

### 10.7. 上限（代码中的值）

| 上限 | 值 | 来源 |
|---|---|---|
| 单个窗口的纹理（图片、阴影、渐变、角遮罩） | 256 MiB 与 4096 条记录，共享预算 | `UiWindow.cs`，第 90–91 行；`Core/TextureLedger.cs`；「单一预算」未在游戏中测量 |
| 纹理从预算中移除 | 每次一趟最多移除预算的四分之三；释放有几分之一秒的延迟 | `TextureLedger.cs`（第 87–88 行）；`AUDIT_STAGE_C.md`，第 7–8 条 |
| 重阴影烘焙 | 每帧不超过 2 个 | `Core/BoxShadow.cs`，`DecorBudget.HeavyPerFrame = 2` |
| 轻量烘焙（条带、角、区域） | 每帧不超过 64 个 | `BoxShadow.cs`，`LightPerFrame = 64` |
| `background-repeat` 重复 | 块内 1024 个副本 | `AUTHORING`（「属性」一节，背景） |
| `box-shadow` 与文字的模糊 | 最大 48 px；阴影纹理半径最大 96 px | `AUTHORING`（块阴影）；`TextShadow`（文字模糊不绘制） |
| 渐变纹理 | 长于 512 px 的会被采样器拉伸 | `AUTHORING` |
| 渐变角半径 | 最大 96 px 纹理，超过则为直角 | `AUTHORING`；`AUDIT_STAGE_C.md`，第 3 条 |
| 样式缓存 | 2048 条 `style` 字符串记录，8192 条样式每代（两代） | `Core/StyleEngine.cs`，第 576 行 |
| 字体 | 每个大小约 8 MiB（图集与缓冲区） | `Core/FontCache.cs`；`HANDOFF.md`，A2 |
| 虚拟列表 | `overflow: auto/scroll` 内超过 100 行 | `Core/Template.cs`，`AutoVirtual` |
| 代码编辑器 | 只绘制可见行；20 000 行——加载 3 ms | `AUTHORING`（「代码编辑器」一节） |
| 编辑器撤销历史 | 1000 步 | `AUTHORING` |
| 下拉列表 | 8 行 | `UiControls.cs`，`DropRows = 8` |
| `title` 提示 | 0.4 秒后 | `UiControls.cs`，`TooltipDelay` |
| 拖动阈值 | 4 px | `PointerRouter.DragThreshold` |
| `ClickCounter` | 0.4 秒与 6 px | `Core/CanvasMouse.cs` |
| `Send` 超时 | 默认 5 秒 | `Api/BaroUiView.cs` |
| `layout-fps` 默认值 | 每秒 30 次 | `Core/Animation.cs`；`BaroUi.LayoutFps` |
| `Esc` 与「窗口显示中」 | 最近 0.5 秒 | `Api/BaroUi.cs`，`PauseMenuPrefix` |
| `ui_host snap` | 通道阈值 8（满分 255）；不同像素比例 0.001 | `tools/ui_host/snap.py` |

AUDIT C 中的旧上限（第 7–8 条：每个 16 MB 的遮罩最多 256 个，角瓦片最多 512 个；装饰缓存最多 1500 条记录、48 MB）是在预算合并之前描述的。当前上限是窗口的共享预算（`TextureLedger`）；（「未核对代码」：旧数字是否已从所有位置删除）。

### 10.8. 分配

- 无变化的稳定帧：没有内存分配（`AUDIT_STAGE_D.md`：「在稳定帧中没有内存分配」；`PERF.md`：无变化更新与离线绘制测量中均为「0 B」）。
- 图表：数据未变化的帧不分配内存（通道 H，2026-11-16；`ChartLaneTests`，热路径上的字节）。热路径之外的分配见第 6.5 节。
- 文字：文字宽度缓存（`UiFonts`，`MaxWidths = 10000`）——考虑到 `letter-spacing` 按字符绘制且每帧无分配（依据 AUTHORING）。
- 包含 `+` 与 `~` 的容器中的重新计算：此前的二次增长已修复（审计 D，第 5 条）。

### 10.9. 代价高昂的情况

| 情况 | 原因 | 应如何做 |
|---|---|---|
| 大子树上的 `:has()` | 每次容器重新层叠时都会检查子树；`.map:has(.x)` 在 6000 个元素上最多 6000 次检查（`PERF.md`，「伪元素与 :has()」） | `:has(> .x)`（仅子元素）或直接在元素上设置规则 |
| 页面祖先上的 `:hover`（`.page:hover …`） | 每次进入与离开时整个祖先都会重新层叠 | 在元素本身或其直接父元素上设置规则 |
| 大列表的结构变化（首次打印、重建） | 所有元素的层叠（地图的 15 968 个元素——1.1–1.8 秒，Debug） | 带 `data-key` 的 `data-for`；不要整体重建整个列表 |
| 每帧 `style="translate: {{x}}px"` | 每帧生成新的样式字符串并解析；字符串缓存有限 | `SetTranslate`/`SetScale`（第 4.9 节） |
| 每帧 `font-size` | 每个新大小都是一个新的游戏字体（约 8 MiB） | 用 `scale` 实现平滑放大（缩放的字体会被卸载） |
| 大块上的长度过渡（`width`、`height`、内边距） | 每个长度值都会重新计算块的布局，按 `layout-fps` 的网格 | 展开使用 `transition: layout`；其余使用 `opacity` 与 `translate` |
| 虚拟列表上的 `layout` | 滚动时行被重新创建，可能跳位（AUTHORING） | 不要在虚拟列表上使用 `layout` |
| 带长度的块上的 `transition: all` | `all` 包含长度，每个都会触发布局 | 列出具体属性 |
| 平滑缩放时的 `box-shadow` 与圆角渐变 | 每帧烘焙有限（2 个阴影，64 个条带/角）；未完成的部分使用最近的级别绘制，或被跳过 | 不要同时动画大量大阴影 |
| 地图中卡片的 `resize`（整层完整重绘） | 父级 `#world` 进入脏根（45–65 ms，Debug） | 小缩放时使用简化视图；是否需要由所有者决定（`PERF.md`，B3；尚无决定） |
| `scale` 内的 `dialog`、`select` | 不缩放打开，其他情况走单独路径 | 在可缩放块之外打开 |
| 带大量绘制调用的 `canvas` | 未测量 | （未核对） |

---

## 11. 不支持的内容、上限与未验证事项

### 11.1. 「不支持」清单

已核对代码：这些名称不在 `StyleEngine.Initial`、`Shorthands`、`CssValues`、`CssParser`、`HtmlParser`、`UserAgentCss` 中，或其行为明确未实现。对于每个属性，未知者都会给出提示。

**CSS 函数与单位**

- `calc()`、`min()`、`max()`、`clamp()`、函数内部的 `var()`（简单替换 `var(--x)` 除外）。
- 单位：`rem`、`vw`、`vh`、`vmin`、`vmax`、`ch`、`ex`、`pt`、`cm`、`in`、`Q`。只有 `px`、`u`、`em`、`%`、`fr`、`auto`、`0`。
- 颜色：`hsl()`、`hwb()`、`lab()`、`lch()`、`color-mix()`、`color()`。有 `rgb()`、`rgba()`、`#…`、颜色名。
- `counter()`、`counters()`、`content` 之外的 `attr()`、`content` 中的 `url()`。

**规则与选择器**

- `@media`、`@import`、`@font-face`、`@supports`、`@layer`、`@container`、`@namespace`。有 `@keyframes`。
- 属性操作符：`[a^=…]`、`[a$=…]`、`[a*=…]`、`[a~=…]`、`[a|=…]`。
- 除 `::before`/`::after` 外的伪元素：`::marker`、`::selection`、`::placeholder`、`::first-line`、`::first-letter`。
- 伪类：`:link`、`:visited`、`:empty`、`:is()`、`:where()`、`:first-of-type`、`:nth-of-type`、`:lang()`、`:target`、`:scope`、`:required`、`:invalid`、`:placeholder-shown`、`:nth-child(2n+1)`（不解析 `an+b`，不匹配）。
- `:not(...)` 与 `:has(...)` 中的列表、嵌套的 `:not` 与 `:has`、其参数中的伪元素。
- 两个组合器相连。

**不存在的属性**（另见第 4.6.10 节）

- `float`、`transform`（`translate` 与 `scale` 除外）、`filter`、`backdrop-filter`、`clip-path`、`mask`、`mix-blend-mode`、`will-change`、`user-select`、`inset`、`aspect-ratio`、`font`（简写）、`grid-row`、`grid-area`、`justify-items`、`justify-self`、`place-*`、`object-position`、`background-clip`、`background-attachment`、`background-origin`、`word-break`、`hyphens`、`text-wrap`、`text-align-last`、`text-justify`、`direction`、`writing-mode`、`font-variant`、`font-stretch`、`list-style`、`counter-reset`、`counter-increment`、`border-top-style`（及各边的同类）、`transition-behavior`、`animation-composition`、`animation-play-state`。

**接受但行为与浏览器不同的值**

- `display: table`、`table-row`、`table-cell`、`list-item`——带提示的块；`<table>` 不是表格。
- `overflow: clip`——与 `hidden` 相同。
- `border-style: double`、`groove`、`ridge`、`inset`、`outset`——实线。
- 除 `solid` 外的 `text-decoration-style`——实线。
- `outline-style: dashed`/`dotted`——实线。
- 带模糊的 `text-shadow`——锐利阴影；阴影列表——取第一个。
- `box-shadow`：只支持一个外部阴影；不支持 `inset`。
- `background-image`：`conic-gradient`、`repeating-*`、颜色提示、径向渐变中以 `%` 表示的半径——不支持（渐变不绘制，给出警告）。
- `vertical-align` 与 `-webkit-box-orient`——无作用。
- `transform-origin`——无作用（缩放中心为左上角）。
- `text-align`——只有 `left`、`center`、`right`。
- `inline-block` 块上的 `line-clamp`，在被舍弃的行中——不隐藏（审计 C，第 12 条）。
- 文字阴影的 `currentcolor` 颜色按帧获取（这是有效的），但阴影不会动画。

**绘制与图片**

- 按 `overflow` 对子元素做圆角裁剪（沿弧线裁剪）——不支持（几何存在，绘制未启用）。
- 背景图片与 `<img>` 的圆角——角为直角。
- `outline` 的 `dashed`/`dotted` 描边——实线。
- 角半径不同时的渐变与阴影——阴影与轮廓使用同一半径。
- 游戏的九个部分的 `sprite()`——不支持（整张精灵被取用）。
- `image-rendering`、`object-position`、`background-clip`——不支持。

**标签与属性**

- `iframe`、`audio`、`svg`（作为图画）、`input type=color`、`input type=email/date/…`（不是字段）、`input type=file`（文件通过拖放与 Ctrl+V，第 5.7 节）、`details`、`summary`、作为模板的 `template`、作为表格的 `<table>`。
- 属性 `onmousedown`、`onmouseup`、`onmouseover`、`onkeydown`、`onkeyup`、`ondblclick`（没有语法糖；使用 `data-mouse` 与 `data-keys` 代替）。
- `title` 提示在浏览器预览中不显示（依据 AUTHORING——是游戏的提示；（「未核对代码」：预览中的提示））。

**输入**

- `keyup` 与按键释放——不支持（第 5.3 节）。
- 从资源管理器拖动文件时对目标的高亮——不支持（第 5.7 节）。
- 从窗口拖到资源管理器——不支持。
- 将游戏物品拖到 HTML 块上——不支持（第 5.10 节）。
- `number` 字段上的滚轮自 2026-11-16 起支持（第 3.5.1 节，`UiControls.WheelNumber`）；未在游戏中验证。`range` 没有滚轮（仍滚动页面），与此前相同。
- 一次调用处理多文件拖放——不支持。
- 画布上的右键——未验证（审计 D）。

**其他**

- JavaScript、`eval`、网络、`http(s)://`、`data:`、`file:`，以及图片与字体地址中的绝对路径。
- 包的字体：没有 `@font-face`；只有第 7.1 节中的字体族。
- RTL 语言、竖排文字——不支持。
- 打印、无障碍（屏幕阅读）——不支持。
- 抗锯齿：弧线、斜线与角的边缘显得生硬（第 11.2 节）。

### 11.2. 已知限制与未完成事项

来自审计与 `docs/HANDOFF.md`（第 0 节，截至 2026-11-16）：

| 编号 | 内容 | 状态 |
|---|---|---|
| 1 | 子元素按圆角裁剪（`overflow` 与 `border-radius`）：几何存在（`Core/RoundedClip.cs`），绘制未启用——在帧中途切换绘制目标会清空屏幕（已在 `ui_host` 上核对） | 未完成；需要在帧开始时的一趟与 `PreserveContents`（未核对） |
| 2 | 抗锯齿：不在像素网格上的圆角与边缘显得像素化且生硬（所有者，2026-11-16） | 通道 G 已于 2026-11-16 完成，未在游戏中验证：圆角、圆环与角的边缘以解析覆盖率计算，经由 1 px 的线性过渡（`Core/Coverage.cs`、`Core/Corners.cs`、`Core/RoundedClip.cs`、`Render/Painter.cs`、`Render/UiDraw.cs`）。旧的 4×4 采样与 0.55–0.7 幂次修正已移除。1–2 px 细圆环以 0.8（1 px）与 0.9（2 px）的指数提升（`Coverage.ThinLift`）：数值通过计算选定（在线性空间中半覆盖的浅色线在 sRGB 中光线更少），并非依据 Chrome 截图拟合。剩余：不生成分数半径的遮罩，半径取整为整数 px（`Corners.CornerPx`）；页面图片以 `PointClamp` 绘制（`UiImages.cs`，约第 180 行）；需与 Chrome 及游戏中的外观对比 |
| 3 | 缩放时的阴影与渐变：烘焙按帧限制；快速缩放时阴影可能跳动或闪烁一帧 | 已完成（审计 C，第 1–2 条），游戏中未测量 |
| 4 | 块阴影与圆角渐变：阴影与轮廓使用同一半径 | 已记录（审计 C，第 3 条——渐变按角区分；阴影与轮廓不区分） |
| 5 | 图表：数据未变化的帧不分配内存（通道 H，2026-11-16，`ChartLaneTests`）；热路径之外仍有分配（第 6.5 节：`smooth` 曲线在点变化时、刻度或悬停变化时的标签） | 部分完成；游戏中未测量 |
| 6 | 图表：没有渐变填充、对数坐标轴、滚轮缩放、`chart`/`gauge` 的键盘焦点 | 未完成（AUTHORING） |
| 7 | `scale` 内的画布：通过 `PlayerInput` 读取鼠标的回调以屏幕像素计算 | 已记录 |
| 8 | `scale` 内的 `slot`、`select`（列表项）、`dialog` | 已记录（第 4.9 节） |
| 9 | 水平 `sticky`、`scale` 内的 `sticky` | 未完成 |
| 10 | 被舍弃行上的 `line-clamp` 与原子块 | 已记录（审计 C，第 12 条） |
| 11 | 大子树上的 `:has()`——成本 | 已记录（`PERF.md`）；游戏中未测量 |
| 12 | 默认开启俄语粘连（`-bbe-ru-glue: auto`）——所有窗口的俄语换行略有不同 | 所有者尚未决定（`HANDOFF`，问题 1；审计 C，第 4 条） |
| 13 | 细长块上的 `50%` 生成椭圆（此前为短边上的圆）；带单侧边框的圆角块——按边；输入框改变光标 | 所有者的决定，保留（审计 C，第 4 条） |
| 14 | 无参考的 `ui_host` 快照 | 参考未创建（第 9.3 节） |
| 15 | 测试 `shop page: 500 cards open within 50 ms`——安静机器上 53–59 ms，负载下 54–74 ms | 2026-11-16 最后一次运行——0 failed |
| 16 | 地图（引擎之外）：两次页面更新之间卡片层不跟随相机；模组启动时的 `Settle` | 引擎之外，`HANDOFF` 第 4.3 节，问题 2 与 5 |
| 17 | `HANDOFF`（第 0 节）中「已记录但未完成」的条目：`UiMouseEvent.Under`、`<img>` 的自然尺寸、共享纹理预算——据 `HANDOFF` 已完成（未在游戏中验证） | 见 `HANDOFF` 第 0 节 |

审计 D（交互）：全部 14 项已按测试修复；游戏中未验证（`AUDIT_STAGE_D.md`，「未验证」一节）。遗留条目：`Over` 一词的三种含义，以及公开名称 `DragDrop.Swallow` 未重命名（为免改动测试与页面；审计 D，第 14 条）。

审计 C（外观）：第 4 条——所有者的决定；第 12 条——已记录；其余已按测试与 `ui_host` 修复；游戏中未验证。

### 11.3. 游戏中未验证的内容

整理自 `AUTHORING`（「游戏中未验证」）、`AUDIT_STAGE_C.md` 与 `AUDIT_STAGE_D.md`（「未验证」）、`PERF.md`（「未验证」）、`HANDOFF.md`：

- **外观。** 阶段 C、D、E 的几乎所有新属性：阴影、渐变、圆角、轮廓、边框、9-slice 边框图片、背景图片、Lucide 图标、游戏文件夹中的 Noto Sans、抗锯齿、游戏中的光标（「游戏中的外观未经人工核对」；AUTHORING）。
- **字体。** 游戏中的字符宽度（FreeType 取整）；字符缺失以及不间断空格的行为；合成斜体字体的斜体行为。
- **输入。** 窗口之外的鼠标捕获；拖放文件时的 SDL 事件；通过 user32 与 shell32 读取剪贴板；IME（组合输入）；曲线的按键；`number` 字段上的滚轮（`WheelNumber`，自 2026-11-16）；`Fail` 之后游戏键盘分发器的异常；画布上的右键；真实缩放下的 `GhostShift`。
- **菜单与拖放。** 游戏中的菜单、右键菜单、卡片拖放场景；菜单中字段的按键。
- **自检。** 阶段 C、D、E、F 的自检未在游戏中运行（`HANDOFF`）。
- **游戏中的测量。** 阴影与渐变的烘焙时间；`:has()` 的成本；绘制分配；帧中图表的成本。
- **地图。** 游戏中的所有模组管理器场景（`HANDOFF`，阶段 B6 等待游戏）。
- **LuaCs。** 标准（stock）LuaCs 上窗口的工作情况——未验证（`HANDOFF`，第 0 节，关于标准 LuaCs 的条目）。
- **原样包。** `own/BBE_UI` 在 LuaCs 1.15.4（新插件）上的构建与运行——未验证（`docs/luacs/COMPARE_PLUGIN.md`）。

---

## 12. 兼容性与版本

### 12.1. 游戏

- 包的 `filelist.xml` 中的版本：`gameversion="1.13.5.0"`（2026-11-16 之前为 1.13.4.0）。
- LuaCs 分支（`forks/LuaCs`，在菜单中运行模组）是在游戏 **1.13.5.0** 上构建的（`docs/HANDOFF.md`，第 4.1 节与第 0 节）。包元数据中的版本（1.13.4.0）与分支构建所用的版本（1.13.5.0）不一致。这对游戏意味着什么——未验证（由所有者决定写哪个版本）。
- **所有者已于 2026-11-16 决定：**包的 `gameversion` 已提升为 1.13.5.0，与分支一致。原先的记录： **所有者的未决决定**（核对第 19 条，2026-11-16）：在所有者决定包与分支使用哪个游戏版本之前，不修改 `own/BBE_UI/filelist.xml` 中的 `gameversion`（1.13.4.0）。核对期间未修改代码或 `filelist.xml`。
- 新版 LuaCs 插件（`LuaCsForBarotraumaPlugin`）以游戏 1.15.4.0 为目标（`docs/luacs/COMPARE_PLUGIN.md`，第 0 节）。

### 12.2. LuaCs：BBE 分支与标准版

| 问题 | BBE 分支（`forks/LuaCs`） | 标准（stock）LuaCs |
|---|---|---|
| 主菜单中的模组 | 工作（在菜单中运行模组是分支的可开关设置；`docs/HANDOFF.md`，第 0 节） | 进入菜单时卸载（`HANDOFF`，第 0 节）——引擎的窗口在菜单中不工作 |
| 大厅、回合、编辑器 | 工作 | 工作（对此包未验证——`HANDOFF`，第 0 节） |
| 游戏补丁 | 分支修改游戏文件：在 `build client\|server` 之前执行 `py -X utf8 tools/engine.py save-patch`（构建会清除补丁），然后 `install-client`（`HANDOFF`，第 3 节） | 不需要 |
| 分支 | `luacs/bbe`、`moonsharp/bbe`，位于 `Nice2Live/Barotrauma-Beyond-the-Europa`（`HANDOFF`，第 0 节） | — |

`own/BBE_UI` 包只在 `Api/BaroUi.cs` 中设置 Harmony 前缀（`harmony.Patch`，第 45–52 行）：针对 `DebugConsole.AddToGUIUpdateList` 与暂停菜单（第 1.3 节）。在 `CSharp/` 中未发现其他补丁（通过搜索 `harmony` 核对）。这些目标与 LuaCs 运行的游戏版本是否吻合——（「未核对代码」：每个版本）。

### 12.3. 新版 LuaCs 插件

新版 LuaCs 插件（`Luatrauma/LuaCsForBarotraumaPlugin`）不是游戏的分支：它通过插件加载器与 Harmony 接入，以游戏 1.15.4.0 为目标，因此我们的 1.13.5 分支不能直接移植；`own/BBE_UI` 按 `ModConfig.xml` 格式与 Harmony 目标可能会加载，但未在 1.15.4 上验证（`docs/luacs/COMPARE_PLUGIN.md`，第 0 节，第 1–6 条）。

### 12.4. 版本与名称

- 模块版本：`modversion="0.0.1-bbe"`（`filelist.xml`）；`UiInfo.Version = 0`（`CSharp/Shared/UiPlugin.cs`）。
- 引擎对外名称为 Baro-UI（所有者决定，2026-11-15）；包与程序集的内部名称为 `Baro-UI` / `BaroUI`。重命名尚未决定（`HANDOFF`，第 0 节）。
- 仓库 `Nice2Live/baro-ui` 创建为私有，截至 2026-11-15 为空，只有一个本地提交（`HANDOFF`，第 0 节）。公开与否尚未决定。
- 界面文字：`data-t` 从游戏的 `TextManager` 取文字（与游戏在其语言中的文字相同）；包中没有文字表（第 3.6 节）。
- 俄语粘连默认开启（第 4.6.4a 节）。

### 12.5. 浏览器预览与游戏

依据文档（AUTHORING、`PERF.md`）已知的差异：

- 缩放中心：游戏中为边框左上角；浏览器中为中心。对于预览，使用 `transform-origin: 0 0`。
- `transition: layout` 在浏览器中不可见（块会直接跳变）。
- `data-viewport="screen"` 在预览中不工作（绘制 16:9 画布）。
- 游戏专用标签在预览中为占位符。
- 字符宽度：游戏中的行略宽（FreeType 取整）。
- Noto Sans 字体：游戏中取自游戏文件夹；预览为该字体族选用哪种字体，以及度量是否一致（「未核对代码」）。

---

## 13. 给译者的说明

- 本文件为主版本。译文为 `REFERENCE.en.md` 与 `REFERENCE.zh-CN.md`（依据所有者的决定，2026-11-16）。修改先写入本文件，再同步到译文。
- 标识符、属性名、选择器、标签、属性、函数与错误文本保持不变。
- 「（已核对代码）」「（依据 AUTHORING）」「（未核对代码）」「（未在游戏中验证）」等标记按原样保留（在简体中文译文中分别对应「已核对代码」「依据 AUTHORING」「未核对代码」「未在游戏中验证」）。
