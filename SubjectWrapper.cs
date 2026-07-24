
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
        private readonly string _realName;
        private string? _nameOverride;
        public string Name => _nameOverride ?? _realName;
        public Texture2D? IconTextureOverride;
        public Rectangle? IconCropOverride;

        // Marks this wrapper as a "variant alias" entry - a known texture
        // reskin (e.g. "Corrupt Bat") of a real underlying monster (e.g.
        // "Bat") that shares its exact stats/data, just displayed with a
        // different name/icon. Lets search and grouping code recognize
        // these and avoid double-counting them against the real entry.
        public string? VariantOfInternalName;

        // Variant display overrides keyed by the raw subject reference
        // (reference equality) - since Create() makes a brand new
        // wrapper instance every time with no caching, an override set on
        // one wrapper instance would be lost the next time the same raw
        // subject gets wrapped again (e.g. when MobileSearchMenu builds
        // its own internal list). Storing it here, keyed by the actual
        // subject object, means the constructor can look it up and apply
        // it automatically regardless of which wrapper instance asks.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, VariantInfo> _variantRegistry = new();
        private sealed class VariantInfo
        {
            public string? DisplayName;
            public string? RealInternalName;
            public Texture2D? Icon;
            public Rectangle? IconCrop;
            public string? SpawnCondition;
        }

        public static void RegisterVariant(object rawSubject, string displayName, string realInternalName, Texture2D? icon, Rectangle? iconCrop, string? spawnCondition = null)
        {
            _variantRegistry.AddOrUpdate(rawSubject, new VariantInfo
            {
                DisplayName = displayName,
                RealInternalName = realInternalName,
                Icon = icon,
                IconCrop = iconCrop,
                SpawnCondition = spawnCondition,
            });
        }

        public static bool TryGetVariantSpawnCondition(object rawSubject, out string? displayName, out string? spawnCondition)
        {
            if (_variantRegistry.TryGetValue(rawSubject, out VariantInfo? info))
            {
                displayName = info.DisplayName;
                spawnCondition = info.SpawnCondition;
                return spawnCondition != null;
            }
            displayName = null;
            spawnCondition = null;
            return false;
        }

        public void SetVariantDisplay(string displayName, string internalNameOfReal, Texture2D? icon, Rectangle? iconCrop)
        {
            _nameOverride = displayName;
            VariantOfInternalName = internalNameOfReal;
            IconTextureOverride = icon;
            IconCropOverride = iconCrop;
        }

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

            _realName = GetValue<string>(_nameProperty) ?? "Unknown";
            Description = GetValue<string>(_descriptionProperty) ?? "";
            SubjectType = GetValue<string>(_typeProperty) ?? "";
            IsValid = _nameProperty != null;

            InternalName = ComputeInternalName();

            if (_variantRegistry.TryGetValue(subject, out VariantInfo? variant))
            {
                _nameOverride = variant.DisplayName;
                VariantOfInternalName = variant.RealInternalName;
                IconTextureOverride = variant.Icon;
                IconCropOverride = variant.IconCrop;
                // IsFromMod()/ModGroupLabel() check InternalName against
                // VanillaMonsters/MonsterNameToModName - without this
                // override, InternalName stayed as the real underlying
                // name (e.g. "Bat"), which IS in VanillaMonsters, so
                // every variant entry was silently misclassified as
                // Vanilla instead of its own mod.
                if (variant.DisplayName != null) InternalName = variant.DisplayName;
            }
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
            "Spiker", "Crow", "Fireball", "Frog", "Angry Roger", "Cat", "Skeleton Warrior",
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
            // Defense in depth: a null/empty InternalName previously
            // crashed the whole update loop (Dictionary.TryGetValue
            // throws ArgumentNullException on a null key), confirmed from
            // a real crash log. Guard against it here directly so this
            // exact crash class can't recur regardless of what future
            // code path might produce one.
            if (string.IsNullOrEmpty(InternalName)) return false;

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
            ["StygiumLurk"] = "Sword & Sorcery", ["StygiumSentry"] = "Sword & Sorcery",
            ["Stygium_Duggy"] = "Sword & Sorcery", ["StygiumGolem_Purple"] = "Sword & Sorcery",
            ["StygiumMushroom"] = "Sword & Sorcery", ["StygiumMushroom_Duggy"] = "Sword & Sorcery",
            ["StygiumSkeleton_Rare"] = "Sword & Sorcery",
            ["Cirrus"] = "Sword & Sorcery", ["Roslin"] = "Sword & Sorcery",
            ["Solomon"] = "Sword & Sorcery", ["Dandelion"] = "Sword & Sorcery",
            ["Silly"] = "Adventurer's Guild Expanded", ["Gabriel"] = "Adventurer's Guild Expanded",
            ["Zinnia"] = "Adventurer's Guild Expanded", ["Daisy"] = "Adventurer's Guild Expanded",
            ["Daniel"] = "Adventurer's Guild Expanded", ["JaviGiex"] = "GI Extra Locations",
            ["SenS"] = "Lurking in the Dark", ["Nora"] = "Nora the Herpetologist",
            ["Nova.Eli"] = "Eli and Dylan", ["Nova.Dylan"] = "Eli and Dylan",
            // Real internal name confirmed from log: "SootInside" (Custom
            // Companions names the companion instance this, not "Soot")
            ["Soot"] = "Eli and Dylan", ["SootInside"] = "Eli and Dylan", ["SootOutside"] = "Eli and Dylan",

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
            ["VivienneLK"] = "East Scarp", ["JadeMalic"] = "East Scarp", ["ToriLK"] = "East Scarp",

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
            ["HighlandsDwarf"] = "Stardew Valley Expanded",
            ["Freya"] = "Stardew Valley Expanded",
            // SVE Adventurer's Guild Guests (cycle through the guild hall
            // by season, confirmed by user)
            ["Gertrude"] = "Stardew Valley Expanded", ["Cordelia"] = "Stardew Valley Expanded",
            ["Cassandra"] = "Stardew Valley Expanded", ["Sawyer"] = "Stardew Valley Expanded",
            ["Drake"] = "Stardew Valley Expanded", ["Brock"] = "Stardew Valley Expanded",
            ["Brianna"] = "Stardew Valley Expanded", ["Emin"] = "Stardew Valley Expanded",
            ["Treyvon"] = "Stardew Valley Expanded", ["Hank"] = "Stardew Valley Expanded", ["HankSVE"] = "Stardew Valley Expanded",
            ["Charlie"] = "Stardew Valley Expanded",
            ["Gale"] = "Stardew Valley Expanded", ["Edmund"] = "Stardew Valley Expanded",

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
            ["Trinnie"] = "Ridgeside Village", ["Undreya"] = "Ridgeside Village",
            ["Yuuma"] = "Ridgeside Village", ["Zachary"] = "Ridgeside Village",
            ["Zayne"] = "Ridgeside Village", ["RelicSpirit"] = "Ridgeside Village",
            ["TreehouseGirl"] = "Ridgeside Village",
        };

        // internal name itself has no prefix at all (Sword & Sorcery's
        // "Stygium ..." family, for example).
        // Base vanilla monster type used to construct each SVE custom
        // monster, confirmed directly from FarmTypeManager's own spawn
        // config (content.json under "[FTM] Stardew Valley Expanded") -
        // every one of these is really a vanilla monster instance with an
        // overridden sprite/texture, never registered in Data/Monsters at
        // all, which is why constructing them by name alone always
        // failed ("key not present in the dictionary").
        internal static readonly Dictionary<string, string> SveMonsterBaseType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Apophis"] = "Royal Serpent",
            ["Badlands Serpent"] = "Serpent",
            ["Royal Badlands Serpent"] = "Royal Serpent",
            ["Corrupt Serpent"] = "Serpent",
            ["Corrupt Mummy"] = "Mummy",
            ["Corrupt Spirit"] = "Carbon Ghost",
            ["Fallen Adventurer"] = "Mummy",
            ["Bully Rex"] = "Pepper Rex",
            ["Poltergeist"] = "Pepper Rex",
            ["Swamp Golem"] = "Wilderness Golem",
            ["Swamp Lurk"] = "Lava Lurk",
            ["Swamp Putrid Ghost"] = "Putrid Ghost",
            ["Swamp Flower Crab"] = "Truffle Crab",
            ["Toxic Bubble"] = "Ghost",
            ["Legendary Purple Mushroom Crab"] = "Iridium Crab",
            ["Legendary Sand Scorpion"] = "Iridium Golem",
            ["Legendary Gold Slime"] = "Iridium Golem",
            ["Sand Scorpion"] = "Wilderness Golem",
            ["Copper Crab"] = "Rock Crab",
            ["Gold Crab"] = "Rock Crab",
            ["Iron Crab"] = "Rock Crab",
            // RSV custom monsters - confirmed from wiki text reviewed
            // earlier ("based on X" statements for each).
            ["Serperial"] = "Royal Serpent",
            ["Viperial"] = "Pepper Rex",
            ["Wraith"] = "Putrid Ghost",
            ["Corrupted Spirit"] = "Ghost",
            ["Beast 1"] = "Shadow Brute", ["Beast 2"] = "Shadow Brute", ["Beast 3"] = "Shadow Brute",
            // East Scarp custom monsters - confirmed from its own
            // FarmTypeManager config ("East Scarp FTM/content.json").
            ["ES Mine Bat"] = "Frost Bat",
            ["ES Mine Bat Iridium"] = "Bat",
        };

        internal static readonly Dictionary<string, string> MonsterNameToModName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ES Mine Bat"] = "East Scarp", ["ES Mine Bat Iridium"] = "East Scarp",
            // SVE danger-zone reskin variants (InternalName now correctly
            // resolves to these display names via the variant registry
            // fix, so they need their own mod-classification entry too).
            ["Wilderness Golem Spring"] = "Stardew Valley Expanded", ["Wilderness Golem Summer"] = "Stardew Valley Expanded",
            ["Wilderness Golem Fall"] = "Stardew Valley Expanded", ["Wilderness Golem Winter"] = "Stardew Valley Expanded",
            ["Evil Mummy"] = "Stardew Valley Expanded",
            ["Corrupt Bat"] = "Stardew Valley Expanded", ["Evil Bat"] = "Stardew Valley Expanded", ["Dangerous Bat"] = "Stardew Valley Expanded",
            ["Skeleton Dangerous"] = "Stardew Valley Expanded", ["Skeleton Mage Dangerous"] = "Stardew Valley Expanded",
            ["Shadow Brute Dangerous"] = "Stardew Valley Expanded", ["Shadow Shaman Dangerous"] = "Stardew Valley Expanded",
            ["Stone Golem Dangerous"] = "Stardew Valley Expanded", ["Dangerous Metal Head"] = "Stardew Valley Expanded",
            ["Dust Spirit Dangerous"] = "Stardew Valley Expanded", ["Green Dust Spirit Dangerous"] = "Stardew Valley Expanded",
            ["White Dust Spirit Dangerous"] = "Stardew Valley Expanded",
            ["Stygium Bat"] = "Sword & Sorcery", ["Stygium Crab"] = "Sword & Sorcery",
            ["Stygium Golem"] = "Sword & Sorcery", ["Stygium Golem (Blue)"] = "Sword & Sorcery",
            ["Stygium Head"] = "Sword & Sorcery", ["Stygium Leviathan"] = "Sword & Sorcery",
            ["Stygium Miner"] = "Sword & Sorcery", ["Stygium Miner Mage"] = "Sword & Sorcery",
            ["Stygium Party Skeleton"] = "Sword & Sorcery", ["Stygium Rex"] = "Sword & Sorcery",
            ["Stygium Serpent"] = "Sword & Sorcery", ["Stygium Skeleton"] = "Sword & Sorcery",
            ["Stygium Skull"] = "Sword & Sorcery", ["Stygium Squid"] = "Sword & Sorcery",
            ["Stygium False Mushroom"] = "Sword & Sorcery", ["Duskspire Remnant"] = "Sword & Sorcery",
            ["Stygium Droplet"] = "Sword & Sorcery",
            ["Duskspire Behemoth"] = "Sword & Sorcery",
            // Stardew Valley Expanded - confirmed from the official wiki
            // (SVE adds only 6-8 new monster species total; several names
            // guessed earlier from sprite filenames alone turned out to
            // be variant/danger-mode reskins, not separate new species).
            ["Apophis"] = "Stardew Valley Expanded", ["Badlands Serpent"] = "Stardew Valley Expanded",
            ["Corrupt Mummy"] = "Stardew Valley Expanded", ["Corrupt Serpent"] = "Stardew Valley Expanded",
            ["Corrupt Spirit"] = "Stardew Valley Expanded",
            ["Fallen Adventurer"] = "Stardew Valley Expanded",
            ["Bully Rex"] = "Stardew Valley Expanded", ["Poltergeist"] = "Stardew Valley Expanded",
            ["Sand Scorpion"] = "Stardew Valley Expanded", ["Swamp Golem"] = "Stardew Valley Expanded",
            ["Swamp Lurk"] = "Stardew Valley Expanded", ["Swamp Putrid Ghost"] = "Stardew Valley Expanded",
            ["Toxic Bubble"] = "Stardew Valley Expanded", ["Legendary Gold Slime"] = "Stardew Valley Expanded",
            ["Legendary Purple Mushroom Crab"] = "Stardew Valley Expanded", ["Legendary Sand Scorpion"] = "Stardew Valley Expanded",
            ["Copper Crab"] = "Stardew Valley Expanded", ["Gold Crab"] = "Stardew Valley Expanded",
            ["Iron Crab"] = "Stardew Valley Expanded", ["Royal Badlands Serpent"] = "Stardew Valley Expanded",
            ["Swamp Flower Crab"] = "Stardew Valley Expanded",
            // Ridgeside Village - confirmed from the official wiki's monster list
            ["Beast 1"] = "Ridgeside Village",
            ["Beast 2"] = "Ridgeside Village", ["Beast 3"] = "Ridgeside Village",
            ["Serperial"] = "Ridgeside Village", ["Corrupted Spirit"] = "Ridgeside Village",
            ["Viperial"] = "Ridgeside Village", ["Wraith"] = "Ridgeside Village",
            // No longer a collision - confirmed from RSV's own wiki that
            // its monster is actually spelled "Corrupted Spirit" (with a
            // "d"), distinct from SVE's "Corrupt Spirit". Moved to the
            // SVE list above where it belongs.
        };

        public string ModGroupLabel()
        {
            if (!IsFromMod()) return "Vanilla";
            string cat = GetCategory();

            if (_loggedModGroupTrace.Add(Name))
            {
                ModEntry.SMonitor?.Log($"[SubjectWrapper] ModGroupLabel trace for '{Name}': category={cat}, InternalName='{InternalName}', "
                        + $"inNpcTable={NpcNameToModName.ContainsKey(InternalName)}, inMonsterTable={MonsterNameToModName.ContainsKey(InternalName)}", LogLevel.Debug);
            }

            if (cat == "Monsters" && MonsterNameToModName.TryGetValue(InternalName, out string? mName))
                return mName;
            if (cat == "NPCs" && NpcNameToModName.TryGetValue(InternalName, out string? nName))
                return nName;

            // Before giving up to a generic "Mod" label, also try
            // extracting a "." or "_" namespaced prefix and looking THAT
            // up - this catches any NPC/monster/etc. using a dot-prefixed
            // internal name (e.g. Eli and Dylan's "Nova.Eli") even if it
            // isn't individually listed by exact name above. Previously
            // this category branch returned "Mod" immediately without
            // ever trying this, so ANY not-explicitly-listed NPC using
            // this naming convention fell back to the generic label
            // regardless of whether its prefix was actually known.
            int dot = InternalName.IndexOf('.');
            int us = InternalName.IndexOf('_');
            int cut = dot >= 0 && (us < 0 || dot < us) ? dot : us;
            if (cut > 0 && PrefixToModName.TryGetValue(InternalName[..cut], out string? prefixMatch))
                return prefixMatch;

            if (cat == "Buildings" || cat == "NPCs" || cat == "Monsters" || cat == "Animals") return "Mod";

            string prefix = cut > 0 ? InternalName[..cut] : InternalName;
            return PrefixToModName.TryGetValue(prefix, out string? friendly) ? friendly : prefix;
        }

        public bool DrawPortrait(SpriteBatch b, Vector2 position, Vector2 size)
        {
            if (IconTextureOverride != null)
            {
                var crop = IconCropOverride ?? new Rectangle(0, 0, IconTextureOverride.Width, IconTextureOverride.Height);
                float ovScale = Math.Min(size.X / crop.Width, size.Y / crop.Height);
                float ovW = crop.Width * ovScale;
                float ovH = crop.Height * ovScale;
                var ovPos = new Vector2(position.X + (size.X - ovW) / 2f, position.Y + (size.Y - ovH) / 2f);
                b.Draw(IconTextureOverride, ovPos, crop, Color.White, 0f, Vector2.Zero, ovScale, SpriteEffects.None, 1f);
                return true;
            }
            // For NPCs: also try OUR OWN drawing first, same reasoning as
            // monsters below. Lookup Anything's own DrawPortrait succeeds
            // without throwing for these (confirmed - no error logged)
            // but renders nothing visible, which strongly suggests it
            // deliberately withholds the portrait for an NPC the player
            // hasn't met yet (a sensible spoiler-prevention default for
            // its normal use case). That's exactly backwards for what we
            // built this for - showing an NPC's unlock condition BEFORE
            // meeting them - so bypass that behavior entirely and draw
            // straight from the character's own Portrait texture.
            if (!_isMonster && GetCategory() == "NPCs")
            {
                try
                {
                    object? t = GetTarget();
                    bool isNpc = t is StardewValley.NPC;
                    bool hasPortrait = t is StardewValley.NPC n0 && n0.Portrait != null;
                    if (_loggedPortraitIssues.Add("npc-trace:" + Name))
                    {
                        ModEntry.SMonitor?.Log($"[SubjectWrapper] NPC portrait trace for '{Name}': target={(t == null ? "null" : t.GetType().FullName)}, "
                                + $"isNpc={isNpc}, hasPortrait={hasPortrait}", LogLevel.Debug);
                    }
                    if (t is StardewValley.NPC npcTarget)
                    {
                        bool forceSprite = ForceSpriteOverPortrait.Contains(npcTarget.Name);
                        Texture2D? tex = forceSprite ? null : npcTarget.Portrait;
                        if (tex == null && !forceSprite)
                        {
                            // Portrait wasn't loaded into memory yet - this
                            // happens for real, already-spawned NPCs the
                            // game just hasn't needed to show a portrait
                            // for recently (confirmed via log: several
                            // vanilla NPCs like Leo had a real NPC target
                            // but a null Portrait). Try loading it
                            // directly from the standard content path,
                            // unless a known override applies (some
                            // characters' portrait asset name doesn't
                            // match their in-game Name - e.g. Leo's
                            // portrait file is "ParrotBoy", confirmed
                            // directly by the user).
                            string portraitAssetName = PortraitAssetNameOverrides.TryGetValue(npcTarget.Name, out string? overrideName)
                                    ? overrideName : npcTarget.Name;
                            try { tex = Game1.content.Load<Texture2D>($"Portraits\\{portraitAssetName}"); }
                            catch (Exception loadEx)
                            {
                                if (_loggedPortraitIssues.Add("npc-portrait-load:" + Name))
                                {
                                    ModEntry.SMonitor?.Log($"[SubjectWrapper] Couldn't force-load portrait for NPC '{Name}': {loadEx.Message}", LogLevel.Debug);
                                }
                            }
                            // Best-effort extra attempts for characters
                            // whose real sprite lives in a mod-specific
                            // subfolder rather than the standard
                            // Portraits\ location - e.g. SVE's
                            // "Adventurer's Guild Guest" characters, whose
                            // art the user pointed out lives under
                            // assets/CharacterFiles/OverworldSprites/Adventurers/.
                            // These candidate paths are unverified guesses
                            // at how Content Patcher exposes that folder
                            // as an asset key; logged either way so we can
                            // confirm or rule them out from real evidence.
                            if (tex == null)
                            {
                                string[] extraCandidates =
                                {
                                    $"Mods\\FlashShifter.StardewValleyExpanded\\CharacterFiles\\OverworldSprites\\Adventurers\\{portraitAssetName}",
                                    $"Mods\\FlashShifter.StardewValleyExpanded\\Adventurers\\{portraitAssetName}",
                                };
                                foreach (string candidate in extraCandidates)
                                {
                                    try { tex = Game1.content.Load<Texture2D>(candidate); if (tex != null) break; }
                                    catch { }
                                }
                                if (_loggedPortraitIssues.Add("npc-extra-path:" + Name))
                                {
                                    ModEntry.SMonitor?.Log($"[SubjectWrapper] Extra path attempts for '{Name}': "
                                            + (tex != null ? "one of them worked" : "none worked"), LogLevel.Debug);
                                }
                            }
                        }
                        // Confirmed directly from SVE's own mod source: for
                        // "Adventurer's Guild Guest" characters, the
                        // Portraits/{name} asset is explicitly a "(fake)"
                        // blank placeholder image the mod loads on
                        // purpose - a small (16x32, sprite-sized) file
                        // meant to satisfy the game's requirement for
                        // *some* portrait texture to exist, not to
                        // actually be shown. Detecting that size and
                        // skipping straight to the real overworld sprite
                        // (which DOES load normally at the standard
                        // Characters/{name} path - no special path
                        // needed) avoids ever trying to draw the
                        // known-blank placeholder.
                        if (tex != null && tex.Height > 48)
                        {
                            // Some characters' "Portrait" field actually
                            // points to their overworld sprite texture
                            // instead of a real portrait sheet - confirmed
                            // directly from a log trace showing
                            // texSize=16x32 (a standard sprite frame size)
                            // for several SVE "guest" characters, instead
                            // of the usual ~64x64 portrait sheet. Cropping
                            // that as if it were a 64x64 portrait cell
                            // still produced in-bounds numbers but the
                            // wrong pixels for these characters, so detect
                            // this case and use sprite-style single-frame
                            // math instead: use the whole texture rather
                            // than assuming it's a bigger multi-cell sheet.
                            bool looksLikeSprite = tex.Height <= 48;
                            int cellW = looksLikeSprite ? tex.Width : Math.Min(64, tex.Width);
                            int cellH = looksLikeSprite ? tex.Height : Math.Min(64, tex.Height);
                            var srcRect = new Rectangle(0, 0, cellW, cellH);
                            float scale = Math.Min(size.X / cellW, size.Y / cellH);
                            float drawW = cellW * scale;
                            float drawH = cellH * scale;
                            var pos = new Vector2(position.X + (size.X - drawW) / 2f, position.Y + (size.Y - drawH) / 2f);
                            if (_loggedPortraitIssues.Add("npc-draw-detail:" + Name))
                            {
                                ModEntry.SMonitor?.Log($"[SubjectWrapper] NPC portrait draw detail for '{Name}': "
                                        + $"texSize={tex.Width}x{tex.Height}, isDisposed={tex.IsDisposed}, looksLikeSprite={looksLikeSprite}, "
                                        + $"cellSize={cellW}x{cellH}, requestedSize={size.X}x{size.Y}, scale={scale}, "
                                        + $"drawSize={drawW}x{drawH}, drawPos={pos.X},{pos.Y}, "
                                        + $"iconPosition={position.X},{position.Y}", LogLevel.Debug);
                            }
                            b.Draw(tex, pos, srcRect, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                            return true;
                        }

                        // No portrait at all - some "guest"/map-based
                        // characters (e.g. SVE's Adventurer's Guild
                        // Guests, which the game treats more like a map
                        // fixture than a full social NPC) may not use the
                        // normal portrait system. Fall back to their
                        // overworld walking sprite instead, same approach
                        // already working for monsters.
                        if (npcTarget.Sprite?.Texture != null)
                        {
                            var sprite = npcTarget.Sprite;
                            int frameW = sprite.SpriteWidth;
                            int frameH = sprite.SpriteHeight;
                            Rectangle srcRect2;
                            if (SpriteCropOverrides.TryGetValue(npcTarget.Name, out Rectangle cropOverride))
                            {
                                // Confirmed exact pixel coordinates from the
                                // user for characters whose visible frame
                                // isn't at the default (0,0) origin.
                                srcRect2 = cropOverride;
                                frameW = cropOverride.Width;
                                frameH = cropOverride.Height;
                            }
                            else
                            {
                                srcRect2 = new Rectangle(0, 0, frameW, frameH);
                            }
                            float scale2 = Math.Min(size.X / frameW, size.Y / frameH);
                            float drawW2 = frameW * scale2;
                            float drawH2 = frameH * scale2;
                            var pos2 = new Vector2(position.X + (size.X - drawW2) / 2f, position.Y + (size.Y - drawH2) / 2f);
                            if (_loggedPortraitIssues.Add("npc-sprite-fallback:" + Name))
                            {
                                ModEntry.SMonitor?.Log($"[SubjectWrapper] NPC sprite fallback used for '{Name}': "
                                        + $"texSize={sprite.Texture.Width}x{sprite.Texture.Height}, cropRect={srcRect2}", LogLevel.Debug);
                            }
                            b.Draw(sprite.Texture, pos2, srcRect2, Color.White, 0f, Vector2.Zero, scale2, SpriteEffects.None, 1f);
                            return true;
                        }

                        // npcTarget.Sprite itself is null (not just its
                        // Texture) - this happens for characters whose NPC
                        // object was never fully positioned/initialized,
                        // e.g. weather/time-conditional ones like Old
                        // Mariner. Try loading the standard Characters\
                        // texture directly and drawing its first frame,
                        // without relying on the (missing) Sprite object
                        // at all - confirmed the wiki shows real character
                        // art exists for this NPC, so the asset should
                        // still be loadable even if this instance's Sprite
                        // was never set up.
                        try
                        {
                            string characterAssetName = CharacterAssetNameOverrides.TryGetValue(npcTarget.Name, out string? charOverride)
                                    ? charOverride : npcTarget.Name;
                            var directTex = Game1.content.Load<Texture2D>($"Characters\\{characterAssetName}");
                            if (directTex != null)
                            {
                                Rectangle fRect;
                                int fw, fh;
                                if (SpriteCropOverrides.TryGetValue(npcTarget.Name, out Rectangle cropOverride2))
                                {
                                    fRect = cropOverride2;
                                    fw = cropOverride2.Width;
                                    fh = cropOverride2.Height;
                                }
                                else
                                {
                                    // Standard NPC sprite sheets use 16x32
                                    // frames; take the first one.
                                    fw = Math.Min(16, directTex.Width);
                                    fh = Math.Min(32, directTex.Height);
                                    fRect = new Rectangle(0, 0, fw, fh);
                                }
                                float fScale = Math.Min(size.X / fw, size.Y / fh);
                                float fDrawW = fw * fScale;
                                float fDrawH = fh * fScale;
                                var fPos = new Vector2(position.X + (size.X - fDrawW) / 2f, position.Y + (size.Y - fDrawH) / 2f);
                                if (_loggedPortraitIssues.Add("npc-direct-sprite:" + Name))
                                {
                                    ModEntry.SMonitor?.Log($"[SubjectWrapper] Direct Characters\\ load succeeded for '{Name}' "
                                            + $"(internalName='{npcTarget.Name}', Sprite was null): "
                                            + $"texSize={directTex.Width}x{directTex.Height}, cropRect={fRect}", LogLevel.Debug);
                                }
                                b.Draw(directTex, fPos, fRect, Color.White, 0f, Vector2.Zero, fScale, SpriteEffects.None, 1f);
                                return true;
                            }
                        }
                        catch (Exception directEx)
                        {
                            if (_loggedPortraitIssues.Add("npc-direct-sprite-fail:" + Name))
                            {
                                ModEntry.SMonitor?.Log($"[SubjectWrapper] Direct Characters\\ load also failed for '{Name}' (internalName='{npcTarget.Name}'): {directEx.Message}", LogLevel.Debug);
                            }
                        }

                        if (_loggedPortraitIssues.Add("npc-no-visual:" + Name))
                        {
                            ModEntry.SMonitor?.Log($"[SubjectWrapper] '{Name}' has neither a usable Portrait nor a Sprite texture - "
                                    + $"spriteIsNull={npcTarget.Sprite == null}, spriteTextureIsNull={npcTarget.Sprite?.Texture == null}. "
                                    + "No visual can be shown for this NPC.", LogLevel.Debug);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_loggedPortraitIssues.Add("npc-draw:" + Name))
                    {
                        ModEntry.SMonitor?.Log($"[SubjectWrapper] Custom NPC portrait drawing threw for '{Name}': {ex.Message}", LogLevel.Debug);
                    }
                }
            }

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
                    object? mt = GetTarget();
                    bool isChar = mt is StardewValley.Character;
                    bool hasSprite = mt is StardewValley.Character c0 && c0.Sprite != null;
                    bool hasTexture = mt is StardewValley.Character c1 && c1.Sprite?.Texture != null;
                    if (_loggedPortraitIssues.Add("monster-trace:" + Name))
                    {
                        ModEntry.SMonitor?.Log($"[SubjectWrapper] Monster portrait trace for '{Name}': target={(mt == null ? "null" : mt.GetType().FullName)}, "
                                + $"isCharacter={isChar}, hasSprite={hasSprite}, hasTexture={hasTexture}", LogLevel.Debug);
                    }
                    if (mt is StardewValley.Character target && target.Sprite != null)
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
                        // Sanity check: if the reported frame height
                        // exactly equals the WHOLE sheet's height, that's
                        // a strong sign it's incorrectly reporting the
                        // full multi-frame sheet as if it were a single
                        // frame - confirmed this happens for many
                        // monsters, not just ones with exactly 2 frames
                        // stacked. Try dividing by increasing frame
                        // counts until a reasonable (<=64px) size divides
                        // evenly, for both width and height independently.
                        for (int n = 2; n <= 12 && frameH > 64; n++)
                        {
                            if (sprite.Texture.Height % n == 0 && sprite.Texture.Height / n <= 64)
                            {
                                frameH = sprite.Texture.Height / n;
                                break;
                            }
                        }
                        for (int n = 2; n <= 12 && frameW > 64; n++)
                        {
                            if (sprite.Texture.Width % n == 0 && sprite.Texture.Width / n <= 64)
                            {
                                frameW = sprite.Texture.Width / n;
                                break;
                            }
                        }
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
                        if (sprite.Texture == null)
                        {
                            // The sprite's texture isn't loaded into memory
                            // yet - this can happen for hidden/waiting-room
                            // NPCs and freshly-constructed monster previews
                            // that the game never had a normal reason to
                            // render before now. Try loading it directly
                            // into a local variable - AnimatedSprite.Texture
                            // is read-only, so it can't be assigned back
                            // onto the sprite object itself, but we only
                            // need a valid Texture2D for this draw call.
                            Texture2D? loadedTexture = null;
                            try
                            {
                                var texField = sprite.GetType().GetField("textureName",
                                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                        ?? sprite.GetType().GetField("_textureName",
                                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                string? texName = texField?.GetValue(sprite) as string;
                                if (!string.IsNullOrEmpty(texName))
                                    loadedTexture = Game1.content.Load<Texture2D>(texName);
                            }
                            catch (Exception loadEx)
                            {
                                if (_loggedPortraitIssues.Add("texture-load:" + Name))
                                {
                                    ModEntry.SMonitor?.Log($"[SubjectWrapper] Couldn't force-load sprite texture for '{Name}': {loadEx.Message}", LogLevel.Debug);
                                }
                            }
                            if (loadedTexture != null)
                            {
                                b.Draw(loadedTexture, centeredPos, sourceRect, tint, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                                return true;
                            }
                        }
                        if (sprite.Texture == null)
                        {
                            if (_loggedPortraitIssues.Add("null-texture:" + Name))
                            {
                                ModEntry.SMonitor?.Log($"[SubjectWrapper] Sprite texture is still null for '{Name}' after attempting to load it - portrait will be blank.", LogLevel.Debug);
                            }
                        }
                        else
                        {
                            b.Draw(sprite.Texture, centeredPos, sourceRect, tint, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_loggedPortraitIssues.Add("custom-draw:" + Name))
                    {
                        ModEntry.SMonitor?.Log($"[SubjectWrapper] Custom monster portrait drawing threw for '{Name}': {ex.Message}", LogLevel.Debug);
                    }
                }
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

        // Some characters' portrait file name doesn't match their in-game
        // Name - confirmed cases only, not guessed.
        private static readonly Dictionary<string, string> PortraitAssetNameOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Leo"] = "ParrotBoy",
        };

        // Character (overworld sprite) asset name overrides - confirmed
        // directly from an uploaded file: "Old Mariner"'s real asset file
        // is named "Mariner" (no "Old " prefix), same pattern as Leo's
        // portrait being named "ParrotBoy". The file size (~2123 bytes
        // decompressed) matches a small 16x32 sprite far better than a
        // 64x64 portrait, so this override applies to the Characters\
        // path specifically.
        // Search aliases: "flavor name" texture variants confirmed (from
        // the actual mod's own data files) to share the SAME underlying
        // Data/Monsters entry as a vanilla monster - no separate stats or
        // name of their own exists in the game data, they're just a
        // reskin applied at spawn time by the mod's own code. Mapping
        // these lets a player search for the name they actually saw in a
        // quest or in-game encounter (e.g. "Corrupt Bat") and still find
        // the real entry, even though the result will show as the
        // vanilla monster's own name/stats (which is factually accurate -
        // they really are the same monster underneath). Only includes
        // pairings confirmed with reasonably high confidence from actual
        // mod source; many other "Dangerous/Legendary" variant names seen
        // in mod files aren't included yet since their real underlying
        // monster isn't confirmed.
        internal static readonly Dictionary<string, string> MonsterSearchAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Wilderness Golem Spring"] = "Wilderness Golem", ["Wilderness Golem Summer"] = "Wilderness Golem",
            ["Wilderness Golem Fall"] = "Wilderness Golem", ["Wilderness Golem Winter"] = "Wilderness Golem",
            ["Evil Mummy"] = "Mummy",
            ["Corrupt Bat"] = "Bat", ["Evil Bat"] = "Bat", ["Dangerous Bat"] = "Bat",
            ["Skeleton Dangerous"] = "Skeleton", ["Skeleton Mage Dangerous"] = "Skeleton Mage",
            ["Shadow Brute Dangerous"] = "Shadow Brute", ["Shadow Shaman Dangerous"] = "Shadow Shaman",
            ["Stone Golem Dangerous"] = "Stone Golem", ["Dangerous Metal Head"] = "Metal Head",
            ["Dust Spirit Dangerous"] = "Dust Spirit", ["Green Dust Spirit Dangerous"] = "Dust Spirit",
            ["White Dust Spirit Dangerous"] = "Dust Spirit",
        };

        // Spawn-condition descriptions shown as an extra info field on the
        // variant's detail page - only filled in where confirmed from
        // actual mod source (e.g. RSV's location-conditional reskin);
        // generic ones note the general pattern without over-claiming
        // specifics that aren't confirmed.
        // Combat mechanic / behavior tips for monsters with confirmed
        // special mechanics from wiki sources - keyed by InternalName
        // (unlike the variant-only tables above, this applies to any
        // monster, main-table entry or variant). Lookup Anything's own
        // GetDataForMonster only shows Health/Drops/Experience/Defense/
        // Attack - no behavior/strategy info at all, confirmed directly
        // from its own source code.
        // Real HP/Damage confirmed from wiki text and FarmTypeManager's
        // own spawn config - shown as an extra info field since these
        // monsters are constructed from a vanilla base type (to work
        // around having no Data/Monsters entry of their own), so the
        // base game's own Health/Attack display shows the BASE type's
        // stats, not these actual values.
        internal static readonly Dictionary<string, string> MonsterRealStats = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Apophis"] = "20,000 HP, 120 damage (per source; wiki text separately cites 13,000 HP - exact figure may vary by version)",
            ["Badlands Serpent"] = "150-320 HP, 25-35 damage",
            ["Bully Rex"] = "13,000 HP",
            ["Corrupt Mummy"] = "2,000 HP",
            ["Corrupt Serpent"] = "1,300 HP",
            ["Corrupt Spirit"] = "335 HP",
            ["Fallen Adventurer"] = "400 HP",
            ["Legendary Gold Slime"] = "10,000 HP",
            ["Legendary Purple Mushroom Crab"] = "6,000 HP",
            ["Legendary Sand Scorpion"] = "12,000 HP",
            ["Poltergeist"] = "1,000 HP",
            ["Sand Scorpion"] = "80 HP",
            ["Swamp Golem"] = "220 HP",
            ["Swamp Lurk"] = "230 HP",
            ["Swamp Putrid Ghost"] = "750 HP",
            ["Swamp Flower Crab"] = "90 HP, 25 damage",
            ["Toxic Bubble"] = "180 HP",
            ["Royal Badlands Serpent"] = "850 HP, 65 damage",
            ["Beast 1"] = "400 HP",
            ["Beast 2"] = "550 HP",
            ["Beast 3"] = "700 HP",
            ["Corrupted Spirit"] = "60 HP (100 HP in the Corrupted Spirit Realm)",
            ["Serperial"] = "1,500 HP base, +50 HP per tail segment (3-18 segments)",
            ["Viperial"] = "3,000 HP",
            ["Wraith"] = "200 HP",
        };

        internal static readonly Dictionary<string, string> MonsterCombatTips = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Copper Crab"] = "Disguises itself as a stationary copper ore node until approached or struck. Hit its shell with a pickaxe first to break its defense before attacking with a weapon.",
            ["Gold Crab"] = "Disguises itself as a stationary gold ore node until approached or struck. Hit its shell with a pickaxe first to break its (very high) defense before attacking with a weapon.",
            ["Iron Crab"] = "Disguises itself as a stationary iron ore node until approached or struck. Hit its shell with a pickaxe first to break its defense before attacking with a weapon.",
            ["Legendary Purple Mushroom Crab"] = "Appears as an ordinary purple mushroom until attacked or hit with a pickaxe - it cannot be picked up like a real mushroom.",
            ["Royal Badlands Serpent"] = "Has a long segmented body like Royal Serpent, giving it a wide hitbox - any part of its body deals damage on contact and can be damaged when struck.",
            ["Serperial"] = "Its health scales with body length (3-18 tail segments, +50 HP per segment) - any part of its body deals and takes damage on contact.",
            ["Viperial"] = "Pauses movement and breathes a continuous flame blast in one direction when attacking; can turn quickly if you circle to its side too fast. Best approached diagonally, then attack from the side it isn't firing from.",
            ["Wraith"] = "Flies through walls and periodically charges at increased speed. Can inflict a Nauseated debuff (blocks healing from food/drink for 2 minutes) - cure it by eating Ginger or drinking Ginger Ale.",
            ["Corrupted Spirit"] = "Flies straight at the player ignoring obstacles like rocks, cliffs, and water. Teleports to a random spot after landing a hit, then slowly floats back for another strike.",
            ["Beast 1"] = "Resists knockback and has no special attacks - simple to fight by backing away as it approaches.",
            ["Beast 2"] = "Resists knockback and has no special attacks - simple to fight by backing away as it approaches.",
            ["Beast 3"] = "Resists knockback and has no special attacks - simple to fight by backing away as it approaches.",
            ["Toxic Bubble"] = "A flying enemy that actively chases the player through the air.",
        };

        internal static readonly Dictionary<string, string> MonsterVariantSpawnConditions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Corrupt Bat"] = "A recolored appearance of Bat in Stardew Valley Expanded's higher-difficulty areas (exact spawn location not yet confirmed).",
            ["Evil Bat"] = "A recolored appearance of Bat in Stardew Valley Expanded's higher-difficulty areas (exact spawn location not yet confirmed).",
            ["Dangerous Bat"] = "A recolored appearance of Bat used in \"Danger in the Deep\" / danger-mode mine runs.",
            ["Evil Mummy"] = "A recolored appearance of Mummy in Stardew Valley Expanded's higher-difficulty areas (exact spawn location not yet confirmed).",
            ["Skeleton Dangerous"] = "A recolored appearance of Skeleton used in danger-mode mine runs.",
            ["Skeleton Mage Dangerous"] = "A recolored appearance of Skeleton Mage used in danger-mode mine runs.",
            ["Shadow Brute Dangerous"] = "A recolored appearance of Shadow Brute used in danger-mode mine runs.",
            ["Shadow Shaman Dangerous"] = "A recolored appearance of Shadow Shaman used in danger-mode mine runs.",
            ["Stone Golem Dangerous"] = "A recolored appearance of Stone Golem used in danger-mode mine runs.",
            ["Dangerous Metal Head"] = "A recolored appearance of Metal Head used in danger-mode mine runs.",
            ["Dust Spirit Dangerous"] = "A recolored appearance of Dust Spirit in Stardew Valley Expanded's higher-difficulty areas.",
            ["Green Dust Spirit Dangerous"] = "A recolored appearance of Dust Spirit in Stardew Valley Expanded's higher-difficulty areas.",
            ["White Dust Spirit Dangerous"] = "A recolored appearance of Dust Spirit in Stardew Valley Expanded's higher-difficulty areas.",
        };

        private static readonly Dictionary<string, string> CharacterAssetNameOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Old Mariner"] = "Mariner",
        };

        // Characters whose Portrait is known to be unreliable/wrong (e.g.
        // a hidden secret character whose Portrait may show something
        // other than expected) - forces the overworld sprite path instead
        // for JUST these specific names, rather than changing the
        // priority for every NPC (which would regress already-working
        // ones like Cirrus/Roslin, who look better via their Portrait).
        private static readonly HashSet<string> ForceSpriteOverPortrait = new(StringComparer.OrdinalIgnoreCase)
        {
            "JunimoJade",
        };

        // Precise pixel-coordinate crop overrides for characters whose
        // visible sprite frame isn't at the default (0,0) origin of their
        // sheet - confirmed exact coordinates from the user (JunimoJade's
        // 64x224 sheet has its visible 16x16 frame at x17-32, y0-16, not
        // at the top-left corner like most characters).
        private static readonly Dictionary<string, Rectangle> SpriteCropOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["JunimoJade"] = new Rectangle(0, 17, 16, 16),
        };
        private static readonly HashSet<string> _loggedModGroupTrace = new();

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
                if (cat == StardewValley.Object.CookingCategory)
                {
                    return "Food/Drink";
                }
                if (cat == StardewValley.Object.FruitsCategory
                        || cat == StardewValley.Object.VegetableCategory
                        || cat == StardewValley.Object.FishCategory
                        || cat == StardewValley.Object.GreensCategory
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

        // Actually assigns the force-loaded/corrected texture onto the
        // underlying NPC's Portrait property (not just a local variable
        // used only for our own list-icon drawing) - called right before
        // handing the subject off to Lookup Anything's own detail page, so
        // that page's rendering (which we don't control) inspects the
        // SAME character object and finds a valid Portrait already
        // populated, rather than whatever null/fake placeholder it had
        // before.
        public void PrimeVisualData()
        {
            if (GetTarget() is StardewValley.NPC npcTarget) PrimeNpcVisualData(npcTarget);
        }

        // Names known to need visual priming for their detail-page clone -
        // either from the explicit override tables, or the SVE
        // Adventurer's Guild Guests (confirmed fake Portrait by SVE's own
        // source comments) who don't have individual crop/asset overrides
        // but still need their sprite substituted for their fake portrait.
        private static readonly HashSet<string> KnownSveGuestNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Freya", "Gertrude", "Cordelia", "Cassandra", "Sawyer", "Drake",
            "Treyvon", "Brock", "Brianna", "Emin", "Hank", "HankSVE", "Charlie", "Gale", "Edmund",
        };

        public static bool NeedsVisualPriming(string internalName)
        {
            return SpriteCropOverrides.ContainsKey(internalName)
                    || ForceSpriteOverPortrait.Contains(internalName)
                    || CharacterAssetNameOverrides.ContainsKey(internalName)
                    || KnownSveGuestNames.Contains(internalName);
        }

        public static void PrimeNpcVisualData(StardewValley.NPC npcTarget)
        {
            try
            {
                bool needsFix = SpriteCropOverrides.ContainsKey(npcTarget.Name)
                        || ForceSpriteOverPortrait.Contains(npcTarget.Name)
                        || (npcTarget.Portrait != null && npcTarget.Portrait.Height <= 48);

                if (needsFix)
                {
                    // Instead of crafting a fake portrait-shaped image to
                    // satisfy Lookup Anything's portrait-cropping
                    // assumptions, explicitly clear Portrait to null -
                    // monsters have no Portrait at all and Lookup
                    // Anything's own detail page already draws them
                    // correctly (full body, no cropping) by falling back
                    // to their Sprite in that case. Setting Portrait to
                    // null here should trigger that same built-in
                    // fallback for these NPCs too, instead of us trying to
                    // out-guess how it crops/scales a substitute portrait.
                    npcTarget.Portrait = null;
                }

                // Ensure Sprite points to the real texture so whatever
                // fallback rendering Lookup Anything uses (the same path
                // that already works for monsters) has real data to draw.
                if (npcTarget.Sprite?.Texture == null || needsFix)
                {
                    string characterAssetName = CharacterAssetNameOverrides.TryGetValue(npcTarget.Name, out string? charOverride)
                            ? charOverride : npcTarget.Name;
                    try
                    {
                        var tex = Game1.content.Load<Texture2D>($"Characters\\{characterAssetName}");
                        if (tex != null) npcTarget.Sprite = new AnimatedSprite($"Characters\\{characterAssetName}", 0, 16, 32);
                    }
                    catch { /* no sprite available - leave as-is */ }
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[SubjectWrapper] PrimeNpcVisualData failed for '{npcTarget.Name}': {ex.Message}", LogLevel.Trace);
            }
        }

        // Extracts just the given pixel region out of a larger texture
        // into a brand new, correctly-sized Texture2D - needed because
        // Lookup Anything's own portrait rendering draws the WHOLE
        // texture it's given with no cropping of its own, so substituting
        // a full multi-frame sprite sheet (even with a valid crop
        // rectangle recorded elsewhere) showed every frame stacked
        // together instead of just the one relevant frame.
        private static Texture2D CropTexture(Texture2D source, Rectangle region)
        {
            var data = new Color[region.Width * region.Height];
            source.GetData(0, region, data, 0, data.Length);

            // Canvas is 64 wide x 128 tall (per user request) - scale the
            // small cropped sprite frame UP to fill that space
            // (nearest-neighbor, to keep the pixel-art look crisp rather
            // than blurry) instead of pasting it at its tiny natural size.
            // Preserve aspect ratio - scale by the SAME factor on both
            // axes (chosen so the content fits within the 64x64 canvas)
            // and center it, leaving transparent letterboxing on
            // whichever axis has room to spare. Previously each axis was
            // scaled independently to fill the full 64x64 square, which
            // squished tall sprites into short/wide "dwarf" proportions -
            // confirmed directly by the user.
            const int canvasW = 64;
            const int canvasH = 128;
            var canvasData = new Color[canvasW * canvasH];
            float uniformScale = Math.Min((float)canvasW / region.Width, (float)canvasH / region.Height);
            int contentW = (int)(region.Width * uniformScale);
            int contentH = (int)(region.Height * uniformScale);
            int offsetX = (canvasW - contentW) / 2;
            int offsetY = (canvasH - contentH) / 2;
            for (int y = 0; y < contentH; y++)
            {
                int srcY = Math.Min(region.Height - 1, (int)(y / uniformScale));
                for (int x = 0; x < contentW; x++)
                {
                    int srcX = Math.Min(region.Width - 1, (int)(x / uniformScale));
                    canvasData[(y + offsetY) * canvasW + (x + offsetX)] = data[srcY * region.Width + srcX];
                }
            }
            var cropped = new Texture2D(source.GraphicsDevice, canvasW, canvasH);
            cropped.SetData(canvasData);
            return cropped;
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
