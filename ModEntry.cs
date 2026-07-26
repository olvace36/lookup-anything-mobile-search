using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LookupAnythingMobileSearch.Framework;
using LookupAnythingMobileSearch.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData;
using StardewValley.GameData.Objects;
using StardewValley.Monsters;

namespace LookupAnythingMobileSearch
{
    // Exposed via GetApi() so other mods (e.g. BirthdayRolodex) can open the
    // Lookup Anything viewer for a specific NPC directly, without needing a
    // project reference. SMAPI's GetApi<T> matches by method signature, so
    // the caller just needs a same-shaped interface of its own.
    public interface ILookupAnythingMobileSearchApi
    {
        /// <summary>Open the Lookup Anything viewer for the NPC with the
        /// given internal (unlocalized) name. Returns false if the NPC
        /// isn't found or Lookup Anything didn't report a matching search
        /// subject for it.</summary>
        bool ShowNpcByName(string npcInternalName);
    }

    public class ModEntry : Mod, ILookupAnythingMobileSearchApi
    {
        private LookupAnythingBridge? _bridge;
        private List<object>? _monsterSubjectsCache;
        private MobileSearchMenu? _lastSearchMenu;
        private bool _awaitingDetailReturn;
        private PersistenceManager? _persistence;
        internal static IMonitor? SMonitor;
        internal static ITranslationHelper? STranslation;
        internal static ITranslationHelper I18n = null!;

        // ชื่อ class ของ SearchMenu จริงๆ ใน Lookup Anything
        private const string SearchMenuClassName = "SearchMenu";

        public override object? GetApi() => this;

        public override void Entry(IModHelper helper)
        {
            SMonitor = Monitor;
            STranslation = Helper.Translation;
            I18n = helper.Translation;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        }

        public bool ShowNpcByName(string npcInternalName)
        {
            if (_bridge == null || !_bridge.IsValid) {
                return false;
            }
            NPC? npc = Game1.getCharacterFromName(npcInternalName);
            if (npc == null) {
                Monitor.Log($"ShowNpcByName: no NPC found named '{npcInternalName}'", LogLevel.Warn);
                return false;
            }
            string displayName = npc.displayName;

            var subjects = _bridge.GetSearchSubjects();
            if (subjects == null) {
                return false;
            }
            foreach (var raw in subjects)
            {
                var wrapped = SubjectWrapper.Create(raw);
                if (wrapped == null) continue;
                if (wrapped.GetCategory() == "NPCs" && wrapped.Name == displayName)
                {
                    return _bridge.ShowLookupFor(wrapped.RawSubject);
                }
            }
            Monitor.Log($"ShowNpcByName: no matching search subject for '{displayName}'", LogLevel.Warn);
            return false;
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            if (!Helper.ModRegistry.IsLoaded("Pathoschild.LookupAnything"))
            {
                Monitor.Log("Lookup Anything not found — this mod requires it.", LogLevel.Error);
                return;
            }

            _bridge = new LookupAnythingBridge(Monitor, Helper);
            if (!_bridge.IsValid)
            {
                Monitor.Log("Failed to connect to Lookup Anything.", LogLevel.Error);
                return;
            }

            // Hook ตอน menu เปลี่ยน
            Helper.Events.Display.MenuChanged += OnMenuChanged;

            Monitor.Log("LookupAnything Mobile Search ready! ✓", LogLevel.Info);
        }

        // Some monster names don't have their own "Characters\Monsters\{name}"
        // texture - either because they're a vanilla reskin of another
        // monster (recolored by code, not a separate file), or because a
        // mod stores its sprites under a completely different convention.
        // Hand-mapped by inspecting the actual asset files - there's no
        // general way to detect this automatically.
        //
        // Value starting with "@" is a full absolute asset path to use
        // as-is; otherwise it's "use this OTHER monster's standard texture".
        private static readonly Dictionary<string, string> TextureAliases = new()
        {
            // Vanilla monsters that share another monster's texture
            ["Frost Jelly"] = "Green Slime",
            ["Sludge"] = "Green Slime",
            ["Shadow Guy"] = "Shadow Brute",
            ["Skeleton Warrior"] = "Skeleton",

            // Sword & Sorcery (Deep Dark dungeon) - confirmed directly from
            // the mod's own SpaceCore spawner definitions
            // (MonsterTextureOverride field in Dungeon.json).
            // Stygium monsters that use the default 16x24 frame size as-is
            ["Stygium Crab"] = "@Monsters/DN.SnS/StygiumCrab",
            ["Stygium Golem"] = "@Monsters/DN.SnS/StygiumGolem_Purple",
            ["Stygium Golem (Blue)"] = "@Monsters/DN.SnS/StygiumGolem_Blue",
            ["Stygium Bat"] = "@Monsters/DN.SnS/StygiumBat",
            ["Stygium Skull"] = "@Monsters/DN.SnS/StygiumSkull",
            ["Stygium False Mushroom"] = "@Monsters/DN.SnS/StygiumMushroom",
            ["Stygium Droplet"] = "@Monsters/DN.SnS/StygiumDroplet",
            // Stygium Skeleton, Party Skeleton, Miner, Miner Mage, Head,
            // Serpent, Leviathan, Rex, Squid moved to TextureAliasesSized
            // below - they need a non-default frame size.
            // Duskspire Behemoth/Remnant and a few reskin frames
            // (StygiumLurk, StygiumSentry, Stygium_Duggy,
            // StygiumMushroom_Duggy) aren't spawned via this table, so their
            // real monster-name mapping is still unconfirmed - left out.
        };

        // Same as TextureAliases, but for entries needing a non-standard
        // frame size (the alias path, frame width, frame height).
        private static readonly Dictionary<string, (string Path, int Width, int Height)> TextureAliasesSized = new()
        {
            // Sword & Sorcery's Duskspire boss ships its own 96x96 sprite as
            // a mod-internal asset rather than through Content Patcher, so
            // the path depends on the mod's exact UniqueID - best guess from
            // context (DestyNova is the credited CP author); safe no-op via
            // DoesAssetExist below if this guess is wrong.
            // Path/UniqueID verified directly from the mod's manifest
            // ("[SMAPI] Sword & Sorcery" UniqueID is "KCC.SnS", NOT the
            // earlier guessed "DestyNova.SwordAndSorcery"). Frame size is
            // still an unverified guess - this monster is a heavily custom
            // "DuskspireMonster" class that may draw itself via
            // TemporaryAnimatedSprite rather than the standard Sprite
            // field, so this may still not display correctly even with
            // the corrected path.
            ["Duskspire Behemoth"] = ("Mods/KCC.SnS/assets/duskspire-behemoth", 96, 96),

            // Verified directly from the mod's own C# source
            // (PirateGhost.cs constructor): exact texture path and frame
            // size the class itself uses.
            ["mistyspring.GiEXredux/PirateGhost"] = ("Mods/mistyspring.GiEXredux/Monsters/PirateGhost", 16, 32),

            // Stygium monsters that use a MonsterType whose frame size
            // differs from the default 16x24 (confirmed from each vanilla
            // type's own constructor: Skeleton=16x32, MetalHead=16x16,
            // Serpent=32x32, DinoMonster=32x32, BlueSquid=24x24).
            ["Stygium Skeleton"] = ("Monsters/DN.SnS/StygiumSkeleton", 16, 32),
            ["Stygium Party Skeleton"] = ("Monsters/DN.SnS/StygiumSkeleton_Rare", 16, 32),
            ["Stygium Miner"] = ("Monsters/DN.SnS/StygiumMiner", 16, 32),
            ["Stygium Miner Mage"] = ("Monsters/DN.SnS/StygiumMiner_Mage", 16, 32),
            ["Stygium Head"] = ("Monsters/DN.SnS/StygiumHead", 16, 16),
            ["Stygium Serpent"] = ("Monsters/DN.SnS/StygiumSerpent", 32, 32),
            ["Stygium Leviathan"] = ("Monsters/DN.SnS/StygiumLeviathan", 32, 32),
            ["Stygium Rex"] = ("Monsters/DN.SnS/StygiumRex", 32, 32),
            ["Stygium Squid"] = ("Monsters/DN.SnS/StygiumSquid", 24, 24),
        };

        // Tries to fix up a freshly-built fake monster's sprite using the
        // alias tables above. Safe no-op if there's no alias or the aliased
        // asset doesn't exist either.
        private void TryFixMonsterTexture(Monster fake, string monsterName)
        {
            try
            {
                if (TextureAliasesSized.TryGetValue(monsterName, out var sized))
                {
                    if (Game1.content.DoesAssetExist<Texture2D>(sized.Path)) {
                        fake.Sprite = new AnimatedSprite(sized.Path, 0, sized.Width, sized.Height);
                    }
                    return;
                }
                if (!TextureAliases.TryGetValue(monsterName, out string? alias)) {
                    return;
                }
                string path = alias.StartsWith("@") ? alias.Substring(1) : "Characters/Monsters/" + alias;
                if (Game1.content.DoesAssetExist<Texture2D>(path)) {
                    fake.Sprite = new AnimatedSprite(path);
                }
            }
            catch
            {
                // leave the original (possibly blank) sprite as-is
            }
        }

        // Monsters aren't in Lookup Anything's own searchable subject list at
        // all (it only ever builds a monster's info page from a live instance
        // you're actually looking at). We build a throwaway generic Monster
        // for every known monster name (Data/Monsters, via the same data
        // Lookup Anything already parses) purely to hand to its real subject
        // factory, so the resulting page looks and works exactly like
        // looking up a monster you encountered in the field.
        // The likely real root cause of the persistent monster-icon
        // issue: GetMonsterSubjects() was constructing every monster via
        // the generic base Monster(name, position) class regardless of
        // its actual species. Vanilla monster species each have their own
        // C# subclass (StardewValley.Monsters.SquidKid, .Bug, .MetalHead,
        // etc.) whose OWN constructor sets up that species' correct
        // sprite/frame layout - the generic base class doesn't know any
        // of that and just falls back to some default, which explains why
        // the affected monster list was so broad and didn't correlate
        // with any single pattern (size, vanilla vs mod, etc.) - it hit
        // any species whose real sprite setup differs from the generic
        // default. This tries to find and construct the ACTUAL subclass
        // by name (Stardew's naming convention is consistently the
        // display name with spaces removed - "Squid Kid" -> SquidKid),
        // discovering its real constructor via reflection the same way
        // already proven to work for villager NPCs, rather than guessing
        // a signature. Falls back to null (caller uses the generic
        // Monster class as before) for anything this can't resolve -
        // typically modded monster types with their own custom class name
        // that doesn't follow this convention.
        // Resolves a plain item name (e.g. "Void Essence") to its current
        // item ID by searching the game's own object data - avoids
        // hardcoding fragile numeric IDs that can change between game
        // versions. Cached after first build since object data doesn't
        // change during a session.
        private static Dictionary<string, string>? _itemDisplayNameToIdCache;
        private static Dictionary<string, string>? _multiCategoryNameToIdCache;

        // (DataLoader method name, qualified-ID prefix) for each
        // additional item category beyond plain Objects - checked via
        // reflection so a category that doesn't exist in a given game
        // version just gets skipped instead of breaking the others.
        private static readonly (string method, string prefix)[] ExtraCategorySources =
        {
            ("Furniture", "(F)"),
            ("Clothing", "(CL)"),
            ("Hats", "(H)"),
            ("Boots", "(B)"),
            ("Weapons", "(W)"),
            ("BigCraftables", "(BC)"),
            ("Trinkets", "(TR)"),
        };

        private static void BuildMultiCategoryCache()
        {
            _multiCategoryNameToIdCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Type? dataLoaderType = typeof(Game1).Assembly.GetType("StardewValley.GameData.DataLoader");
            if (dataLoaderType == null)
            {
                SMonitor?.Log("Couldn't resolve DataLoader type - only plain Objects will be searchable for drop icons.", LogLevel.Trace);
                return;
            }
            foreach (var (methodName, prefix) in ExtraCategorySources)
            {
                try
                {
                    var method = dataLoaderType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (method == null)
                    {
                        // Fallback: manually search all public static
                        // methods case-insensitively, in case GetMethod's
                        // IgnoreCase flag doesn't behave as expected for
                        // this overload resolution.
                        foreach (var candidate in dataLoaderType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase))
                            {
                                method = candidate;
                                break;
                            }
                        }
                    }
                    if (method == null)
                    {
                        SMonitor?.Log($"Couldn't find DataLoader.{methodName} (tried case-insensitive too) - '{prefix}' category items won't be searchable for drop icons.", LogLevel.Trace);
                        continue;
                    }
                    object? dict = method.Invoke(null, new object?[] { Game1.content });
                    int countBefore = _multiCategoryNameToIdCache.Count;
                    if (dict is System.Collections.IDictionary idict)
                    {
                        foreach (System.Collections.DictionaryEntry entry in idict)
                        {
                            string key = entry.Key?.ToString() ?? "";
                            var nameProp = entry.Value?.GetType().GetField("Name") as MemberInfo
                                    ?? entry.Value?.GetType().GetProperty("Name");
                            string? name = nameProp switch
                            {
                                FieldInfo fi => fi.GetValue(entry.Value) as string,
                                PropertyInfo pi => pi.GetValue(entry.Value) as string,
                                _ => null
                            };
                            string qualified = prefix + key;
                            if (!string.IsNullOrEmpty(name) && !_multiCategoryNameToIdCache.ContainsKey(name))
                                _multiCategoryNameToIdCache[name] = qualified;

                            // Also index by resolved DisplayName, same
                            // reasoning as the Object-category fallback.
                            try
                            {
                                var item = ItemRegistry.Create(qualified, 1, 0, false);
                                string? disp = item?.DisplayName;
                                if (!string.IsNullOrEmpty(disp) && !_multiCategoryNameToIdCache.ContainsKey(disp))
                                    _multiCategoryNameToIdCache[disp] = qualified;
                            }
                            catch { /* skip items that fail to construct */ }
                        }
                        SMonitor?.Log($"Loaded {_multiCategoryNameToIdCache.Count - countBefore} '{prefix}' category item names for drop-icon lookup.", LogLevel.Trace);
                    }
                }
                catch (Exception ex)
                {
                    SMonitor?.Log($"Couldn't load '{methodName}' category for drop icons: {ex.Message}", LogLevel.Trace);
                }
            }
        }

        // Overrides a constructed monster's Health/MaxHealth/DamageToFarmer/
        // resilience(Defense) fields with confirmed real values, so Lookup
        // Anything's own native stat display shows correct numbers. -1 in
        // any field means "unconfirmed" and is left alone (keeps showing
        // the base type's own value for that specific stat only).
        private void ApplyRealNumericStatsIfKnown(Monster fake, string name, string? baseTypeForXp = null)
        {
            if (SubjectWrapper.MonsterRealNumericStats.TryGetValue(name, out var realNums))
            {
                if (realNums.hp >= 0)
                {
                    try { fake.MaxHealth = realNums.hp; fake.Health = realNums.hp; } catch { }
                }
                if (realNums.dmg >= 0)
                {
                    try { fake.DamageToFarmer = realNums.dmg; } catch { }
                }
                if (realNums.def >= 0)
                {
                    try
                    {
                        var resilienceField = fake.GetType().GetField("resilience", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                ?? typeof(Monster).GetField("resilience", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        object? netInt = resilienceField?.GetValue(fake);
                        netInt?.GetType().GetProperty("Value")?.SetValue(netInt, realNums.def);
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"Couldn't override resilience/Defense for '{name}': {ex.Message}", LogLevel.Trace);
                    }
                }
            }

            // No mod config ever customizes XP for these reconstructed
            // monsters - they should always show their base type's own
            // XP, but a construction quirk sometimes leaves it at 0.
            // Check for a monster-specific confirmed XP first (e.g. each
            // slime color has its own real XP despite sharing a base
            // type), then fall back to the base type's own XP.
            if (SubjectWrapper.MonsterSpecificXP.TryGetValue(name, out int specificXp))
            {
                try { fake.ExperienceGained = specificXp; } catch { }
            }
            else if (baseTypeForXp != null && SubjectWrapper.BaseTypeXP.TryGetValue(baseTypeForXp, out int xp))
            {
                try { fake.ExperienceGained = xp; } catch { }
            }
        }

        private static string? ResolveItemIdByName(string itemName)
        {
            try
            {
                if (_itemNameToIdCache == null)
                {
                    _itemNameToIdCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in Game1.content.Load<Dictionary<string, ObjectData>>("Data/Objects"))
                    {
                        string? dispName = kv.Value?.Name;
                        if (!string.IsNullOrEmpty(dispName) && !_itemNameToIdCache.ContainsKey(dispName))
                            _itemNameToIdCache[dispName] = kv.Key;
                    }
                }
                if (_itemNameToIdCache.TryGetValue(itemName, out string? id))
                    return "(O)" + id;

                // Fallback: many mod items only match by their resolved
                // (localized) DisplayName, not the internal Name field -
                // e.g. SVE/RSV items are often registered with a
                // no-space or prefixed internal Name. Build this second,
                // more expensive cache lazily only once it's needed.
                if (_itemDisplayNameToIdCache == null)
                {
                    _itemDisplayNameToIdCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in Game1.content.Load<Dictionary<string, ObjectData>>("Data/Objects"))
                    {
                        try
                        {
                            var item = ItemRegistry.Create("(O)" + kv.Key, 1, 0, false);
                            string? disp = item?.DisplayName;
                            if (!string.IsNullOrEmpty(disp) && !_itemDisplayNameToIdCache.ContainsKey(disp))
                                _itemDisplayNameToIdCache[disp] = kv.Key;
                        }
                        catch { /* skip items that fail to construct */ }
                    }
                }
                if (_itemDisplayNameToIdCache.TryGetValue(itemName, out string? id2))
                    return "(O)" + id2;

                // Last resort: search other item categories (Furniture,
                // Clothing, Hats, Boots, Weapons) via DataLoader, for
                // drops that aren't plain Objects (e.g. clothing/mannequin
                // monster loot).
                if (_multiCategoryNameToIdCache == null)
                    BuildMultiCategoryCache();
                return _multiCategoryNameToIdCache!.TryGetValue(itemName, out string? id3) ? id3 : null;
            }
            catch (Exception ex)
            {
                SMonitor?.Log($"Couldn't resolve item ID for '{itemName}': {ex.Message}", LogLevel.Trace);
                return null;
            }
        }

        private Monster? TryConstructSpecificMonsterType(string name)
        {
            string className = string.Concat(name.Where(c => !char.IsWhiteSpace(c) && c != '\'' && c != '.'));
            Type? monsterType = typeof(Monster).Assembly.GetType($"StardewValley.Monsters.{className}");
            if (monsterType == null || !typeof(Monster).IsAssignableFrom(monsterType)) return null;

            var constructors = monsterType.GetConstructors()
                    .OrderBy(c => c.GetParameters().Length)
                    .ToArray();

            foreach (var ctor in constructors)
            {
                var pars = ctor.GetParameters();
                var args = new object?[pars.Length];
                bool ok = true;
                foreach (var (p, i) in pars.Select((p, i) => (p, i)))
                {
                    Type t = p.ParameterType;
                    string pn = p.Name?.ToLowerInvariant() ?? "";
                    if (t == typeof(Vector2)) args[i] = Vector2.Zero;
                    else if (t == typeof(string)) args[i] = name;
                    else if (t == typeof(int)) args[i] = 0;
                    else if (t == typeof(bool)) args[i] = false;
                    else if (t == typeof(float)) args[i] = 0f;
                    else if (!t.IsValueType) args[i] = null;
                    else
                    {
                        try { args[i] = Activator.CreateInstance(t); }
                        catch { ok = false; break; }
                    }
                }
                if (!ok) continue;

                try
                {
                    if (ctor.Invoke(args) is Monster m)
                    {
                        // Same lesson learned from the NPC construction
                        // crash: guarantee Name is never null/empty
                        // regardless of which constructor overload matched.
                        try { if (string.IsNullOrEmpty(m.Name)) m.Name = name; } catch { }
                        if (string.IsNullOrEmpty(m.Name)) continue;
                        return m;
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Specific-type construction attempt failed for monster '{name}' as {className} "
                            + $"({pars.Length} params): {ex.InnerException?.Message ?? ex.Message}", LogLevel.Trace);
                }
            }
            return null;
        }

        private static bool _variantPatchApplied;
        private static Type? _genericFieldType;
        private static Type? _itemDropListFieldType;
        private static Type? _itemDropDataType;
        private static Dictionary<string, string>? _itemNameToIdCache;
        private static Type? _iCustomFieldType;

        // Patches the subject's own GetData method (same technique already
        // proven working in the companion ItemSources mod) to append a
        // "found as this variant" info field for our registered variant
        // entries (e.g. explaining where/when "Corrupt Bat" specifically
        // appears, on top of the real "Bat" data LA already shows).
        // Applied lazily the first time we have a real subject instance,
        // since we need its runtime type to know what to patch.
        private void EnsureVariantInfoPatchApplied(object sampleSubject)
        {
            if (_variantPatchApplied) return;
            _variantPatchApplied = true;
            try
            {
                Assembly? asm = _bridge?.LookupAnythingAssembly;
                if (asm == null) return;
                const string ns = "Pathoschild.Stardew.LookupAnything.Framework";
                _genericFieldType = asm.GetType($"{ns}.Fields.GenericField");
                _iCustomFieldType = asm.GetType($"{ns}.Fields.ICustomField");
                _itemDropListFieldType = asm.GetType($"{ns}.Fields.ItemDropListField");
                _itemDropDataType = asm.GetType($"{ns}.Data.ItemDropData");
                if (_itemDropListFieldType == null || _itemDropDataType == null)
                {
                    Monitor.Log("Couldn't resolve ItemDropListField/ItemDropData types - drop lists will stay as plain text.", LogLevel.Trace);
                }
                if (_genericFieldType == null || _iCustomFieldType == null)
                {
                    Monitor.Log("Couldn't resolve field types for variant info patch.", LogLevel.Trace);
                    return;
                }

                Type subjectType = sampleSubject.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo? getData = subjectType.GetMethod("GetData", flags, null, Type.EmptyTypes, null);
                if (getData == null)
                {
                    Monitor.Log("Couldn't find GetData method to patch for variant info.", LogLevel.Trace);
                    return;
                }

                var harmony = new Harmony("olvace36.LookupAnythingMobileSearch");
                harmony.Patch(getData, postfix: new HarmonyMethod(typeof(ModEntry), nameof(GetData_VariantInfoPostfix)));
            }
            catch (Exception ex)
            {
                Monitor.Log("Couldn't apply variant-info Harmony patch: " + ex.Message, LogLevel.Warn);
            }
        }

        private static void GetData_VariantInfoPostfix(object __instance, ref object __result)
        {
            try
            {
                if (_genericFieldType == null || _iCustomFieldType == null) return;

                var extraFields = new List<object>();

                if (SubjectWrapper.TryGetVariantSpawnCondition(__instance, out string? displayName, out string? condition) && condition != null)
                {
                    extraFields.Add(Activator.CreateInstance(_genericFieldType,
                            new object?[] { $"{displayName} appears", condition, null })!);
                }

                var wrapped = SubjectWrapper.Create(__instance);

                if (wrapped != null && SubjectWrapper.MonsterStatsI18nKeys.TryGetValue(wrapped.InternalName, out string? statsKey) && STranslation != null)
                {
                    string realStats = STranslation.Get(statsKey).Default("");
                    if (!string.IsNullOrEmpty(realStats))
                    {
                        string label = STranslation.Get("field.actual-stats").Default("Actual stats (per wiki/mod data)");
                        extraFields.Add(Activator.CreateInstance(_genericFieldType,
                                new object?[] { label, realStats, null })!);
                    }
                }

                if (wrapped != null && SubjectWrapper.MonsterVariantStatsI18nKeys.TryGetValue(wrapped.InternalName, out string? variantStatsKey) && STranslation != null)
                {
                    string variantStats = STranslation.Get(variantStatsKey).Default("");
                    if (!string.IsNullOrEmpty(variantStats))
                    {
                        string label = STranslation.Get("field.actual-stats").Default("Actual stats (per wiki/mod data)");
                        extraFields.Add(Activator.CreateInstance(_genericFieldType,
                                new object?[] { label, variantStats, null })!);
                    }
                }

                if (wrapped != null && SubjectWrapper.MonsterTipsI18nKeys.TryGetValue(wrapped.InternalName, out string? tipsKey) && STranslation != null)
                {
                    string tip = STranslation.Get(tipsKey).Default("");
                    if (!string.IsNullOrEmpty(tip))
                    {
                        string label = STranslation.Get("field.combat-tips").Default("Combat tips");
                        extraFields.Add(Activator.CreateInstance(_genericFieldType,
                                new object?[] { label, tip, null })!);
                    }
                }

                if (wrapped != null && wrapped.GetCategory() == "Monsters" && STranslation != null)
                {
                    string label = STranslation.Get("field.mines-bottom-bonus").Default("Bonus if you've reached the bottom of the Mines");
                    string text = STranslation.Get("monster.mines-bottom-bonus-text").Default("Any monster you kill after reaching floor 120 of the Mines has a small extra chance to drop a Diamond (0.05%) or Prismatic Shard (0.05%), regardless of what it normally drops.");
                    extraFields.Add(Activator.CreateInstance(_genericFieldType,
                            new object?[] { label, text, null })!);
                }

                if (wrapped != null && _itemDropListFieldType != null && _itemDropDataType != null
                        && SubjectWrapper.MonsterStructuredDrops.TryGetValue(wrapped.InternalName, out var dropList))
                {
                    try
                    {
                        // Extract GameHelper (protected property, inherited
                        // from BaseSubject) and Codex (private field on
                        // CharacterSubject) via reflection - confirmed from
                        // the mod's own source that these are exactly what
                        // ItemDropListField's constructor needs.
                        object? gameHelper = __instance.GetType().GetProperty("GameHelper",
                                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)?.GetValue(__instance);
                        object? codex = __instance.GetType().GetField("Codex",
                                BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);

                        if (gameHelper != null && codex != null)
                        {
                            Array dropsArray = Array.CreateInstance(_itemDropDataType, dropList.Count);
                            int written = 0;
                            foreach (var (itemName, min, max, chance) in dropList)
                            {
                                string? itemId = ResolveItemIdByName(itemName);
                                if (itemId == null)
                                {
                                    SMonitor?.Log($"Couldn't resolve item ID for drop '{itemName}' on '{wrapped.InternalName}' - skipped from clickable list.", LogLevel.Trace);
                                    continue;
                                }
                                object dropData = Activator.CreateInstance(_itemDropDataType,
                                        new object?[] { itemId, min, max, chance, null })!;
                                dropsArray.SetValue(dropData, written++);
                            }
                            if (written > 0)
                            {
                                Array trimmed = Array.CreateInstance(_itemDropDataType, written);
                                Array.Copy(dropsArray, trimmed, written);
                                string dropsLabel = "Drops";
                                object field = Activator.CreateInstance(_itemDropListFieldType,
                                        new object?[] { gameHelper, codex, dropsLabel, trimmed, true, false, false, null, null })!;
                                extraFields.Add(field);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SMonitor?.Log($"Couldn't build clickable drop list for '{wrapped.InternalName}': {ex.Message}", LogLevel.Trace);
                    }
                }

                if (extraFields.Count == 0) return;

                Array extraArray = Array.CreateInstance(_iCustomFieldType, extraFields.Count);
                for (int i = 0; i < extraFields.Count; i++) extraArray.SetValue(extraFields[i], i);

                MethodInfo concat = typeof(Enumerable).GetMethods()
                        .First(m => m.Name == "Concat" && m.GetParameters().Length == 2)
                        .MakeGenericMethod(_iCustomFieldType);
                __result = concat.Invoke(null, new object[] { __result, extraArray })!;
            }
            catch (Exception ex)
            {
                SMonitor?.Log("Error adding variant/combat-tip field: " + ex.Message, LogLevel.Trace);
            }
        }

        private List<object> GetMonsterSubjects()
        {
            if (_monsterSubjectsCache != null) {
                return _monsterSubjectsCache;
            }
            var result = new List<object>();
            List<string>? names = _bridge?.GetMonsterNames();
            if (names == null) {
                names = new List<string>();
            }
            // Merge in monster names we've confirmed exist through actual
            // research (wiki data, mod source files) even if they aren't
            // discoverable through Data/Monsters - confirmed some mods
            // (e.g. SVE's "new species" like Apophis) implement their
            // monsters via custom code rather than registering them in
            // Data/Monsters at all, so GetMonsterNames() (which reads
            // Data/Monsters) never sees them.
            names = names.Concat(SubjectWrapper.MonsterNameToModName.Keys)
                    .Concat(SubjectWrapper.SveMonsterBaseType.Keys)
                    // Confirmed real, current vanilla monsters that
                    // Lookup Anything's own GetMonsterNames() never
                    // returns for some reason - attempted directly here
                    // instead of relying solely on its enumeration.
                    .Concat(new[] { "Armored Bug", "Assassin Bug", "Haunted Skull", "Mutant Fly", "Mutant Grub", "Shadow Girl", "Stick Bug", "Angry Roger", "Wilderness Golem" })
                    .Distinct().ToList();
            foreach (string name in names)
            {
                try
                {
                    // For SVE's custom monsters (which have no
                    // Data/Monsters entry of their own - confirmed from
                    // FarmTypeManager's spawn config), construct using
                    // their confirmed vanilla base type instead, then
                    // swap in the correct custom texture. Constructing by
                    // the custom name directly always failed with "key
                    // not present in the dictionary" since the game has
                    // no stats data to look up for these at all.
                    string constructName = SubjectWrapper.SveMonsterBaseType.TryGetValue(name, out string? baseType)
                            ? baseType : name;
                    Monster fake = TryConstructSpecificMonsterType(constructName) ?? new Monster(constructName, Vector2.Zero);
                    if (baseType != null)
                    {
                        // This is a custom-sprite variant built on a
                        // vanilla base - force-load its real texture
                        // (registered under Characters/Monsters/{name
                        // with spaces stripped}, confirmed from the
                        // mod's own Content Patcher "Load" actions).
                        try
                        {
                            // RSV's file naming convention prefixes "RSV"
                            // (e.g. "RSVSerperial.png"), unlike SVE's
                            // plain concatenated-name convention.
                            bool isRsv = name is "Serperial" or "Viperial" or "Wraith" or "Corrupted Spirit" or "Beast 1" or "Beast 2" or "Beast 3";
                            string strippedKey = (isRsv ? "RSV" : "") + string.Concat(name.Where(c => !char.IsWhiteSpace(c)));
                            // Try several real-world naming variations in
                            // order, since confirmed file names don't all
                            // follow the same convention (e.g. "Armored
                            // Bug.png" keeps its space, "ESMineBats.png"
                            // has a trailing 's' the internal name lacks).
                            string[] candidates = name switch
                            {
                                "Toxic Bubble (Weak Variant)" => new[] { "ToxicBubble_Variant" },
                                "ES Mine Bat Iridium" => new[] { "ESMineBatsIridium", strippedKey },
                                _ => new[] { strippedKey, name, strippedKey + "s", name + "s" },
                            };
                            Texture2D? tex = null;
                            string? matchedKey = null;
                            foreach (string candidate in candidates.Distinct())
                            {
                                try
                                {
                                    tex = Game1.content.Load<Texture2D>($"Characters/Monsters/{candidate}");
                                    matchedKey = candidate;
                                    break;
                                }
                                catch { /* try next candidate */ }
                            }
                            if (tex != null && matchedKey != null && fake.Sprite != null)
                                fake.Sprite = new AnimatedSprite($"Characters/Monsters/{matchedKey}", 0, fake.Sprite.SpriteWidth, fake.Sprite.SpriteHeight);
                        }
                        catch (Exception texEx)
                        {
                            Monitor.Log($"Couldn't load custom texture for '{name}' (base '{baseType}'): {texEx.Message}", LogLevel.Trace);
                        }
                    }
                    TryFixMonsterTexture(fake, constructName);
                    if (baseType != null)
                    {
                        // Restore the real display/internal name - the
                        // base-type construction above set it to the
                        // vanilla name (e.g. "Royal Serpent"), which would
                        // otherwise make this show up as vanilla instead
                        // of correctly classified as its real SVE name.
                        try { fake.Name = name; } catch { }
                        try
                        {
                            // Lookup Anything's title/list display uses
                            // Character.getName(), which returns the
                            // separately-cached displayName property
                            // rather than re-deriving from .Name -
                            // confirmed as a public property directly
                            // from the game's own DLL (get_displayName/
                            // set_displayName both exist), so it can be
                            // set directly without reflection.
                            fake.displayName = name;
                        }
                        catch (Exception ex)
                        {
                            Monitor.Log($"Couldn't override displayName for '{name}': {ex.Message}", LogLevel.Trace);
                        }

                        // Override the actual stat fields with confirmed
                        // real numbers where we have them, so Lookup
                        // Anything's own native HP/Damage display is
                        // correct instead of showing the base type's.
                        ApplyRealNumericStatsIfKnown(fake, name, baseType);
                        if (fake is StardewValley.Monsters.GreenSlime slimeInstance)
                        {
                            Microsoft.Xna.Framework.Color? slimeColor = name switch
                            {
                                "Sludge" => new Microsoft.Xna.Framework.Color(230, 40, 40),
                                "Frost Jelly" => new Microsoft.Xna.Framework.Color(60, 130, 255),
                                "Purple Slime" => new Microsoft.Xna.Framework.Color(160, 60, 200),
                                "Copper Slime" => new Microsoft.Xna.Framework.Color(190, 110, 60),
                                "Iron Slime" => new Microsoft.Xna.Framework.Color(180, 180, 190),
                                _ => null,
                            };
                            if (slimeColor.HasValue)
                            {
                                try { slimeInstance.color.Value = slimeColor.Value; } catch { }
                            }
                        }
                    }
                    // Force a clean idle frame right when we build this
                    // instance - not just when OUR OWN list code later
                    // draws it. The detail page the player opens after
                    // selecting this exact entry reads its portrait
                    // straight from THIS SAME instance's current
                    // animation state (it's the same object, not a fresh
                    // "real" encounter), so fixing the frame only inside
                    // our own draw call never affected what the detail
                    // page shows - this needs to happen at creation time
                    // to help both.
                    try { fake.Sprite.CurrentFrame = 0; } catch { }
                    object? subject = _bridge!.GetSubjectFor(fake);
                    if (subject != null) {
                        EnsureVariantInfoPatchApplied(subject);
                        result.Add(subject);
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Skipped monster '{name}' (couldn't build a preview instance): {ex.Message}", LogLevel.Trace);
                }
            }
            // Add known texture-variant "aliases" as their OWN distinct
            // search entries too (e.g. "Corrupt Bat" appears as its own
            // result, showing its actual in-game texture and name), not
            // just as a search-shortcut to the real "Bat" entry. Built by
            // wrapping ANOTHER instance of the real underlying monster
            // (so its stats/wiki-style info stay accurate - they really
            // are the same monster under the hood) but overriding the
            // display name and icon to the variant's own.
            foreach (var kv in SubjectWrapper.MonsterSearchAliases)
            {
                string variantName = kv.Key;
                string realName = kv.Value;
                try
                {
                    Monster variantFake = TryConstructSpecificMonsterType(realName) ?? new Monster(realName, Vector2.Zero);
                    TryFixMonsterTexture(variantFake, realName);
                    try { variantFake.Sprite.CurrentFrame = 0; } catch { }
                    // Restore the variant's own name - constructing from
                    // realName (e.g. "Bat") leaves .Name/.displayName as
                    // that base name, which would otherwise make the
                    // detail page show "Bat" instead of "Corrupt Bat"
                    // even though the search list (via SubjectWrapper's
                    // own name override) already showed it correctly.
                    try { variantFake.Name = variantName; } catch { }
                    try { variantFake.displayName = variantName; } catch { }
                    ApplyRealNumericStatsIfKnown(variantFake, variantName, realName);
                    object? variantSubject = _bridge!.GetSubjectFor(variantFake);
                    if (variantSubject == null) continue;

                    Texture2D? variantTex = null;
                    string assetKey = string.Concat(variantName.Where(c => !char.IsWhiteSpace(c)));
                    try { variantTex = Game1.content.Load<Texture2D>($"Characters/Monsters/{assetKey}"); }
                    catch (Exception texEx)
                    {
                        Monitor.Log($"Couldn't load variant texture for '{variantName}' (tried '{assetKey}'): {texEx.Message}", LogLevel.Trace);
                    }

                    string? condition = null;
                    if (SubjectWrapper.MonsterVariantConditionI18nKeys.TryGetValue(variantName, out string? conditionKey) && STranslation != null)
                    {
                        string resolved = STranslation.Get(conditionKey).Default("");
                        if (!string.IsNullOrEmpty(resolved)) condition = resolved;
                    }
                    SubjectWrapper.RegisterVariant(variantSubject, variantName, realName, variantTex, null, condition);
                    result.Add(variantSubject);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Skipped monster variant '{variantName}': {ex.Message}", LogLevel.Trace);
                }
            }

            Monitor.Log($"Built {result.Count} monster subjects for search.", LogLevel.Debug);
            _monsterSubjectsCache = result;
            return result;
        }

        private List<object>? _villagerSubjectsCache;

        // Data/Characters lists every villager regardless of unlock
        // status, but Lookup Anything's own search list only includes
        // ones the player has actually met - meaning a locked NPC's
        // unlock condition (the whole point of the info we added) can
        // never be looked up until after it no longer matters. Most
        // "locked" NPCs (Cirrus, Roslin, Eyvinder, etc.) already exist as
        // real spawned instances sitting in a hidden waiting-room map -
        // Game1.getCharacterFromName finds those directly with zero
        // construction risk. Only the rarer NPC that truly hasn't been
        // instantiated yet (e.g. one gated by a mail-flag UnlockConditions
        // with no waiting-room home) needs an actual constructed preview,
        // which is attempted carefully and skipped silently on failure.
        private List<object> GetAllVillagerSubjects()
        {
            if (_villagerSubjectsCache != null) return _villagerSubjectsCache;
            var result = new List<object>();
            if (_bridge == null) { _villagerSubjectsCache = result; return result; }

            try
            {
                object rawData = Game1.content.Load<object>("Data/Characters");
                var keysProp = rawData.GetType().GetProperty("Keys");
                if (keysProp?.GetValue(rawData) is System.Collections.IEnumerable keys)
                {
                    foreach (object k in keys)
                    {
                        string? name = k?.ToString();
                        if (name == null) continue;
                        try
                        {
                            NPC? npc = Game1.getCharacterFromName(name);
                            if (npc == null)
                            {
                                npc = TryConstructNpcDynamically(name);
                            }
                            if (npc == null) continue;

                            object? subject = _bridge!.GetSubjectFor(npc);
                            if (subject != null) result.Add(subject);
                        }
                        catch (Exception ex)
                        {
                            Monitor.Log($"Skipped villager '{name}' while building the full NPC list: {ex.Message}", LogLevel.Trace);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log("Error loading Data/Characters for the full NPC list: " + ex.Message, LogLevel.Warn);
            }

            // SnS "temporary actor" Stygium sprites - confirmed from the
            // mod's own TemporaryActors.json to reuse the same monster
            // art but registered as event/cutscene-only actors, with NO
            // CharacterData entry at all (unlike JunimoJade, which had
            // hidden-but-real CharacterData). TryConstructNpcDynamically
            // relies on reading CharacterData, so it won't work here -
            // build a bare-minimum NPC directly instead and force its
            // sprite/portrait from the confirmed Characters\{name} path.
            string[] sansTempActorNames = { "StygiumLurk", "StygiumSentry", "Stygium_Duggy",
                    "StygiumGolem_Purple", "StygiumMushroom", "StygiumMushroom_Duggy", "StygiumSkeleton_Rare" };
            foreach (string rawName in sansTempActorNames)
            {
                try
                {
                    Texture2D? portraitTex = null;
                    try { portraitTex = Game1.content.Load<Texture2D>($"Portraits\\{rawName}"); } catch { /* no portrait - fine, pass null */ }
                    var npc = new NPC(new AnimatedSprite($"Characters\\{rawName}", 0, 16, 32),
                            Vector2.Zero * 64f, "Custom_DeepDark", 2, rawName, false, portraitTex);
                    try { npc.displayName = rawName.Replace("Stygium", "Stygium ").Replace("_", " ").Trim(); } catch { }
                    object? subject = _bridge!.GetSubjectFor(npc);
                    if (subject != null) result.Add(subject);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Skipped SnS temp-actor '{rawName}': {ex.Message}", LogLevel.Trace);
                }
            }

            Monitor.Log($"Built {result.Count} villager subjects for search (including locked/unmet ones).", LogLevel.Debug);
            _villagerSubjectsCache = result;
            return result;
        }

        // Instead of hardcoding NPC's constructor signature (guessed
        // wrong three times in a row without decompiled source to verify
        // against), this discovers the REAL constructors at runtime via
        // reflection and tries each one, building argument values by
        // matching each parameter's type and name to something sensible.
        // This adapts to whatever the actual signature is instead of
        // requiring it to be known in advance.
        private NPC? TryConstructNpcDynamically(string name)
        {
            string? textureName = null;
            string? portraitName = null;
            try
            {
                object rawData = Game1.content.Load<object>("Data/Characters");
                if (rawData is System.Collections.IDictionary dict && dict.Contains(name))
                {
                    object? data = dict[name];
                    if (data != null)
                    {
                        textureName = data.GetType().GetProperty("Texture")?.GetValue(data) as string;
                        portraitName = data.GetType().GetProperty("Portrait")?.GetValue(data) as string;
                    }
                }
            }
            catch { }
            textureName ??= $"Characters\\{name}";
            portraitName ??= $"Portraits\\{name}";

            AnimatedSprite? sprite = null;
            try { sprite = new AnimatedSprite(textureName, 0, 16, 32); } catch { }
            Texture2D? portrait = null;
            try { portrait = Game1.content.Load<Texture2D>(portraitName); } catch { }

            var constructors = typeof(NPC).GetConstructors()
                    .OrderBy(c => c.GetParameters().Length)
                    .ToArray();

            foreach (var ctor in constructors)
            {
                var pars = ctor.GetParameters();
                var args = new object?[pars.Length];
                bool ok = true;
                foreach (var (p, i) in pars.Select((p, i) => (p, i)))
                {
                    string pn = p.Name?.ToLowerInvariant() ?? "";
                    Type t = p.ParameterType;
                    if (t == typeof(AnimatedSprite)) args[i] = sprite;
                    else if (t == typeof(Texture2D)) args[i] = portrait;
                    else if (t == typeof(Vector2)) args[i] = Vector2.Zero;
                    else if (t == typeof(string) && pn.Contains("name") && !pn.Contains("map") && !pn.Contains("texture")) args[i] = name;
                    else if (t == typeof(string) && (pn.Contains("map") || pn.Contains("location"))) args[i] = "Town";
                    else if (t == typeof(string)) args[i] = name;
                    else if (t == typeof(int)) args[i] = pn.Contains("facing") || pn.Contains("direction") ? 2 : 0;
                    else if (t == typeof(bool)) args[i] = false;
                    else if (!t.IsValueType) args[i] = null; // reference types (schedules, callbacks, etc.) default to null
                    else
                    {
                        try { args[i] = Activator.CreateInstance(t); }
                        catch { ok = false; break; }
                    }
                }
                if (!ok) continue;
                if (sprite == null && pars.Any(p => p.ParameterType == typeof(AnimatedSprite))) continue;

                try
                {
                    if (ctor.Invoke(args) is NPC npc)
                    {
                        // Guarantee Name is set regardless of which
                        // constructor overload succeeded - a shorter
                        // overload with no name parameter at all would
                        // otherwise leave this null, which crashed every
                        // subsequent Dictionary lookup keyed on it
                        // (confirmed directly from a real crash log).
                        try { if (string.IsNullOrEmpty(npc.Name)) npc.Name = name; } catch { }
                        if (string.IsNullOrEmpty(npc.Name)) continue; // still null somehow - skip rather than risk another crash

                        // Explicitly (re-)assign Portrait after
                        // construction, regardless of whether this
                        // specific overload had a Texture2D parameter to
                        // receive it through - confirmed from a real log
                        // trace that NPCs built via a shorter overload
                        // (Gabriel, Zinnia, Silly, etc.) ended up with no
                        // portrait at all, since "portrait" was only ever
                        // wired in when the chosen constructor happened
                        // to have a matching parameter.
                        if (portrait != null)
                        {
                            try { npc.Portrait = portrait; }
                            catch (Exception portraitEx)
                            {
                                Monitor.Log($"Couldn't set Portrait directly on constructed NPC '{name}': {portraitEx.Message}", LogLevel.Trace);
                            }
                        }
                        return npc;
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Constructor attempt failed for '{name}' ({pars.Length} params): {ex.InnerException?.Message ?? ex.Message}", LogLevel.Trace);
                }
            }
            return null;
        }

        private List<object>? _animalSubjectsCache;

        // Lookup Anything's own search list doesn't include farm animal
        // species at all (confirmed from the log: no FarmAnimal-related
        // subject type ever showed up, unlike monsters which at least
        // appear once encountered) - same situation as monsters, so we
        // build our own list directly from Data/FarmAnimals the same way
        // GetMonsterSubjects() does for Data/Monsters.
        private List<object> GetAnimalSubjects()
        {
            if (_animalSubjectsCache != null) return _animalSubjectsCache;
            var result = new List<object>();
            if (_bridge == null) { _animalSubjectsCache = result; return result; }

            try
            {
                // Load as plain object and walk .Keys via reflection,
                // rather than casting to Dictionary<string, object>. The
                // content cache returns this asset as its real concrete
                // type (Dictionary<string, FarmAnimalData>), and casting
                // THAT to a different generic instantiation like
                // Dictionary<string, object> is an invalid, non-covariant
                // cast in .NET - it throws "Specified cast is not valid"
                // rather than just failing silently. Reflection-walking
                // .Keys sidesteps the generic-type mismatch entirely.
                object rawData = Game1.content.Load<object>("Data/FarmAnimals");
                var keysProp = rawData.GetType().GetProperty("Keys");
                if (keysProp?.GetValue(rawData) is System.Collections.IEnumerable keys)
                {
                    var typeNames = keys.Cast<object>().Select(k => k?.ToString()).Where(k => k != null).Distinct();
                    foreach (string typeName in typeNames)
                    {
                        try
                        {
                            var fake = new FarmAnimal(typeName, Game1.Multiplayer.getNewID(), Game1.player.UniqueMultiplayerID);
                            object? subject = _bridge.GetSubjectFor(fake);
                            if (subject != null) result.Add(subject);
                        }
                        catch (Exception ex)
                        {
                            Monitor.Log($"Skipped farm animal '{typeName}' (couldn't build a preview instance): {ex.Message}", LogLevel.Trace);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log("Error loading Data/FarmAnimals: " + ex.Message, LogLevel.Warn);
            }

            Monitor.Log($"Built {result.Count} farm animal subjects for search.", LogLevel.Debug);
            _animalSubjectsCache = result;
            return result;
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (_bridge == null) return;

            // Only restore the saved search menu if we specifically just
            // sent the player into a detail page FROM our own menu (see
            // the onSelect callback below, which sets this flag). Without
            // this check, ANY menu closing anywhere in the game (the
            // inventory, a chest, another mod's menu, the pause menu...)
            // would trigger e.NewMenu == null and incorrectly pop our
            // search menu back open on top of it - which is almost
            // certainly what caused the "closing a menu makes everything
            // disappear/break" bug reported.
            if (e.NewMenu == null)
            {
                if (_awaitingDetailReturn && _lastSearchMenu != null)
                {
                    _awaitingDetailReturn = false;
                    // Recompute layout against the CURRENT viewport before
                    // showing this instance again - without this, stale
                    // absolute-pixel bounds from whenever the menu was
                    // first built can make it draw undersized/mispositioned
                    // and break click hit-testing (looked like the menu
                    // "shrinking and freezing" when re-opened).
                    _lastSearchMenu.RefreshLayout();
                    Game1.activeClickableMenu = _lastSearchMenu;
                }
                return;
            }

            // Any OTHER menu opening (that isn't the detail page we just
            // sent the player to) means they navigated away on their own -
            // don't try to restore our menu once whatever they opened
            // eventually closes.
            if (_awaitingDetailReturn && e.NewMenu != _lastSearchMenu)
            {
                _awaitingDetailReturn = false;
            }

            // ตรวจว่าเป็น SearchMenu ของ Lookup Anything
            if (e.NewMenu.GetType().Name != SearchMenuClassName) return;

            // ปิด SearchMenu เดิมก่อน
            try { e.NewMenu.exitThisMenu(false); } catch { }

            // เปิด MobileSearchMenu แทน
            try
            {
                var subjects = _bridge.GetSearchSubjects();
                if (subjects == null)
                {
                    Monitor.Log("No search subjects available", LogLevel.Warn);
                    return;
                }

                _persistence ??= new PersistenceManager(Helper);

                var menu = new MobileSearchMenu(subjects, subject =>
                {
                    // Mark that we're deliberately sending the player into
                    // a detail page and expect to bring them back to this
                    // exact menu once they close it.
                    _awaitingDetailReturn = true;
                    // Instead of mutating the shared NPC (which caused
                    // list-icon regressions since it's the same object our
                    // list reads from), construct a throwaway CLONE
                    // specifically for names known to need visual fixing,
                    // prime that clone (safe - nothing else references
                    // it), and show it instead. The real shared NPC our
                    // list uses stays completely untouched.
                    object? subjectToShow = subject;
                    var wrapped = SubjectWrapper.Create(subject);
                    if (wrapped != null && SubjectWrapper.NeedsVisualPriming(wrapped.InternalName))
                    {
                        try
                        {
                            NPC? clone = TryConstructNpcDynamically(wrapped.InternalName);
                            if (clone != null)
                            {
                                SubjectWrapper.PrimeNpcVisualData(clone);
                                object? cloneSubject = _bridge!.GetSubjectFor(clone);
                                if (cloneSubject != null) subjectToShow = cloneSubject;
                            }
                        }
                        catch (Exception ex)
                        {
                            Monitor.Log($"Couldn't build a primed clone for detail view of '{wrapped.InternalName}': {ex.Message}", LogLevel.Trace);
                        }
                    }
                    _bridge.ShowLookupFor(subjectToShow!);
                }, GetMonsterSubjects, _persistence, onExplicitClose: () =>
                {
                    // The player closed the search menu itself (its own X
                    // button or Escape) - don't restore it afterward.
                    _lastSearchMenu = null;
                    _awaitingDetailReturn = false;
                }, animalProvider: GetAnimalSubjects, allVillagersProvider: GetAllVillagerSubjects);

                _lastSearchMenu = menu;
                Game1.activeClickableMenu = menu;

                Monitor.Log("Opened MobileSearchMenu", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Monitor.Log("Error opening MobileSearchMenu: " + ex.Message, LogLevel.Error);
            }
        }
    }
}

