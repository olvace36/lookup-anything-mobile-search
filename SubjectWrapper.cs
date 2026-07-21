
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LookupAnythingMobileSearch;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
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
        private readonly FieldInfo? _targetField;
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
            _targetField = type.GetField("Target", flags);
            _targetProperty = _targetField == null ? type.GetProperty("Target", flags) : null;
            _altTargetProperty = (_targetField == null && _targetProperty == null) ? type.GetProperty("Item", flags) : null;
            _altTargetField = (_targetField == null && _targetProperty == null && _altTargetProperty == null) ? type.GetField("Item", flags) : null;
            // GetData method resolution removed - no longer needed now
            // that the "·" name-marker check handles clone detection on
            // its own (see HasRealData below); this also skips a
            // reflection lookup for every single subject at construction.

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
            if (_targetField != null)
            {
                try { return _targetField.GetValue(_subject); }
                catch { return null; }
            }
            if (_targetProperty != null)
            {
                try { return _targetProperty.GetValue(_subject); }
                catch { return null; }
            }
            if (_altTargetProperty != null)
            {
                try { return _altTargetProperty.GetValue(_subject); }
                catch { return null; }
            }
            if (_altTargetField != null)
            {
                try { return _altTargetField.GetValue(_subject); }
                catch { return null; }
            }
            if (!_loggedNoTargetProperty)
            {
                _loggedNoTargetProperty = true;
                ModEntry.SMonitor?.Log($"[SubjectWrapper] No 'Target'/'Item' field or property found on {_subject.GetType().FullName} - "
                        + "item sub-category classification and internal-name resolution are disabled for this subject type.",
                        LogLevel.Debug);
            }
            return null;
        }
        private readonly PropertyInfo? _altTargetProperty;
        private readonly FieldInfo? _altTargetField;
        private static bool _loggedNoTargetProperty;

        private bool _internalNameFromTarget;

        private string ComputeInternalName()
        {
            object? target = GetTarget();
            try
            {
                if (target is NPC npc) { _internalNameFromTarget = true; return npc.Name; }
                if (target is Item item) { _internalNameFromTarget = true; return item.QualifiedItemId ?? item.Name ?? Name; }
            }
            catch { }
            _internalNameFromTarget = false;
            return Name;
        }

        // Whether this NPC has ever been met (same signal the game's own
        // Social Page and our NpcInfo mod use: friendshipData.ContainsKey).
        // Only meaningful for NPCs - always true for everything else so it
        // never accidentally hides/dims non-NPC entries.
        private static readonly HashSet<string> _loggedMetIssues = new();

        public bool NpcHasBeenMet()
        {
            if (_isMonster || GetCategory() != "NPCs") return true;

            try
            {
                if (Game1.player.friendshipData.ContainsKey(InternalName)) return true;

                // If InternalName didn't come from the real NPC target
                // (Target reflection failed), it's the TRANSLATED display
                // name instead of the true internal key - friendshipData
                // is always keyed by the internal name, so that lookup
                // would incorrectly report "not met" for every such NPC.
                // Log this once per unique name so a real cause can be
                // pinned down instead of guessed at again.
                if (!_internalNameFromTarget && _loggedMetIssues.Add(InternalName))
                {
                    ModEntry.SMonitor?.Log($"[SubjectWrapper] Couldn't resolve the real internal name for NPC '{Name}' "
                            + $"(got '{InternalName}' from the translated Name instead) - met-status check may be wrong for this one.",
                            LogLevel.Debug);
                }

                // Fallback: case-insensitive match, in case of a casing
                // mismatch between the two systems.
                foreach (string key in Game1.player.friendshipData.Keys)
                {
                    if (string.Equals(key, InternalName, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return true; }
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
        private static MethodInfo? ResolveGetDataMethod(Type type, BindingFlags flags)
        {
            // Try the exact no-arg overload first (fast path).
            var exact = type.GetMethod("GetData", flags, null, Type.EmptyTypes, null);
            if (exact != null) return exact;

            // Fall back to any method named "GetData" whose parameters are
            // all optional - some subject classes may expose it with a
            // default-valued parameter instead of a bare parameterless
            // overload, which the exact lookup above would miss.
            foreach (var m in type.GetMethods(flags))
            {
                if (m.Name != "GetData") continue;
                if (m.GetParameters().All(p => p.IsOptional)) return m;
            }
            return null;
        }

        private static bool _loggedMissingGetData;

        public bool HasRealData()
        {
            if (_hasData.HasValue) return _hasData.Value;

            // Reliable signal confirmed directly from log output: Lookup
            // Anything appends one or more "·" (middle dot, U+00B7) to a
            // subject's Name to disambiguate duplicate/leftover
            // registrations of the same underlying NPC/item (e.g.
            // "Abigail", "Abigail·", "Abigail··"). This check alone is
            // reliable enough on its own - no need to also call GetData()
            // via reflection (which actually builds the subject's full
            // lookup page) just to test for real data. That was wasted,
            // expensive work on literally every single subject in the
            // list (thousands of items) and was very likely the biggest
            // single cause of the menu feeling slow/laggy to open.
            _hasData = !Name.EndsWith('\u00B7');
            return _hasData.Value;
        }

        // Best-effort guess at whether this entry comes from a mod: mod
        // authors almost universally give custom content an id containing
        // a "." (author-namespaced, e.g. "DN.SnS_Item") or a "_" separator,
        // neither of which vanilla ids ever use. Not perfect, but matches
        // the same heuristic already used and tested in the companion
        // LookupAnythingItemSources mod.
        // The general dot/underscore heuristic misses NPCs and monsters
        // added by mods that set a plain, human-readable internal name
        // with no namespace prefix at all (confirmed directly from earlier
        // testing: e.g. Sword & Sorcery's "Cirrus"/"Dandelion"/"Roslin"
        // and its "Stygium ..." monster family use plain names). Same fix
        // as Buildings: check against a known vanilla roster instead, and
        // treat anything else as modded.
        private static readonly HashSet<string> VanillaNpcs = new(StringComparer.OrdinalIgnoreCase)
        {
            "Alex", "Elliott", "Harvey", "Sam", "Sebastian", "Shane",
            "Emily", "Haley", "Leah", "Maru", "Penny", "Abigail", "Caroline",
            "Clint", "Demetrius", "Dwarf", "Evelyn", "George", "Gus", "Jas",
            "Jodi", "Kent", "Krobus", "Leo", "Lewis", "Linus", "Marnie",
            "Mister Qi", "Pam", "Pierre", "Robin", "Sandy", "Vincent",
            "Willy", "Wizard", "Gunther", "Marlon", "Morris", "Governor",
            "Bouncer", "Grandpa", "Henchman", "Birdie", "Auctioneer",
            "Old Mariner", "Welwick", "Merchant", "Bear", "Bat", "Fizz",
        };

        private static readonly HashSet<string> VanillaMonsters = new(StringComparer.OrdinalIgnoreCase)
        {
            "Green Slime", "Frost Jelly", "Sludge", "Tiger Slime", "Big Slime",
            "Bat", "Frost Bat", "Lava Bat", "Iridium Bat", "Haunted Skull",
            "Bug", "Assassin Bug", "Armored Bug", "Duggy", "Magma Duggy",
            "Rock Crab", "Lava Crab", "Iridium Crab", "Stick Bug", "Grub",
            "Fly", "Mutant Grub", "Mutant Fly", "Stone Golem", "Wilderness Golem",
            "Iridium Golem", "Dust Spirit", "Ghost", "Carbon Ghost", "Putrid Ghost",
            "Skeleton", "Skeleton Mage", "Metal Head", "Shadow Brute",
            "Shadow Shaman", "Shadow Sniper", "Squid Kid", "Blue Squid",
            "Mummy", "Serpent", "Royal Serpent", "Pepper Rex", "Spider",
            "Magma Sprite", "Magma Sparker", "Dwarvish Sentry", "False Magma Cap",
            "Hot Head", "Lava Lurk", "Shadow Guy", "Shadow Girl", "Truffle Crab",
        };

        // Vanilla farm animal / critter species (not individual pet names -
        // Lookup Anything's search list is species-level, e.g. "Chicken"
        // as a species entry, not the player's own named hen).
        private static readonly HashSet<string> VanillaAnimals = new(StringComparer.OrdinalIgnoreCase)
        {
            "Chicken", "Void Chicken", "Golden Chicken", "Duck", "Rabbit",
            "Dinosaur", "Ostrich", "Cow", "Goat", "Sheep", "Pig",
            "Cat", "Dog", "Turtle", "White Cow", "Brown Cow",
        };

        public bool IsFromMod()
        {
            string cat = GetCategory();
            if (cat == "Buildings") return IsBuildingFromMod();
            if (cat == "NPCs") return !VanillaNpcs.Contains(InternalName);
            if (cat == "Monsters") return !VanillaMonsters.Contains(InternalName);
            if (cat == "Animals") return !VanillaAnimals.Contains(InternalName);
            string id = InternalName;
            return id.Contains('.') || id.Contains('_');
        }

        // A short label naming which mod, if IsFromMod() - just the part
        // before the first '.' or '_', for the "sort by mod" grouping.
        // Buildings/NPCs/Monsters rarely use that naming convention even
        // when modded, so there's no reliable way to extract a specific
        // mod name from the plain name alone - just group all non-vanilla
        // entries in those categories together as "Mod" rather than
        // guessing a name.
        // Maps known id-prefixes and monster name-sets to friendly mod
        // names (not the raw author/id prefix) - built from mods actually
        // verified during this project's work. Unknown prefixes still
        // fall back to the raw prefix rather than guessing further.
        private static readonly Dictionary<string, string> PrefixToModName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DN"] = "Sword & Sorcery",
            ["KCC"] = "Sword & Sorcery",
            ["Rafseazz"] = "Ridgeside Village",
            ["FlashShifter"] = "Stardew Valley Expanded",
            ["Nova"] = "Eli and Dylan",
            ["EastScarp"] = "East Scarp",
            ["Lemurkat"] = "East Scarp",
            ["mistyspring"] = "GI Extra Locations",
            ["GiEXredux"] = "GI Extra Locations",
            ["supert"] = "Adventurer's Guild Expanded",
            ["7thAxis"] = "Lurking in the Dark",
        };

        // Monster names known to belong to a specific mod even though the
        // internal name itself has no prefix at all (Sword & Sorcery's
        // "Stygium ..." family, for example).
        private static readonly Dictionary<string, string> MonsterNameToModName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Stygium Bat"] = "Sword & Sorcery", ["Stygium Crab"] = "Sword & Sorcery",
            ["Stygium Golem"] = "Sword & Sorcery", ["Stygium Golem (Blue)"] = "Sword & Sorcery",
            ["Stygium Head"] = "Sword & Sorcery", ["Stygium Leviathan"] = "Sword & Sorcery",
            ["Stygium Miner"] = "Sword & Sorcery", ["Stygium Miner Mage"] = "Sword & Sorcery",
            ["Stygium Party Skeleton"] = "Sword & Sorcery", ["Stygium Rex"] = "Sword & Sorcery",
            ["Stygium Serpent"] = "Sword & Sorcery", ["Stygium Skeleton"] = "Sword & Sorcery",
            ["Stygium Skull"] = "Sword & Sorcery", ["Stygium Squid"] = "Sword & Sorcery",
            ["Stygium False Mushroom"] = "Sword & Sorcery", ["Duskspire Remnant"] = "Sword & Sorcery",
            ["Duskspire Behemoth"] = "Sword & Sorcery",
        };

        public string ModGroupLabel()
        {
            if (!IsFromMod()) return "Vanilla";
            string cat = GetCategory();

            if (cat == "Monsters" && MonsterNameToModName.TryGetValue(InternalName, out string? mName))
                return mName;
            if (cat == "Buildings" || cat == "NPCs" || cat == "Monsters" || cat == "Animals") return "Mod";

            int dot = InternalName.IndexOf('.');
            int us = InternalName.IndexOf('_');
            int cut = dot >= 0 && (us < 0 || dot < us) ? dot : us;
            string prefix = cut > 0 ? InternalName[..cut] : InternalName;
            return PrefixToModName.TryGetValue(prefix, out string? friendly) ? friendly : prefix;
        }

        public bool DrawPortrait(SpriteBatch b, Vector2 position, Vector2 size)
        {
            if (_isMonster)
            {
                try
                {
                    if (GetTarget() is StardewValley.Character target && target.Sprite != null)
                    {
                        var sprite = target.Sprite;
                        Color tint = Color.White;
                        Rectangle sourceRect = sprite.SourceRect;
                        int frameW = sprite.SpriteWidth;
                        int frameH = sprite.SpriteHeight;
                        if (target is StardewValley.Monsters.GreenSlime slime)
                        {
                            sourceRect = new Rectangle(32, 120, 16, 24);
                            frameW = 16;
                            frameH = 24;
                            tint = slime.color.Value;
                        }
                        float scale = Math.Min(size.X / frameW, size.Y / frameH);
                        float drawWidth = frameW * scale;
                        float drawHeight = frameH * scale;
                        var centeredPos = new Vector2(
                                position.X + (size.X - drawWidth) / 2f,
                                position.Y + (size.Y - drawHeight) / 2f);
                        b.Draw(sprite.Texture, centeredPos, sourceRect, tint, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                        return true;
                    }
                }
                catch { }
            }

            if (_drawPortraitMethod == null)
            {
                if (_loggedPortraitIssues.Add(_subject.GetType().FullName ?? Name))
                {
                    ModEntry.SMonitor?.Log($"[SubjectWrapper] No DrawPortrait method found on {_subject.GetType().FullName} "
                            + $"(subject: {Name}) - portrait will be blank.", LogLevel.Debug);
                }
                return false;
            }
            try { _drawPortraitMethod.Invoke(_subject, new object[] { b, position, size }); return true; }
            catch (Exception ex)
            {
                if (_loggedPortraitIssues.Add(Name))
                {
                    ModEntry.SMonitor?.Log($"[SubjectWrapper] DrawPortrait threw for '{Name}' ({_subject.GetType().Name}): {ex.Message}", LogLevel.Debug);
                }
                return false;
            }
        }
        private static readonly HashSet<string> _loggedPortraitIssues = new();

        public string GetCategory()
        {
            if (_isMonster) return "Monsters";
            if (_className.Contains("CharacterSubject")) return "NPCs";
            if (_className.Contains("ItemSubject")) return "Items";
            if (_className.Contains("BuildingSubject")) return "Buildings";
            if (_className.Contains("CropSubject") || _className.Contains("FruitTreeSubject") || _className.Contains("WildTreeSubject")) return "Crops";
            if (_className.Contains("TerrainFeature") || _className.Contains("BushSubject")) return "Terrain";
            if (_className.Contains("FarmAnimal")) return "Animals";

            // Diagnostic: log every distinct class name that falls into
            // "Other" (once each) - this is meant to catch cases like
            // regular Chicken/Duck/Pig not appearing anywhere, in case
            // Lookup Anything uses a different class name for them that
            // isn't being matched above.
            if (_loggedOtherClassNames.Add(_className))
            {
                ModEntry.SMonitor?.Log($"[SubjectWrapper] Unclassified subject type falling into 'Other': {_className} (example: {Name})", LogLevel.Debug);
            }
            return "Other";
        }
        private static readonly HashSet<string> _loggedOtherClassNames = new();

        // Sub-category within the main category, or "" if this category
        // doesn't have a meaningful split. Computed once and cached since
        // it may involve a bit of string work.
        public string GetSubCategory()
        {
            if (_subCategoryCache != null) return _subCategoryCache;
            string cat = GetCategory();
            string result = cat switch
            {
                "Items" => ClassifyItemSubCategory(),
                "NPCs" => NpcCanBeRomanced() ? "Romanceable" : "Not romanceable",
                "Monsters" => IsFromMod() ? "Mod" : "Vanilla",
                "Buildings" => IsBuildableBuilding() ? "Buildable" : "Other",
                _ => "",
            };
            _subCategoryCache = result;
            return result;
        }

        // Classifies items by their actual C# class rather than Lookup
        // Anything's translated "Type" text - that text turned out to be
        // far more granular than expected (a distinct value per weapon
        // subtype like "Dagger"/"Sword"/"Club" instead of one shared
        // "Weapon" label), so stripping a "(...)" suffix from it didn't
        // actually merge anything. Checking against the concrete game
        // classes (MeleeWeapon, Ring, etc. - all long-stable base-game
        // types) is reliable regardless of how any given item's Type text
        // happens to be worded.
        private string ClassifyItemSubCategory()
        {
            object? target = GetTarget();
            if (target == null) return "Other";
            if (target is StardewValley.Tools.MeleeWeapon || target is StardewValley.Tools.Slingshot) return "Weapon";
            if (target is StardewValley.Objects.Ring) return "Ring";
            if (target is StardewValley.Objects.Boots) return "Boots";
            if (target is StardewValley.Objects.Hat) return "Hat";
            if (target is StardewValley.Objects.Clothing) return "Clothing";
            if (target is StardewValley.Objects.Furniture) return "Furniture";
            if (target is StardewValley.Tool) return "Tool";
            return "Other";
        }

        // Known vanilla building names (1.6). Buildings don't reliably use
        // the "." or "_" namespaced-id convention that items/monsters do,
        // so the general IsFromMod() heuristic under-detects modded
        // buildings - anything NOT in this list is treated as a mod
        // building instead, which is far more reliable for this category
        // specifically.
        private static readonly HashSet<string> VanillaBuildings = new(StringComparer.OrdinalIgnoreCase)
        {
            "Coop", "Big Coop", "Deluxe Coop", "Barn", "Big Barn", "Deluxe Barn",
            "Slime Hutch", "Shed", "Big Shed", "Silo", "Well", "Stable",
            "Fish Pond", "Mill", "Junimo Hut", "Farmhouse", "Cabin", "Greenhouse",
            "Shipping Bin", "Earth Obelisk", "Water Obelisk", "Desert Obelisk",
            "Island Obelisk", "Gold Clock", "Trailer", "Trailer Big", "Pet Bowl",
            "Log Cabin", "Plank Cabin", "Stone Cabin",
        };

        private bool IsBuildingFromMod() => !VanillaBuildings.Contains(InternalName);

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
