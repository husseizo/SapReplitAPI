using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Auth;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Models.Invoicing;
using SapReplitAPI.Models.Inventory;
using SapReplitAPI.Models.SoDelivery;

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

    // ── Warehouse inventory ───────────────────────────────────────────────────
    public DbSet<WarehouseInventory> WarehouseInventories { get; set; }

    // ── Bin inventory (one row per ItemCode + WhsCode + BinAbsEntry) ──────────
    public DbSet<BinInventory> BinInventories { get; set; }

    // ── Delivery cache ────────────────────────────────────────────────────────
    public DbSet<CachedDelivery>     Deliveries     { get; set; }
    public DbSet<CachedDeliveryLine> DeliveryLines  { get; set; }

    // ── Pick list cache ───────────────────────────────────────────────────────
    public DbSet<CachedPickList>            PickLists            { get; set; }
    public DbSet<CachedPickListLine>        PickListLines        { get; set; }
    public DbSet<CachedPickListBinAllocation> PickListBinAllocations { get; set; }

    // ── SO → Delivery audit tables ────────────────────────────────────────────
    public DbSet<SoDeliveryRun>     SoDeliveryRuns     { get; set; }
    public DbSet<SoDeliveryLog>     SoDeliveryLogs     { get; set; }
    public DbSet<SoDeliveryLineLog> SoDeliveryLineLogs { get; set; }

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

            entity.Property(i => i.ZoneRef).IsRequired(false);
            entity.Property(i => i.U_ReplitId).IsRequired(false);
            entity.Property(i => i.DeliveryLocation).IsRequired(false);
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
            entity.Property(p => p.Canceled).HasDefaultValue(false);
            entity.Property(p => p.CounterRef).HasDefaultValue(string.Empty);
            entity.Property(p => p.LastUpdated).HasDefaultValue(new DateTime(1900, 1, 1));
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

        // ── WarehouseInventory ────────────────────────────────────────────────
        modelBuilder.Entity<WarehouseInventory>(entity =>
        {
            entity.ToTable("WarehouseInventory");
            entity.HasKey(w => w.Id);

            // Unique identity: one row per ItemCode + WhsCode
            entity.HasIndex(w => new { w.ItemCode, w.WhsCode })
                  .IsUnique()
                  .HasDatabaseName("UX_WarehouseInventory_ItemCode_WhsCode");

            // Supporting indexes for filtering and delta sync watermark
            entity.HasIndex(w => w.ItemCode).HasDatabaseName("IX_WarehouseInventory_ItemCode");
            entity.HasIndex(w => w.WhsCode).HasDatabaseName("IX_WarehouseInventory_WhsCode");
            entity.HasIndex(w => w.LastUpdated).HasDatabaseName("IX_WarehouseInventory_LastUpdated");

            entity.Property(w => w.ItemCode).IsRequired();
            entity.Property(w => w.WhsCode).IsRequired();
            entity.Property(w => w.WarehouseName).IsRequired();

            entity.Property(w => w.OnHand).HasColumnType("decimal(18,4)");
            entity.Property(w => w.IsCommitted).HasColumnType("decimal(18,4)");
            entity.Property(w => w.OnOrder).HasColumnType("decimal(18,4)");
            entity.Property(w => w.AvailableToSell).HasColumnType("decimal(18,4)");

            entity.Property(w => w.IsBinManaged).HasDefaultValue(false);
            entity.Property(w => w.LastUpdated).IsRequired();
        });

        // ── BinInventory ──────────────────────────────────────────────────────
        // One row per (ItemCode, WhsCode, BinAbsEntry).
        // Zero-stock rows are NOT stored — sync removes them when OnHandQty drops to 0.
        // No FK to WarehouseInventory: both are independent cache tables rebuilt on full sync.
        modelBuilder.Entity<BinInventory>(entity =>
        {
            entity.ToTable("BinInventory");
            entity.HasKey(b => b.Id);

            // Identity: one item can span many bins in the same warehouse
            entity.HasIndex(b => new { b.ItemCode, b.WhsCode, b.BinAbsEntry })
                  .IsUnique()
                  .HasDatabaseName("UX_BinInventory_ItemCode_WhsCode_BinAbsEntry");

            // Supporting lookup indexes
            entity.HasIndex(b => b.ItemCode).HasDatabaseName("IX_BinInventory_ItemCode");
            entity.HasIndex(b => b.WhsCode).HasDatabaseName("IX_BinInventory_WhsCode");
            entity.HasIndex(b => new { b.ItemCode, b.WhsCode }).HasDatabaseName("IX_BinInventory_ItemCode_WhsCode");
            entity.HasIndex(b => b.LastUpdated).HasDatabaseName("IX_BinInventory_LastUpdated");

            entity.Property(b => b.ItemCode).IsRequired();
            entity.Property(b => b.WhsCode).IsRequired();
            entity.Property(b => b.BinCode).IsRequired();
            entity.Property(b => b.BinOnHand).HasColumnType("decimal(18,4)");
            entity.Property(b => b.LastUpdated).IsRequired();
        });

        // ── CachedDelivery ────────────────────────────────────────────────────
        modelBuilder.Entity<CachedDelivery>(entity =>
        {
            entity.ToTable("Deliveries");
            entity.HasKey(d => d.DocEntry);
            entity.Property(d => d.DocEntry).ValueGeneratedNever();
            entity.Property(d => d.DocNum).IsRequired();
            entity.Property(d => d.DocDate).IsRequired();
            entity.Property(d => d.DocDueDate).IsRequired();
            entity.Property(d => d.TaxDate).IsRequired();
            entity.Property(d => d.DocStatus).IsRequired();
            entity.Property(d => d.Canceled).IsRequired();
            entity.Property(d => d.CardCode).IsRequired();
            entity.Property(d => d.CardName).IsRequired();
            entity.Property(d => d.DocTotal).HasColumnType("decimal(18,2)");
            entity.Property(d => d.DocCur).IsRequired();
            entity.Property(d => d.SlpName).HasDefaultValue(string.Empty);
            entity.Property(d => d.Comments).HasDefaultValue(string.Empty);
            entity.Property(d => d.CreateDate).IsRequired();
            entity.Property(d => d.UpdateDate).IsRequired();
            entity.Property(d => d.DocStatusDisplay).HasDefaultValue(string.Empty);
            entity.Property(d => d.U_ReplitId).HasDefaultValue(null);
            entity.Property(d => d.ZoneRef).HasDefaultValue(null);
            entity.Property(d => d.DeliveryLocation).HasDefaultValue(null);
            entity.Ignore(d => d.Lines);
            entity.HasIndex(d => d.DocNum);
            entity.HasIndex(d => d.CardCode);
            entity.HasIndex(d => d.DocDate);
            entity.HasIndex(d => new { d.DocStatus, d.Canceled });
        });

        // ── CachedDeliveryLine ────────────────────────────────────────────────
        modelBuilder.Entity<CachedDeliveryLine>(entity =>
        {
            entity.ToTable("DeliveryLines");
            entity.HasKey(l => new { l.DocEntry, l.LineNum });
            entity.Property(l => l.DocEntry).IsRequired();
            entity.Property(l => l.LineNum).IsRequired();
            entity.Property(l => l.ItemCode).IsRequired();
            entity.Property(l => l.Dscription).HasDefaultValue(string.Empty);
            entity.Property(l => l.Quantity).HasColumnType("decimal(18,4)");
            entity.Property(l => l.OpenQty).HasColumnType("decimal(18,4)");
            entity.Property(l => l.WhsCode).IsRequired();
            entity.Property(l => l.Price).HasColumnType("decimal(18,2)");
            entity.Property(l => l.LineTotal).HasColumnType("decimal(18,2)");
            entity.Property(l => l.Currency).HasDefaultValue(string.Empty);
            entity.HasIndex(l => l.DocEntry).HasDatabaseName("IX_DeliveryLines_DocEntry");
            entity.HasIndex(l => l.ItemCode).HasDatabaseName("IX_DeliveryLines_ItemCode");
        });

        // ── SoDeliveryRuns ────────────────────────────────────────────────────
        modelBuilder.Entity<SoDeliveryRun>(entity =>
        {
            entity.ToTable("SoDeliveryRuns");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.ProcessingDate).IsRequired();
            entity.Property(r => r.StartTime).IsRequired();
            entity.Property(r => r.Status).IsRequired().HasDefaultValue(RunStatus.Running);
            entity.Property(r => r.TriggeredBy).IsRequired().HasDefaultValue("Job");
            entity.Property(r => r.IsForced).HasDefaultValue(false);
            entity.Property(r => r.TotalOrders).HasDefaultValue(0);
            entity.Property(r => r.SuccessCount).HasDefaultValue(0);
            entity.Property(r => r.FailedCount).HasDefaultValue(0);
            entity.Property(r => r.SkippedCount).HasDefaultValue(0);
            entity.Property(r => r.ExceptionCount).HasDefaultValue(0);
            entity.Property(r => r.TotalDeliveriesCreated).HasDefaultValue(0);

            // NOT unique — multiple runs per date allowed (force=true)
            entity.HasIndex(r => r.ProcessingDate).HasDatabaseName("IX_SoDeliveryRuns_ProcessingDate");
            entity.HasIndex(r => r.Status).HasDatabaseName("IX_SoDeliveryRuns_Status");
        });

        // ── SoDeliveryLogs ────────────────────────────────────────────────────
        modelBuilder.Entity<SoDeliveryLog>(entity =>
        {
            entity.ToTable("SoDeliveryLogs");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Status).IsRequired();
            entity.Property(l => l.CustomerCode).HasDefaultValue(string.Empty);
            entity.Property(l => l.CustomerName).HasDefaultValue(string.Empty);

            // Prevent the same SO from being logged twice within one run
            entity.HasIndex(l => new { l.RunId, l.SoDocEntry })
                  .IsUnique()
                  .HasDatabaseName("UX_SoDeliveryLogs_RunId_SoDocEntry");

            entity.HasIndex(l => l.RunId).HasDatabaseName("IX_SoDeliveryLogs_RunId");
            entity.HasIndex(l => l.SoDocEntry).HasDatabaseName("IX_SoDeliveryLogs_SoDocEntry");
            entity.HasIndex(l => l.Status).HasDatabaseName("IX_SoDeliveryLogs_Status");

            // Restrict (not Cascade) — preserve historical audit logs
            entity.HasOne(l => l.Run)
                  .WithMany(r => r.Logs)
                  .HasForeignKey(l => l.RunId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ── SoDeliveryLineLogs ────────────────────────────────────────────────
        modelBuilder.Entity<SoDeliveryLineLog>(entity =>
        {
            entity.ToTable("SoDeliveryLineLogs");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Status).IsRequired();
            entity.Property(l => l.ItemCode).HasDefaultValue(string.Empty);
            entity.Property(l => l.ItemDescription).HasDefaultValue(string.Empty);
            entity.Property(l => l.WarehouseCode).HasDefaultValue(string.Empty);

            entity.Property(l => l.Quantity).HasColumnType("decimal(18,4)");
            entity.Property(l => l.OpenQuantity).HasColumnType("decimal(18,4)");
            entity.Property(l => l.OnHandBefore).HasColumnType("decimal(18,4)");
            entity.Property(l => l.OnHandAfter).HasColumnType("decimal(18,4)");

            entity.HasIndex(l => l.LogId).HasDatabaseName("IX_SoDeliveryLineLogs_LogId");

            // Restrict — preserve historical audit logs
            entity.HasOne(l => l.Log)
                  .WithMany(r => r.Lines)
                  .HasForeignKey(l => l.LogId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ── CachedPickList ────────────────────────────────────────────────────
        modelBuilder.Entity<CachedPickList>(entity =>
        {
            entity.ToTable("PickLists");
            entity.HasKey(p => p.AbsEntry);
            entity.Property(p => p.AbsEntry).ValueGeneratedNever();
            entity.Property(p => p.Name).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(p => p.OwnerName).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(p => p.Status).IsRequired();
            entity.Property(p => p.Canceled).IsRequired().HasDefaultValue("N");
            entity.Property(p => p.Remarks).HasDefaultValue(string.Empty);
            entity.Property(p => p.PickDate).IsRequired();
            entity.Property(p => p.CreateDate).IsRequired();
            entity.Property(p => p.UpdateDate).IsRequired();
            entity.Property(p => p.U_ReplitId).HasDefaultValue(null);
            entity.Property(p => p.SlpCode).HasDefaultValue(null);
            entity.Property(p => p.SlpName).HasDefaultValue(string.Empty);
            entity.Property(p => p.LastSyncedAt).IsRequired();
            entity.Property(p => p.ZoneRef).HasDefaultValue(null);
            entity.Property(p => p.DeliveryLocation).HasDefaultValue(null);
            entity.HasIndex(p => p.Status).HasDatabaseName("IX_PickLists_Status");
            entity.HasIndex(p => p.OwnerCode).HasDatabaseName("IX_PickLists_OwnerCode");
            entity.HasIndex(p => p.UpdateDate).HasDatabaseName("IX_PickLists_UpdateDate");
        });

        // ── CachedPickListLine ────────────────────────────────────────────────
        modelBuilder.Entity<CachedPickListLine>(entity =>
        {
            entity.ToTable("PickListLines");
            entity.HasKey(l => new { l.AbsEntry, l.PickEntry });
            entity.Property(l => l.RelQtty).HasColumnType("decimal(18,4)");
            entity.Property(l => l.PickQtty).HasColumnType("decimal(18,4)");
            entity.Property(l => l.PrevReleas).HasColumnType("decimal(18,4)");
            entity.Property(l => l.PickStatus).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(l => l.ItemCode).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(l => l.Dscription).HasDefaultValue(string.Empty);
            entity.Property(l => l.WhsCode).HasDefaultValue(string.Empty);
            entity.Property(l => l.ZoneRef).HasDefaultValue(null);
            entity.Property(l => l.DeliveryLocation).HasDefaultValue(null);
            entity.Property(l => l.U_ReplitId).HasDefaultValue(null);
            entity.HasIndex(l => l.AbsEntry).HasDatabaseName("IX_PickListLines_AbsEntry");
            entity.HasIndex(l => l.OrderEntry).HasDatabaseName("IX_PickListLines_OrderEntry");
        });

        // ── CachedPickListBinAllocation ───────────────────────────────────────
        modelBuilder.Entity<CachedPickListBinAllocation>(entity =>
        {
            entity.ToTable("PickListBinAllocations");
            entity.HasKey(b => new { b.AbsEntry, b.PickEntry, b.Pkl2LinNum });
            entity.Property(b => b.PickQtty).HasColumnType("decimal(18,4)");
            entity.Property(b => b.RelQtty).HasColumnType("decimal(18,4)");
            entity.Property(b => b.OpenCreQty).HasColumnType("decimal(18,4)");
            entity.Property(b => b.ItemCode).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(b => b.WhsCode).HasDefaultValue(string.Empty);
            entity.Property(b => b.BinCode).HasDefaultValue(string.Empty);
            entity.Property(b => b.PickListName).HasDefaultValue(string.Empty);
            entity.Property(b => b.PickListStatus).HasDefaultValue(string.Empty);
            entity.Property(b => b.SlpCode).HasDefaultValue(null);
            entity.Property(b => b.SlpName).HasDefaultValue(string.Empty);
            entity.Property(b => b.ZoneRef).HasDefaultValue(null);
            entity.Property(b => b.DeliveryLocation).HasDefaultValue(null);
            entity.Property(b => b.U_ReplitId).HasDefaultValue(null);
            entity.HasIndex(b => b.AbsEntry).HasDatabaseName("IX_PickListBinAllocations_AbsEntry");
            entity.HasIndex(b => b.BinAbsEntry).HasDatabaseName("IX_PickListBinAllocations_BinAbsEntry");
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
