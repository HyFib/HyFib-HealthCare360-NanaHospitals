using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A message to a patient is written in the language their record prefers when HC360 writes it, so the hospital's Hindi
/// or Tamil template is used; the record's preference is free text, as the registration desk typed it.
/// </summary>
public sealed class PatientRecipientTests
{
    private static readonly Guid PatientId = Guid.CreateVersion7();

    [Theory]
    [InlineData("Tamil", "ta")]
    [InlineData("தமிழ்", "ta")]
    [InlineData("ta-IN", "ta")]
    [InlineData("Hindi", "hi")]
    [InlineData("हिन्दी", "hi")]
    [InlineData("English", "en")]
    public void A_language_hc360_writes_is_used(string preference, string language) =>
        NotificationRecipient.Patient("+919876543210", null, PatientId, "Selvi", preference).Language.ShouldBe(language);

    [Theory]
    [InlineData("Marathi")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_preference_gets_english(string? preference) =>
        NotificationRecipient.Patient("+919876543210", null, PatientId, "Selvi", preference).Language.ShouldBe("en");

    [Fact]
    public void The_recipient_names_the_patient_and_their_contact_details() =>
        NotificationRecipient.Patient("+919876543210", "selvi@example.org", PatientId, "Selvi", "Tamil")
            .ShouldBe(new NotificationRecipient("+919876543210", "selvi@example.org", PatientId, "Selvi", "ta"));
}
