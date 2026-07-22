
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

        // Pets (as opposed to livestock/coop-and-barn animals) - checked
        // by name since there's no distinct C# class separating "pet
        // species" from "farm animal species" at this level.
        private static readonly HashSet<string> PetAnimalNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Cat", "Dog", "Turtle",
        };

        private string ClassifyAnimalSubCategory()
        {
            return PetAnimalNames.Contains(InternalName) ? "Pet" : "Livestock";
        }

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
            ["TenebrousNova"] = "Eli and Dylan",
            ["EastScarp"] = "East Scarp",
            ["Lemurkat"] = "East Scarp",
            ["atravita"] = "East Scarp",
            ["mistyspring"] = "GI Extra Locations",
            ["GiEXredux"] = "GI Extra Locations",
            ["supert"] = "Adventurer's Guild Expanded",
            ["7thAxis"] = "Lurking in the Dark",
            ["Bagi"] = "Nora the Herpetologist",
        };

        // Monster names known to belong to a specific mod even though the
        // NPC names known to belong to a specific mod even though the
        // internal name has no prefix at all - built from NPCs actually
        // verified during this project's work across several mods.
        private static readonly Dictionary<string, string> NpcNameToModName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Mateo"] = "Sword & Sorcery", ["Hector"] = "Sword & Sorcery",
            ["Cirrus"] = "Sword & Sorcery", ["Roslin"] = "Sword & Sorcery",
            ["Solomon"] = "Sword & Sorcery", ["Dandelion"] = "Sword & Sorcery",
            ["Silly"] = "Adventurer's Guild Expanded", ["Gabriel"] = "Adventurer's Guild Expanded",
            ["Zinnia"] = "Adventurer's Guild Expanded", ["JaviGiex"] = "GI Extra Locations",
            ["SenS"] = "Lurking in the Dark", ["Nora"] = "Nora the Herpetologist",

            // East Scarp - verified from Data/Characters entry keys
            ["Eyvinder"] = "East Scarp", ["Eloise"] = "East Scarp", ["Jacob"] = "East Scarp",
            ["Jessie"] = "East Scarp", ["Juliet"] = "East Scarp", ["Leximonster"] = "East Scarp",
            ["MichaelHart"] = "East Scarp", ["EthanHart"] = "East Scarp", ["StellaHart"] = "East Scarp",
            ["Abyssrooster"] = "East Scarp", ["Aideen"] = "East Scarp", ["Beatrice"] = "East Scarp",
            ["CameronLK"] = "East Scarp", ["CaptainRod"] = "East Scarp", ["CorwinLK"] = "East Scarp",
            ["DaleWaede"] = "East Scarp", ["Duck2NPC"] = "East Scarp", ["DuckNPC"] = "East Scarp",
            ["EdithHart"] = "East Scarp", ["Gremlin"] = "East Scarp", ["HappySlime"] = "East Scarp",
            ["Jasper"] = "East Scarp", ["JosephineK"] = "East Scarp", ["JunimoJade"] = "East Scarp",
            ["KatarynaLK"] = "East Scarp", ["KeanuAvis"] = "East Scarp", ["KennedyLK"] = "East Scarp",
            ["LadySheba"] = "East Scarp", ["LittleGruff"] = "East Scarp", ["LumaJunimo"] = "East Scarp",
            ["OliverK"] = "East Scarp", ["PepperPup"] = "East Scarp", ["RichieTheMacaw"] = "East Scarp",
            ["Rosa"] = "East Scarp", ["ValkyrieDog"] = "East Scarp", ["TristanLK"] = "East Scarp",

            // Stardew Valley Expanded - verified from CharacterFiles/Portraits
            ["Alesia"] = "Stardew Valley Expanded", ["Andy"] = "Stardew Valley Expanded",
            ["Apples"] = "Stardew Valley Expanded", ["Axel"] = "Stardew Valley Expanded",
            ["Brooklyn"] = "Stardew Valley Expanded", ["Camilla"] = "Stardew Valley Expanded",
            ["Chloe"] = "Stardew Valley Expanded", ["Claire"] = "Stardew Valley Expanded",
            ["Dusty"] = "Stardew Valley Expanded", ["Gil"] = "Stardew Valley Expanded",
            ["GuntherSilvian"] = "Stardew Valley Expanded", ["Isaac"] = "Stardew Valley Expanded",
            ["Jace"] = "Stardew Valley Expanded", ["Jadu"] = "Stardew Valley Expanded",
            ["Jolyne"] = "Stardew Valley Expanded", ["Lance"] = "Stardew Valley Expanded",
            ["Magnus"] = "Stardew Valley Expanded", ["Martin"] = "Stardew Valley Expanded",
            ["Mermaid"] = "Stardew Valley Expanded", ["Morgan"] = "Stardew Valley Expanded",
            ["Olivia"] = "Stardew Valley Expanded", ["Peaches"] = "Stardew Valley Expanded",
            ["Scarlett"] = "Stardew Valley Expanded", ["ScarlettFake"] = "Stardew Valley Expanded",
            ["Sophia"] = "Stardew Valley Expanded", ["Suki"] = "Stardew Valley Expanded",
            ["Susan"] = "Stardew Valley Expanded", ["Victor"] = "Stardew Valley Expanded",
            ["Zoey"] = "Stardew Valley Expanded", ["SVE_Henchman"] = "Stardew Valley Expanded",

            // Ridgeside Village - verified from Data/NPCData/Dispositions.json entry keys
            ["Acorn"] = "Ridgeside Village", ["Aguar"] = "Ridgeside Village", ["Alissa"] = "Ridgeside Village",
            ["Althea"] = "Ridgeside Village", ["Anton"] = "Ridgeside Village", ["Ariah"] = "Ridgeside Village",
            ["Belinda"] = "Ridgeside Village", ["Bert"] = "Ridgeside Village", ["Blair"] = "Ridgeside Village",
            ["Bliss"] = "Ridgeside Village", ["Bryle"] = "Ridgeside Village", ["Carmen"] = "Ridgeside Village",
            ["Corine"] = "Ridgeside Village", ["Daia"] = "Ridgeside Village", ["Ezekiel"] = "Ridgeside Village",
            ["Faye"] = "Ridgeside Village", ["Flor"] = "Ridgeside Village", ["Freddie"] = "Ridgeside Village",
            ["Helen"] = "Ridgeside Village", ["Ian"] = "Ridgeside Village", ["Irene"] = "Ridgeside Village",
            ["Jeric"] = "Ridgeside Village", ["Jio"] = "Ridgeside Village", ["June"] = "Ridgeside Village",
            ["Keahi"] = "Ridgeside Village", ["Kenneth"] = "Ridgeside Village", ["Kiarra"] = "Ridgeside Village",
            ["Kimpoi"] = "Ridgeside Village", ["Kiwi"] = "Ridgeside Village", ["Lenny"] = "Ridgeside Village",
            ["Lola"] = "Ridgeside Village", ["Lorenzo"] = "Ridgeside Village", ["Lorraine"] = "Ridgeside Village",
            ["Louie"] = "Ridgeside Village", ["Maddie"] = "Ridgeside Village", ["Maive"] = "Ridgeside Village",
            ["Malaya"] = "Ridgeside Village", ["Nadaline"] = "Ridgeside Village", ["Naomi"] = "Ridgeside Village",
            ["Olga"] = "Ridgeside Village", ["Paula"] = "Ridgeside Village", ["Philip"] = "Ridgeside Village",
            ["Pika"] = "Ridgeside Village", ["Pipo"] = "Ridgeside Village", ["Raeriyala"] = "Ridgeside Village",
            ["Richard"] = "Ridgeside Village", ["Sari"] = "Ridgeside Village", ["Sean"] = "Ridgeside Village",
            ["Shanice"] = "Ridgeside Village", ["Shiro"] = "Ridgeside Village", ["Sonny"] = "Ridgeside Village",
            ["Torts"] = "Ridgeside Village", ["Ysabelle"] = "Ridgeside Village",
        };

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
            if (cat == "NPCs" && NpcNameToModName.TryGetValue(InternalName, out string? nName))
                return nName;
            if (cat == "Buildings" || cat == "NPCs" || cat == "Monsters" || cat == "Animals") return "Mod";

            int dot = InternalName.IndexOf('.');
            int us = InternalName.IndexOf('_');
            int cut = dot >= 0 && (us < 0 || dot < us) ? dot : us;
            string prefix = cut > 0 ? InternalName[..cut] : InternalName;
            return PrefixToModName.TryGetValue(prefix, out string? friendly) ? friendly : prefix;
        }

        public bool DrawPortrait(SpriteBatch b, Vector2 position, Vector2 size)
        {
            // For monsters: try OUR OWN drawing FIRST, not Lookup
            // Anything's. Confirmed directly by the user: Lookup
            // Anything's own detail page has the SAME overflow/bleed bug
            // for these monsters (Squid Kid etc.) - it's a bug in Lookup
            // Anything itself, not something introduced by us. Calling
            // into LA's own method would just import that same bug into
            // our list too. Our own math (below) actively guards against
            // the overflow (scale is derived from the exact rect being
            // drawn, so it mathematically cannot exceed the icon box),
            // so it's the better default for monsters specifically.
            if (_isMonster)
            {
                try
                {
                    if (GetTarget() is StardewValley.Character target && target.Sprite != null)
                    {
                        var sprite = target.Sprite;
                        Color tint = Color.White;

                        // Force frame 0 (the resting/idle pose) before
                        // reading the source rect. Without this, we read
                        // whatever frame the fake monster instance happened
                        // to be on at construction time - which is often a
                        // mid-attack or transition frame that looks wrong
                        // or off-model when cropped down to icon size. This
                        // affects nearly every monster type (not just
                        // slimes), since none of them were ever forced to
                        // a known frame before this fix.
                        // These "fake" monster instances are built once
                        // for search-listing purposes and never go through
                        // a real game Update() loop - so relying on
                        // AnimatedSprite's own computed SourceRect (which
                        // multiplies the current frame index by frame
                        // size to find its position in the sheet) turned
                        // out to be unreliable across many different
                        // monster types, not just one or two: sometimes
                        // it disagreed with SpriteWidth/SpriteHeight,
                        // sometimes forcing frame 0 didn't stick, and
                        // various fixes targeting those symptoms
                        // individually kept resurfacing on other monster
                        // types. Rather than trust that computed offset at
                        // all, crop directly from the sheet's fixed
                        // origin (0,0) using only the declared per-frame
                        // size - frame 0 always starts at the top-left
                        // corner of any standard sprite sheet, so this
                        // sidesteps the unreliable index math entirely.
                        int frameW = sprite.SpriteWidth;
                        int frameH = sprite.SpriteHeight;
                        Rectangle sourceRect = new(0, 0, frameW, frameH);
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

            // Fallback: Lookup Anything's own DrawPortrait. This is the
            // primary (and only) path for non-monster subjects, and a
            // last resort for monsters if our own drawing above failed
            // for some reason.
            if (_drawPortraitMethod != null)
            {
                try
                {
                    _drawPortraitMethod.Invoke(_subject, new object[] { b, position, size });
                    return true;
                }
                catch (Exception ex)
                {
                    if (_loggedPortraitIssues.Add(Name))
                    {
                        ModEntry.SMonitor?.Log($"[SubjectWrapper] DrawPortrait threw for '{Name}' ({_subject.GetType().Name}): {ex.Message}", LogLevel.Debug);
                    }
                }
            }

            return false;
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
                "Animals" => ClassifyAnimalSubCategory(),
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
        private static HashSet<string>? _machineIdsCache;
        private static readonly HashSet<string> NonProcessingFarmEquipment = new(StringComparer.OrdinalIgnoreCase)
        {
            "Scarecrow", "Deluxe Scarecrow", "Rarecrow", "Rarecrow #1", "Rarecrow #2",
            "Rarecrow #3", "Rarecrow #4", "Rarecrow #5", "Rarecrow #6", "Rarecrow #7", "Rarecrow #8",
            "Sprinkler", "Quality Sprinkler", "Iridium Sprinkler", "Pressure Nozzle",
        };

        // Data/Machines is what the game itself uses to decide which
        // placed objects behave as machines (Furnace, Keg, Preserves Jar,
        // Bee House, Tapper, Mushroom Box, Lightning Rod, Crystalarium,
        // Seed Maker, incubators, etc.) - reading it directly means every
        // vanilla AND modded machine is covered automatically, without
        // hardcoding a name list that would miss new/modded ones. Loaded
        // as plain object + reflection (not a typed Dictionary) for the
        // same reason the farm animal fix needed it: the content cache
        // returns this asset as its own concrete type, and casting that
        // to a different generic Dictionary instantiation throws
        // "Specified cast is not valid".
        private static HashSet<string> GetMachineIds()
        {
            if (_machineIdsCache != null) return _machineIdsCache;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                object rawData = Game1.content.Load<object>("Data/Machines");
                var keysProp = rawData.GetType().GetProperty("Keys");
                if (keysProp?.GetValue(rawData) is System.Collections.IEnumerable keys)
                {
                    foreach (object k in keys)
                    {
                        string? key = k?.ToString();
                        if (key != null) ids.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log("Error loading Data/Machines for item classification: " + ex.Message, LogLevel.Warn);
            }
            _machineIdsCache = ids;
            return ids;
        }

        private static bool IsMachineItem(StardewValley.Item item)
        {
            if (NonProcessingFarmEquipment.Contains(item.Name)) return true;
            var machineIds = GetMachineIds();
            return machineIds.Contains(item.QualifiedItemId) || machineIds.Contains(item.ItemId);
        }

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
            if (target is StardewValley.Objects.Trinkets.Trinket) return "Trinket";
            if (target is StardewValley.Objects.Wallpaper) return "Wallpaper";
            if (target is StardewValley.Item item0 && IsMachineItem(item0)) return "Machine";

            // Farm produce: crops, fish, foraged mushrooms/greens,
            // preserves (jams/pickles use the Cooking category), and
            // animal products (eggs/milk). Category constant names
            // verified directly against the real StardewValley.dll (not
            // guessed).
            if (target is StardewValley.Object obj)
            {
                int cat = obj.Category;
                if (cat == StardewValley.Object.FruitsCategory
                        || cat == StardewValley.Object.VegetableCategory
                        || cat == StardewValley.Object.FishCategory
                        || cat == StardewValley.Object.GreensCategory
                        || cat == StardewValley.Object.CookingCategory
                        || cat == StardewValley.Object.EggCategory
                        || cat == StardewValley.Object.MilkCategory
                        || cat == StardewValley.Object.ingredientsCategory
                        || cat == StardewValley.Object.syrupCategory)
                {
                    return "Farm Produce";
                }
                if (cat == StardewValley.Object.GemCategory || cat == StardewValley.Object.mineralsCategory)
                {
                    return "Mineral/Gem";
                }
                if (cat == StardewValley.Object.monsterLootCategory)
                {
                    return "Monster Loot";
                }
                if (cat == StardewValley.Object.tackleCategory)
                {
                    return "Tackle";
                }
                if (cat == StardewValley.Object.skillBooksCategory)
                {
                    return "Skill Book";
                }
                if (cat == StardewValley.Object.metalResources || cat == StardewValley.Object.buildingResources)
                {
                    return "Resource";
                }
                if (cat == StardewValley.Object.junkCategory || cat == StardewValley.Object.litterCategory)
                {
                    return "Junk";
                }
                if (cat == StardewValley.Object.BigCraftableCategory)
                {
                    return "Big Craftable";
                }
                if (cat == StardewValley.Object.SeedsCategory || cat == StardewValley.Object.fertilizerCategory)
                {
                    return "Seed";
                }
                if (obj.Name is "Tree Fertilizer" or "Grass Starter")
                {
                    return "Seed";
                }
            }

            // Fencing and flooring/paths - farm equipment items that
            // don't process or produce anything (same reasoning as
            // Scarecrow/Sprinkler), so Data/Machines wouldn't cover them
            // either. No clean C# type or category constant distinguishes
            // these from other crafted goods, so this uses a name-pattern
            // match against the known vanilla naming convention (every
            // fence is named "... Fence", every floor "... Floor", every
            // path "... Path") - lower confidence than the type-based
            // checks above, but a reasonable middle ground.
            if (target is StardewValley.Item fenceFloorItem)
            {
                string n = fenceFloorItem.Name;
                if (n.EndsWith(" Fence", StringComparison.OrdinalIgnoreCase)
                        || n.EndsWith(" Floor", StringComparison.OrdinalIgnoreCase)
                        || n.EndsWith(" Path", StringComparison.OrdinalIgnoreCase)
                        || n.EndsWith(" Walkway Floor", StringComparison.OrdinalIgnoreCase))
                {
                    return "Fencing/Flooring";
                }
            }

            // Diagnostic: log the real .NET type behind every item that
            // falls into "Other" (once per distinct type) - this is meant
            // to catch cases like Boots/starting Tools ending up here
            // because Lookup Anything's search catalog represents them via
            // some other wrapper type instead of the real Boots/Tool
            // class, which the checks above wouldn't match.
            if (_loggedOtherItemTypes.Add(target.GetType().FullName ?? "?"))
            {
                ModEntry.SMonitor?.Log($"[SubjectWrapper] Item falling into 'Other' sub-category: target type = {target.GetType().FullName} (example: {Name})", LogLevel.Debug);
            }
            return "Other";
        }
        private static readonly HashSet<string> _loggedOtherItemTypes = new();

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
