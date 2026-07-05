using System;
using System.Collections.Generic;
using LookupAnythingMobileSearch.Framework;
using LookupAnythingMobileSearch.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

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

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (_bridge == null) return;
            if (e.NewMenu == null) return;

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

                Game1.activeClickableMenu = new MobileSearchMenu(subjects, subject =>
                {
                    _bridge.ShowLookupFor(subject);
                });

                Monitor.Log("Opened MobileSearchMenu", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Monitor.Log("Error opening MobileSearchMenu: " + ex.Message, LogLevel.Error);
            }
        }
    }
}

