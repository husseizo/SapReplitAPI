namespace SapReplitAPI.Models
{
    public class ApiSecuritySettings
    {
        /// <summary>
        /// Key callers must send in the X-API-Key header on every API request.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Password used to access the Swagger UI page (HTTP Basic Auth, any username).
        /// </summary>
        public string SwaggerPassword { get; set; } = string.Empty;
    }
}
