using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models;

namespace SapReplitAPI.Filters
{
    public class ApiKeyAuthFilter : IAsyncActionFilter
    {
        private const string ApiKeyHeader = "X-API-Key";
        private readonly ApiSecuritySettings _settings;

        public ApiKeyAuthFilter(IOptions<ApiSecuritySettings> settings)
        {
            _settings = settings.Value;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeader, out var supplied)
                    || supplied.ToString() != _settings.ApiKey)
                {
                    context.Result = new UnauthorizedObjectResult(new
                    {
                        message = "Missing or invalid API key. Provide it in the X-API-Key header."
                    });
                    return;
                }
            }

            await next();
        }
    }
}
