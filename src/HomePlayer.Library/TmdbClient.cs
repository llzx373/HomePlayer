using System.Globalization;
using System.Text.Json;
using HomePlayer.Core;
using HomePlayer.Data;

namespace HomePlayer.Library;

public sealed class TmdbClient
{
    private readonly LibraryRepository _repository;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public TmdbClient(LibraryRepository repository) => _repository = repository;

    public async Task<IReadOnlyList<TmdbHit>> SearchAsync(string query, int? year, bool tv, string apiKey, CancellationToken cancellationToken)
    {
        var key = $"search:{(tv ? "tv" : "movie")}:{query}:{year?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
        var cached = _repository.ReadCache(key, TimeSpan.FromDays(30));
        if (cached != null)
            return ParseSearch(cached, tv);

        var url = tv
            ? $"https://api.themoviedb.org/3/search/tv?api_key={Uri.EscapeDataString(apiKey)}&language=zh-CN&query={Uri.EscapeDataString(query)}"
            : $"https://api.themoviedb.org/3/search/movie?api_key={Uri.EscapeDataString(apiKey)}&language=zh-CN&query={Uri.EscapeDataString(query)}";
        if (year is int value)
            url += tv ? $"&first_air_date_year={value}" : $"&year={value}";
        var json = await GetStringAsync(url, cancellationToken);
        _repository.WriteCache(key, json);
        return ParseSearch(json, tv);
    }

    public async Task<string?> GetMovieCollectionAsync(int movieId, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;
        var key = "movie-details:" + movieId.ToString(CultureInfo.InvariantCulture);
        var json = _repository.ReadCache(key, TimeSpan.FromDays(30));
        if (json == null)
        {
            var url = $"https://api.themoviedb.org/3/movie/{movieId}?api_key={Uri.EscapeDataString(apiKey)}&language=zh-CN";
            json = await GetStringAsync(url, cancellationToken);
            _repository.WriteCache(key, json);
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("belongs_to_collection", out var collection)
            && collection.ValueKind == JsonValueKind.Object
            && collection.TryGetProperty("name", out var name)
                ? name.GetString()
                : null;
    }

    public async Task<string?> DownloadPosterAsync(string? posterPath, int tmdbId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(posterPath))
            return null;
        var destination = Path.Combine(AppPaths.Posters, $"{tmdbId}.jpg");
        Directory.CreateDirectory(AppPaths.Posters);
        var url = "https://image.tmdb.org/t/p/w342" + posterPath;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(destination))
                return destination;
            await PaceAsync(cancellationToken);
            using var response = await _http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = File.Create(destination);
            await response.Content.CopyToAsync(stream, cancellationToken);
            return destination;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await PaceAsync(cancellationToken);
            return await _http.GetStringAsync(url, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromMilliseconds(260) - (DateTime.UtcNow - _lastRequestUtc);
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, cancellationToken);
        _lastRequestUtc = DateTime.UtcNow;
    }

    private static IReadOnlyList<TmdbHit> ParseSearch(string json, bool tv)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results))
            return [];
        var list = new List<TmdbHit>();
        foreach (var item in results.EnumerateArray())
        {
            var dateName = tv ? "first_air_date" : "release_date";
            int? year = null;
            if (item.TryGetProperty(dateName, out var date) && date.GetString() is string text && text.Length >= 4 && int.TryParse(text[..4], out var parsed))
                year = parsed;
            list.Add(new TmdbHit(
                item.GetProperty("id").GetInt32(),
                tv ? "tv" : "movie",
                item.TryGetProperty(tv ? "name" : "title", out var title) ? title.GetString() ?? "" : "",
                item.TryGetProperty(tv ? "original_name" : "original_title", out var original) ? original.GetString() : null,
                year,
                item.TryGetProperty("overview", out var overview) ? overview.GetString() : null,
                item.TryGetProperty("poster_path", out var poster) && poster.ValueKind != JsonValueKind.Null ? poster.GetString() : null,
                item.TryGetProperty("vote_average", out var rating) ? rating.GetDouble() : null));
            if (list.Count == 8)
                break;
        }

        return list;
    }
}
