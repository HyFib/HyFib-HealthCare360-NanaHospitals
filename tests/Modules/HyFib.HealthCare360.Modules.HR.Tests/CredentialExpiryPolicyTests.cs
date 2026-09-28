using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class CredentialExpiryPolicyTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    [Theory]
    [InlineData(null, CredentialExpiryStage.None)]
    [InlineData(45, CredentialExpiryStage.None)]
    [InlineData(31, CredentialExpiryStage.None)]
    [InlineData(30, CredentialExpiryStage.DueIn30Days)]
    [InlineData(8, CredentialExpiryStage.DueIn30Days)]
    [InlineData(7, CredentialExpiryStage.DueIn7Days)]
    [InlineData(0, CredentialExpiryStage.DueIn7Days)]
    [InlineData(-1, CredentialExpiryStage.Expired)]
    public void Stage_depends_on_days_left(int? daysLeft, CredentialExpiryStage expected) =>
        CredentialExpiryPolicy.StageFor(daysLeft is { } d ? Today.AddDays(d) : null, Today).ShouldBe(expected);

    [Theory]
    [InlineData(CredentialExpiryStage.DueIn30Days, CredentialExpiryStage.None, true)]
    [InlineData(CredentialExpiryStage.DueIn30Days, CredentialExpiryStage.DueIn30Days, false)]
    [InlineData(CredentialExpiryStage.DueIn7Days, CredentialExpiryStage.DueIn30Days, true)]
    [InlineData(CredentialExpiryStage.Expired, CredentialExpiryStage.DueIn7Days, true)]
    [InlineData(CredentialExpiryStage.Expired, CredentialExpiryStage.Expired, false)]
    [InlineData(CredentialExpiryStage.DueIn7Days, CredentialExpiryStage.None, true)]
    [InlineData(CredentialExpiryStage.None, CredentialExpiryStage.None, false)]
    public void Each_stage_alerts_once(CredentialExpiryStage current, CredentialExpiryStage lastAlerted, bool expected) =>
        CredentialExpiryPolicy.ShouldAlert(current, lastAlerted).ShouldBe(expected);

    [Fact]
    public void Renewing_a_credential_restarts_its_reminders()
    {
        var credential = new Credential();
        credential.SetValidity(new DateOnly(2024, 10, 1), Today.AddDays(5));
        credential.MarkAlerted(CredentialExpiryStage.DueIn7Days, Today);

        credential.SetValidity(new DateOnly(2024, 10, 1), Today.AddDays(5));
        credential.LastAlertStage.ShouldBe(CredentialExpiryStage.DueIn7Days);

        credential.SetValidity(Today, Today.AddYears(2));
        credential.LastAlertStage.ShouldBe(CredentialExpiryStage.None);
        credential.LastAlertedOn.ShouldBeNull();
    }
}
