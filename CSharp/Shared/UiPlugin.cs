using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;

namespace BaroUI
{
    /// <summary>BBE UI engine entry (docs/ui/ENGINE_PLAN.md). Client: BaroUi (windows, console, self-test); stage 0 probe by flag.</summary>
    public sealed class UiPlugin : IAssemblyPlugin
    {
        public void PreInitPatching() { }

        public void Initialize()
        {
            Log($"loaded, assembly {UiInfo.AssemblyName}");
            UiEcho.Init();
#if CLIENT
            Probe.Init();
            BaroUi.Init();
#endif
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
#if CLIENT
            BaroUi.Dispose();
            Probe.Dispose();
#endif
        }

        internal static void Log(string text) => LuaCsSetup.PrintCsMessage($"[Baro-UI] {text}");
    }

    /// <summary>Public surface other packages compile against; Ping is a call, not a constant, so the reference is
    /// resolved at run time too.</summary>
    public static class UiInfo
    {
        public const int Version = 0;
        public static string AssemblyName => typeof(UiInfo).Assembly.GetName().Name;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Ping(string caller) => $"{caller} -> {AssemblyName} v{Version}";
    }
}
