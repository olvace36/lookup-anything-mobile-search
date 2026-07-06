
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
        private readonly PropertyInfo? _targetProperty;
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
            _targetProperty = type.GetProperty("Target", flags);

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
            // Lookup Anything's own DrawPortrait scales monsters by
            // (size.X / frameWidth) only, assuming a roughly square frame.
            // Most monster frames are taller than wide (e.g. 16x32), so at
            // our square icon size that overflows below the box. Draw it
            // ourselves instead, scaling to fit both dimensions.
            if (_isMonster && _targetProperty != null)
            {
                try
                {
                    if (_targetProperty.GetValue(_subject) is StardewValley.Character target && target.Sprite != null)
                    {
                        var sprite = target.Sprite;
                        // GreenSlime (and its recolored family - Frost Jelly,
                        // Sludge, Tiger Slime) doesn't use the generic sprite
                        // frame system at all - it has its own custom draw()
                        // with a fixed, hand-picked source rect for its
                        // resting pose. Using the generic frame math for it
                        // slices the wrong part of the texture entirely.
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
                        // Center within the requested box so narrower/shorter
                        // sprites don't hug one corner.
                        var centeredPos = new Vector2(
                                position.X + (size.X - drawWidth) / 2f,
                                position.Y + (size.Y - drawHeight) / 2f);
                        b.Draw(sprite.Texture, centeredPos, sourceRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                        return true;
                    }
                }
                catch { /* fall through to the default method below */ }
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
