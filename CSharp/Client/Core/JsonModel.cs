using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BaroUI.Core
{
    /// <summary>A page's model from JSON: the object as the page's data-model paths read it. Objects are dictionaries by key, arrays are lists, numbers
    /// are doubles (the game's Expr reads numbers so), strings and booleans as they are, null as null. Shared by the game (BaroUi.OpenPage reads
    /// UI/&lt;page&gt;.data.json when no view gives the window a model) and the host outside the game (tools/ui_host --data).</summary>
    internal static class JsonModel
    {
        /// <summary>Reads the file; the root must be an object.</summary>
        public static Dictionary<string, object> Load(string path) => Parse(File.ReadAllText(path));

        /// <summary>Parses the text; the root must be an object. Throws FormatException (or JsonException, a FormatException too) when it is not.</summary>
        public static Dictionary<string, object> Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { throw new FormatException("the model must be a JSON object"); }
            return (Dictionary<string, object>)Convert(doc.RootElement);
        }

        private static object Convert(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var d = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var p in e.EnumerateObject()) { d[p.Name] = Convert(p.Value); }
                    return d;
                }
                case JsonValueKind.Array:
                {
                    var list = new List<object>();
                    foreach (var item in e.EnumerateArray()) { list.Add(Convert(item)); }
                    return list;
                }
                case JsonValueKind.Number: return e.GetDouble();
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
