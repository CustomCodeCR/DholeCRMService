namespace Dhole.Crm.Domain;
public static class ActivityVisitLocationRules
{
    public static bool IsValid(string type, decimal? latitude, decimal? longitude)
    {
        if (latitude.HasValue != longitude.HasValue) return false;
        if (!latitude.HasValue) return true;
        return type == "VISIT" &&
            latitude.Value is >= -90m and <= 90m &&
            longitude!.Value is >= -180m and <= 180m;
    }
}
