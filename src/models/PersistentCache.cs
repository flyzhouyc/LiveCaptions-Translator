using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace LiveCaptionsTranslator.models
{
    public class PersistentCache
    {
        private readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>();
        private readonly string _cacheFilePath;

        public PersistentCache(string cacheFilePath)
        {
            _cacheFilePath = cacheFilePath;
            LoadCacheAsync().Wait();
        }

        private async Task LoadCacheAsync()
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = await File.ReadAllTextAsync(_cacheFilePath);
                var cachedItems = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                foreach (var item in cachedItems)
                {
                    _cache.TryAdd(item.Key, item.Value);
                }
            }
        }

        public bool TryGetCachedTranslation(string key, out string translation)
        {
            return _cache.TryGetValue(key, out translation);
        }

        public void AddToCache(string key, string translation)
        {
            _cache.TryAdd(key, translation);
            SaveCacheAsync().Wait();
        }

        private async Task SaveCacheAsync()
        {
            var cachedItems = new Dictionary<string, string>(_cache);
            var json = JsonSerializer.Serialize(cachedItems);
            await File.WriteAllTextAsync(_cacheFilePath, json);
        }
    }
}
