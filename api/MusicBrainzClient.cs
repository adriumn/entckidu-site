using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace Entckidu.Api;

/// <summary>What the landing page shows under an enlarged record.</summary>
public sealed record AlbumDetails(
    string Id,
    string Title,
    string Artist,
    string? Year,
    string? Country,
    string? Label,
    string? CatalogNumber,
    string? Format,
    IReadOnlyList<string> Genres,
    IReadOnlyList<Member> Members,
    IReadOnlyList<Track> Tracks,
    string MusicBrainzUrl);

public sealed record Member(string Name, string? Roles, string? Years);

public sealed record Track(string Number, string Title, string? Length);

/// <summary>MusicBrainz is busy or unreachable; the caller should try again later.</summary>
public sealed class MusicBrainzUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Small client for the free MusicBrainz web service (https://musicbrainz.org/doc/MusicBrainz_API).
/// Rules MusicBrainz asks for: a User-Agent that names the app, and at most about one request per second.
/// One instance is shared by all calls, so the rate gate and the cache work across requests.
/// </summary>
public sealed class MusicBrainzClient
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2/";
    private const int MinScore = 90;      // search matches below this are too uncertain to show
    private const int MaxCacheEntries = 500;

    private readonly HttpClient _http;
    private readonly TimeSpan _minGap;
    private readonly TimeSpan[] _retryDelays;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;

    // Remembers answers while the Function stays warm. null = "looked up, nothing found".
    private readonly ConcurrentDictionary<string, AlbumDetails?> _cache = new();

    public MusicBrainzClient(HttpClient http, TimeSpan? minGapBetweenCalls = null, TimeSpan[]? retryDelays = null)
    {
        _http = http;
        _minGap = minGapBetweenCalls ?? TimeSpan.FromMilliseconds(1100);
        _retryDelays = retryDelays ?? [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3)];
    }

    /// <summary>Find the album by artist and title. Returns null when nothing matches well.</summary>
    public async Task<AlbumDetails?> FindAsync(string artist, string album, CancellationToken ct = default)
    {
        var key = "q:" + artist.Trim().ToLowerInvariant() + "|" + album.Trim().ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var releaseId = await SearchReleaseAsync(artist, album, ct);
        if (releaseId is null) { Remember(key, null); return null; }
        var (details, complete) = await GetReleaseAsync(releaseId, ct);
        if (complete) Remember(key, details);
        return details;
    }

    /// <summary>Look up one exact release by its MusicBrainz ID.</summary>
    public async Task<AlbumDetails?> GetByIdAsync(Guid releaseId, CancellationToken ct = default)
    {
        var key = "id:" + releaseId;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var (details, complete) = await GetReleaseAsync(releaseId.ToString(), ct);
        if (complete) Remember(key, details);
        return details;
    }

    private void Remember(string key, AlbumDetails? details)
    {
        if (_cache.Count < MaxCacheEntries) _cache[key] = details;
    }

    // Step 1: search releases (pressings) by title and artist, pick the best one.
    private async Task<string?> SearchReleaseAsync(string artist, string album, CancellationToken ct)
    {
        var query = $"release:\"{Escape(album)}\" AND artist:\"{Escape(artist)}\"";
        var json = await GetJsonAsync("release?query=" + Uri.EscapeDataString(query) + "&limit=10&fmt=json", ct);
        if (json?["releases"] is not JsonArray releases) return null;

        var wanted = Normalize(album);
        var best = releases
            .OfType<JsonObject>()
            .Where(r => Score(r) >= MinScore)
            .OrderByDescending(r => Normalize(Str(r, "title")) == wanted)    // exact title first
            .ThenByDescending(r => Str(r["release-group"] as JsonObject, "primary-type") == "Album")  // the album, not a single of the title track
            .ThenByDescending(r => Str(r, "status") == "Official")
            .ThenBy(r => Str(r, "date") is { Length: > 0 } d ? d : "9999")  // oldest pressing first
            .FirstOrDefault();

        return best is null ? null : Str(best, "id");
    }

    // Step 2: the full release (tracks, label, year), then the band members.
    // complete = false when the members lookup failed: the answer is shown but not remembered,
    // so the next click tries again.
    private async Task<(AlbumDetails? Details, bool Complete)> GetReleaseAsync(string releaseId, CancellationToken ct)
    {
        var r = await GetJsonAsync(
            $"release/{releaseId}?inc=recordings+labels+release-groups+genres+artist-credits&fmt=json", ct);
        if (r is null) return (null, true);
        var complete = true;

        var credits = (r["artist-credit"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var artistName = string.Concat(credits.Select(c => Str(c, "name") + Str(c, "joinphrase")));
        var group = r["release-group"] as JsonObject;

        var firstDate = group is null ? null : Str(group, "first-release-date");
        var date = string.IsNullOrEmpty(firstDate) ? Str(r, "date") : firstDate;
        var labelInfo = (r["label-info"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var media = (r["media"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

        var tracks = media
            .SelectMany(m => (m["tracks"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(t => new Track(
                Number: FirstNonEmpty(Str(t, "number"), Str(t, "position")) ?? "",
                Title: Str(t, "title") ?? "",
                Length: FormatLength(t["length"])))
            .ToList();

        // Members only for a single credited artist (a band); duos like "A & B" are skipped.
        IReadOnlyList<Member> members = [];
        IReadOnlyList<string> artistGenres = [];
        var artistId = credits.Count == 1 ? Str(credits[0]["artist"] as JsonObject, "id") : null;
        if (artistId is not null)
        {
            try
            {
                var a = await GetJsonAsync($"artist/{artistId}?inc=artist-rels+genres&fmt=json", ct);
                if (a is not null)
                {
                    if (Str(a, "type") == "Group") members = ReadMembers(a);
                    artistGenres = ReadGenres(a);
                }
            }
            catch (MusicBrainzUnavailableException)
            {
                // The album itself is already known; show it without members.
                complete = false;
            }
        }

        // Genres are voted on in MusicBrainz; the album's own votes win, then the band's.
        var genres = ReadGenres(r);
        if (genres.Count == 0 && group is not null) genres = ReadGenres(group);
        if (genres.Count == 0) genres = artistGenres;

        return (new AlbumDetails(
            Id: releaseId,
            Title: Str(r, "title") ?? "",
            Artist: artistName,
            Year: date is { Length: >= 4 } ? date[..4] : null,
            Country: Str(r, "country"),
            Label: labelInfo is null ? null : Str(labelInfo["label"] as JsonObject, "name"),
            CatalogNumber: labelInfo is null ? null : Str(labelInfo, "catalog-number"),
            Format: media.Count == 0 ? null : Str(media[0], "format"),
            Genres: genres,
            Members: members,
            Tracks: tracks,
            MusicBrainzUrl: "https://musicbrainz.org/release/" + releaseId), complete);
    }

    private static IReadOnlyList<Member> ReadMembers(JsonObject artist)
    {
        var rels = (artist["relations"] as JsonArray)?.OfType<JsonObject>() ?? [];
        return rels
            .Where(rel => Str(rel, "type") == "member of band" && Str(rel, "direction") == "backward")
            .Select(rel => new
            {
                Name = Str(rel["artist"] as JsonObject, "name") ?? "",
                Roles = (rel["attributes"] as JsonArray)?
                    .Select(x => x?.ToString() ?? "")
                    .Where(x => x is not ("" or "original" or "founder" or "minor"))
                    .ToList() ?? [],
                Begin = Year(Str(rel, "begin")),
                End = Year(Str(rel, "end")),
            })
            .Where(m => m.Name.Length > 0)
            .GroupBy(m => m.Name)                                // one line per person, even with several stints
            .Select(g => new
            {
                Name = g.Key,
                Roles = g.SelectMany(m => m.Roles).Distinct().ToList(),
                Begin = g.Select(m => m.Begin).Where(y => y is not null).Min(),
                End = g.Any(m => m.End is null) ? null : g.Max(m => m.End),
            })
            .OrderBy(m => m.Begin ?? "9999")
            .Take(12)
            .Select(m => new Member(
                m.Name,
                m.Roles.Count == 0 ? null : string.Join(", ", m.Roles),
                m.Begin is null ? null : m.End is null ? m.Begin + "–" : m.Begin == m.End ? m.Begin : m.Begin + "–" + m.End))
            .ToList();
    }

    private static IReadOnlyList<string> ReadGenres(JsonObject entity) =>
        ((entity["genres"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .OrderByDescending(g => int.TryParse(g["count"]?.ToString(), out var n) ? n : 0)
            .Select(g => Str(g, "name"))
            .OfType<string>()
            .Take(4)
            .ToList();

    // One HTTP call, waiting so calls are at least _minGap apart.
    private async Task<JsonObject?> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(15), ct))
            throw new MusicBrainzUnavailableException("Too many lookups waiting.");
        try
        {
            // MusicBrainz answers 503 when it is busy or the caller's IP is going too fast.
            // Azure shares outgoing IPs between many apps, so this happens now and then:
            // wait a little longer each time and try again before giving up.
            for (var attempt = 0; ; attempt++)
            {
                var wait = _lastCallUtc + _minGap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastCallUtc = DateTime.UtcNow;

                using var response = await _http.GetAsync(BaseUrl + pathAndQuery, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;

                var busy = response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
                if (busy && attempt < _retryDelays.Length)
                {
                    await Task.Delay(_retryDelays[attempt], ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new MusicBrainzUnavailableException($"MusicBrainz answered {(int)response.StatusCode}.");

                var text = await response.Content.ReadAsStringAsync(ct);
                return JsonNode.Parse(text) as JsonObject;
            }
        }
        catch (HttpRequestException e)
        {
            throw new MusicBrainzUnavailableException("MusicBrainz could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new MusicBrainzUnavailableException("MusicBrainz took too long.", e);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- small helpers ----

    private static string? Str(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Score(JsonObject release) =>
        int.TryParse(release["score"]?.ToString(), out var n) ? n : 0;

    private static string? Year(string? date) => date is { Length: >= 4 } ? date[..4] : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? FormatLength(JsonNode? ms)
    {
        if (ms is null || !long.TryParse(ms.ToString(), out var n) || n <= 0) return null;
        var t = TimeSpan.FromMilliseconds(n);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // Lucene search syntax: only backslash and quote matter inside a "quoted phrase".
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Normalize(string? s) =>
        new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
