using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Imaging;

/// <summary>Worklist routing for radiology services that have no procedure master yet.</summary>
public sealed class ModalityResolverTests
{
    [Theory]
    [InlineData("RAD-XR-CHEST", "X-ray chest PA view", Modality.XR)]
    [InlineData("RAD-CT-HEAD", "CT brain (plain)", Modality.CT)]
    [InlineData("RAD-CT-CHEST-HRCT", "HRCT chest", Modality.CT)]
    [InlineData("RAD-CECT-NECK", "Contrast CT neck", Modality.CT)]
    [InlineData("RAD-MRI-BRAIN", "MRI brain (plain)", Modality.MR)]
    [InlineData("RAD-MRCP", "Magnetic resonance cholangiopancreatography", Modality.MR)]
    [InlineData("RAD-USG-ABD", "Ultrasound whole abdomen", Modality.US)]
    [InlineData("RAD-VENOUS-LL", "Venous Doppler both lower limbs", Modality.US)]
    [InlineData("RAD-MAMMO", "Mammography (bilateral)", Modality.MG)]
    [InlineData("RAD-HSG", "Hysterosalpingography", Modality.RF)]
    [InlineData("RAD-BA-SWALLOW", "Barium swallow", Modality.RF)]
    [InlineData("RAD-DX-KNEE", "Digital radiograph knee AP and lateral", Modality.DX)]
    public void The_modality_is_read_from_the_service_code_and_name(string code, string name, Modality expected) =>
        ModalityResolver.Resolve(code, name).ShouldBe(expected);

    [Fact]
    public void Cross_sectional_modalities_win_over_words_shared_with_plain_films() =>
        ModalityResolver.Resolve("RAD-MRI-SPINE-DX", "MRI whole spine screening").ShouldBe(Modality.MR);

    [Fact]
    public void Words_are_matched_whole_so_names_containing_modality_letters_are_not_misrouted()
    {
        // "fracture" contains "CT" and "sinuses" contains "US", but only whole words count.
        ModalityResolver.Resolve("RAD-XR-WRIST", "X-ray wrist, suspected fracture").ShouldBe(Modality.XR);
        ModalityResolver.Resolve("RAD-XR-PNS", "X-ray paranasal sinuses (Water's view)").ShouldBe(Modality.XR);
    }

    [Theory]
    [InlineData("RAD-CT-HEAD", "Head")]
    [InlineData("RAD-XR-SPINE-LS", "Spine LS")]
    [InlineData("RAD-MRI-BRAIN", "Brain")]
    [InlineData("RAD-USG-ABD", "Abd")]
    [InlineData("rad-xr-chest", "Chest")]
    public void The_body_part_is_taken_from_the_code_after_the_modality(string code, string expected) =>
        ModalityResolver.BodyPartFromCode(code).ShouldBe(expected);

    [Fact]
    public void A_code_without_a_body_part_has_none() =>
        ModalityResolver.BodyPartFromCode("RAD-MAMMO").ShouldBeNull();
}
