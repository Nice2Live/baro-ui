using BaroUI;
using BaroUI.Core;

namespace BaroUiExample
{
    /// <summary>The C# side of the example page (UI/example.html): the view. Its [UiHandler] methods are what the markup calls
    /// (onclick="refuel()"). The model is local here, so the handler changes Data and calls Refresh. A real mod that changes game
    /// state (buys, crafts, sets a value on the server) sends a request with Send(source, action) and updates Data when the answer
    /// comes back: docs/ui/AUTHORING.md, "Изменения игры идут через сервер".</summary>
    public sealed class ExampleView : BaroUiView
    {
        private ExampleModel model;

        /// <summary>The model the window shows (its Data). Set before the first draw, in OnOpen.</summary>
        public ExampleModel Model => model;

        protected override void OnOpen()
        {
            model = ExampleSample.Create();
            Data = model;
        }

        /// <summary>Fills every battery to 100 %: the model changes here, the page follows through Refresh.</summary>
        [UiHandler]
        private void refuel()
        {
            foreach (var b in model.Batteries) { b.Pct = 100; }
            Refresh();
        }

        /// <summary>Shows the sample again (the reset button of the page), in the language shown now.</summary>
        [UiHandler]
        private void reset()
        {
            model = ExampleSample.Create(model?.Lang ?? "ru");
            Data = model;
        }

        /// <summary>The language switch of the header: ru, en or zh. Only the words change; the values the player set stay.</summary>
        [UiHandler]
        private void lang(string code)
        {
            ExampleSample.Translate(model, code);
            Refresh();
        }
    }
}
