using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Options;

namespace NeuralBridge.Application.Location;

/// <param name="Label">What the UI shows, e.g. "Ulm, Germany", or "48.40° N, 9.99° E" when no city is known.</param>
/// <param name="IsCityResolved">Whether a city/town name was found.</param>
public sealed record LocationDisplay(string Label, string? City, string? Country, double Latitude, double Longitude, bool IsCityResolved);

public interface ILocationDisplayService
{
    /// <summary>
    /// Turns browser coordinates (sent only after the user granted permission) into a
    /// display label. Coordinates are rounded before any lookup and are never logged or stored.
    /// </summary>
    Task<Result<LocationDisplay>> ResolveAsync(double latitude, double longitude, string clientKey, CancellationToken cancellationToken = default);
}

public sealed class LocationDisplayService : ILocationDisplayService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    private readonly IReverseGeocoder _geocoder;
    private readonly IAppRateLimiter _rateLimiter;
    private readonly IMemoryCache _cache;
    private readonly LocationOptions _options;
    private readonly ILogger<LocationDisplayService> _logger;

    public LocationDisplayService(
        IReverseGeocoder geocoder,
        IAppRateLimiter rateLimiter,
        IMemoryCache cache,
        IOptions<LocationOptions> options,
        ILogger<LocationDisplayService> logger)
    {
        _geocoder = geocoder;
        _rateLimiter = rateLimiter;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<LocationDisplay>> ResolveAsync(double latitude, double longitude, string clientKey, CancellationToken cancellationToken = default)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return Result<LocationDisplay>.Failure(SessionError.ValidationFailed, "The browser reported an invalid position.");
        }

        var precision = Math.Clamp(_options.CoordinatePrecision, 0, 4);
        var lat = Math.Round(latitude, precision);
        var lon = Math.Round(longitude, precision);
        var fallback = new LocationDisplay(FormatCoordinates(lat, lon), null, null, lat, lon, IsCityResolved: false);

        if (!_options.ReverseGeocodingEnabled)
        {
            return Result<LocationDisplay>.Success(fallback);
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"geo:{lat}:{lon}");
        if (_cache.TryGetValue(key, out LocationDisplay? cached) && cached is not null)
        {
            return Result<LocationDisplay>.Success(cached);
        }

        if (!_rateLimiter.TryAcquire(RateLimitPolicy.LocationLookup, clientKey))
        {
            return Result<LocationDisplay>.Success(fallback);
        }

        GeoPlace? place = null;
        try
        {
            place = await _geocoder.ReverseAsync(lat, lon, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning("Reverse geocoding unavailable: {Reason}", ex.GetType().Name);
        }

        if (place is null || (place.City is null && place.Country is null))
        {
            return Result<LocationDisplay>.Success(fallback);
        }

        var label = string.Join(", ", new[] { place.City ?? place.Region, place.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var display = new LocationDisplay(label, place.City, place.Country, lat, lon, place.City is not null);
        _cache.Set(key, display, CacheDuration);
        return Result<LocationDisplay>.Success(display);
    }

    public static string FormatCoordinates(double lat, double lon) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(lat):0.00}° {(lat >= 0 ? "N" : "S")}, {Math.Abs(lon):0.00}° {(lon >= 0 ? "E" : "W")}");
}
