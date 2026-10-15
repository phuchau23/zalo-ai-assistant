using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

// CRM C3 (docs/CRM-PLAN.md): hóa đơn, thanh toán, gói của khách, ví, voucher, ca thu ngân, doanh thu, tài khoản ngân hàng.

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<InvoiceStatus>>();
        builder.Property(i => i.DiscountReason).HasMaxLength(500);
        builder.Property(i => i.VoidReason).HasMaxLength(500);
        builder.Ignore(i => i.Due);
        builder.HasIndex(i => new { i.TenantId, i.Number }).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.BranchId, i.IssuedAt });
        builder.HasIndex(i => new { i.TenantId, i.ContactId });
        builder.HasIndex(i => new { i.TenantId, i.AppointmentId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(i => i.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Contact>().WithMany().HasForeignKey(i => i.ContactId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Appointment>().WithMany().HasForeignKey(i => i.AppointmentId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Voucher>().WithMany().HasForeignKey(i => i.VoucherId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<CashShift>().WithMany().HasForeignKey(i => i.CashShiftId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("invoice_lines");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<InvoiceLineKind>>();
        builder.Property(l => l.Description).HasMaxLength(300);
        builder.HasIndex(l => new { l.TenantId, l.InvoiceId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(l => l.CatalogItemId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(l => l.PerformerStaffId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<CustomerPackage>().WithMany().HasForeignKey(l => l.CustomerPackageId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Method).HasMaxLength(20).HasConversion<LowercaseEnumConverter<PaymentMethod>>();
        builder.Property(p => p.Reference).HasMaxLength(100);
        builder.HasIndex(p => new { p.TenantId, p.InvoiceId });
        builder.HasIndex(p => new { p.TenantId, p.ReceivedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CashShift>().WithMany().HasForeignKey(p => p.CashShiftId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class CustomerPackageConfiguration : IEntityTypeConfiguration<CustomerPackage>
{
    public void Configure(EntityTypeBuilder<CustomerPackage> builder)
    {
        builder.ToTable("customer_packages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(200);
        builder.Property(p => p.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<CustomerPackageStatus>>();
        builder.Ignore(p => p.Remaining);
        builder.HasIndex(p => new { p.TenantId, p.ContactId, p.Status });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(p => p.ContactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogItem>().WithMany().HasForeignKey(p => p.CatalogItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PackageRedemptionConfiguration : IEntityTypeConfiguration<PackageRedemption>
{
    public void Configure(EntityTypeBuilder<PackageRedemption> builder)
    {
        builder.ToTable("package_redemptions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasIndex(r => new { r.TenantId, r.CustomerPackageId });
        builder.HasIndex(r => new { r.TenantId, r.AppointmentId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CustomerPackage>().WithMany().HasForeignKey(r => r.CustomerPackageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Appointment>().WithMany().HasForeignKey(r => r.AppointmentId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets");
        builder.HasKey(w => w.ContactId);
        builder.HasIndex(w => w.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(w => w.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithOne().HasForeignKey<Wallet>(w => w.ContactId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> builder)
    {
        builder.ToTable("wallet_transactions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<WalletTransactionKind>>();
        builder.Property(t => t.Note).HasMaxLength(300);
        builder.HasIndex(t => new { t.TenantId, t.ContactId, t.At });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(t => t.ContactId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VoucherConfiguration : IEntityTypeConfiguration<Voucher>
{
    public void Configure(EntityTypeBuilder<Voucher> builder)
    {
        builder.ToTable("vouchers");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Code).HasMaxLength(50);
        builder.Property(v => v.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<VoucherKind>>();
        builder.Property(v => v.Note).HasMaxLength(300);
        builder.HasIndex(v => new { v.TenantId, v.Code }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(v => v.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CashShiftConfiguration : IEntityTypeConfiguration<CashShift>
{
    public void Configure(EntityTypeBuilder<CashShift> builder)
    {
        builder.ToTable("cash_shifts");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Note).HasMaxLength(500);
        builder.HasIndex(s => new { s.TenantId, s.BranchId, s.OpenedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(s => s.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RevenueEntryConfiguration : IEntityTypeConfiguration<RevenueEntry>
{
    public void Configure(EntityTypeBuilder<RevenueEntry> builder)
    {
        builder.ToTable("revenue_entries");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<RevenueKind>>();
        builder.Property(r => r.Source).HasMaxLength(20);
        builder.HasIndex(r => new { r.TenantId, r.Date });
        builder.HasIndex(r => new { r.TenantId, r.BranchId, r.Date });
        builder.HasIndex(r => new { r.TenantId, r.InvoiceId });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(r => r.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TenantBankAccountConfiguration : IEntityTypeConfiguration<TenantBankAccount>
{
    public void Configure(EntityTypeBuilder<TenantBankAccount> builder)
    {
        builder.ToTable("tenant_bank_accounts");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();
        builder.Property(b => b.BankBin).HasMaxLength(10);
        builder.Property(b => b.BankName).HasMaxLength(100);
        builder.Property(b => b.AccountName).HasMaxLength(100);
        builder.HasIndex(b => b.TenantId);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Branch>().WithMany().HasForeignKey(b => b.BranchId).OnDelete(DeleteBehavior.SetNull);
    }
}
