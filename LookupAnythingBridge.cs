using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using StardewModdingAPI;

namespace LookupAnythingMobileSearch.Framework
{
    public class LookupAnythingBridge
    {
        private readonly IMonitor _monitor;
        private object? _modEntry;
        private MethodInfo? _showLookupForMethod; // internal void ShowLookupFor(ISubject subject)
        private FieldInfo? _targetFactoryField;   // private TargetFactory? TargetFactory
        private MethodInfo? _getSearchSubjectsMethod; // IEnumerable<ISubject> GetSearchSubjects()
        private MethodInfo? _showSearchMethod;    // private void ShowSearch()

        public bool IsValid { get; private set; }

        public LookupAnythingBridge(IMonitor monitor, IModHelper helper)
        {
            _monitor = monitor;
            Initialize(helper);
        }

        private void Initialize(IModHelper helper)
        {
            try
            {
                var modInfo = helper.ModRegistry.Get("Pathoschild.LookupAnything");
                if (modInfo == null)
                {
                    _monitor.Log("Lookup Anything not found!", LogLevel.Error);
                    return;
                }

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var infoType = modInfo.GetType();

                _modEntry = infoType.GetProperty("Mod", flags)?.GetValue(modInfo)
                         ?? infoType.GetField("Mod", flags)?.GetValue(modInfo)
                         ?? infoType.GetField("_mod", flags)?.GetValue(modInfo);

                if (_modEntry == null)
                {
                    _monitor.Log("Cannot access Lookup Anything mod entry!", LogLevel.Error);
                    return;
                }

                var entryType = _modEntry.GetType();

                // ShowLookupFor(ISubject subject) — internal method
                _showLookupForMethod = entryType.GetMethod("ShowLookupFor", flags);

                // ShowSearch() — private method ที่จะ patch
                _showSearchMethod = entryType.GetMethod("ShowSearch", flags);

                // TargetFactory field
                _targetFactoryField = entryType.GetField("TargetFactory", flags);
                if (_targetFactoryField != null)
                {
                    var factory = _targetFactoryField.GetValue(_modEntry);
                    if (factory != null)
                        _getSearchSubjectsMethod = factory.GetType()
                            .GetMethod("GetSearchSubjects", BindingFlags.Instance | BindingFlags.Public);
                }

                IsValid = _showLookupForMethod != null && _targetFactoryField != null;

                _monitor.Log($"Bridge init: ShowLookupFor={_showLookupForMethod != null}, " +
                             $"ShowSearch={_showSearchMethod != null}, " +
                             $"TargetFactory={_targetFactoryField != null}, " +
                             $"GetSearchSubjects={_getSearchSubjectsMethod != null}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log("Bridge init error: " + ex.Message, LogLevel.Error);
            }
        }

        /// <summary>ดึง subjects ทั้งหมดจาก TargetFactory.GetSearchSubjects()</summary>
        public IEnumerable<object>? GetSearchSubjects()
        {
            if (_targetFactoryField == null || _modEntry == null) return null;
            try
            {
                var factory = _targetFactoryField.GetValue(_modEntry);
                if (factory == null) return null;

                if (_getSearchSubjectsMethod == null)
                    _getSearchSubjectsMethod = factory.GetType()
                        .GetMethod("GetSearchSubjects", BindingFlags.Instance | BindingFlags.Public);

                var result = _getSearchSubjectsMethod?.Invoke(factory, null);
                if (result is IEnumerable enumerable)
                {
                    var list = new List<object>();
                    foreach (var item in enumerable)
                        if (item != null) list.Add(item);
                    _monitor.Log($"GetSearchSubjects: {list.Count} subjects", LogLevel.Debug);
                    return list;
                }
                return null;
            }
            catch (Exception ex)
            {
                _monitor.Log("GetSearchSubjects error: " + ex.Message, LogLevel.Warn);
                return null;
            }
        }

        /// <summary>เรียก ShowLookupFor(ISubject) บน ModEntry</summary>
        public bool ShowLookupFor(object subject)
        {
            if (_modEntry == null || _showLookupForMethod == null) return false;
            try
            {
                _showLookupForMethod.Invoke(_modEntry, new[] { subject });
                return true;
            }
            catch (Exception ex)
            {
                _monitor.Log("ShowLookupFor error: " + ex.Message, LogLevel.Warn);
                return false;
            }
        }
    }
}
