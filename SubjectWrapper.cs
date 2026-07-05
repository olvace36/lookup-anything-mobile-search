
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
        private readonly string _className;
        private readonly bool _isMonster;

        public object RawSubject => _subject;
        public string Name { get; }
        public string Description { get; }
        public string SubjectType { get; }
        public bool IsValid { get; }

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
            if (_isMonster) return "Monsters";
            if (_className.Contains("CharacterSubject")) return "NPCs";
            if (_className.Contains("ItemSubject")) return "Items";
            if (_className.Contains("BuildingSubject")) return "Buildings";
            if (_className.Contains("CropSubject") || _className.Contains("FruitTreeSubject") || _className.Contains("WildTreeSubject")) return "Crops";
            if (_className.Contains("TerrainFeature") || _className.Contains("BushSubject")) return "Terrain";
            if (_className.Contains("FarmAnimal")) return "Animals";
            return "Other";
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
