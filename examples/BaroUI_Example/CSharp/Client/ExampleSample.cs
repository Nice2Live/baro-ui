namespace BaroUiExample
{
    /// <summary>The sample model of the page, the same numbers as UI/example.data.json (keep the two in step: the page test reads the JSON).</summary>
    public static class ExampleSample
    {
        public static ExampleModel Create()
        {
            var m = new ExampleModel();
            m.Power.Labels.AddRange(new[] { "00", "04", "08", "12", "16", "20", "24", "28", "32", "36", "40", "44" });
            m.Power.Gen.AddRange(new double[] { 40, 42, 44, 47, 50, 52, 55, 53, 50, 48, 46, 44 });
            m.Power.Load.AddRange(new double[] { 30, 35, 38, 44, 46, 49, 58, 61, 50, 47, 42, 39 });
            m.Batteries.Add(new Battery { Name = "Аккумулятор A / Battery A", Pct = 82 });
            m.Batteries.Add(new Battery { Name = "Аккумулятор B / Battery B", Pct = 64 });
            m.Batteries.Add(new Battery { Name = "Аккумулятор C / Battery C", Pct = 23 });
            m.Compartments.Add(new Compartment { Id = "c1", Name = "Нос", En = "Bow", Hull = 98, Crew = 4, State = "ok", StateText = "В норме / Normal" });
            m.Compartments.Add(new Compartment { Id = "c2", Name = "Торпедный", En = "Torpedo", Hull = 91, Crew = 2, State = "ok", StateText = "В норме / Normal" });
            m.Compartments.Add(new Compartment { Id = "c3", Name = "Машинное", En = "Engine", Hull = 74, Crew = 3, State = "warn", StateText = "Течь / Leak" });
            m.Compartments.Add(new Compartment { Id = "c4", Name = "Корма", En = "Stern", Hull = 58, Crew = 1, State = "bad", StateText = "Отсек закрыт / Sealed" });
            m.Compartments.Add(new Compartment { Id = "c5", Name = "Центральный пост", En = "Control room", Hull = 99, Crew = 6, State = "ok", StateText = "В норме / Normal" });
            return m;
        }
    }
}
