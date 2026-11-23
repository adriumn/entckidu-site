using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Entckidu.Api;

/// <summary>
/// GET /api/album?artist=Jane&amp;album=Lady   – search by names
/// GET /api/album?mbid=&lt;release id&gt;          – exact MusicBrainz release
/// Answers 200 with details, 404 when nothing matches, 503 when MusicBrainz is busy.
/// </summary>
public sealed class AlbumFunction(ILogger<AlbumFunction> log)
{
    // Shared by every call: one HttpClient, one rate gate, one cache.
    private static readonly MusicBrainzClient MusicBrainz = new(CreateHttpClient());

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // MusicBrainz asks every app to say who it is.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("entckidu.net/0.1 ( https://entckidu.net )");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    [Function("album")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "album")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var mbid = query["mbid"]?.Trim();
        var artist = query["artist"]?.Trim();
        var album = query["album"]?.Trim();

        try
        {
            AlbumDetails? details;
            if (!string.IsNullOrEmpty(mbid))
            {
                if (!Guid.TryParse(mbid, out var id)) return await Error(req, HttpStatusCode.BadRequest, "mbid is not a valid ID");
                details = await MusicBrainz.GetByIdAsync(id, ct);
            }
            else if (IsReasonable(artist) && IsReasonable(album))
            {
                details = await MusicBrainz.FindAsync(artist!, album!, ct);
            }
            else
            {
                return await Error(req, HttpStatusCode.BadRequest, "give artist and album, or mbid");
            }

            if (details is null)
            {
                log.LogInformation("No match for {Artist} / {Album} / {Mbid}", artist, album, mbid);
                return await Error(req, HttpStatusCode.NotFound, "not found", cacheSeconds: 3600);
            }

            var ok = req.CreateResponse(HttpStatusCode.OK);
            ok.Headers.Add("Content-Type", "application/json; charset=utf-8");
            ok.Headers.Add("Cache-Control", "public, max-age=86400");   // album facts rarely change
            await ok.WriteStringAsync(JsonSerializer.Serialize(details, Json), ct);
            return ok;
        }
        catch (MusicBrainzUnavailableException e)
        {
            log.LogWarning(e, "MusicBrainz unavailable");
            return await Error(req, HttpStatusCode.ServiceUnavailable, "lookup unavailable, try again later");
        }
    }

    private static bool IsReasonable(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 200;

    private static async Task<HttpResponseData> Error(
        HttpRequestData req, HttpStatusCode status, string message, int cacheSeconds = 0)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        res.Headers.Add("Cache-Control", cacheSeconds > 0 ? $"public, max-age={cacheSeconds}" : "no-store");
        await res.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, Json));
        return res;
    }
}
