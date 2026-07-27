using Newtonsoft.Json;

namespace ParentalGuard.Core;

public class AutoBlockEntry
{
    public string Site { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
}

/// <summary>
/// Owns the combined block list: user-added domains and keywords, temporary
/// 30/30 auto-block entries, and the externally fetched StevenBlack domain set.
/// Persists user + auto-block state to blocklist.json.
/// </summary>
public class BlocklistManager
{
    private class PersistedState
    {
        public List<string> Domains { get; set; } = new();
        public List<string> Keywords { get; set; } = new();
        public List<AutoBlockEntry> AutoBlocked { get; set; } = new();
    }

    private readonly string _path;
    private readonly object _lock = new();

    private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _autoBlocked = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _externalDomains = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public BlocklistManager(string? blocklistPath = null)
    {
        _path = blocklistPath ?? Path.Combine(AppPaths.DataDirectory, "blocklist.json");
        Load();
    }

    public IReadOnlyCollection<string> Domains
    {
        get { lock (_lock) { return _domains.ToArray(); } }
    }

    public IReadOnlyCollection<string> Keywords
    {
        get { lock (_lock) { return _keywords.ToArray(); } }
    }

    public IReadOnlyCollection<AutoBlockEntry> AutoBlocked
    {
        get
        {
            lock (_lock)
            {
                return _autoBlocked.Select(kv => new AutoBlockEntry { Site = kv.Key, ExpiresUtc = kv.Value }).ToArray();
            }
        }
    }

    public bool IsBlocked(string host, string url)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.TrimStart('.').ToLowerInvariant();

        lock (_lock)
        {
            if (MatchesAny(host, _domains)) return true;
            if (MatchesAny(host, _externalDomains)) return true;

            foreach (var kv in _autoBlocked)
            {
                if (kv.Value <= DateTime.UtcNow) continue;
                if (MatchesDomain(host, kv.Key)) return true;
            }

            if (_keywords.Count > 0)
            {
                var haystack = (host + url).ToLowerInvariant();
                foreach (var keyword in _keywords)
                {
                    if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }

        return false;
    }

    private static bool MatchesAny(string host, HashSet<string> domains)
    {
        foreach (var domain in domains)
        {
            if (MatchesDomain(host, domain)) return true;
        }

        return false;
    }

    private static bool MatchesDomain(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public void AddDomain(string domain)
    {
        domain = domain.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(domain)) return;

        lock (_lock)
        {
            if (!_domains.Add(domain)) return;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveDomain(string domain)
    {
        lock (_lock)
        {
            if (!_domains.Remove(domain)) return;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddKeyword(string keyword)
    {
        keyword = keyword.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(keyword)) return;

        lock (_lock)
        {
            if (!_keywords.Add(keyword)) return;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveKeyword(string keyword)
    {
        lock (_lock)
        {
            if (!_keywords.Remove(keyword)) return;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds (or refreshes) a temporary auto-block entry, typically expiring at the next midnight.</summary>
    public void AddAutoBlock(string site, DateTime expiresUtc)
    {
        site = site.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(site)) return;

        lock (_lock)
        {
            _autoBlocked[site] = expiresUtc;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes auto-block entries whose expiry has passed. Called on day rollover.</summary>
    public void PurgeExpiredAutoBlocks()
    {
        bool changed;
        lock (_lock)
        {
            var expired = _autoBlocked.Where(kv => kv.Value <= DateTime.UtcNow).Select(kv => kv.Key).ToList();
            foreach (var key in expired) _autoBlocked.Remove(key);
            changed = expired.Count > 0;
        }

        if (!changed) return;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the externally fetched (StevenBlack) domain set. Not persisted to blocklist.json.</summary>
    public void SetExternalDomains(HashSet<string> domains)
    {
        lock (_lock)
        {
            _externalDomains = new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int ExternalDomainCount
    {
        get { lock (_lock) { return _externalDomains.Count; } }
    }

    /// <summary>Re-reads blocklist.json from disk, picking up changes made by another process.</summary>
    public void Reload() => Load();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path);
            var state = JsonConvert.DeserializeObject<PersistedState>(json);
            if (state == null) return;

            lock (_lock)
            {
                _domains.Clear();
                foreach (var d in state.Domains) _domains.Add(d);

                _keywords.Clear();
                foreach (var k in state.Keywords) _keywords.Add(k);

                _autoBlocked.Clear();
                foreach (var entry in state.AutoBlocked) _autoBlocked[entry.Site] = entry.ExpiresUtc;
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private void Save()
    {
        PersistedState state;
        lock (_lock)
        {
            state = new PersistedState
            {
                Domains = _domains.ToList(),
                Keywords = _keywords.ToList(),
                AutoBlocked = _autoBlocked.Select(kv => new AutoBlockEntry { Site = kv.Key, ExpiresUtc = kv.Value }).ToList(),
            };
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        File.WriteAllText(_path, json);
    }
}
