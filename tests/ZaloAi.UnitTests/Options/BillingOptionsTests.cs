using System.ComponentModel.DataAnnotations;
using Shouldly;
using ZaloAi.Core.Options;

namespace ZaloAi.UnitTests.Options;

public sealed class BillingOptionsTests
{
    [Fact]
    public void Trial_plan_defaults_to_100_conversations_and_tenant_override_wins()
    {
        var billing = new BillingOptions();
        billing.QuotaFor("trial", null).ShouldBe(100);
        billing.QuotaFor("trial", 300).ShouldBe(300);
        billing.QuotaFor("goi-la", null).ShouldBe(100); // gói lạ → như gói dùng thử
        billing.SuspendAfterExpiryDays.ShouldBe(3);
        billing.ExpiryWarningDays.ShouldBe(7);
    }

    [Fact]
    public void Validation_requires_trial_and_well_formed_plans()
    {
        Errors(new BillingOptions()).ShouldBeEmpty();

        var noTrial = new BillingOptions { Plans = new() { ["pro"] = new PlanDefinition { Name = "Pro", ConversationsPerMonth = 1000 } } };
        Errors(noTrial).ShouldNotBeEmpty();

        var bad = new BillingOptions();
        bad.Plans["Pro Max"] = new PlanDefinition { Name = "", ConversationsPerMonth = -1 };
        Errors(bad).ShouldNotBeEmpty();
    }

    private static List<ValidationResult> Errors(BillingOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
