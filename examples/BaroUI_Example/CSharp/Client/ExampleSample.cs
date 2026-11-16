namespace BaroUiExample
{
    /// <summary>The sample model of the page in one language, the same numbers and words as UI/example[.en|.zh].data.json (ExampleTexts holds the words;
    /// the page test reads the JSON files).</summary>
    public static class ExampleSample
    {
        public static ExampleModel Create(string lang = "ru")
        {
            int i = ExampleTexts.Index(lang);
            var m = new ExampleModel { Lang = ExampleTexts.Langs[i], T = ExampleTexts.Page(lang) };
            m.Sub.Name = ExampleTexts.SubName[i];
            m.Power.Labels.AddRange(new[] { "00", "04", "08", "12", "16", "20", "24", "28", "32", "36", "40", "44" });
            m.Power.Gen.AddRange(new double[] { 40, 42, 44, 47, 50, 52, 55, 53, 50, 48, 46, 44 });
            m.Power.Load.AddRange(new double[] { 30, 35, 38, 44, 46, 49, 58, 61, 50, 47, 42, 39 });
            foreach (var (name, pct) in ExampleTexts.Batteries) { m.Batteries.Add(new Battery { Name = name[i], Pct = pct }); }
            foreach (var (id, name, hull, crew, state) in ExampleTexts.Compartments)
            {
                m.Compartments.Add(new Compartment { Id = id, Name = name[i], Hull = hull, Crew = crew, State = state, StateText = ExampleTexts.States[state][i] });
            }
            return m;
        }

        /// <summary>The words of a model in another language; the numbers the player changed (batteries, ballast, speed, switches) stay.</summary>
        public static void Translate(ExampleModel m, string lang)
        {
            int i = ExampleTexts.Index(lang);
            m.Lang = ExampleTexts.Langs[i];
            m.T = ExampleTexts.Page(lang);
            m.Sub.Name = ExampleTexts.SubName[i];
            for (int k = 0; k < m.Batteries.Count && k < ExampleTexts.Batteries.Length; k++) { m.Batteries[k].Name = ExampleTexts.Batteries[k].Name[i]; }
            foreach (var c in m.Compartments)
            {
                foreach (var (id, name, _, _, _) in ExampleTexts.Compartments) { if (id == c.Id) { c.Name = name[i]; } }
                if (ExampleTexts.States.TryGetValue(c.State, out var st)) { c.StateText = st[i]; }
            }
        }
    }
}
