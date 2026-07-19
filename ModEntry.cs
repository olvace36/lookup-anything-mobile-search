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
        private PersistenceManager? _persistence;

        // ชื่อ class ของ SearchMenu จริงๆ ใน Lookup Anything
        private const string SearchMenuClassName = "SearchMenu";

        public override object? GetApi() => this;

        public override void Entry(IModHelper helper)
        {
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
                    Monster fake = new(name, Vector2.Zero);
                    TryFixMonsterTexture(fake, name);
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

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (_bridge == null) return;

            // Everything just closed (e.g. the player closed the Lookup
            // Anything detail page we opened from our own search menu).
            // If we still have a search menu saved from before, bring it
            // back instead of leaving the player dropped out to the game -
            // this is what makes "select an entry -> view it -> close it"
            // return to the search list instead of exiting entirely.
            if (e.NewMenu == null)
            {
                if (_lastSearchMenu != null)
                {
                    Game1.activeClickableMenu = _lastSearchMenu;
                }
                return;
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
                    // Don't clear _lastSearchMenu here - selecting an entry
                    // should still let the player come back to this exact
                    // menu (with their search/scroll position intact) once
                    // they close the detail page.
                    _bridge.ShowLookupFor(subject);
                }, GetMonsterSubjects, _persistence, onExplicitClose: () =>
                {
                    // The player closed the search menu itself (its own X
                    // button or Escape) - don't restore it afterward.
                    _lastSearchMenu = null;
                });

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

