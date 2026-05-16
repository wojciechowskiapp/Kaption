using System;
using System.Collections.Generic;
using System.Linq;

namespace GI_Subtitles.Core.Cache
{
    /// <summary>
    /// Thread-safe LRU (Least Recently Used) cache implementation
    /// </summary>
    public class LRUCache<TKey, TValue>
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cache;
        private readonly LinkedList<CacheItem> _list;
        private readonly object _syncRoot = new object();

        private class CacheItem
        {
            public TKey Key { get; set; }
            public TValue Value { get; set; }
        }

        public LRUCache(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("Capacity must be greater than 0", nameof(capacity));

            _capacity = capacity;
            _cache = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
            _list = new LinkedList<CacheItem>();
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_syncRoot)
            {
                value = default(TValue);
                if (!_cache.TryGetValue(key, out var node))
                    return false;

                _list.Remove(node);
                _list.AddFirst(node);

                value = node.Value.Value;
                return true;
            }
        }

        public void AddOrUpdate(TKey key, TValue value)
        {
            lock (_syncRoot)
            {
                if (_cache.TryGetValue(key, out var existingNode))
                {
                    existingNode.Value.Value = value;
                    _list.Remove(existingNode);
                    _list.AddFirst(existingNode);
                }
                else
                {
                    if (_cache.Count >= _capacity)
                    {
                        var lastNode = _list.Last;
                        if (lastNode != null)
                        {
                            _cache.Remove(lastNode.Value.Key);
                            _list.RemoveLast();
                        }
                    }

                    var newNode = new LinkedListNode<CacheItem>(new CacheItem { Key = key, Value = value });
                    _list.AddFirst(newNode);
                    _cache[key] = newNode;
                }
            }
        }

        public bool ContainsKey(TKey key)
        {
            lock (_syncRoot)
            {
                if (!_cache.TryGetValue(key, out var node))
                    return false;

                _list.Remove(node);
                _list.AddFirst(node);
                return true;
            }
        }

        public bool Remove(TKey key)
        {
            lock (_syncRoot)
            {
                if (!_cache.TryGetValue(key, out var node))
                    return false;

                _cache.Remove(key);
                _list.Remove(node);
                return true;
            }
        }

        public IEnumerable<TKey> Keys
        {
            get
            {
                lock (_syncRoot)
                {
                    return _cache.Keys.ToList();
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_syncRoot)
                {
                    return _cache.Count;
                }
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _cache.Clear();
                _list.Clear();
            }
        }

        public TValue this[TKey key]
        {
            get
            {
                if (TryGetValue(key, out var value))
                    return value;
                throw new KeyNotFoundException($"Key '{key}' not found in cache");
            }
            set => AddOrUpdate(key, value);
        }
    }
}
