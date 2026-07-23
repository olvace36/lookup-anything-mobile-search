using System;
using System.Collections.Generic;
using System.Linq;
using LookupAnythingMobileSearch.Framework;
using LookupAnythingMobileSearch.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
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
        internal static ITranslationHelper I18n = null!;

        // ชื่อ class ของ SearchMenu จริงๆ ใน Lookup Anything
        private const string SearchMenuClassName = "SearchMenu";

        public override object? GetApi() => this;

        public override void Entry(IModHelper helper)
        {
            SMonitor = Monitor;
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
                string path = alias.StartsWith("@") ? alias.Substring(1) : "Characters\\Monsters\\" + alias;
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

        private List<object> GetMonsterSubjects()
        {
            if (_monsterSubjectsCache != null) {
                return _monsterSubjectsCache;
            }
            var result = new List<object>();
            List<string>? names = _bridge?.GetMonsterNames();
            if (names == null) {
                _monsterSubjectsCache = result;
                return result;
            }
            foreach (string name in names.Distinct())
            {
                try
                {
                    Monster fake = TryConstructSpecificMonsterType(name) ?? new Monster(name, Vector2.Zero);
                    TryFixMonsterTexture(fake, name);
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
                        result.Add(subject);
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Skipped monster '{name}' (couldn't build a preview instance): {ex.Message}", LogLevel.Trace);
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

