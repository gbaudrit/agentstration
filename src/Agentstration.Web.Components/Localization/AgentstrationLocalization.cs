using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Components.Localization;

public sealed class AgentstrationLocalizationOptions
{
    public const string SectionName = "Agentstration:Localization";

    public string DefaultCulture { get; set; } = "en-US";
    public string[] SupportedCultures { get; set; } = [];
}

public static class AgentstrationLocalizationExtensions
{
    public static IServiceCollection AddAgentstrationLocalization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        var localizationOptions = services.AddOptions<AgentstrationLocalizationOptions>();
        var localizationSection = configuration.GetSection(AgentstrationLocalizationOptions.SectionName);
        if (localizationSection.Exists()) localizationOptions.Bind(localizationSection);
        localizationOptions
            .PostConfigure(options =>
            {
                if (options.SupportedCultures.Length == 0) options.SupportedCultures = ["en-US", "fr-FR"];
            })
            .Validate(Validate, "Localization cultures must be valid and the default culture must be supported.")
            .ValidateOnStart();
        services.AddOptions<RequestLocalizationOptions>()
            .Configure<IOptions<AgentstrationLocalizationOptions>>((requestOptions, configuredOptions) =>
            {
                var configured = configuredOptions.Value;
                var cultures = configured.SupportedCultures
                    .Select(CultureInfo.GetCultureInfo)
                    .ToArray();
                requestOptions.DefaultRequestCulture = new RequestCulture(configured.DefaultCulture);
                requestOptions.SupportedCultures = cultures;
                requestOptions.SupportedUICultures = cultures;
                requestOptions.FallBackToParentCultures = true;
                requestOptions.FallBackToParentUICultures = true;
            });
        return services;
    }

    public static IEndpointConventionBuilder MapAgentstrationCultureEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/_culture", (
            string? culture,
            string? returnUrl,
            HttpContext context,
            TimeProvider timeProvider,
            IOptions<AgentstrationLocalizationOptions> configuredOptions) =>
        {
            if (string.Equals(culture, "auto", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Cookies.Delete(CookieRequestCultureProvider.DefaultCookieName, CookieOptions(context, timeProvider));
            }
            else
            {
                var supported = configuredOptions.Value.SupportedCultures.FirstOrDefault(value =>
                    string.Equals(value, culture, StringComparison.OrdinalIgnoreCase));
                if (supported is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["culture"] = ["The requested culture is not supported."]
                    });

                context.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(supported)),
                    CookieOptions(context, timeProvider));
            }

            return Results.LocalRedirect(LocalReturnUrl(returnUrl));
        });
    }

    private static CookieOptions CookieOptions(HttpContext context, TimeProvider timeProvider) => new()
    {
        Expires = timeProvider.GetUtcNow().AddYears(1),
        HttpOnly = true,
        IsEssential = true,
        Path = "/",
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps
    };

    private static string LocalReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    private static bool Validate(AgentstrationLocalizationOptions options)
    {
        if (options.SupportedCultures.Length == 0
            || options.SupportedCultures.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.SupportedCultures.Length
            || !options.SupportedCultures.Contains(options.DefaultCulture, StringComparer.OrdinalIgnoreCase))
            return false;

        try
        {
            _ = CultureInfo.GetCultureInfo(options.DefaultCulture);
            foreach (var culture in options.SupportedCultures) _ = CultureInfo.GetCultureInfo(culture);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}

public static class CultureNavigation
{
    public static bool NavigateToPreferredCulture(NavigationManager navigation, string? language)
    {
        if (string.IsNullOrWhiteSpace(language)
            || string.Equals(CultureInfo.CurrentUICulture.Name, language, StringComparison.OrdinalIgnoreCase))
            return false;

        var current = new Uri(navigation.Uri);
        var returnUrl = current.PathAndQuery;
        navigation.NavigateTo(
            $"/_culture?culture={Uri.EscapeDataString(language)}&returnUrl={Uri.EscapeDataString(returnUrl)}",
            forceLoad: true);
        return true;
    }
}
