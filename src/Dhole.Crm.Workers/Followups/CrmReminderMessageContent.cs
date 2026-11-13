using System.Net;

namespace Dhole.Crm.Workers.Followups;

/// <summary>
/// Formats CRM commitment reminders while preserving the plain-text content used
/// by System notifications. Only known CRM routes can become email action links.
/// </summary>
public static class CrmReminderMessageContent
{
    public static string? RouteFor(string entityType, Guid entityId)
    {
        if (entityId==Guid.Empty) return null;
        return entityType switch
        {
            "Activity"=>$"/crm/activities/{entityId:D}",
            "Followup"=>$"/crm/followups/{entityId:D}",
            _=>null
        };
    }

    public static string Render(string body, string channel, string relationshipType,
        string relationshipName, string route, string? publicWebBaseUrl)
    {
        var label=string.IsNullOrWhiteSpace(relationshipType)
            ? "Cliente/prospecto" : relationshipType.Trim();
        var name=string.IsNullOrWhiteSpace(relationshipName)
            ? "Sin vincular" : relationshipName.Trim();

        if (!string.Equals(channel,"Email",StringComparison.OrdinalIgnoreCase))
            return $"{body}\n{label}: {name}";

        // Notifications sends Email bodies as HTML. Encode ALL CRM data before
        // placing it into markup; subjects, company names and notes are user data.
        var safeBody=WebUtility.HtmlEncode(body).Replace("\r\n","\n",StringComparison.Ordinal)
            .Replace("\n","<br />",StringComparison.Ordinal);
        var safeLabel=WebUtility.HtmlEncode(label);
        var safeName=WebUtility.HtmlEncode(name);
        var destination=AbsoluteLink(route,publicWebBaseUrl);
        var action=destination is null
            ? $"<p style=\"margin:16px 0 0\">Ruta del seguimiento: {WebUtility.HtmlEncode(route)}</p>"
            : $"<p style=\"margin:18px 0 0\"><a href=\"{WebUtility.HtmlEncode(destination)}\" " +
              "style=\"display:inline-block;padding:10px 16px;border-radius:6px;" +
              "background:#a80e18;color:#ffffff;text-decoration:none;font-weight:600\">" +
              "Ver seguimiento en Dhole</a></p>";
        return "<div style=\"font-family:Arial,sans-serif;font-size:14px;" +
            "line-height:1.6;color:#273142\">" +
            $"<p>{safeBody}</p><p><strong>{safeLabel}:</strong> {safeName}</p>{action}</div>";
    }

    public static string? AbsoluteLink(string route, string? publicWebBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicWebBaseUrl) ||
            !Uri.TryCreate(publicWebBaseUrl.Trim(),UriKind.Absolute,out var baseUri) ||
            (baseUri.Scheme!=Uri.UriSchemeHttps &&
             !(baseUri.Scheme==Uri.UriSchemeHttp && baseUri.IsLoopback)) ||
            !string.IsNullOrWhiteSpace(baseUri.UserInfo) ||
            !IsKnownRoute(route))
            return null;

        var origin=baseUri.GetLeftPart(UriPartial.Authority);
        return new Uri(new Uri(origin),route).AbsoluteUri;
    }

    private static bool IsKnownRoute(string route)
    {
        var pieces=route.Split('/',StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length==3 && pieces[0]=="crm" &&
            (pieces[1]=="activities" || pieces[1]=="followups") &&
            Guid.TryParseExact(pieces[2],"D",out var id) && id!=Guid.Empty;
    }
}
