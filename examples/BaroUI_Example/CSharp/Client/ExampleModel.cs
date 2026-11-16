using System.Collections.Generic;

namespace BaroUiExample
{
    /// <summary>The page's model: the same shape as UI/example.data.json, so ui_host --data and the game read one thing. Plain data:
    /// the page binds to the names (case does not matter: sub.depth, batteries[0].pct). Change a value in C#, then call Refresh.</summary>
    public sealed class ExampleModel
    {
        public SubInfo Sub { get; set; } = new SubInfo();
        public PowerInfo Power { get; set; } = new PowerInfo();
        public List<Battery> Batteries { get; set; } = new List<Battery>();
        /// <summary>The ballast curve as text: "x:y, x:y" (the <curve> element reads and writes this form).</summary>
        public string Ballast { get; set; } = "0:0.4, 75:1.1, 150:1.9, 225:2.5, 300:2.9";
        public List<Compartment> Compartments { get; set; } = new List<Compartment>();
        public SystemsInfo Systems { get; set; } = new SystemsInfo();
        public string Trim { get; set; } = "normal";
        public string Mode { get; set; } = "auto";
    }

    public sealed class SubInfo
    {
        public string Name { get; set; } = "Нарвал / Narwhal";
        public double Depth { get; set; } = 212;
        public double Speed { get; set; } = 4.5;
        public double Hull { get; set; } = 96;
    }

    /// <summary>Two series of one chart (generation and load, per hour): the chart reads the lists by path (power.gen).</summary>
    public sealed class PowerInfo
    {
        public List<string> Labels { get; set; } = new List<string>();
        public List<double> Gen { get; set; } = new List<double>();
        public List<double> Load { get; set; } = new List<double>();
    }

    public sealed class Battery
    {
        public string Name { get; set; } = "";
        public double Pct { get; set; }
    }

    public sealed class Compartment
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string En { get; set; } = "";
        public double Hull { get; set; }
        public int Crew { get; set; }
        /// <summary>ok, warn or bad: the row's pill and its class follow it.</summary>
        public string State { get; set; } = "ok";
        public string StateText { get; set; } = "";
    }

    public sealed class SystemsInfo
    {
        public bool Lights { get; set; } = true;
        public bool Sonar { get; set; } = true;
        public bool Pump { get; set; }
    }
}
