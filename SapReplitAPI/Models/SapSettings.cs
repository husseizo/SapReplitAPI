public class SapSettings
{
    public string Server { get; set; } = "";
    public string CompanyDB { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string DbServerType { get; set; } = "";
    public string LicenseServer { get; set; } = ""; // e.g. "saplic-host:30000"
    public string SLDServer { get; set; } = "";     // e.g. "saplic-host:40000"
}