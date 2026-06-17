using System;
using System.Collections.Generic;
using LookupAnythingMobileSearch.Framework;
using LookupAnythingMobileSearch.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace LookupAnythingMobileSearch
{
    public class ModEntry : Mod
    {
        private LookupAnythingBridge? _bridge;

        // ชื่อ class ของ SearchMenu จริงๆ ใน Lookup Anything
        private const string SearchMenuClassName = "SearchMenu";

        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
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
