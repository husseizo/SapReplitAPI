using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountsController : ControllerBase
{
    private static readonly string[] ValidAccounts = ["163000", "164000", "165000", "166000", "167000"];

    private static readonly Dictionary<string, string> AccountNames = new()
    {
        ["163000"] = "Cash on Hand",
        ["164000"] = "CRDB",
        ["165000"] = "M-Pesa Lipa",
        ["166000"] = "AAL NMB",
        ["167000"] = "Tigo Lipa",
    };

    // GET /api/accounts/statement?account=163000&from=2024-01-01&to=2024-12-31&page=1&pageSize=500
    [HttpGet("statement")]
    public async Task<IActionResult> GetStatement(
        [FromServices] NeonDbContext neon,
        [FromQuery] string? account,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 500)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 2000);

        if (account != null && !ValidAccounts.Contains(account))
            return BadRequest(new
            {
                message = $"Invalid account '{account}'. Valid GL codes: {string.Join(", ", ValidAccounts.Select(a => $"{a} ({AccountNames[a]})"))}."
            });

        var fromDate = from ?? new DateTime(2024, 1, 1);
        var toDate   = (to ?? DateTime.Today).AddDays(1).AddTicks(-1); // inclusive end-of-day

        var query = neon.AccountStatements
            .AsNoTracking()
            .Where(s => s.RefDate >= fromDate && s.RefDate <= toDate);

        if (account != null)
            query = query.Where(s => s.Account == account);

        var total = await query.CountAsync();
        var rows  = await query
            .OrderByDescending(s => s.RefDate)
            .ThenByDescending(s => s.TransId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new
        {
            TotalCount    = total,
            Page          = page,
            PageSize      = pageSize,
            From          = fromDate.ToString("yyyy-MM-dd"),
            To            = toDate.ToString("yyyy-MM-dd"),
            AccountFilter = account,
            AccountNames,
            Results       = rows
        });
    }

    // GET /api/accounts/list — returns the 5 valid payment GL accounts
    [HttpGet("list")]
    public IActionResult GetAccountList() =>
        Ok(AccountNames.Select(kv => new { Code = kv.Key, Name = kv.Value }));
}
