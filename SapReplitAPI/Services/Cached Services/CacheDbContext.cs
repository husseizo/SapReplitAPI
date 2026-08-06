using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Auth;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Models.Invoicing;

public class CacheDbContext : DbContext
{
    public CacheDbContext(DbContextOptions<CacheDbContext> options) : base(options) { }

    public DbSet<CachedProduct> Products { get; set; }
    public DbSet<CachedInvoice> Invoices { get; set; }
    public DbSet<CachedInvoiceLine> InvoiceLines { get; set; }
    public DbSet<CachedInvoicePayment> InvoicePayments { get; set; }
    public DbSet<SyncMetadata> SyncMetadata { get; set; }
    public DbSet<CachedCustomer> Customers { get; set; }
    public DbSet<CachedOrder> OrderHeaders { get; set; }
    public DbSet<CachedOrderLine> OrderLines { get; set; }
    public DbSet<SalesTarget> SalesTargets { get; set; }
    public DbSet<User> Users { get; set; } // 🧑 Authentication users
    public DbSet<CachedTodayOrder> TodayOrderHeaders { get; set; }
    public DbSet<CachedTodayOrderLine> TodayOrderLines { get; set; }
    public DbSet<CachedOpenOrder> OpenOrderHeaders { get; set; }
    public DbSet<CachedOpenOrderLine> OpenOrderLines { get; set; }
    public DbSet<GlAccountStatement> AccountStatements { get; set; }
    public DbSet<PaymentIdempotencyLog> PaymentIdempotencyLogs { get; set; }
    public DbSet<InvoiceFromDeliveryLog> InvoiceFromDeliveryLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 🧾 CachedProduct table
        modelBuilder.Entity<CachedProduct>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.ItemCode).IsUnique();
            entity.Property(p => p.ItemCode).IsRequired();
            entity.Property(p => p.ItemName).IsRequired();
            entity.Property(p => p.WhsCode).IsRequired();
            entity.Property(p => p.OnHand).HasColumnType("decimal(18,2)");
            entity.Property(p => p.TotalOnHand).HasColumnType("decimal(18,2)").HasDefaultValue(0);
            entity.Property(p => p.OnHandQty).HasColumnType("decimal(18,2)");
            entity.Property(p => p.Price).HasColumnType("decimal(18,2)");
            entity.Property(p => p.Price05).HasColumnType("decimal(18,2)").HasDefaultValue(0);
            entity.Property(p => p.LastUpdated).IsRequired();
            entity.Property(p => p.U_Item_Name).IsRequired();
            entity.Property(p => p.Whs_001).HasColumnType("REAL").HasDefaultValue(0);
            entity.Property(p => p.Whs_002).HasColumnType("REAL").HasDefaultValue(0);
            entity.Property(p => p.Whs_003).HasColumnType("REAL").HasDefaultValue(0);
            entity.Property(p => p.Whs_004).HasColumnType("REAL").HasDefaultValue(0);
        });

        // 🧾 CachedInvoice table — ✅ Primary Key is DocEntry
        modelBuilder.Entity<CachedInvoice>(entity =>
        {
            entity.HasKey(i => i.DocEntry);
            entity.Property(i => i.DocEntry).ValueGeneratedNever();

            entity.Property(i => i.DocNum).IsRequired();
            entity.Property(i => i.InvoiceDocNum).IsRequired();

            entity.Property(i => i.DocDate).IsRequired();
            entity.Property(i => i.DocStatus).IsRequired();
            entity.Property(i => i.CardCode).IsRequired();
            entity.Property(i => i.CardName).IsRequired();
            entity.Property(i => i.DocTotal).HasColumnType("decimal(18,2)");
            entity.Property(i => i.PaidToDate).HasColumnType("decimal(18,2)");
            entity.Property(i => i.BalanceDue).HasColumnType("decimal(18,2)");
            entity.Property(i => i.SalesEmployeeCode).IsRequired();
            entity.Property(i => i.SalesEmployeeName).IsRequired();
            entity.Property(i => i.DaysOverdue).HasDefaultValue(0);
            entity.Property(i => i.Canceled).IsRequired();

            // ✅ NEW REQUIRED FIX
            entity.Property(i => i.GroupNum)
                  .HasDefaultValue(0)
                  .IsRequired();

            entity.Property(i => i.DocStatusDisplay).HasDefaultValue(string.Empty);
        });

        // 🧾 CachedInvoiceLine table
        modelBuilder.Entity<CachedInvoiceLine>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.DocEntry).IsRequired();
            entity.Property(l => l.LineNum).IsRequired();
            entity.Property(l => l.ItemCode).IsRequired();
            entity.Property(l => l.Dscription).IsRequired();
            entity.Property(l => l.Quantity).HasColumnType("decimal(18,2)");
            entity.Property(l => l.Price).HasColumnType("decimal(18,2)");
            entity.Property(l => l.LineTotal).HasColumnType("decimal(18,2)");
            entity.Property(l => l.U_Item_Name).HasDefaultValue(string.Empty);
            entity.Property(l => l.U_ItemName).HasDefaultValue(string.Empty);
            entity.Property(l => l.U_MdlTEST).HasDefaultValue(string.Empty);
            entity.Property(l => l.U_Manufacturer).HasDefaultValue(string.Empty);

            entity.HasOne<CachedInvoice>()
                  .WithMany(i => i.Lines)
                  .HasForeignKey(l => l.DocEntry)
                  .HasPrincipalKey(i => i.DocEntry)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // 💰 CachedInvoicePayment table
        modelBuilder.Entity<CachedInvoicePayment>(entity =>
        {
            entity.HasKey(p => p.Id);

            // A single SAP incoming payment can be applied to multiple invoices,
            // so the unique row identity must be invoice + payment, not payment alone.
            entity.HasIndex(p => new { p.DocEntry, p.PaymentDocEntry })
                  .IsUnique();
            entity.HasIndex(p => p.DocEntry);
            entity.HasIndex(p => p.PaymentDocEntry);

            entity.Property(p => p.DocEntry).IsRequired();
            entity.Property(p => p.PaymentDocEntry).IsRequired();
            entity.Property(p => p.PaymentNumber).IsRequired();
            entity.Property(p => p.InvoiceDocNum).IsRequired();
            entity.Property(p => p.PaymentDate).IsRequired();
            entity.Property(p => p.CardCode).IsRequired();
            entity.Property(p => p.CardName).IsRequired();
            entity.Property(p => p.AmountApplied).HasColumnType("decimal(18,2)");
            entity.Property(p => p.BankTransferAmount).HasColumnType("decimal(18,2)");
            entity.Property(p => p.BankTransferReference).HasDefaultValue(string.Empty);
            entity.Property(p => p.DebitAccountCode).HasDefaultValue(string.Empty);
            entity.Property(p => p.DebitAccountName).HasDefaultValue(string.Empty);
            entity.Property(p => p.SalesEmployeeCode).HasDefaultValue(string.Empty);
            entity.Property(p => p.SalesEmployeeName).HasDefaultValue(string.Empty);
            entity.Property(p => p.ClientReference).HasDefaultValue(string.Empty);
        });

        // 🕒 SyncMetadata table
        modelBuilder.Entity<SyncMetadata>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Type).IsRequired();
            entity.Property(s => s.LastSyncedAt).IsRequired();
            entity.HasIndex(s => s.Type).IsUnique();
        });

        // 📦 CachedOrder (OrderHeaders)
        modelBuilder.Entity<CachedOrder>(entity =>
        {
            entity.HasKey(o => o.DocEntry);
            entity.Property(o => o.DocNum).IsRequired();
            entity.Property(o => o.CardName).IsRequired();
            entity.Property(o => o.DocDate).IsRequired();
            entity.Property(o => o.OrderValue).HasColumnType("decimal(18,2)");
            entity.Property(o => o.Status).IsRequired();
            entity.Property(o => o.SlpCode); // 🆕 Added
            entity.Property(o => o.SlpName).HasDefaultValue(string.Empty); // 🆕 Added
            // ✅ Restore DEFAULT '' that was lost when DropOrderHeadersId rebuilt the table without it.
            //    The raw SQL UPSERT always supplies '' explicitly; this default is a safety net.
            entity.Property(o => o.CancellationStatus).HasDefaultValue(string.Empty);
        });

        // 📦 CachedOrderLine
        modelBuilder.Entity<CachedOrderLine>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => new { l.DocEntry, l.LineNum }).IsUnique();
            entity.Property(l => l.DocEntry).IsRequired();
            entity.Property(l => l.ItemCode).IsRequired();
            entity.Property(l => l.Dscription).IsRequired();
            entity.Property(l => l.Quantity).HasColumnType("decimal(18,2)");
            entity.Property(l => l.Price).HasColumnType("decimal(18,2)");
            entity.Property(l => l.WhsCode).IsRequired();
            entity.Property(l => l.U_ItemName).HasDefaultValue(string.Empty);
            
            entity.Property(l => l.U_Manufacturer).HasDefaultValue(string.Empty);
            
            entity.Property(l => l.DocDate).IsRequired();

            entity.HasOne<CachedOrder>()
                  .WithMany()
                  .HasForeignKey(l => l.DocEntry)
                  .HasPrincipalKey(o => o.DocEntry)
                  .OnDelete(DeleteBehavior.Cascade);
        });


        // 🎯 SalesTarget table
        modelBuilder.Entity<SalesTarget>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.SlpCode).IsRequired();
            entity.Property(t => t.SlpName).IsRequired();
            entity.Property(t => t.SalesEmployeeCode).IsRequired();
            entity.Property(t => t.SalesEmployeeName).IsRequired();
            entity.Property(t => t.Quarter).IsRequired();
            entity.Property(t => t.TargetAmount).HasColumnType("decimal(18,2)");
        });



        // 📊 DetailedInvoiceStatusCache table
        modelBuilder.Entity<DetailedInvoiceStatusCache>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SlpCode).IsRequired(); // ✅ Add this line
            entity.Property(e => e.SalesName).IsRequired();
            entity.Property(e => e.InvoiceNo).IsRequired();
            entity.Property(e => e.Customer).IsRequired();
            entity.Property(e => e.InvoiceStatus).IsRequired();
            entity.Property(e => e.PaymentsStatus).IsRequired();
            entity.Property(e => e.CancellationStatus).IsRequired();

            entity.Property(e => e.CashSales).HasColumnType("decimal(18,2)");
            entity.Property(e => e.CreditSales).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ReturnedCashInvoice).HasColumnType("decimal(18,2)");
        });


        // 👥 Users table
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Username).IsRequired(); // ← ✅ Add this line
            entity.Property(u => u.SlpCode).IsRequired();
            entity.Property(u => u.SlpName).IsRequired();
            entity.Property(u => u.Password).IsRequired();
            entity.Property(u => u.Role).IsRequired();
            entity.Property(u => u.TwoFASecret).HasDefaultValue(null);
        });


        // 📆 CachedTodayOrder table
        modelBuilder.Entity<CachedTodayOrder>(entity =>
        {
            entity.HasKey(o => o.DocEntry);
            entity.Property(o => o.DocNum).IsRequired();
            entity.Property(o => o.CardName).IsRequired();
            entity.Property(o => o.DocDate).IsRequired();
            entity.Property(o => o.OrderValue).HasColumnType("decimal(18,2)");
            entity.Property(o => o.Status).IsRequired(); // "Cancelled" / "Not Cancelled"
            entity.Property(o => o.SlpCode);
            entity.Property(o => o.SlpName).HasDefaultValue(string.Empty);
            entity.Property(o => o.Cancelled).IsRequired(); // ✅ Boolean field for logic/filtering
        });


        // 📆 CachedTodayOrderLine table
        modelBuilder.Entity<CachedTodayOrderLine>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.DocEntry).IsRequired();
            entity.Property(l => l.ItemCode).IsRequired();
            entity.Property(l => l.Dscription).IsRequired();
            entity.Property(l => l.Quantity).HasColumnType("decimal(18,2)");
            entity.Property(l => l.Price).HasColumnType("decimal(18,2)");
            entity.Property(l => l.WhsCode).IsRequired();
            entity.Property(l => l.U_ItemName).HasDefaultValue(string.Empty);
            entity.Property(l => l.U_Manufacturer).HasDefaultValue(string.Empty);
            entity.Property(l => l.DocDate).IsRequired();

            // ✅ Define proper foreign key to CachedTodayOrder.DocEntry
            entity.HasOne(l => l.Header)
                  .WithMany(h => h.Lines)  // ← FIX: Reference the Lines collection
                  .HasForeignKey(l => l.DocEntry)
                  .HasPrincipalKey(h => h.DocEntry)
                  .OnDelete(DeleteBehavior.Cascade);
        });





        // 📦 CachedOpenOrder
        modelBuilder.Entity<CachedOpenOrder>(entity =>
        {
            entity.HasKey(o => o.DocEntry);
            entity.Property(o => o.DocNum).IsRequired();
            entity.Property(o => o.CardCode).IsRequired();
            entity.Property(o => o.CardName).IsRequired();
            entity.Property(o => o.DocDate).IsRequired();
            entity.Property(o => o.OrderTotal).HasColumnType("decimal(18,2)");
            entity.Property(o => o.SlpCode).IsRequired();
            entity.Property(o => o.SlpName).HasDefaultValue(string.Empty);
            entity.Property(o => o.Status).IsRequired();
        });

        // 📦 CachedOpenOrderLine
        modelBuilder.Entity<CachedOpenOrderLine>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.DocEntry).IsRequired();
            entity.Property(l => l.LineNum).IsRequired();
            entity.Property(l => l.ItemCode).IsRequired();
            entity.Property(l => l.Dscription).IsRequired();
            entity.Property(l => l.Quantity).HasColumnType("decimal(18,2)");
            entity.Property(l => l.Price).HasColumnType("decimal(18,2)");
            entity.Property(l => l.LineTotal).HasColumnType("decimal(18,2)");
            entity.Property(l => l.WhsCode).IsRequired();
            entity.Property(l => l.DocDate).IsRequired();

            entity.HasOne(l => l.Header)
                  .WithMany(h => h.Lines)
                  .HasForeignKey(l => l.DocEntry)
                  .HasPrincipalKey(h => h.DocEntry)
                  .OnDelete(DeleteBehavior.Cascade);
        });



        // 📊 GlAccountStatement table — excluded from EF migrations because the table
        // is created at startup via explicit CREATE TABLE IF NOT EXISTS SQL.
        // EF still knows about it for LINQ queries.
        modelBuilder.Entity<PaymentIdempotencyLog>(entity =>
        {
            entity.ToTable("PaymentIdempotencyLogs", t => t.ExcludeFromMigrations());
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.ClientReference).IsUnique();
            entity.Property(p => p.ClientReference).IsRequired();
        });

        modelBuilder.Entity<GlAccountStatement>(entity =>
        {
            entity.ToTable("AccountStatements", t => t.ExcludeFromMigrations());
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => new { a.TransId, a.Account }).IsUnique();
            entity.Property(a => a.Account).IsRequired();
            entity.Property(a => a.Debit).HasColumnType("decimal(18,2)");
            entity.Property(a => a.Credit).HasColumnType("decimal(18,2)");
            entity.Property(a => a.AccountName).HasDefaultValue(string.Empty);
            entity.Property(a => a.LineMemo).HasDefaultValue(string.Empty);
            entity.Property(a => a.TransType).HasDefaultValue(string.Empty);
            entity.Property(a => a.Ref1).HasDefaultValue(string.Empty);
            entity.Property(a => a.Ref2).HasDefaultValue(string.Empty);
            entity.Property(a => a.CardCode).HasDefaultValue(string.Empty);
            entity.Property(a => a.CardName).HasDefaultValue(string.Empty);
        });

        modelBuilder.Entity<InvoiceFromDeliveryLog>(entity =>
        {
            entity.ToTable("InvoiceFromDeliveryLogs", t => t.ExcludeFromMigrations());
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => new { l.DeliveryDocEntry, l.Status });
            entity.Property(l => l.CardCode).IsRequired();
            entity.Property(l => l.Status).IsRequired();
            entity.Property(l => l.TriggerSource).IsRequired();
            entity.Property(l => l.ProcessedAt).IsRequired();
        });

        // inside OnModelCreating(ModelBuilder modelBuilder)
        modelBuilder.Entity<CachedCustomer>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => c.CardCode).IsUnique();
            entity.Property(c => c.CardCode).IsRequired();
            entity.Property(c => c.CardName).IsRequired();
            entity.Property(c => c.Balance).HasColumnType("decimal(18,2)");
            entity.Property(c => c.Region).HasDefaultValue(string.Empty);
            entity.Property(c => c.Phone).HasDefaultValue(string.Empty);
            entity.Property(c => c.CustomerType).HasDefaultValue(string.Empty);
            entity.Property(c => c.SalesPersonName).HasDefaultValue(string.Empty);
            entity.Property(c => c.TotalSpent).HasColumnType("decimal(18,2)");
            entity.Property(c => c.AddressesJson).HasDefaultValue(string.Empty);

            // 🔹 NEW VIN columns
            entity.Property(c => c.VIN1).HasDefaultValue(string.Empty);
            entity.Property(c => c.VIN2).HasDefaultValue(string.Empty);
            entity.Property(c => c.VIN3).HasDefaultValue(string.Empty);
        });
    }
}
