
using System;
using System.Collections;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace LookupAnythingMobileSearch.Framework
{
    public class SubjectWrapper
    {
        private readonly object _subject;
        private readonly PropertyInfo? _nameProperty;
        private readonly PropertyInfo? _descriptionProperty;
        private readonly PropertyInfo? _typeProperty;
        private readonly MethodInfo? _drawPortraitMethod;
        private readonly MethodInfo? _getDataMethod;
        private readonly PropertyInfo? _targetProperty;
        private readonly string _className;
        private readonly bool _isMonster;

        // Lazily computed the first time something asks for it (usually
        // once, when the result list is grouped/sorted) rather than for
        // every subject up front - calling GetData() is the same work the
        // real lookup page does, so we only want to pay that cost once per
        // subject, not twice.
        private bool? _hasData;
        private string? _subCategoryCache;

        public object RawSubject => _subject;
        public string Name { get; }
        public string Description { get; }
        public string SubjectType { get; }
        public bool IsValid { get; }

        // The unlocalized/internal identifier (NPC.Name, Monster.Name, or
        // the item's qualified id) - always in English/ASCII regardless of
        // game language, used for: mod-origin detection, "copy id", and
        // letting the player search by the English name even when playing
        // in Thai.
        public string InternalName { get; }

        private SubjectWrapper(object subject)
        {
            _subject = subject;
            _className = subject.GetType().FullName ?? "";

            var type = subject.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _nameProperty = type.GetProperty("Name", flags);
            _descriptionProperty = type.GetProperty("Description", flags);
            _typeProperty = type.GetProperty("Type", flags);
            _drawPortraitMethod = type.GetMethod("DrawPortrait", flags);
            _targetProperty = type.GetProperty("Target", flags);
            _getDataMethod = type.GetMethod("GetData", flags, null, Type.EmptyTypes, null);

            // CharacterSubject is used for both villager NPCs and monsters -
            // className alone can't tell them apart. It keeps the actual
            // SubjectType enum value in a private "TargetType" field, which
            // is locale-independent (unlike the translated Type string).
            FieldInfo? targetTypeField = type.GetField("TargetType", flags);
            _isMonster = targetTypeField?.GetValue(subject)?.ToString() == "Monster";

            Name = GetValue<string>(_nameProperty) ?? "Unknown";
            Description = GetValue<string>(_descriptionProperty) ?? "";
            SubjectType = GetValue<string>(_typeProperty) ?? "";
            IsValid = _nameProperty != null;

            InternalName = ComputeInternalName();
        }

        private T? GetValue<T>(PropertyInfo? prop)
        {
            if (prop == null) return default;
            try { return prop.GetValue(_subject) is T v ? v : default; }
            catch { return default; }
        }

        private object? GetTarget()
        {
            if (_targetProperty == null) return null;
            try { return _targetProperty.GetValue(_subject); }
            catch { return null; }
        }

        private string ComputeInternalName()
        {
            object? target = GetTarget();
            try
            {
                if (target is NPC npc) return npc.Name;
                if (target is Item item) return item.QualifiedItemId ?? item.Name ?? Name;
            }
            catch { }
            return Name;
        }

        // Whether this NPC has ever been met (same signal the game's own
        // Social Page and our NpcInfo mod use: friendshipData.ContainsKey).
        // Only meaningful for NPCs - always true for everything else so it
        // never accidentally hides/dims non-NPC entries.
        public bool NpcHasBeenMet()
        {
            if (!_isMonster && GetCategory() == "NPCs")
            {
                try { return Game1.player.friendshipData.ContainsKey(InternalName); }
                catch { return true; }
            }
            return true;
        }

        public bool NpcCanBeRomanced()
        {
            if (GetTarget() is NPC npc)
            {
                try { return npc.datable.Value; } catch { return false; }
            }
            return false;
        }

        // True once we've actually built this subject's real lookup fields
        // and found at least one - a "clone"/leftover duplicate entry (the
        // kind that shows nothing when opened) will come back empty here.
        // Calling GetData() is the same cost as opening the real page, so
        // this is deliberately computed once and cached, not on every draw.
        public bool HasRealData()
        {
            if (_hasData.HasValue) return _hasData.Value;
            if (_getDataMethod == null) { _hasData = true; return true; } // unknown -> don't penalize
            try
            {
                object? result = _getDataMethod.Invoke(_subject, null);
                if (result is IEnumerable enumerable)
                {
                    foreach (var _ in enumerable) { _hasData = true; return true; }
                    _hasData = false;
                    return false;
                }
                _hasData = result != null;
                return _hasData.Value;
            }
            catch
            {
                // Couldn't tell - assume real rather than risk hiding a
                // genuine entry behind a transient reflection error.
                _hasData = true;
                return true;
            }
        }

        // Best-effort guess at whether this entry comes from a mod: mod
        // authors almost universally give custom content an id containing
        // a "." (author-namespaced, e.g. "DN.SnS_Item") or a "_" separator,
        // neither of which vanilla ids ever use. Not perfect, but matches
        // the same heuristic already used and tested in the companion
        // LookupAnythingItemSources mod.
        public bool IsFromMod()
        {
            string id = InternalName;
            return id.Contains('.') || id.Contains('_');
        }

        // A short label naming which mod, if IsFromMod() - just the part
        // before the first '.' or '_', for the "sort by mod" grouping.
        public string ModGroupLabel()
        {
            if (!IsFromMod()) return "Vanilla";
            int dot = InternalName.IndexOf('.');
            int us = InternalName.IndexOf('_');
            int cut = dot >= 0 && (us < 0 || dot < us) ? dot : us;
            return cut > 0 ? InternalName[..cut] : InternalName;
        }

        public bool DrawPortrait(SpriteBatch b, Vector2 position, Vector2 size)
        {
            if (_isMonster && _targetProperty != null)
            {
                try
                {
                    if (_targetProperty.GetValue(_subject) is StardewValley.Character target && target.Sprite != null)
                    {
                        var sprite = target.Sprite;
                        Rectangle sourceRect = sprite.SourceRect;
                        int frameW = sprite.SpriteWidth;
                        int frameH = sprite.SpriteHeight;
                        if (target is StardewValley.Monsters.GreenSlime)
                        {
                            sourceRect = new Rectangle(32, 120, 16, 24);
                            frameW = 16;
                            frameH = 24;
                        }
                        float scale = Math.Min(size.X / frameW, size.Y / frameH);
                        float drawWidth = frameW * scale;
                        float drawHeight = frameH * scale;
                        var centeredPos = new Vector2(
                                position.X + (size.X - drawWidth) / 2f,
                                position.Y + (size.Y - drawHeight) / 2f);
                        b.Draw(sprite.Texture, centeredPos, sourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                        return true;
                    }
                }
                catch { }
            }

            if (_drawPortraitMethod == null) return false;
            try { _drawPortraitMethod.Invoke(_subject, new object[] { b, position, size }); return true; }
            catch { return false; }
        }

        public string GetCategory()
        {
            if (_isMonster) return "Monsters";
            if (_className.Contains("CharacterSubject")) return "NPCs";
            if (_className.Contains("ItemSubject")) return "Items";
            if (_className.Contains("BuildingSubject")) return "Buildings";
            if (_className.Contains("CropSubject") || _className.Contains("FruitTreeSubject") || _className.Contains("WildTreeSubject")) return "Crops";
            if (_className.Contains("TerrainFeature") || _className.Contains("BushSubject")) return "Terrain";
            if (_className.Contains("FarmAnimal")) return "Animals";
            return "Other";
        }

        // Sub-category within the main category, or "" if this category
        // doesn't have a meaningful split. Computed once and cached since
        // it may involve a bit of string work.
        public string GetSubCategory()
        {
            if (_subCategoryCache != null) return _subCategoryCache;
            string cat = GetCategory();
            string result = cat switch
            {
                "Items" => StripParenthetical(SubjectType),
                "NPCs" => NpcCanBeRomanced() ? "Romanceable" : "Not romanceable",
                "Monsters" => IsFromMod() ? "Mod" : "Vanilla",
                "Buildings" => IsBuildableBuilding() ? "Buildable" : "Other",
                _ => "",
            };
            _subCategoryCache = result;
            return result;
        }

        private bool IsBuildableBuilding()
        {
            // Player-constructable buildings are the ones listed in the
            // carpenter/wizard build menu, which Data/Buildings marks with
            // a non-empty Builder field (who offers to build it for you).
            // Loaded as Dictionary<string, object> (not a direct reference
            // to StardewValley.GameData.Buildings.BuildingData) since this
            // project's referenced game assembly may not expose that
            // namespace; the Builder field is then read via reflection on
            // whatever concrete type comes back, avoiding a hard
            // compile-time dependency on a type we can't verify here.
            try
            {
                var data = Game1.content.Load<System.Collections.Generic.Dictionary<string, object>>("Data/Buildings");
                if (data != null && data.TryGetValue(InternalName, out object? buildingData) && buildingData != null)
                {
                    var builderProp = buildingData.GetType().GetProperty("Builder");
                    string? builder = builderProp?.GetValue(buildingData) as string;
                    return !string.IsNullOrWhiteSpace(builder);
                }
            }
            catch { }
            return false;
        }

        private static string StripParenthetical(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOf('(');
            return i > 0 ? s[..i].Trim() : s.Trim();
        }

        public static SubjectWrapper? Create(object? subject)
        {
            if (subject == null) return null;
            try
            {
                var w = new SubjectWrapper(subject);
                return w.IsValid ? w : null;
            }
            catch { return null; }
        }
    }
}
