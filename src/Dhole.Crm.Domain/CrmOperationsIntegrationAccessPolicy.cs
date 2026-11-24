using System.Security.Cryptography;
using System.Text;

namespace Dhole.Crm.Domain;

/// <summary>A service key alone never grants CRM read-all access.</summary>
public static class CrmOperationsIntegrationAccessPolicy
{
    public static bool HasServiceKey(string? expected, string? provided)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided))
            return false;
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(provided);
        return left.Length == right.Length &&
            CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static bool IsAuthorized(bool isActive, IEnumerable<string>? scopes) =>
        isActive && scopes?.Contains("crm.account.read-all",
            StringComparer.OrdinalIgnoreCase) == true;
}
