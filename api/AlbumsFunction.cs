using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Entckidu.Api;

/// <summary>
/// GET /api/albums – every album in the Azure SQL table dbo.Albums, sorted by artist and album.
/// Answers 200 with [{ id, artist, album }], 503 when the database can't be reached
/// (asleep and waking, or paused for the month); the page then shows its saved copy.
/// The connection string comes from the app setting SQL_CONNECTION_STRING (never from a file in git).
/// </summary>
public sealed class AlbumsFunction(ILogger<AlbumsFunction> log)
{
    public sealed record AlbumRow(int Id, string Artist, string Album);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Keep the list in memory for 10 minutes: fewer database wake-ups, fewer free vCore seconds used.
    private static List<AlbumRow>? cached;
    private static DateTime cachedAt;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    [Function("albums")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "albums")] HttpRequestData req,
        CancellationToken ct)
    {
        var connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            log.LogError("SQL_CONNECTION_STRING is not set");
            return await Answer(req, HttpStatusCode.ServiceUnavailable, new { error = "database not configured" }, 0);
        }

        try
        {
            var albums = cached;
            if (albums is null || DateTime.UtcNow - cachedAt > CacheFor)
            {
                albums = await ReadAlbums(connectionString, ct);
                cached = albums;
                cachedAt = DateTime.UtcNow;
            }
            return await Answer(req, HttpStatusCode.OK, albums, 600);
        }
        catch (SqlException e)
        {
            // 40613 = database not available right now (waking up, or paused until next month)
            log.LogWarning(e, "Database not reachable (SQL error {Number})", e.Number);
            return await Answer(req, HttpStatusCode.ServiceUnavailable, new { error = "database asleep, try again later" }, 0);
        }
    }

    private static async Task<List<AlbumRow>> ReadAlbums(string connectionString, CancellationToken ct)
    {
        // A sleeping serverless database can refuse the first tries while it wakes: retry a few times.
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectRetryCount = 3, ConnectRetryInterval = 10, ConnectTimeout = 60 };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT Id, Artist, Album FROM dbo.Albums ORDER BY Artist, Album;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<AlbumRow>();
        while (await reader.ReadAsync(ct))
            list.Add(new AlbumRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    private static async Task<HttpResponseData> Answer(HttpRequestData req, HttpStatusCode status, object body, int cacheSeconds)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        res.Headers.Add("Cache-Control", cacheSeconds > 0 ? $"public, max-age={cacheSeconds}" : "no-store");
        await res.WriteStringAsync(JsonSerializer.Serialize(body, Json));
        return res;
    }
}
