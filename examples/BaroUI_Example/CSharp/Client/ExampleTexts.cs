using System.Collections.Generic;

namespace BaroUiExample
{
    /// <summary>The texts of the example page in three languages (ru, en, zh) and the localized sample data. Generated from one table together with
    /// UI/example.data.json (ru), UI/example.en.data.json and UI/example.zh.data.json, so the game and ui_host show the same words: edit all of them together.</summary>
    public static class ExampleTexts
    {
        public static readonly string[] Langs = { "ru", "en", "zh" };

        /// <summary>The index of a language code in Langs (unknown: ru).</summary>
        public static int Index(string lang) { int i = System.Array.IndexOf(Langs, lang); return i < 0 ? 0 : i; }

        /// <summary>The page texts of a language: the page reads them as t.title, t.depth, ...</summary>
        public static Dictionary<string, object> Page(string lang)
        {
            int i = Index(lang);
            var d = new Dictionary<string, object>();
            d["title"] = new[] { "Субмарина «Нарвал»", "Submarine “Narwhal”", "“独角鲸”号潜艇" }[i];
            d["subtitle"] = new[] { "Состояние систем", "Systems status", "系统状态" }[i];
            d["diving"] = new[] { "Погружение", "Diving", "下潜中" }[i];
            d["refuel"] = new[] { "Дозаправка", "Refuel", "补充能源" }[i];
            d["reset"] = new[] { "Сброс", "Reset", "重置" }[i];
            d["close"] = new[] { "Закрыть (Esc)", "Close (Esc)", "关闭（Esc）" }[i];
            d["depth"] = new[] { "Глубина", "Depth", "深度" }[i];
            d["depthSub"] = new[] { "метры, зоны 350 и 550", "metres, zones at 350 and 550", "米，警戒区 350 与 550" }[i];
            d["speed"] = new[] { "Скорость", "Speed", "航速" }[i];
            d["heading"] = new[] { "Курс", "Heading", "航向" }[i];
            d["knots"] = new[] { "уз", "kn", "节" }[i];
            d["power"] = new[] { "Мощность", "Power", "功率" }[i];
            d["powerSub"] = new[] { "кВт по часам", "kW by hour", "千瓦，按小时" }[i];
            d["gen"] = new[] { "Генерация", "Generation", "发电" }[i];
            d["load"] = new[] { "Нагрузка", "Load", "负载" }[i];
            d["batteries"] = new[] { "Аккумуляторы", "Batteries", "蓄电池" }[i];
            d["batteriesSub"] = new[] { "заряд, %", "charge, %", "电量，%" }[i];
            d["range"] = new[] { "запас около 3 ч", "about 3 h left", "续航约 3 小时" }[i];
            d["ballast"] = new[] { "Балласт", "Ballast", "压载" }[i];
            d["ballastSub"] = new[] { "м³ по глубине", "m³ by depth", "按深度的立方米" }[i];
            d["ballastHint"] = new[] { "Тяните точки, двойной щелчок добавляет новую", "Drag the points; double-click adds one", "拖动节点，双击添加新节点" }[i];
            d["controls"] = new[] { "Управление", "Controls", "控制" }[i];
            d["mode"] = new[] { "Режим", "Mode", "模式" }[i];
            d["modeAuto"] = new[] { "Автомат", "Auto", "自动" }[i];
            d["modeManual"] = new[] { "Ручной", "Manual", "手动" }[i];
            d["modeSilent"] = new[] { "Тихий", "Silent", "静默" }[i];
            d["modeDive"] = new[] { "Погружение", "Dive", "下潜" }[i];
            d["trim"] = new[] { "Дифферент", "Trim", "纵倾" }[i];
            d["eco"] = new[] { "Эко", "Eco", "节能" }[i];
            d["normal"] = new[] { "Норма", "Normal", "标准" }[i];
            d["boost"] = new[] { "Буст", "Boost", "加力" }[i];
            d["lights"] = new[] { "Свет", "Lights", "照明" }[i];
            d["sonar"] = new[] { "Сонар", "Sonar", "声呐" }[i];
            d["pump"] = new[] { "Помпа", "Pump", "水泵" }[i];
            d["compartments"] = new[] { "Отсеки", "Compartments", "舱室" }[i];
            d["compartmentsSub"] = new[] { "целостность и экипаж", "hull and crew", "船体与船员" }[i];
            d["colName"] = new[] { "Отсек", "Compartment", "舱室" }[i];
            d["colHull"] = new[] { "Корпус", "Hull", "船体" }[i];
            d["colCrew"] = new[] { "Экипаж", "Crew", "船员" }[i];
            d["colState"] = new[] { "Статус", "Status", "状态" }[i];
            d["hull"] = new[] { "Корпус", "Hull", "船体" }[i];
            d["hullSub"] = new[] { "целостность, %", "integrity, %", "完整度，%" }[i];
            d["hullHint"] = new[] { "Сектор окрашен по зоне значения", "The sector takes the colour of its zone", "扇区颜色随数值所在区间变化" }[i];
            return d;
        }

        public static readonly string[] SubName = { "Нарвал", "Narwhal", "独角鲸" };
        public static readonly (string Id, string[] Name, double Hull, int Crew, string State)[] Compartments =
        {
            ("c1", new[] { "Нос", "Bow", "艏舱" }, 98, 4, "ok"),
            ("c2", new[] { "Торпедный", "Torpedo room", "鱼雷舱" }, 91, 2, "ok"),
            ("c3", new[] { "Машинное", "Engine room", "轮机舱" }, 74, 3, "warn"),
            ("c4", new[] { "Корма", "Stern", "艉舱" }, 58, 1, "bad"),
            ("c5", new[] { "Центральный пост", "Control room", "指挥舱" }, 99, 6, "ok"),
        };
        public static readonly Dictionary<string, string[]> States = new()
        {
            ["ok"] = new[] { "В норме", "Normal", "正常" },
            ["warn"] = new[] { "Течь", "Leak", "漏水" },
            ["bad"] = new[] { "Отсек закрыт", "Sealed", "已封闭" },
        };
        public static readonly (string[] Name, double Pct)[] Batteries =
        {
            (new[] { "Аккумулятор A", "Battery A", "蓄电池 A" }, 82),
            (new[] { "Аккумулятор B", "Battery B", "蓄电池 B" }, 64),
            (new[] { "Аккумулятор C", "Battery C", "蓄电池 C" }, 23),
        };
    }
}
