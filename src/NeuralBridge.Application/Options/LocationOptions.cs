namespace NeuralBridge.Application.Options;

/// <summary>Bound from <c>NeuralBridge:Location</c>.</summary>
public sealed class LocationOptions
{
    public const string SectionName = "NeuralBridge:Location";

    /// <summary>
    /// When <c>true</c>, rounded coordinates are sent (server-side) to the configured
    /// reverse-geocoding endpoint to find a city name. When <c>false</c>, only rounded
    /// coordinates and the browser time zone are displayed.
    /// </summary>
    public bool ReverseGeocodingEnabled { get; set; } = true;

    /// <summary>OpenStreetMap Nominatim-compatible endpoint.</summary>
    public string ReverseGeocodingEndpoint { get; set; } = "https://nominatim.openstreetmap.org/";

    /// <summary>Nominatim's usage policy requires an identifying User-Agent with contact info.</summary>
    public string UserAgent { get; set; } = "NeuralBridge/1.0 (self-hosted; configure NeuralBridge:Location:UserAgent)";

    /// <summary>Decimal places kept from browser coordinates (2 ≈ 1.1 km).</summary>
    public int CoordinatePrecision { get; set; } = 2;

    public int TimeoutSeconds { get; set; } = 4;
}
