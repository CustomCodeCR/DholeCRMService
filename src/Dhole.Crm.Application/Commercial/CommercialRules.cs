namespace Dhole.Crm.Application.Commercial;
public static class CommercialRules
{
    public static bool ValidMoney(decimal amount, decimal percentage) =>
        amount >= 0m && amount <= 9999999999999999.99m &&
        percentage >= 0m && percentage <= 100m;
    public static bool ValidOpportunitySubject(Guid? accountId, Guid? leadId) =>
        (accountId.HasValue && !leadId.HasValue) || (!accountId.HasValue && leadId.HasValue);
    public static bool ValidActivitySchedule(DateTime? startUtc, DateTime? endUtc) =>
        (!startUtc.HasValue || startUtc.Value.Kind == DateTimeKind.Utc) &&
        (!endUtc.HasValue || endUtc.Value.Kind == DateTimeKind.Utc) &&
        (!startUtc.HasValue || !endUtc.HasValue || endUtc >= startUtc);
    public static bool ValidActivityType(string? type) =>
        type is "VISIT" or "CALL" or "EMAIL" or "MEETING" or "OTHER";
    public static bool ValidStage(decimal probability, bool won, bool lost) =>
        probability is >= 0m and <= 100m && !(won && lost) &&
        (!won || probability == 100m) && (!lost || probability == 0m);
    public static bool ValidClose(bool won, bool lost, string? reason) =>
        (won && !lost && string.IsNullOrWhiteSpace(reason)) ||
        (!won && lost && !string.IsNullOrWhiteSpace(reason));
    public static bool ValidVolume(decimal amount, string? unit) =>
        amount >= 0m && amount <= 99999999999999.9999m &&
        !string.IsNullOrWhiteSpace(unit) && unit.Length <= 24;
}
