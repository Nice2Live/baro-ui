using Barotrauma;

namespace BaroUI.Render
{
    /// <summary>The game's icons a page can show with &lt;item sprite="kind:id"&gt;: a talent (<c>talent:burstofspeed</c>), a faction (<c>faction:coalition</c>), a
    /// mission type of a prefab (<c>mission:salvagemission</c>), a job (<c>job:captain</c>) or an item's icon (<c>item:oxygentank</c>, the same as
    /// <c>&lt;item prefab&gt;</c>). Null when there is no such prefab or it has no icon; the page then shows what it has next to it (initials).</summary>
    internal static class UiSprites
    {
        public static Sprite Find(string key)
        {
            int at = key.IndexOf(':');
            if (at <= 0 || at == key.Length - 1) { return null; }
            string kind = key.Substring(0, at).ToLowerInvariant();
            var id = key.Substring(at + 1).ToIdentifier();
            switch (kind)
            {
                case "talent": return TalentPrefab.TalentPrefabs.TryGet(id, out var talent) ? talent.Icon : null;
                case "faction": return FactionPrefab.Prefabs.TryGet(id, out var faction) ? faction.Icon : null;
                case "mission": return MissionPrefab.Prefabs.TryGet(id, out var mission) ? mission.Icon : null;
                case "job": return JobPrefab.Prefabs.TryGet(id, out var job) ? job.Icon : null;
                case "item": return ItemPrefab.Prefabs.TryGet(id, out var item) ? item.InventoryIcon ?? item.Sprite : null;
                case "affliction": return AfflictionPrefab.Prefabs.TryGet(id, out var affliction) ? affliction.Icon : null;
                case "ui": return UiIcon(id);
            }
            return null;
        }

        /// <summary>A picture of the game's interface (<c>ui:speechbubbleicon</c>, <c>ui:outposticon</c>...): a named sprite of the style files (GUIStyle.Sprites), else the
        /// first picture of a component style (<c>ui:guibuttonsettings</c>). A sliced sprite gives the whole picture, not the nine pieces.</summary>
        private static Sprite UiIcon(Identifier id)
        {
            if (GUIStyle.Sprites.TryGetValue(id, out var named)) { return named.Value?.Sprite; }
            var style = GUIStyle.GetComponentStyle(id);
            if (style == null) { return null; }
            foreach (var list in style.Sprites.Values)
            {
                if (list.Count > 0 && list[0].Sprite != null) { return list[0].Sprite; }
            }
            return null;
        }
    }
}
