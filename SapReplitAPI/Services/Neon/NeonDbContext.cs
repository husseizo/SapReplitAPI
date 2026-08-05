using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.CachedProducts;

namespace SapReplitAPI.Services.Neon;

/// <summary>
/// PostgreSQL (Neon) mirror of the local SQLite cache.
/// Used only for EnsureCreated() at startup to auto-create the schema.
/// Actual writes are done with raw Npgsql SQL in NeonSyncJob for speed.
/// </summary>
public class NeonDbContext : DbContext
{
    public NeonDbContext(DbContextOptions<NeonDbContext> options) : base(options) { }

    public DbSet<CachedProduct> Products { get; set; }
    public DbSet<CachedCustomer> Customers { get; set; }
    public DbSet<CachedInvoice> Invoices { get; set; }
    public DbSet<CachedInvoiceLine> InvoiceLines { get; set; }
    public DbSet<CachedInvoicePayment> InvoicePayments { get; set; }
    public DbSet<CachedOrder> OrderHeaders { get; set; }
    public DbSet<CachedOrderLine> OrderLines { get; set; }
    public DbSet<CachedTodayOrder> TodayOrderHeaders { get; set; }
    public DbSet<CachedTodayOrderLine> TodayOrderLines { get; set; }
    public DbSet<CachedOpenOrder> OpenOrderHeaders { get; set; }
    public DbSet<CachedOpenOrderLine> OpenOrderLines { get; set; }
    public DbSet<DetailedInvoiceStatusCache> InvoiceStatusCache { get; set; }
    public DbSet<GlAccountStatement> AccountStatements { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Products ──────────────────────────────────────────────────────────
        mb.Entity<CachedProduct>(e =>
        {
            e.ToTable("Products");
            e.HasKey(p => p.Id);
            e.Property(p => p.ItemCode).IsRequired();
            e.HasIndex(p => p.ItemCode).IsUnique(); // needed for ON CONFLICT(ItemCode)
            e.Property(p => p.Price).HasColumnType("numeric(18,2)");
            e.Property(p => p.Price05).HasColumnType("numeric(18,2)").HasDefaultValue(0);
            e.Property(p => p.TotalOnHand).HasColumnType("numeric(18,2)").HasDefaultValue(0);
            e.Property(p => p.OnHand).HasColumnType("numeric(18,2)");
            e.Property(p => p.OnHandQty).HasColumnType("numeric(18,2)");
            // Whs_001-004 are int? → PostgreSQL integer — no special column type needed
        });

        // ── Customers ─────────────────────────────────────────────────────────
        mb.Entity<CachedCustomer>(e =>
        {
            e.ToTable("Customers");
            e.HasKey(c => c.Id);
            e.Property(c => c.CardCode).IsRequired();
            e.HasIndex(c => c.CardCode).IsUnique(); // needed for ON CONFLICT(CardCode)
            e.Property(c => c.Balance).HasColumnType("numeric(18,2)");
            e.Property(c => c.TotalSpent).HasColumnType("numeric(18,2)");
            e.Property(c => c.AddressesJson).HasDefaultValue("");
            e.Property(c => c.VIN1).HasDefaultValue("");
            e.Property(c => c.VIN2).HasDefaultValue("");
            e.Property(c => c.VIN3).HasDefaultValue("");
        });

        // ── Invoices ──────────────────────────────────────────────────────────
        mb.Entity<CachedInvoice>(e =>
        {
            e.ToTable("Invoices");
            e.HasKey(i => i.DocEntry);
            e.Property(i => i.DocEntry).ValueGeneratedNever();
            e.Property(i => i.DocTotal).HasColumnType("numeric(18,2)");
            e.Property(i => i.PaidToDate).HasColumnType("numeric(18,2)");
            e.Property(i => i.BalanceDue).HasColumnType("numeric(18,2)");
            e.Property(i => i.GroupNum).HasDefaultValue(0);
            e.Property(i => i.DocStatusDisplay).HasDefaultValue("");
        });

        // ── InvoiceLines ──────────────────────────────────────────────────────
        mb.Entity<CachedInvoiceLine>(e =>
        {
            e.ToTable("InvoiceLines");
            e.HasKey(l => l.Id);
            e.Property(l => l.Quantity).HasColumnType("numeric(18,2)");
            e.Property(l => l.Price).HasColumnType("numeric(18,2)");
            e.Property(l => l.LineTotal).HasColumnType("numeric(18,2)");
            e.Property(l => l.U_Item_Name).HasDefaultValue("");
            e.Property(l => l.U_ItemName).HasDefaultValue("");
            e.Property(l => l.U_MdlTEST).HasDefaultValue("");
            e.Property(l => l.U_Manufacturer).HasDefaultValue("");
            // Cascade from invoice
            e.HasOne<CachedInvoice>()
             .WithMany(i => i.Lines)
             .HasForeignKey(l => l.DocEntry)
             .HasPrincipalKey(i => i.DocEntry)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── InvoicePayments ───────────────────────────────────────────────────
        mb.Entity<CachedInvoicePayment>(e =>
        {
            e.ToTable("InvoicePayments");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.PaymentDocEntry).IsUnique();
            e.Property(p => p.AmountApplied).HasColumnType("numeric(18,2)");
            e.Property(p => p.BankTransferAmount).HasColumnType("numeric(18,2)");
            e.Property(p => p.BankTransferReference).HasDefaultValue("");
            e.Property(p => p.DebitAccountCode).HasDefaultValue("");
            e.Property(p => p.DebitAccountName).HasDefaultValue("");
            e.Property(p => p.SalesEmployeeCode).HasDefaultValue("");
            e.Property(p => p.SalesEmployeeName).HasDefaultValue("");
            e.Property(p => p.ClientReference).HasDefaultValue("");
        });

        // ── OrderHeaders ──────────────────────────────────────────────────────
        mb.Entity<CachedOrder>(e =>
        {
            e.ToTable("OrderHeaders");
            e.HasKey(o => o.DocEntry);
            e.Property(o => o.DocEntry).ValueGeneratedNever();
            e.Property(o => o.OrderValue).HasColumnType("numeric(18,2)");
            e.Property(o => o.SlpName).HasDefaultValue("");
        });

        // ── OrderLines ────────────────────────────────────────────────────────
        mb.Entity<CachedOrderLine>(e =>
        {
            e.ToTable("OrderLines");
            e.HasKey(l => l.Id);
            e.Property(l => l.Quantity).HasColumnType("numeric(18,2)");
            e.Property(l => l.Price).HasColumnType("numeric(18,2)");
            e.Property(l => l.U_ItemName).HasDefaultValue("");
            e.Property(l => l.U_Manufacturer).HasDefaultValue("");
            e.HasOne<CachedOrder>()
             .WithMany()
             .HasForeignKey(l => l.DocEntry)
             .HasPrincipalKey(o => o.DocEntry)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── TodayOrderHeaders ─────────────────────────────────────────────────
        mb.Entity<CachedTodayOrder>(e =>
        {
            e.ToTable("TodayOrderHeaders");
            e.HasKey(o => o.DocEntry);
            e.Property(o => o.DocEntry).ValueGeneratedNever();
            e.Property(o => o.OrderValue).HasColumnType("numeric(18,2)");
            e.Property(o => o.SlpName).HasDefaultValue("");
        });

        // ── TodayOrderLines ───────────────────────────────────────────────────
        mb.Entity<CachedTodayOrderLine>(e =>
        {
            e.ToTable("TodayOrderLines");
            e.HasKey(l => l.Id);
            e.Property(l => l.Quantity).HasColumnType("numeric(18,2)");
            e.Property(l => l.Price).HasColumnType("numeric(18,2)");
            e.Property(l => l.U_ItemName).HasDefaultValue("");
            e.Property(l => l.U_Manufacturer).HasDefaultValue("");
            e.HasOne(l => l.Header)
             .WithMany()
             .HasForeignKey(l => l.DocEntry)
             .HasPrincipalKey(h => h.DocEntry)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── OpenOrderHeaders ──────────────────────────────────────────────────
        mb.Entity<CachedOpenOrder>(e =>
        {
            e.ToTable("OpenOrderHeaders");
            e.HasKey(o => o.DocEntry);
            e.Property(o => o.DocEntry).ValueGeneratedNever();
            e.Property(o => o.OrderTotal).HasColumnType("numeric(18,2)");
            e.Property(o => o.SlpName).HasDefaultValue("");
        });

        // ── OpenOrderLines ────────────────────────────────────────────────────
        mb.Entity<CachedOpenOrderLine>(e =>
        {
            e.ToTable("OpenOrderLines");
            e.HasKey(l => l.Id);
            e.Property(l => l.Quantity).HasColumnType("numeric(18,2)");
            e.Property(l => l.Price).HasColumnType("numeric(18,2)");
            e.Property(l => l.LineTotal).HasColumnType("numeric(18,2)");
            e.HasOne(l => l.Header)
             .WithMany(h => h.Lines)
             .HasForeignKey(l => l.DocEntry)
             .HasPrincipalKey(h => h.DocEntry)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── InvoiceStatusCache ────────────────────────────────────────────────
        mb.Entity<DetailedInvoiceStatusCache>(e =>
        {
            e.ToTable("InvoiceStatusCache");
            e.HasKey(x => x.Id);
            e.Property(x => x.CashSales).HasColumnType("numeric(18,2)");
            e.Property(x => x.CreditSales).HasColumnType("numeric(18,2)");
            e.Property(x => x.ReturnedCashInvoice).HasColumnType("numeric(18,2)");
        });

        // ── AccountStatements ─────────────────────────────────────────────────
        mb.Entity<GlAccountStatement>(e =>
        {
            e.ToTable("AccountStatements");
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.TransId, a.Account }).IsUnique();
            e.Property(a => a.Account).IsRequired();
            e.Property(a => a.Debit).HasColumnType("numeric(18,2)");
            e.Property(a => a.Credit).HasColumnType("numeric(18,2)");
            e.Property(a => a.AccountName).HasDefaultValue("");
            e.Property(a => a.LineMemo).HasDefaultValue("");
            e.Property(a => a.TransType).HasDefaultValue("");
            e.Property(a => a.Ref1).HasDefaultValue("");
            e.Property(a => a.Ref2).HasDefaultValue("");
            e.Property(a => a.CardCode).HasDefaultValue("");
            e.Property(a => a.CardName).HasDefaultValue("");
        });
    }
}
