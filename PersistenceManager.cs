using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;

namespace LookupAnythingMobileSearch.Framework
{
    // Stores favorites and recently-viewed entries per save file, keyed by
    // each subject's InternalName (stable, unlocalized) so it still matches
    // correctly if the player changes the game's language later.
    internal class SearchData
    {
        public List<string> Favorites { get; set; } = new();
        public List<string> RecentlyViewed { get; set; } = new();
    }

    public class PersistenceManager
    {
        private const string FileName = "search-data.json";
        private const int MaxRecent = 10;

        private readonly IModHelper _helper;
        private SearchData _data;

        public PersistenceManager(IModHelper helper)
        {
            _helper = helper;
            _data = _helper.Data.ReadSaveData<SearchData>(FileName) ?? new SearchData();
        }

        private void Save() => _helper.Data.WriteSaveData(FileName, _data);

        public bool IsFavorite(string internalName) => _data.Favorites.Contains(internalName);

        public void ToggleFavorite(string internalName)
        {
            if (!_data.Favorites.Remove(internalName))
                _data.Favorites.Add(internalName);
            Save();
        }

        public IReadOnlyList<string> Favorites => _data.Favorites;

        public void RecordViewed(string internalName)
        {
            _data.RecentlyViewed.Remove(internalName);
            _data.RecentlyViewed.Insert(0, internalName);
            if (_data.RecentlyViewed.Count > MaxRecent)
                _data.RecentlyViewed = _data.RecentlyViewed.Take(MaxRecent).ToList();
            Save();
        }

        public IReadOnlyList<string> RecentlyViewed => _data.RecentlyViewed;
    }
}
