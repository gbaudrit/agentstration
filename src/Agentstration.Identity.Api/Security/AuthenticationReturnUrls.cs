namespace Agentstration.Web.Security;

public static class AuthenticationReturnUrls
{
    public static string Normalize(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || returnUrl[0] != '/'
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.Contains('\\')
            || returnUrl.Any(char.IsControl))
            return "/";

        return returnUrl;
    }
}
