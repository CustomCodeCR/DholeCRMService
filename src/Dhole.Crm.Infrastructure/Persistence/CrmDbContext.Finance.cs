using Dhole.Crm.Domain;
using Microsoft.EntityFrameworkCore;
namespace Dhole.Crm.Infrastructure.Persistence;
public sealed partial class CrmDbContext
{
    private static void ConfigureFinance(ModelBuilder b)
    {
        ConfigureRecord<CreditProfile>(b,"crm_credit_profiles");
        ConfigureRecord<CreditCoverage>(b,"crm_credit_coverages");
        ConfigureRecord<CreditContract>(b,"crm_credit_contracts");
        ConfigureRecord<CreditTermsHistory>(b,"crm_credit_terms_history");
        ConfigureRecord<CreditContractRenewal>(b,"crm_credit_contract_renewals");
        b.Entity<CreditProfile>(e=>{
            e.HasOne<Account>().WithMany().HasForeignKey(x=>x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.CreditLimit).HasPrecision(18,2);
            e.Property(x=>x.MonthlyFinancingPct).HasPrecision(7,4);
            e.Property(x=>x.CurrencyCode).HasMaxLength(12);
            e.Property(x=>x.PaymentConditions).HasMaxLength(4000);
            e.Property(x=>x.Status).HasMaxLength(24);
            e.HasIndex(x=>x.AccountId).IsUnique().HasFilter("status='Approved'");
            e.HasIndex(x=>x.AccountId).IsUnique().HasFilter("status='PendingApproval'");
            e.HasIndex(x=>new{x.Status,x.ReviewAt});
        });
        b.Entity<CreditCoverage>(e=>{
            e.HasOne<CreditProfile>().WithMany().HasForeignKey(x=>x.CreditProfileId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.ServiceCode).HasMaxLength(80);
            e.Property(x=>x.CoverageType).HasMaxLength(12);
            e.Property(x=>x.Description).HasMaxLength(2000);
            e.HasIndex(x=>new{x.CreditProfileId,x.ServiceCode}).IsUnique().HasFilter("is_active");
        });
        b.Entity<CreditContract>(e=>{
            e.HasOne<Account>().WithMany().HasForeignKey(x=>x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CreditProfile>().WithMany().HasForeignKey(x=>x.CreditProfileId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.ContractNumber).HasMaxLength(100);
            e.Property(x=>x.PaymentTerms).HasMaxLength(4000);
            e.Property(x=>x.Status).HasMaxLength(20);
            e.HasIndex(x=>x.ContractNumber).IsUnique().HasFilter("contract_number IS NOT NULL");
            e.HasIndex(x=>new{x.Status,x.RenewalDate});
        });
        b.Entity<CreditTermsHistory>(e=>{
            e.HasOne<CreditProfile>().WithMany().HasForeignKey(x=>x.CreditProfileId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.ApprovalReason).HasMaxLength(1000);
            e.HasIndex(x=>new{x.CreditProfileId,x.ChangedAt});
        });
        b.Entity<CreditContractRenewal>(e=>{
            e.HasOne<CreditContract>().WithMany().HasForeignKey(x=>x.ContractId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x=>x.Reason).HasMaxLength(1000);
            e.HasIndex(x=>new{x.ContractId,x.CreatedAt});
        });
    }
}
