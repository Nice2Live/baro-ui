using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Barotrauma;
using BaroUI.Core;
using Microsoft.Xna.Framework;

namespace BaroUI.Render
{
    public enum FileInputSource
    {
        /// <summary>A file dropped on the game window from Explorer.</summary>
        Drop,
        /// <summary>Ctrl+V with files copied in Explorer on the clipboard.</summary>
        PasteFile,
        /// <summary>Ctrl+V with a text that names a file (a path, "Copy as path").</summary>
        PastePath,
        /// <summary>Ctrl+V with a picture on the clipboard (a screenshot, a copied image): ImageData, no Path.</summary>
        PasteImage,
    }

    /// <summary>One file (or picture) that reached a target of the page (UiWindow.SetFileInput).</summary>
    public sealed class UiFileInput
    {
        public FileInputSource Source { get; }
        /// <summary>The file; null for a pasted picture.</summary>
        public string Path { get; }
        /// <summary>The encoded picture of a pasted picture (PNG, or BMP when only a bitmap was on the clipboard); null for files.</summary>
        public byte[] ImageData { get; }
        /// <summary>The element of the page that took it (the registered one, the dropped-on element or one of its ancestors).</summary>
        public Element Target { get; }
        /// <summary>Where the file was dropped on the screen; for a paste, where the mouse is.</summary>
        public Point Position { get; }

        internal UiFileInput(FileInputSource source, string path, byte[] image, Element target, Point position)
        {
            Source = source; Path = path; ImageData = image; Target = target; Position = position;
        }

        /// <summary>The lower-case extension with its dot; ".png" for a pasted picture.</summary>
        public string Extension => Path != null ? FileInput.Extension(Path) : ImageData != null && ImageData.Length > 1 && ImageData[0] == 'B' && ImageData[1] == 'M' ? ".bmp" : ".png";
        public bool IsImage => ImageData != null || FileInput.IsImage(Path);
        public string Name => Path != null ? System.IO.Path.GetFileName(Path) : "pasted-image" + Extension;

        /// <summary>The bytes: of the file, or of the pasted picture. Throws what reading a file throws.</summary>
        public byte[] ReadBytes() => ImageData ?? File.ReadAllBytes(Path);

        public override string ToString() => $"{Source} {Path ?? Name}";
    }

    /// <summary>The file targets of one window: elements (by id) that take dropped files and pasted files, paths and pictures.</summary>
    internal sealed class UiFileTargets
    {
        private sealed class Target
        {
            public Func<UiFileInput, bool> Handler;
            public string Accept;
        }

        private readonly UiWindow w;
        private readonly Dictionary<string, Target> targets = new(StringComparer.Ordinal);

        internal UiFileTargets(UiWindow window) { w = window; }

        public int Count => targets.Count;

        public void Set(string id, Func<UiFileInput, bool> handler, string accept)
        {
            if (id == null) { return; }
            if (handler == null) { targets.Remove(id); return; }
            targets[id] = new Target { Handler = handler, Accept = accept };
            UiFileDrop.Hook();
        }

        /// <summary>The element, from this one up, that is a registered target.</summary>
        public Element TargetOf(Element e)
        {
            if (targets.Count == 0) { return null; }
            for (; e != null; e = e.Parent) { if (e.Id != null && targets.ContainsKey(e.Id)) { return e; } }
            return null;
        }

        /// <summary>A file dropped at a screen point. Over: the point is on this window (nothing under it gets the file); Handled: a target took it.</summary>
        public (bool Over, bool Handled) Drop(string path, Point at)
        {
            var hit = w.ElementAt(at);
            if (hit == null) { return (false, false); }
            for (var e = hit; e != null; e = e.Parent)
            {
                if (e.Id == null || !targets.TryGetValue(e.Id, out var t) || UiWindow.IsDisabled(e) || !FileInput.Accepts(t.Accept, path)) { continue; }
                if (Run(e, t, new UiFileInput(FileInputSource.Drop, path, null, e, at))) { return (true, true); }
            }
            return (true, false);
        }

        /// <summary>Ctrl+V on the focused element (or inside a target): files, then a picture, then a text that names a file. True: a target took something.</summary>
        public bool Paste(Element focus)
        {
            var el = TargetOf(focus);
            if (el == null || !targets.TryGetValue(el.Id, out var t) || UiWindow.IsDisabled(el)) { return false; }
            var at = PlayerInput.MousePosition.ToPoint();
            foreach (string file in UiClipboard.Files())
            {
                if (!FileInput.Accepts(t.Accept, file)) { continue; }
                if (Run(el, t, new UiFileInput(FileInputSource.PasteFile, file, null, el, at))) { return true; }
            }
            if (FileInput.Accepts(t.Accept, ".png") && UiClipboard.Image() is { } image
                && Run(el, t, new UiFileInput(FileInputSource.PasteImage, null, image, el, at))) { return true; }
            foreach (string file in FileInput.PathsFromText(Clipboard.GetText(), File.Exists))
            {
                if (!FileInput.Accepts(t.Accept, file)) { continue; }
                if (Run(el, t, new UiFileInput(FileInputSource.PastePath, file, null, el, at))) { return true; }
            }
            return false;
        }

        private bool Run(Element e, Target t, UiFileInput input)
        {
            try { return t.Handler(input); }
            catch (Exception ex)
            {
                UiPlugin.Log($"{w.Name}: file input handler of '{e.Id}' failed: {ex}");
                return false;
            }
        }
    }

    /// <summary>Files dropped on the game window (SDL's drop event through MonoGame's Window.FileDropped): queued where they arrive, handed to the
    /// newest window that has the point under it, once a frame, from the window update. The event carries no point and there is no event while the file
    /// is still being dragged, so the point is where the mouse is when the file lands and a target cannot light up before it.</summary>
    internal static class UiFileDrop
    {
        private static readonly object Gate = new();
        private static readonly List<(string Path, Point At)> Queue = new();
        private static bool hooked;
        private static EventHandler<FileDropEventArgs> handler;

        public static void Hook()
        {
            if (hooked) { return; }
            try
            {
                handler = OnDropped;
                GameMain.Instance.Window.FileDropped += handler;
                hooked = true;
            }
            catch (Exception ex) { UiPlugin.Log($"file drop: cannot subscribe to the window's FileDropped: {ex.Message}"); }
        }

        public static void Unhook()
        {
            if (!hooked) { return; }
            try { GameMain.Instance.Window.FileDropped -= handler; } catch (Exception) { }
            hooked = false;
            lock (Gate) { Queue.Clear(); }
        }

        private static void OnDropped(object sender, FileDropEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.FilePath)) { return; }
            var at = LiveMouse().ToPoint();
            lock (Gate) { Queue.Add((args.FilePath, at)); }
        }

        private static Func<Vector2> liveMouse;

        /// <summary>Where the cursor is when the file arrives. The BBE fork of LuaCs has PlayerInput.BbeLiveMousePosition (asked of the OS: the drop may come between two polls of a busy main thread);
        /// stock LuaCs has not, and the engine must build there too, so it is looked up once by name, and without it the game's last polled position is used.</summary>
        private static Vector2 LiveMouse()
        {
            if (liveMouse == null)
            {
                var prop = typeof(PlayerInput).GetProperty("BbeLiveMousePosition", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                liveMouse = prop?.PropertyType == typeof(Vector2) && prop.GetMethod != null
                    ? (Func<Vector2>)Delegate.CreateDelegate(typeof(Func<Vector2>), prop.GetMethod)
                    : () => PlayerInput.LatestMousePosition;
            }
            return liveMouse();
        }

        /// <summary>Hands what arrived to the windows (BaroUi's per-frame hook). Cheap when nothing arrived.</summary>
        public static void Pump(IReadOnlyList<UiWindow> windows, IReadOnlyList<UiWindow> hosted)
        {
            if (!hooked) { return; }
            (string Path, Point At)[] pending;
            lock (Gate)
            {
                if (Queue.Count == 0) { return; }
                pending = Queue.ToArray();
                Queue.Clear();
            }
            var open = windows.Concat(hosted).Where(x => x.IsOpen && x.FileTargets.Count > 0).OrderByDescending(x => x.ShownAt).ToList();
            foreach (var (path, at) in pending)
            {
                foreach (var win in open)
                {
                    var (over, handled) = win.FileTargets.Drop(path, at);
                    if (over || handled) { break; }
                }
            }
        }
    }

    /// <summary>What the clipboard holds besides text: files copied in Explorer, a picture. Windows only (user32, shell32); anywhere else, or when the
    /// clipboard is busy, nothing.</summary>
    internal static class UiClipboard
    {
        private const uint CfDib = 8, CfHDrop = 15;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr drop, uint index, StringBuilder file, uint capacity);

        private static bool Open()
        {
            for (int i = 0; i < 4; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) { return true; }
                System.Threading.Thread.Sleep(5);
            }
            return false;
        }

        /// <summary>The files on the clipboard (copied in Explorer), existing ones only.</summary>
        public static List<string> Files()
        {
            var result = new List<string>();
            try
            {
                if (!IsClipboardFormatAvailable(CfHDrop) || !Open()) { return result; }
                try
                {
                    var drop = GetClipboardData(CfHDrop);
                    if (drop == IntPtr.Zero) { return result; }
                    uint count = DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
                    for (uint i = 0; i < count && i < 64; i++)
                    {
                        uint length = DragQueryFileW(drop, i, null, 0);
                        if (length == 0 || length > 32767) { continue; }
                        var sb = new StringBuilder((int)length + 1);
                        DragQueryFileW(drop, i, sb, length + 1);
                        string path = sb.ToString();
                        if (File.Exists(path)) { result.Add(path); }
                    }
                }
                finally { CloseClipboard(); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { }
            return result;
        }

        /// <summary>A picture on the clipboard: the PNG some programs put there, else the bitmap wrapped as a .bmp file. Null when there is none.</summary>
        public static byte[] Image()
        {
            try
            {
                uint png = RegisterClipboardFormatW("PNG");
                bool hasPng = png != 0 && IsClipboardFormatAvailable(png);
                if (!hasPng && !IsClipboardFormatAvailable(CfDib)) { return null; }
                if (!Open()) { return null; }
                try
                {
                    if (hasPng && Copy(png) is { Length: > 8 } p) { return p; }
                    return FileInput.DibToBmp(Copy(CfDib));
                }
                finally { CloseClipboard(); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return null; }
        }

        private static byte[] Copy(uint format)
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) { return null; }
            long size = (long)GlobalSize(handle);
            if (size <= 0 || size > 256L * 1024 * 1024) { return null; }
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) { return null; }
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(ptr, bytes, 0, (int)size);
                return bytes;
            }
            finally { GlobalUnlock(handle); }
        }
    }
}
