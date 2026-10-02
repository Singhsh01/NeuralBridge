using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NeuralBridge.Application.Abstractions;

namespace NeuralBridge.Infrastructure.Location;

/// <summary>
/// Reverse geocoding through an OpenStreetMap Nominatim-compatible API. The request carries
/// only rounded coordinates. Nothing identifying the user is forwarded, because the server
/// makes the request. Respect the provider's usage policy, or self-host Nominatim, for
/// heavier use.
/// </summary>
public sealed class NominatimReverseGeocoder : IReverseGeocoder
{
    private readonly HttpClient _http;
    private readonly ILogger<NominatimReverseGeocoder> _logger;

    public NominatimReverseGeocoder(HttpClient http, ILogger<NominatimReverseGeocoder> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<GeoPlace?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"reverse?format=jsonv2&zoom=10&addressdetails=1&accept-language=en&lat={latitude}&lon={longitude}");

        using var response = await _http.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Reverse geocoding returned HTTP {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<NominatimResponse>(cancellationToken);
        var a = payload?.Address;
        if (a is null)
        {
            return null;
        }

        var city = a.City ?? a.Town ?? a.Village ?? a.Municipality ?? a.Suburb;
        return new GeoPlace(city, a.State ?? a.County, a.Country, a.CountryCode?.ToUpperInvariant());
    }

    private sealed class NominatimResponse
    {
        [JsonPropertyName("address")]
        public NominatimAddress? Address { get; set; }
    }

    private sealed class NominatimAddress
    {
        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("town")]
        public string? Town { get; set; }

        [JsonPropertyName("village")]
        public string? Village { get; set; }

        [JsonPropertyName("municipality")]
        public string? Municipality { get; set; }

        [JsonPropertyName("suburb")]
        public string? Suburb { get; set; }

        [JsonPropertyName("county")]
        public string? County { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; set; }
    }
}

/// <summary>Used when reverse geocoding is disabled: the UI then shows rounded coordinates only.</summary>
public sealed class DisabledReverseGeocoder : IReverseGeocoder
{
    public Task<GeoPlace?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default) =>
        Task.FromResult<GeoPlace?>(null);
}
