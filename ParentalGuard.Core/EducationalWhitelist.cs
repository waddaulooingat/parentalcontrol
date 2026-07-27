using Newtonsoft.Json;

namespace ParentalGuard.Core;

public class EducationalWhitelist
{
    private static readonly string[] BuiltIn =
    {
        "khanacademy.org", "wikipedia.org", "quizlet.com", "duolingo.com", "coursera.org",
        "edx.org", "wolframalpha.com", "desmos.com", "ck12.org", "collegeboard.org",
        "commonlit.org", "sparknotes.com", "cliffsnotes.com", "scholar.google.com",
        "jstor.org", "codecademy.com", "stackoverflow.com", "github.com", "w3schools.com",
        "mathway.com", "symbolab.com", "prepscholar.com",
    };

    private readonly string _path;
    private readonly HashSet<string> _custom = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public EducationalWhitelist(string? whitelistPath = null)
    {
        _path = whitelistPath ?? Path.Combine(AppPaths.DataDirectory, "whitelist.json");
        Load();
    }

    public IReadOnlyCollection<string> BuiltInSites => BuiltIn;

    public IReadOnlyCollection<string> CustomSites
    {
        get { lock (_lock) { return _custom.ToArray(); } }
    }

    public bool IsWhitelisted(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.TrimStart('.').ToLowerInvariant();

        foreach (var site in BuiltIn)
        {
            if (MatchesDomain(host, site)) return true;
        }

        lock (_lock)
        {
            foreach (var site in _custom)
            {
                if (MatchesDomain(host, site)) return true;
            }
        }

        return false;
    }

    private static bool MatchesDomain(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
           || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public void Add(string domain)
    {
        domain = domain.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(domain)) return;

        lock (_lock)
        {
            if (!_custom.Add(domain)) return;
        }

        Save();
    }

    public void Remove(string domain)
    {
        lock (_lock)
        {
            if (!_custom.Remove(domain)) return;
        }

        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path);
            var sites = JsonConvert.DeserializeObject<List<string>>(json);
            if (sites == null) return;

            lock (_lock)
            {
                _custom.Clear();
                foreach (var site in sites) _custom.Add(site);
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
        List<string> sites;
        lock (_lock)
        {
            sites = _custom.ToList();
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonConvert.SerializeObject(sites, Formatting.Indented);
        File.WriteAllText(_path, json);
    }
}
