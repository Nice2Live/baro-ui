using System;
using System.IO;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using BaroUI;

namespace BaroUiExample
{
    /// <summary>Plugin entry of the example. The first thing it does is ask the engine for its version (BaroUi.Requires): an older Baro-UI
    /// gets one line in the log and no command, so a mod that depends on the engine never opens a broken window.
    /// Console: "baroui_example" opens the page (again: closes the old one first); Esc or the close button closes it.</summary>
    public sealed class ExamplePlugin : IAssemblyPlugin
    {
        public const string Command = "baroui_example";
        public const string ModName = "Baro-UI Example";
        public const string EngineMin = "0.2.0";

        public void PreInitPatching() { }

        public void Initialize()
        {
            if (!BaroUi.Requires(EngineMin, ModName)) { return; }
            DebugConsole.Commands.RemoveAll(c => c.Names.Contains(Command.ToIdentifier()));
            DebugConsole.Commands.Add(new DebugConsole.Command(Command,
                $"{Command}: the Baro-UI example page (a submarine status dashboard); the template for a mod on the Baro-UI engine (own/BaroUI_Example/README.md).",
                OnCommand));
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            DebugConsole.Commands.RemoveAll(c => c.Names.Contains(Command.ToIdentifier()));
            foreach (var w in BaroUi.Windows.Where(w => w.Name == "example").ToList()) { w.Close(); }
        }

        private static void OnCommand(string[] args)
        {
            try
            {
                string dir = PackageDir();
                if (dir == null) { DebugConsole.ThrowError($"{Command}: the package folder is not found (is {ModName} enabled?)"); return; }
                foreach (var w in BaroUi.Windows.Where(w => w.Name == "example").ToList()) { w.Close(); }
                BaroUi.Open(Path.Combine(dir, "UI", "example.html"), new ExampleView(), "example");
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError($"{Command}: {e.Message}");
            }
        }

        private static string PackageDir() => ContentPackageManager.EnabledPackages.All.FirstOrDefault(p => p.Name == ModName)?.Dir;
    }
}
