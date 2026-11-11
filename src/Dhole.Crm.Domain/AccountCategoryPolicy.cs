namespace Dhole.Crm.Domain;

/// <summary>Server-side CRM account category policy. Never trust a caller's category without its scope.</summary>
public static class AccountCategoryPolicy
{
    // Existing legacy codes are preserved. Without the scope, the server assigns
    // the lowest available tier for the selected account kind.
    public static string DefaultCategory(string type) => type switch
    {
        "CUSTOMER" => "D",
        "NVOCC" => "B",
        "LITTLE" => "A",
        _ => "D"
    };

    public static string ResolveOnCreate(string type, string requestedCode, bool canAssign) =>
        canAssign ? requestedCode : DefaultCategory(type);

    public static bool CanUpdate(string existingType, string existingCode,
        string requestedType, string requestedCode, bool canAssign) =>
        canAssign || (existingType == requestedType && existingCode == requestedCode);

    public static bool Valid(string type, string code) =>
        code is { Length: 1 } &&
        ((type == "CUSTOMER" && "ABCD".Contains(code, StringComparison.Ordinal)) ||
         (type == "NVOCC" && "AB".Contains(code, StringComparison.Ordinal)) ||
         (type == "LITTLE" && code == "A"));
}
