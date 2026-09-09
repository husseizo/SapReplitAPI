using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Models.Inventory;
using SapReplitAPI.Models.Offline;
using SapReplitAPI.Models.Pending;

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
    public DbSet<PendingOrder> PendingOrders { get; set; }
    public DbSet<PendingOrderLine> PendingOrderLines { get; set; }
    public DbSet<PendingCustomer> PendingCustomers { get; set; }
    public DbSet<WarehouseInventory> WarehouseInventories { get; set; }
    public DbSet<BinInventory> BinInventories { get; set; }
    public DbSet<CachedDelivery>     Deliveries    { get; set; }
    public DbSet<CachedDeliveryLine> DeliveryLines { get; set; }
    public DbSet<CachedPickList>            PickLists            { get; set; }
    public DbSet<CachedPickListLine>        PickListLines        { get; set; }
    public DbSet<CachedPickListBinAllocation> PickListBinAllocations { get; set; }

    // ── Offline Fulfillment V2 ─────────────────────────────────────────────────
    // These tables are NEW and additive. V1 PendingOrders tables are unchanged.
    public DbSet<OfflineFulfillmentOrder>     OfflineFulfillmentOrders     { get; set; }
    public DbSet<OfflineFulfillmentOrderLine> OfflineFulfillmentOrderLines { get; set; }
    public DbSet<OfflineFulfillmentPick>      OfflineFulfillmentPicks      { get; set; }
    public DbSet<OfflineReservation>          OfflineReservations          { get; set; }

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
            e.HasIndex(p => new { p.DocEntry, p.PaymentDocEntry }).IsUnique();
            e.Property(p => p.AmountApplied).HasColumnType("numeric(18,2)");
            e.Property(p => p.BankTransferAmount).HasColumnType("numeric(18,2)");
            e.Property(p => p.BankTransferReference).HasDefaultValue("");
            e.Property(p => p.DebitAccountCode).HasDefaultValue("");
            e.Property(p => p.DebitAccountName).HasDefaultValue("");
            e.Property(p => p.SalesEmployeeCode).HasDefaultValue("");
            e.Property(p => p.SalesEmployeeName).HasDefaultValue("");
            e.Property(p => p.ClientReference).HasDefaultValue("");
            e.Property(p => p.Canceled).HasDefaultValue(false);
            e.Property(p => p.CounterRef).HasDefaultValue("");
            e.Property(p => p.LastUpdated).HasDefaultValue(new DateTime(1900, 1, 1));
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

        // ── PendingOrders ─────────────────────────────────────────────────────
        mb.Entity<PendingOrder>(e =>
        {
            e.ToTable("PendingOrders");
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.ReplitId).IsUnique();
            e.HasIndex(o => o.Status);
            e.Property(o => o.ReplitId).IsRequired();
            e.Property(o => o.CardCode).IsRequired();
            e.Property(o => o.Status).HasDefaultValue("Pending");
            e.Property(o => o.DocCurrency).HasDefaultValue("TZS");
            e.Property(o => o.CreatedAt).HasDefaultValueSql("NOW()");
        });

        // ── PendingOrderLines ────────────────────────────────────────────────
        mb.Entity<PendingOrderLine>(e =>
        {
            e.ToTable("PendingOrderLines");
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.PendingOrderId, l.LineNum }).IsUnique();
            e.Property(l => l.Price).HasColumnType("numeric(18,2)");
            e.Property(l => l.WhsCode).HasDefaultValue("001");
            e.HasOne(l => l.Order)
             .WithMany(o => o.Lines)
             .HasForeignKey(l => l.PendingOrderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── PendingCustomers ──────────────────────────────────────────────────
        mb.Entity<PendingCustomer>(e =>
        {
            e.ToTable("PendingCustomers");
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.Status);
            e.Property(c => c.CardName).IsRequired();
            e.Property(c => c.Phone).HasDefaultValue("");
            e.Property(c => c.CustomerType).HasDefaultValue("");
            e.Property(c => c.Region).HasDefaultValue("");
            e.Property(c => c.SalesPersonName).HasDefaultValue("");
            e.Property(c => c.Status).HasDefaultValue("Pending");
            e.Property(c => c.CreatedAt).HasDefaultValueSql("NOW()");
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

        // ── WarehouseInventory ────────────────────────────────────────────────
        mb.Entity<WarehouseInventory>(e =>
        {
            e.ToTable("WarehouseInventory");
            e.HasKey(w => w.Id);
            e.Property(w => w.ItemCode).IsRequired();
            e.Property(w => w.WhsCode).IsRequired();
            e.Property(w => w.WarehouseName).HasDefaultValue("");
            e.Property(w => w.OnHand).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
            e.Property(w => w.IsCommitted).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
            e.Property(w => w.OnOrder).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
            e.Property(w => w.AvailableToSell).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
            e.Property(w => w.IsBinManaged).HasDefaultValue(false);
            e.Property(w => w.LastUpdated).HasColumnType("timestamp with time zone");
            // Unique on (ItemCode, WhsCode) — one row per warehouse per item
            e.HasIndex(w => new { w.ItemCode, w.WhsCode }).IsUnique();
            e.HasIndex(w => w.WhsCode);
        });

        // ── BinInventory ──────────────────────────────────────────────────────
        mb.Entity<BinInventory>(e =>
        {
            e.ToTable("BinInventory");
            e.HasKey(b => b.Id);
            e.Property(b => b.ItemCode).IsRequired();
            e.Property(b => b.WhsCode).IsRequired();
            e.Property(b => b.BinCode).IsRequired();
            e.Property(b => b.BinOnHand).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
            e.Property(b => b.LastUpdated).HasColumnType("timestamp with time zone");
            // Unique on (ItemCode, WhsCode, BinAbsEntry) — one row per bin per item per warehouse
            e.HasIndex(b => new { b.ItemCode, b.WhsCode, b.BinAbsEntry }).IsUnique();
            e.HasIndex(b => b.WhsCode);
            e.HasIndex(b => b.ItemCode);
        });

        // ── Deliveries ────────────────────────────────────────────────────────
        mb.Entity<CachedDelivery>(e =>
        {
            e.ToTable("Deliveries");
            e.HasKey(d => d.DocEntry);
            e.Property(d => d.DocEntry).ValueGeneratedNever();
            e.Property(d => d.DocTotal).HasColumnType("numeric(18,2)");
            e.Property(d => d.SlpName).HasDefaultValue("");
            e.Property(d => d.Comments).HasDefaultValue("");
            e.Property(d => d.DocStatusDisplay).HasDefaultValue("");
            e.Property(d => d.U_ReplitId).HasDefaultValue((string?)null);
            e.Ignore(d => d.Lines);
            e.HasIndex(d => d.DocDate);
            e.HasIndex(d => d.CardCode);
            e.HasIndex(d => new { d.DocStatus, d.Canceled });
        });

        // ── DeliveryLines ─────────────────────────────────────────────────────
        mb.Entity<CachedDeliveryLine>(e =>
        {
            e.ToTable("DeliveryLines");
            e.HasKey(l => new { l.DocEntry, l.LineNum });
            e.Property(l => l.Quantity).HasColumnType("numeric(18,4)");
            e.Property(l => l.OpenQty).HasColumnType("numeric(18,4)");
            e.Property(l => l.Price).HasColumnType("numeric(18,2)");
            e.Property(l => l.LineTotal).HasColumnType("numeric(18,2)");
            e.Property(l => l.Dscription).HasDefaultValue("");
            e.Property(l => l.Currency).HasDefaultValue("");
            e.HasIndex(l => l.DocEntry);
            e.HasIndex(l => l.ItemCode);
        });

        // ── PickLists ─────────────────────────────────────────────────────────
        mb.Entity<CachedPickList>(e =>
        {
            e.ToTable("PickLists");
            e.HasKey(p => p.AbsEntry);
            e.Property(p => p.AbsEntry).ValueGeneratedNever();
            e.Property(p => p.Name).HasDefaultValue("");
            e.Property(p => p.OwnerName).HasDefaultValue("");
            e.Property(p => p.Status).IsRequired();
            e.Property(p => p.Canceled).HasDefaultValue("N");
            e.Property(p => p.Remarks).HasDefaultValue("");
            e.Property(p => p.U_ReplitId).HasDefaultValue((string?)null);
            e.HasIndex(p => p.Status);
            e.HasIndex(p => p.OwnerCode);
            e.HasIndex(p => p.UpdateDate);
        });

        // ── PickListLines ─────────────────────────────────────────────────────
        mb.Entity<CachedPickListLine>(e =>
        {
            e.ToTable("PickListLines");
            e.HasKey(l => new { l.AbsEntry, l.PickEntry });
            e.Property(l => l.RelQtty).HasColumnType("numeric(18,4)");
            e.Property(l => l.PickQtty).HasColumnType("numeric(18,4)");
            e.Property(l => l.PrevReleas).HasColumnType("numeric(18,4)");
            e.Property(l => l.PickStatus).HasDefaultValue("");
            e.Property(l => l.ItemCode).HasDefaultValue("");
            e.Property(l => l.Dscription).HasDefaultValue("");
            e.Property(l => l.WhsCode).HasDefaultValue("");
            e.HasIndex(l => l.AbsEntry);
            e.HasIndex(l => l.OrderEntry);
        });

        // ── PickListBinAllocations ────────────────────────────────────────────
        mb.Entity<CachedPickListBinAllocation>(e =>
        {
            e.ToTable("PickListBinAllocations");
            e.HasKey(b => new { b.AbsEntry, b.Pkl2LinNum });
            e.Property(b => b.PickQtty).HasColumnType("numeric(18,4)");
            e.Property(b => b.RelQtty).HasColumnType("numeric(18,4)");
            e.Property(b => b.ItemCode).HasDefaultValue("");
            e.Property(b => b.WhsCode).HasDefaultValue("");
            e.Property(b => b.BinCode).HasDefaultValue("");
            e.HasIndex(b => b.AbsEntry);
            e.HasIndex(b => b.BinAbsEntry);
        });

        // ── Offline Fulfillment V2 — ADDITIVE ONLY ────────────────────────────
        // V1 PendingOrders / PendingOrderLines tables are untouched.

        mb.Entity<OfflineFulfillmentOrder>(e =>
        {
            e.ToTable("OfflineFulfillmentOrders");
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.OfflineId).IsUnique();
            e.HasIndex(o => o.State);
            e.HasIndex(o => o.CardCode);
            e.Property(o => o.OfflineId).IsRequired();
            e.Property(o => o.WorkflowVersion).IsRequired().HasDefaultValue(FulfillmentWorkflowVersion.OfflineFulfillmentV2);
            e.Property(o => o.CardCode).IsRequired();
            e.Property(o => o.DocCurrency).HasDefaultValue("TZS");
            e.Property(o => o.DeliveryLocation).HasDefaultValue("");
            e.Property(o => o.State).IsRequired().HasDefaultValue(OfflineFulfillmentState.Draft);
            e.Property(o => o.RecoveryStage).HasDefaultValue(Models.Offline.RecoveryStage.None);
            e.Property(o => o.CreatedAtUtc).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()");
            e.Property(o => o.UpdatedAtUtc).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()");
        });

        mb.Entity<OfflineFulfillmentOrderLine>(e =>
        {
            e.ToTable("OfflineFulfillmentOrderLines");
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.OfflineFulfillmentOrderId, l.LineSeq }).IsUnique();
            e.Property(l => l.RequestedLineId).IsRequired();
            e.Property(l => l.ItemCode).IsRequired();
            e.Property(l => l.RequestedQty).HasColumnType("numeric(18,4)");
            e.Property(l => l.UnitPrice).HasColumnType("numeric(18,4)");
            e.HasOne(l => l.Order)
             .WithMany(o => o.Lines)
             .HasForeignKey(l => l.OfflineFulfillmentOrderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<OfflineFulfillmentPick>(e =>
        {
            e.ToTable("OfflineFulfillmentPicks");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.OfflineFulfillmentOrderId);
            e.HasIndex(p => p.RequestedLineId);
            e.HasIndex(p => p.OfflineConfirmId);
            e.Property(p => p.ItemCode).IsRequired();
            e.Property(p => p.WhsCode).IsRequired();
            e.Property(p => p.RequestedQty).HasColumnType("numeric(18,4)");
            e.Property(p => p.PickedQty).HasColumnType("numeric(18,4)");
            e.Property(p => p.PickerReference).HasDefaultValue("");
            e.Property(p => p.IsConfirmed).HasDefaultValue(false);
            e.Property(p => p.PickedAtUtc).HasColumnType("timestamp with time zone");
            e.Property(p => p.ConfirmedAtUtc).HasColumnType("timestamp with time zone");
            e.HasOne(p => p.Order)
             .WithMany(o => o.Picks)
             .HasForeignKey(p => p.OfflineFulfillmentOrderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<OfflineReservation>(e =>
        {
            e.ToTable("OfflineReservations");
            e.HasKey(r => r.Id);
            // Unique per (order, item, whs, bin) — prevents duplicate reservation rows.
            e.HasIndex(r => new { r.OfflineFulfillmentOrderId, r.ItemCode, r.WhsCode, r.BinAbsEntry }).IsUnique();
            // Supports "sum all active reservations for this item+whs+bin" query.
            e.HasIndex(r => new { r.ItemCode, r.WhsCode, r.BinAbsEntry, r.State });
            e.Property(r => r.ItemCode).IsRequired();
            e.Property(r => r.WhsCode).IsRequired();
            e.Property(r => r.ReservedQty).HasColumnType("numeric(18,4)");
            e.Property(r => r.State).IsRequired().HasDefaultValue(OfflineReservationState.Reserved);
            e.Property(r => r.CreatedAtUtc).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()");
            e.Property(r => r.UpdatedAtUtc).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()");
            e.HasOne(r => r.Order)
             .WithMany(o => o.Reservations)
             .HasForeignKey(r => r.OfflineFulfillmentOrderId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
