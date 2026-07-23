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
        public System.Reflection.Assembly? LookupAnythingAssembly => _modEntry?.GetType().Assembly;
        private MethodInfo? _showLookupForMethod; // internal void ShowLookupFor(ISubject subject)
        private FieldInfo? _targetFactoryField;   // private TargetFactory? TargetFactory
        private MethodInfo? _getSearchSubjectsMethod; // IEnumerable<ISubject> GetSearchSubjects()
        private MethodInfo? _getByEntityMethod;   // public ISubject? GetByEntity(object entity, GameLocation? location)
        private MethodInfo? _showSearchMethod;    // private void ShowSearch()
        private FieldInfo? _gameHelperField;      // private GameHelper? GameHelper
        private MethodInfo? _getMonsterDataMethod; // public IEnumerable<MonsterData> GetMonsterData()
        private PropertyInfo? _monsterNameProp;    // MonsterData.Name

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
                    {
                        var factoryType = factory.GetType();
                        _getSearchSubjectsMethod = factoryType
                            .GetMethod("GetSearchSubjects", BindingFlags.Instance | BindingFlags.Public);
                        // GetByEntity(object entity, GameLocation? location) - builds a real
                        // ISubject for any live entity, including a throwaway Monster
                        // instance with no location in the world.
                        _getByEntityMethod = factoryType
                            .GetMethod("GetByEntity", BindingFlags.Instance | BindingFlags.Public);
                    }
                }

                // GameHelper field, needed for GetMonsterData() (list of every
                // known monster type + stats/drops, independent of any live
                // instance - same data LookupAnythingItemSources reads).
                _gameHelperField = entryType.GetField("GameHelper", flags);
                if (_gameHelperField != null)
                {
                    var gameHelper = _gameHelperField.GetValue(_modEntry);
                    if (gameHelper != null)
                    {
                        _getMonsterDataMethod = gameHelper.GetType()
                            .GetMethod("GetMonsterData", BindingFlags.Instance | BindingFlags.Public);
                        Type? monsterDataType = _getMonsterDataMethod?.ReturnType.GetGenericArguments() is { Length: 1 } args
                                ? args[0] : null;
                        _monsterNameProp = monsterDataType?.GetProperty("Name");
                    }
                }

                IsValid = _showLookupForMethod != null && _targetFactoryField != null;

                _monitor.Log($"Bridge init: ShowLookupFor={_showLookupForMethod != null}, " +
                             $"ShowSearch={_showSearchMethod != null}, " +
                             $"TargetFactory={_targetFactoryField != null}, " +
                             $"GetSearchSubjects={_getSearchSubjectsMethod != null}, " +
                             $"GetByEntity={_getByEntityMethod != null}, " +
                             $"GetMonsterData={_getMonsterDataMethod != null}", LogLevel.Info);
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

        /// <summary>รายชื่อมอนสเตอร์ทั้งหมดที่เกมรู้จัก (จาก Data/Monsters ที่ Lookup Anything แกะมาให้แล้ว)</summary>
        public List<string>? GetMonsterNames()
        {
            if (_gameHelperField == null || _modEntry == null || _getMonsterDataMethod == null || _monsterNameProp == null)
                return null;
            try
            {
                var gameHelper = _gameHelperField.GetValue(_modEntry);
                if (gameHelper == null) return null;
                var result = _getMonsterDataMethod.Invoke(gameHelper, null);
                if (result is not IEnumerable enumerable) return null;

                var names = new List<string>();
                foreach (var monster in enumerable)
                {
                    if (_monsterNameProp.GetValue(monster) is string name)
                        names.Add(name);
                }
                return names;
            }
            catch (Exception ex)
            {
                _monitor.Log("GetMonsterNames error: " + ex.Message, LogLevel.Warn);
                return null;
            }
        }

        /// <summary>สร้าง ISubject จริงจาก entity ใดๆ (ใช้กับ Monster ปลอมที่สร้างขึ้นมาเองได้ด้วย
        /// เพราะ TargetFactory.GetByEntity ไม่สนว่ามันอยู่ในโลกจริงหรือเปล่า)</summary>
        public object? GetSubjectFor(object entity)
        {
            if (_targetFactoryField == null || _modEntry == null || _getByEntityMethod == null) return null;
            try
            {
                var factory = _targetFactoryField.GetValue(_modEntry);
                if (factory == null) return null;
                return _getByEntityMethod.Invoke(factory, new object?[] { entity, null });
            }
            catch (Exception ex)
            {
                _monitor.Log("GetSubjectFor error: " + ex.Message, LogLevel.Warn);
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

