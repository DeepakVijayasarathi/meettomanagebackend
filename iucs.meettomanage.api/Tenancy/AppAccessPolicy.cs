using Microsoft.AspNetCore.Http;

namespace iucs.meettomanage.api.Tenancy;

/// <summary>
/// Commercial gate for the mobile parent app. The app marks every request with <c>X-Client: parent-app</c>; when the
/// tenant has not been granted application access those requests (including sign-in) are refused with a plain
/// message. The website is unaffected. This is an entitlement, not a security boundary: it stops an academy's
/// families using the app without a grant, it does not authenticate the app itself (the user's token still does that).
/// </summary>
public static class AppAccessPolicy
{
    public const string ClientHeader = "X-Client";
    public const string ParentAppClient = "parent-app";
    public const string DeniedMessage = "The mobile app isn't enabled for your academy yet. Please contact your academy, or use the website.";

    public static bool IsParentApp(IHeaderDictionary headers) =>
        string.Equals(headers[ClientHeader].ToString().Trim(), ParentAppClient, StringComparison.OrdinalIgnoreCase);

    public static bool IsDenied(IHeaderDictionary headers, bool appAccessGranted) => !appAccessGranted && IsParentApp(headers);
}
