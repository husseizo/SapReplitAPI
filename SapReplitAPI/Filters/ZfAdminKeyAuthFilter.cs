using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models;

namespace SapReplitAPI.Filters;

/// <summary>
/// Admin-only API key filter for ZF operations console.
/// Checks X-ZF-Admin-Key against ZfAdmin__AdminApiKey config.
/// Separate from the operational X-API-Key — different key, different scope.
/// </summary>
public sealed class ZfAdminKeyAuthFilter : IAsyncActionFilter
{
    private const string AdminKeyHeader = "X-ZF-Admin-Key";

    private readonly ZfAdminSettings _settings;

    public ZfAdminKeyAuthFilter(IOptions<ZfAdminSettings> settings)
        => _settings = settings.Value;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!string.IsNullOrWhiteSpace(_settings.AdminApiKey))
        {
            if (!context.HttpContext.Request.Headers.TryGetValue(AdminKeyHeader, out var supplied)
                || supplied.ToString() != _settings.AdminApiKey)
            {
                context.Result = new UnauthorizedObjectResult(new
                {
                    message = $"Missing or invalid admin key. Provide it in the {AdminKeyHeader} header."
                });
                return;
            }
        }

        await next();
    }
}
