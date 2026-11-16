using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Barotrauma;
using BaroUI.Render;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;

namespace BaroUI
{
    /// <summary>Entry point for other packages: open a page as a free window, or build a UiWindow and add it to the
    /// GUI update list yourself (item components, as the terminal does). Free windows join the update list from a
    /// prefix on DebugConsole.AddToGUIUpdateList, which the game calls every frame on every screen before GUI.Update,
    /// so they get input. Console: bbe_ui open|close|reload|list|watch|debug|problems|src (docs/ui/AUTHORING.md).</summary>
    public static class BaroUi
    {
        private const string Command = "baroui";
        /// <summary>The engine's first name, kept as a second name of the command (pages, guides and habits of the BBE modpack use it).</summary>
        private const string OldCommand = "bbe_ui";

        /// <summary>The engine's version (semver, docs/ui/API.md). The same number is in filelist.xml modversion: change both together.
        /// A consumer checks it at start with Requires.</summary>
        public static readonly System.Version Version = new System.Version(0, 3, 0);

        /// <summary>A consumer's check at start: true when the engine is at least minVersion ("0.2.0"). When it is older, or minVersion does not
        /// parse, one line goes to the console log naming modName and the versions, and false comes back: the mod then leaves its UI off.</summary>
        public static bool Requires(string minVersion, string modName)
        {
            if (BaroUI.Core.EngineVersion.AtLeast(Version, minVersion)) { return true; }
            UiPlugin.Log($"{modName} needs Baro-UI {minVersion} or newer, the installed one is {Version.Major}.{Version.Minor}.{Version.Build}: update the Baro-UI package");
            return false;
        }

        /// <summary>bbe_ui anim on|off: transitions, keyframe animations and typewriter text of every window (on by default).</summary>
        public static bool Animations { get; set; } = true;
        /// <summary>bbe_ui anim fps N|max: how many times a second animated lengths (width, height, margins...) are sampled; each sample is one layout of the block. 0: every frame.</summary>
        public static float LayoutFps { get; set; } = 30;
        private static readonly List<UiWindow> windows = new();
        /// <summary>Windows an item component shows itself (Create): reload, watch and the console see them too.</summary>
        private static readonly List<UiWindow> hosted = new();
        private static IEnumerable<UiWindow> All => windows.Concat(hosted);
        private static Harmony harmony;
        private static UiFonts fonts;

        /// <summary>Folder of the BBE UI package (fonts, UI/ pages).</summary>
        public static string PackageDir { get; private set; }
        public static UiFonts Fonts => fonts ??= new UiFonts(PackageDir);

        /// <summary>A font file of a mod, for the windows opened afterwards (not the open ones): the family name a page uses in font-family, the absolute
        /// path of the .ttf or .otf inside the mod's own package, the weight (1..1000, 400 normal, 700 bold) and italic. The file is read when a window
        /// opens: a missing or unreadable one is one line in bbe_ui problems and the family is not there (the next name of the list, then the default).
        /// A window's own @font-face of the same family adds faces to it and shadows the built-in family of that name in that window only. Throws
        /// ArgumentException for a family that is not one name or a path that is not absolute. Docs: docs/ui/API.md.</summary>
        public static void RegisterFont(string family, string file, int weight = 400, bool italic = false) => BaroUI.Core.FontScope.Register(family, file, weight, italic);
        public static IReadOnlyList<UiWindow> Windows => windows;
        internal static bool EscapeHooked { get; private set; }

        internal static void Init()
        {
            PackageDir = FindPackageDir();
            // <i data-icon="name">: the names and code points of the Lucide icon font (Core/Icons.cs)
            BaroUI.Core.Icons.MapFile = PackageDir == null ? null : Path.Combine(PackageDir, "Fonts", "lucide-codepoints.json");
            BaroUI.Core.Icons.Warn = text => UiPlugin.Log(text);
            // data-t="id" in a page: the text of the id from the game's text files, in the language the player plays in (Core/Localizer.cs)
            BaroUI.Core.Localizer.Texts = id => TextManager.ContainsTag(id) ? TextManager.Get(id).Value : null;
            harmony = new Harmony("bbe.ui");
            var target = AccessTools.Method(typeof(DebugConsole), nameof(DebugConsole.AddToGUIUpdateList), Type.EmptyTypes);
            if (target == null) { UiPlugin.Log("ERROR hook target DebugConsole.AddToGUIUpdateList() not found: free windows get no input"); }
            else { harmony.Patch(target, prefix: new HarmonyMethod(typeof(BaroUi), nameof(AddWindowsPrefix))); }
            // Esc: the game opens the pause menu last, when nothing else wanted the key; a free window comes first
            var pause = AccessTools.Method(typeof(GUI), nameof(GUI.TogglePauseMenu), Type.EmptyTypes);
            if (pause == null) { UiPlugin.Log("ERROR hook target GUI.TogglePauseMenu() not found: Esc does not close windows"); }
            else { harmony.Patch(pause, prefix: new HarmonyMethod(typeof(BaroUi), nameof(PauseMenuPrefix))); EscapeHooked = true; }
            // mouse side buttons 4 and 5: a window that took the button for its back or forward swallows the game's click for the rest of the frame
            var side4 = AccessTools.Method(typeof(PlayerInput), nameof(PlayerInput.Mouse4ButtonClicked), Type.EmptyTypes);
            var side5 = AccessTools.Method(typeof(PlayerInput), nameof(PlayerInput.Mouse5ButtonClicked), Type.EmptyTypes);
            if (side4 == null || side5 == null) { UiPlugin.Log("ERROR hook target PlayerInput.Mouse4/5ButtonClicked() not found: side buttons are not swallowed"); }
            else
            {
                harmony.Patch(side4, prefix: new HarmonyMethod(typeof(BaroUi), nameof(SideBackPrefix)));
                harmony.Patch(side5, prefix: new HarmonyMethod(typeof(BaroUi), nameof(SideForwardPrefix)));
            }
            // the start of every frame of the game, before the game draws anything: the pre-pass of the rounded overflow clip (PrePass)
            var draw = AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(Microsoft.Xna.Framework.GameTime) });
            if (draw == null) { UiPlugin.Log("ERROR hook target GameMain.Draw(GameTime) not found: rounded overflow blocks keep the rectangular clip"); }
            else { harmony.Patch(draw, prefix: new HarmonyMethod(typeof(BaroUi), nameof(FramePrefix))); }
            DebugConsole.Commands.RemoveAll(c => c.Names.Contains(Command.ToIdentifier()) || c.Names.Contains(OldCommand.ToIdentifier()));
            DebugConsole.Commands.Add(new DebugConsole.Command($"{Command}|{OldCommand}",
                $"{Command} (or {OldCommand}) open <page> | close [page] | reload [page] | list | version | watch [on|off] | debug [on|off] | problems [page] | src [dir|off] (docs/ui/AUTHORING.md).", OnCommand));
            SelfTest.Init();
            UiPlugin.Log($"client ready, package {PackageDir ?? "NOT FOUND"}");
        }

        internal static void Dispose()
        {
            SelfTest.Dispose();
            foreach (var w in All.ToList()) { w.Close(); }
            UiImages.SweepAll();
            UiFileDrop.Unhook();
            harmony?.UnpatchSelf();
            harmony = null;
            fonts?.Dispose();
            fonts = null;
            prePassBatch?.Dispose();
            prePassBatch = null;
            DebugConsole.Commands.RemoveAll(c => c.Names.Contains(Command.ToIdentifier()) || c.Names.Contains(OldCommand.ToIdentifier()));
        }

        /// <summary>Opens an .html file as a free window on top of the game UI, optionally with its C# view.</summary>
        public static UiWindow Open(string htmlPath, BaroUiView view = null, string name = null)
        {
            var w = UiWindow.FromFile(htmlPath, Fonts, name);
            windows.Add(w);
            view ??= w.Name switch { "test" => new SelfTest.TestView(), "controls" => new SelfTest.ControlsView(), "inputs" => new SelfTest.InputsView(), _ => null };
            view?.Attach(w);
            return w;
        }

        /// <summary>A window an item component shows itself, as the terminal does: the owner adds it to the update
        /// list from its own AddToGUIUpdateList (so it gets input) and closes it. It is not a free window: Esc and a
        /// click outside are the game's (the owner's GuiFrame), not the window's. bbe_ui reload/watch work on it;
        /// owners that keep the reference follow Reloaded.</summary>
        public static UiWindow Create(string htmlPath, BaroUiView view = null, string name = null)
        {
            var w = UiWindow.FromFile(htmlPath, Fonts, name);
            hosted.Add(w);
            view?.Attach(w);
            return w;
        }

        /// <summary>Opens a page with a new view of type T (its [UiHandler] methods become the page's handlers).</summary>
        public static T Open<T>(string htmlPath, string name = null) where T : BaroUiView, new()
        {
            var view = new T();
            Open(htmlPath, view, name);
            return view;
        }

        /// <summary>Opens UI/&lt;page&gt;.html of this package (or of the folder set by bbe_ui src). When no view gave the window a model, the demo data
        /// UI/&lt;page&gt;.data.json next to the page becomes its Data (Core/JsonModel.cs, the same reader as tools/ui_host --data); a broken file is logged and the page opens without it.</summary>
        public static UiWindow OpenPage(string page)
        {
            var w = Open(Path.Combine(PageDir, page + ".html"), null, page);
            string data = Path.Combine(PageDir, page + ".data.json");
            if (w.Data == null && File.Exists(data))
            {
                try { w.Data = BaroUI.Core.JsonModel.Load(data); }
                catch (Exception e) { UiPlugin.Log($"{page}: {Path.GetFileName(data)} not read: {e.Message}"); }
            }
            return w;
        }

        /// <summary>Where OpenPage looks: the package's UI folder, or a working copy set by bbe_ui src (edit the
        /// repository, see it in the game without a deploy).</summary>
        public static string PageDir => SourceOverride ?? Path.Combine(PackageDir ?? "", "UI");
        internal static string SourceOverride;

        /// <summary>A window was hot-reloaded (bbe_ui reload, bbe_ui watch): owners that keep a reference swap it.</summary>
        public static event Action<UiWindow, UiWindow> Reloaded;

        /// <summary>Re-reads the window's page and stylesheets in place: same position in the window list, the same
        /// view, model, event subscribers and scroll. Markup and CSS only; C# is not reloaded. On an error the old
        /// window stays and the error is returned.</summary>
        public static string Reload(UiWindow w)
        {
            var list = windows.Contains(w) ? windows : hosted;
            int i = list.IndexOf(w);
            if (i < 0 || w.SourcePath == null || !w.IsOpen) { return $"{w.Name}: not a reloadable window"; }
            UiWindow fresh;
            try { fresh = UiWindow.FromFile(w.SourcePath, Fonts, w.Name); }
            catch (Exception e) { return $"{w.Name}: reload failed, the old page stays: {e.Message}"; }
            list[i] = fresh;
            fresh.TakeOver(w);
            w.Dispose();
            try { Reloaded?.Invoke(w, fresh); } catch (Exception e) { UiPlugin.Log($"ERROR Reloaded handler: {e}"); }
            UiPlugin.Log($"{w.Name}: reloaded, {fresh.Problems.Count} problem(s)");
            return null;
        }

        /// <summary>bbe_ui watch: reload a window when its page or a stylesheet is saved (polled twice a second).</summary>
        public static bool Watch { get; set; }
        private static double nextWatch;

        private static void PollWatch()
        {
            if (!Watch || Timing.TotalTime < nextWatch) { return; }
            nextWatch = Timing.TotalTime + 0.5;
            foreach (var w in All.ToList())
            {
                bool changed;
                try { changed = w.SourcePath != null && w.SourcesChanged(); }
                catch (IOException) { continue; }   // the editor is still writing: next poll
                if (!changed) { continue; }
                string error = Reload(w);
                if (error != null) { UiPlugin.Log(error); w.MarkSourcesSeen(); }
            }
        }

        internal static void Forget(UiWindow w)
        {
            if (windows.Remove(w) | hosted.Remove(w)) { w.Dispose(); }
        }

        internal static void AfterDraw(UiWindow w, SpriteBatch sb) => SelfTest.AfterDraw(w, sb);

        private static SpriteBatch prePassBatch;
        private static bool prePassFailed;

        /// <summary>A prefix on GameMain.Draw: the first thing of the game's frame, before any drawing to the back buffer (the game's Draw only starts a timer
        /// before it draws, BbePipeline.DrawBegin). Never throws into the game: a failure is logged once and the frame goes on with the rectangular clip.</summary>
        private static void FramePrefix()
        {
            try
            {
                var gd = GameMain.Instance?.GraphicsDevice;
                if (gd != null) { PrePass(gd); }
            }
            catch (Exception e)
            {
                if (prePassFailed) { return; }
                prePassFailed = true;
                UiPlugin.Log($"rounded overflow pre-pass failed, the rectangular clip is used: {e}");
            }
        }

        /// <summary>The start of a frame on this device: every open window (free and hosted) renders the children of its rounded overflow blocks into their
        /// targets (UiWindow.PrePass). Allocates nothing when no window has a rounded block; the device's render target, viewport and scissor are the same after.</summary>
        internal static void PrePass(GraphicsDevice gd)
        {
            if (windows.Count == 0 && hosted.Count == 0) { return; }
            prePassBatch ??= new SpriteBatch(gd);
            for (int i = 0; i < windows.Count; i++) { PrePassWindow(windows[i]); }
            for (int i = 0; i < hosted.Count; i++) { PrePassWindow(hosted[i]); }
        }

        private static void PrePassWindow(UiWindow w)
        {
            try { w.PrePass(prePassBatch); }
            catch (Exception e) { w.Fail(e); }
        }

        private static void AddWindowsPrefix()
        {
            try
            {
                SelfTest.Tick();
                fonts?.Sweep(); // gives back fonts made for a scale that nothing draws with any more, before any window draws
                PollWatch();
                NavigateByInput();
                UiFileDrop.Pump(windows, hosted);
                for (int i = 0; i < windows.Count; i++)
                {
                    var w = windows[i];
                    w.AddToGUIUpdateList();
                    if (i < windows.Count && windows[i] != w) { i--; }
                }
            }
            catch (Exception e)
            {
                UiPlugin.Log($"ERROR update list: {e}");
            }
        }

        /// <summary>Esc reached the pause menu: the topmost open free window takes it instead (its top popup or modal dialog first, then the window, see
        /// UiWindow.OnEscape); the game's menu opens only when no free window is open. Closing an open pause menu is left alone.
        /// The topmost is the last one in the list that is open and on screen (fed to the update list in the last 0.5 s: a window left open where it is
        /// not drawn must not keep the game's menu from opening); the list is the order of the update list, which the game draws in. It used to be the
        /// one with the newest ShownAt, but ShownAt is set on every frame a window is fed, so all windows on screen tie and the oldest won: with the BBE
        /// main menu and the mods window open, Esc went to the main menu page, which does not close, and the game's menu opened over the dialog.</summary>
        private static bool PauseMenuPrefix()
        {
            try
            {
                if (GUI.PauseMenuOpen || !PlayerInput.KeyHit(Microsoft.Xna.Framework.Input.Keys.Escape)) { return true; }
                var top = windows.LastOrDefault(w => w.IsOpen && Timing.TotalTime - w.ShownAt < 0.5);
                return top == null || !top.OnEscape();
            }
            catch (Exception e)
            {
                UiPlugin.Log($"ERROR Esc: {e}");
                return true;
            }
        }

        private static bool swallowBack, swallowForward;

        /// <summary>Side button 4 (back) and 5 (forward) over a window, and Alt+Left / Alt+Right: the topmost window gets them (its onback or
        /// onforward handler, then OnBack/OnForward; Core/NavRoute.cs). A side button the window took is swallowed: the game's
        /// Mouse4ButtonClicked/Mouse5ButtonClicked read false for the rest of the frame (SideBackPrefix). A side button with the pointer over no
        /// window is the game's, as before.</summary>
        private static void NavigateByInput()
        {
            swallowBack = swallowForward = false;
            if (windows.Count == 0) { return; }
            var mp = PlayerInput.MousePosition.ToPoint();
            bool side4 = PlayerInput.Mouse4ButtonClicked(), side5 = PlayerInput.Mouse5ButtonClicked();
            if (side4 || side5)
            {
                var over = windows.LastOrDefault(w => w.IsOpen && w.ElementAt(mp) != null);
                if (over != null)
                {
                    var under = over.ElementAt(mp);
                    if (side4 && over.Navigate(true, under)) { swallowBack = true; }
                    if (side5 && over.Navigate(false, under)) { swallowForward = true; }
                }
            }
            if (PlayerInput.IsAltDown())
            {
                bool left = PlayerInput.KeyHit(Microsoft.Xna.Framework.Input.Keys.Left), right = PlayerInput.KeyHit(Microsoft.Xna.Framework.Input.Keys.Right);
                var top = left || right ? windows.LastOrDefault(w => w.IsOpen && Timing.TotalTime - w.ShownAt < 0.5) : null;
                if (top != null) { top.Navigate(left, null); }
            }
        }

        private static bool SideBackPrefix(ref bool __result)
        {
            if (!swallowBack) { return true; }
            __result = false;
            return false;
        }

        private static bool SideForwardPrefix(ref bool __result)
        {
            if (!swallowForward) { return true; }
            __result = false;
            return false;
        }

        private static bool Toggle(string arg, bool current) => arg?.ToLowerInvariant() switch
        {
            "on" or "1" => true,
            "off" or "0" => false,
            _ => !current,
        };

        private static string FindPackageDir()
        {
            var all = ContentPackageManager.EnabledPackages.All.ToList();
            bool HasFont(ContentPackage p) => File.Exists(Path.Combine(p.Dir, "Fonts", "JetBrainsMono-Regular.ttf"));
            var pkg = all.FirstOrDefault(p => (p.Name == "Baro-UI" || p.Name == "BBE UI") && HasFont(p)) ?? all.FirstOrDefault(HasFont);
            return pkg?.Dir;
        }

        private static void OnCommand(string[] args)
        {
            string verb = args.FirstOrDefault()?.ToLowerInvariant();
            string page = args.Length > 1 ? args[1] : null;
            try
            {
                switch (verb)
                {
                    case "open" when page != null:
                        OpenPage(page);
                        break;
                    case "close":
                        foreach (var w in All.Where(w => page == null || w.Name == page).ToList()) { w.Close(); }
                        break;
                    case "reload":
                        foreach (var w in All.Where(w => page == null || w.Name == page).ToList())
                        {
                            DebugConsole.NewMessage(Reload(w) ?? $"{Command}: {w.Name} reloaded, problems: {All.FirstOrDefault(x => x.Name == w.Name)?.Problems.Count ?? 0}");
                        }
                        break;
                    case "watch":
                        Watch = Toggle(page, Watch);
                        UiDebug.Banner = Watch;
                        DebugConsole.NewMessage($"{Command}: watch {(Watch ? "on: saved pages reload, problems show in a banner" : "off")}");
                        break;
                    case "anim" when page == "fps":
                    {
                        string v = args.Length > 2 ? args[2].ToLowerInvariant() : null;
                        if (v == "max") { LayoutFps = 0; }
                        else if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) && f >= 1) { LayoutFps = Math.Min(f, 240); }
                        DebugConsole.NewMessage($"{Command}: lengths animate at {(LayoutFps > 0 ? LayoutFps.ToString("0.#") + " fps" : "every frame")} (an element's layout-fps wins)");
                        break;
                    }
                    case "anim":
                        Animations = Toggle(page, Animations);
                        DebugConsole.NewMessage($"{Command}: animations {(Animations ? "on: transitions, keyframes and typewriter text" : "off: every change is at once, text shows in full")}");
                        break;
                    case "debug":
                        UiDebug.Outlines = Toggle(page, UiDebug.Outlines);
                        DebugConsole.NewMessage($"{Command}: debug {(UiDebug.Outlines ? "on: block outlines, the element under the mouse, problems" : "off")}");
                        break;
                    case "problems":
                        foreach (var w in All.Where(w => page == null || w.Name == page))
                        {
                            DebugConsole.NewMessage($"{w.Name}: {w.Problems.Count} problem(s)");
                            foreach (var d in w.Problems) { DebugConsole.NewMessage("  " + d); }
                        }
                        break;
                    case "src":
                        string dir = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;
                        if (dir == null) { DebugConsole.NewMessage($"{Command}: pages from {PageDir}"); break; }
                        if (dir.Equals("off", StringComparison.OrdinalIgnoreCase)) { SourceOverride = null; }
                        else if (!Directory.Exists(dir)) { DebugConsole.ThrowError($"{Command}: no folder {dir}"); break; }
                        else { SourceOverride = Path.GetFullPath(dir); }
                        DebugConsole.NewMessage($"{Command}: pages from {PageDir} (open windows keep their files until reopened)");
                        break;
                    case "version":
                        DebugConsole.NewMessage($"{Command}: Baro-UI {Version.Major}.{Version.Minor}.{Version.Build} (assembly {UiInfo.AssemblyName}, docs/ui/API.md)");
                        break;
                    case "list":
                    case null:
                        DebugConsole.NewMessage($"{Command}: {(!All.Any() ? "no windows" : string.Join(", ", windows.Select(w => w.Name).Concat(hosted.Select(w => w.Name + " (item)"))))}");
                        break;
                    default:
                        DebugConsole.NewMessage($"Usage: {Command} open <page> | close [page] | reload [page] | list | version | watch [on|off] | debug [on|off] | anim [on|off|fps N|max] | problems [page] | src [dir|off]");
                        break;
                }
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"{Command}: {e.Message}");
            }
        }
    }
}
