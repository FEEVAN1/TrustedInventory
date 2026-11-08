namespace TrustedInventory.Domain.Locations;

/// <summary>
/// DEC-012 storage compatibility, used when goods land in a bin (receiving, putaway). A product is compatible with
/// a zone when (a) it has no storage profile limits, or the zone's whole operating range lies inside the profile's
/// range (an uncontrolled zone with no range is never inside one), and (b) it has no hazard class, or the zone
/// allows that class.
/// </summary>
public static class StorageCompatibility
{
    public static bool IsCompatible(decimal? zoneMinC, decimal? zoneMaxC, decimal? profileMinC, decimal? profileMaxC,
        string? hazardClass, IReadOnlyCollection<string> zoneAllowedHazardClasses)
    {
        var temperatureOk = profileMinC is null || profileMaxC is null
            || (zoneMinC is { } zmin && zoneMaxC is { } zmax && zmin >= profileMinC && zmax <= profileMaxC);
        var hazardOk = hazardClass is null || zoneAllowedHazardClasses.Contains(hazardClass);
        return temperatureOk && hazardOk;
    }
}
