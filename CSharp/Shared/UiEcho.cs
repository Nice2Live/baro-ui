using System;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;

namespace BaroUI
{
    /// <summary>Round trip for the self-test (docs/ui/ENGINE_PLAN.md, stage 4 check): the client sends a string,
    /// the server sends it straight back to that client. Single player answers locally. Carries nothing else.</summary>
    internal static class UiEcho
    {
        private const string ToServer = "bbe_ui_echo", ToClient = "bbe_ui_echo_back";
        private const int MaxLength = 64;
        public static Action<string> Received;

        public static void Init()
        {
            var net = LuaCsSetup.Instance.Networking;
#if SERVER
            net.Receive(ToServer, new NetMessageReceived((msg, client) =>
            {
                string text = msg.ReadString();
                if (client?.Connection == null || text.Length > MaxLength) { return; }
                var reply = net.Start(ToClient);
                reply.WriteString(text);
                net.SendToClient(reply, client.Connection);
            }));
#else
            net.Receive(ToClient, new NetMessageReceived(msg => Received?.Invoke(msg.ReadString())));
#endif
        }

        public static void Send(string text)
        {
#if CLIENT
            if (GameMain.Client == null) { Received?.Invoke(text); return; }
            var net = LuaCsSetup.Instance.Networking;
            var msg = net.Start(ToServer);
            msg.WriteString(text);
            net.SendToServer(msg);
#endif
        }
    }
}
