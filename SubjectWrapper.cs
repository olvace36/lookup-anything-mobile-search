using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LookupAnythingMobileSearch.Framework
{
    public class SubjectWrapper
    {
        private readonly object _subject;
        private readonly PropertyInfo? _nameProperty;
        private readonly PropertyInfo? _descriptionProperty;
        private readonly PropertyInfo? _typeProperty;
        private readonly MethodInfo? _drawPortraitMethod;

        public object RawSubject => _subject;
        public string Name { get; }
        public string Description { get; }
        public string SubjectType { get; }
        public bool IsValid { get; }

        private SubjectWrapper(object subject)
        {
            _subject = subject;
            var type = subject.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _nameProperty = type.GetProperty("Name", flags);
            _descriptionProperty = type.GetProperty("Description", flags);
            _typeProperty = type.GetProperty("Type", flags);
            _drawPortraitMethod = type.GetMethod("DrawPortrait", flags);

            Name = GetValue<string>(_nameProperty) ?? "Unknown";
            Description = GetValue<string>(_descriptionProperty) ?? "";
            SubjectType = GetValue<string>(_typeProperty) ?? "Other";
            IsValid = _nameProperty != null;
        }

        private T? GetValue<T>(PropertyInfo? prop)
        {
            if (prop == null) return default;
            try { return prop.GetValue(_subject) is T v ? v : default; }
            catch { return default; }
        }

        public bool DrawPortrait(SpriteBatch b, Vector2 position, Vector2 size)
        {
            if (_drawPortraitMethod == null) return false;
            try { _drawPortraitMethod.Invoke(_subject, new object[] { b, position, size }); return true; }
            catch { return false; }
        }

        public string GetCategory()
        {
            return SubjectType?.ToLowerInvariant() switch
            {
                "villager" or "npc" or "pet" or "character" or "child" or "player" or "farmer" => "NPCs",
                "clothing" or "hat" or "boots" or "object" or "weapon" or "item" or "tool" or "ring" => "Items",
                "fruittree" or "crop" or "tree" or "bush" => "Crops",
                "building" or "structure" => "Buildings",
                "terrainfeature" or "resourceclump" => "Terrain",
                "creature" or "monster" => "Monsters",
                "animal" or "farmanimals" => "Animals",
                _ => "Other"
            };
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
