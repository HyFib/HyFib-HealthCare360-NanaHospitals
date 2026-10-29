using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// The rate list a hospital displays (Clinical Establishments Rules 2012, r.9(i)) says what a self-paying patient is
/// charged, so each of its prices is worked out as a bill's charge is: the plan price in force or the base price, the
/// plan's concession on a service that allows one, a room's GST by its rent, and a doctor's own fee in place of the
/// consultation's price.
/// </summary>
public sealed class RateListTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static readonly RateListItem Registration = Item("REG-NEW", "Registration fee (new patient)", ServiceCategory.Registration, 100m);
    private static readonly RateListItem Consultation = Item("CONS-NEW", "Consultation — new visit", ServiceCategory.Consultation, 500m);
    private static readonly RateListItem FollowUp = Item("CONS-FU", "Consultation — follow-up", ServiceCategory.Consultation, 300m);
    private static readonly RateListItem Video = Item("CONS-TELE", "Tele-consultation", ServiceCategory.Consultation, 400m);
    private static readonly RateListItem Emergency = Item("CONS-ER", "Emergency consultation", ServiceCategory.Consultation, 800m);
    private static readonly RateListItem GeneralBed = Item("BED-GEN", "Bed charges — general ward (per day)", ServiceCategory.BedCharge, 1_500m);
    private static readonly RateListItem DeluxeBed = Item("BED-DLX", "Bed charges — deluxe suite (per day)", ServiceCategory.BedCharge, 6_500m);
    private static readonly RateListItem IcuBed = Item("BED-ICU", "Bed charges — ICU (per day)", ServiceCategory.BedCharge, 8_000m);
    private static readonly RateListItem Nursing = Item("NURS-DAY", "Nursing charges (per day)", ServiceCategory.Nursing, 600m);
    private static readonly RateListItem Visit = Item("VISIT-DR", "Consultant visit (in-patient)", ServiceCategory.DoctorVisit, 700m);
    private static readonly RateListItem IcuVisit = Item("VISIT-DR-ICU", "Consultant visit (ICU)", ServiceCategory.DoctorVisit, 1_000m);
    private static readonly RateListItem Cbc = Item("LAB-CBC", "Complete blood count", ServiceCategory.Laboratory, 350m, localName: "முழு இரத்த எண்ணிக்கை");
    private static readonly RateListItem Dressing = Item("PROC-DRESS", "Dressing — minor", ServiceCategory.Procedure, 250m);
    private static readonly RateListItem Certificate = Item("MISC-CERT", "Medical certificate", ServiceCategory.Miscellaneous, 300m, taxRate: 18m, discountAllowed: false);
    private static readonly RateListItem Medicines = Item("PHARM-MED", "Pharmacy — medicines (in-patient)", ServiceCategory.Consumable, 0m, priceEditable: true);
    private static readonly RateListItem PackageItem = Item("PKG-SERVICE", "Package", ServiceCategory.Package, 0m);

    private static readonly RateListItem[] Catalogue =
    [
        Registration, Consultation, FollowUp, Video, Emergency, GeneralBed, DeluxeBed, IcuBed, Nursing, Visit, IcuVisit, Cbc, Dressing, Certificate,
        Medicines, PackageItem,
    ];

    [Fact]
    public void Each_price_is_what_a_bills_charge_for_the_service_comes_to()
    {
        // A plan with a 10% concession and its own price for the blood count, in force today; an older one has lapsed.
        var plan = new RateListPlan(Guid.CreateVersion7(), "General", 10m, new Dictionary<Guid, decimal>());
        var rates = RateListBuilder.Build(Input() with
        {
            Plan = plan,
            PlanPrices = new Dictionary<Guid, IReadOnlyList<PlanPrice>>
            {
                [Cbc.Id] = [new(Guid.CreateVersion7(), 300m, Today.AddYears(-1), Today.AddDays(-1)), new(Guid.CreateVersion7(), 320m, Today, null)],
            },
        });

        var cbc = rates.Sections.Single(s => s.Category == ServiceCategory.Laboratory).Lines.Single();
        var charged = Charged(320m, Cbc, concession: 10m);
        (cbc.Rate, cbc.GstRate, cbc.Payable).ShouldBe((charged.GrossAmount - charged.DiscountAmount, charged.TaxRate, charged.NetAmount));
        cbc.Payable.ShouldBe(288m);

        // A service that allows no discount keeps its price, and its GST is on the whole of it.
        var certificate = rates.Sections.Single(s => s.Category == ServiceCategory.Miscellaneous).Lines.Single();
        var certified = Charged(300m, Certificate, concession: 10m);
        (certificate.Rate, certificate.GstRate, certificate.Payable).ShouldBe((certified.GrossAmount - certified.DiscountAmount, 18m, certified.NetAmount));
        certificate.Payable.ShouldBe(354m);
    }

    [Fact]
    public void A_doctors_own_fee_replaces_the_consultations_price_and_one_without_pays_it()
    {
        var rates = RateListBuilder.Build(Input() with
        {
            Doctors =
            [
                new("Dr. Vikram Rao", "Cardiology", 900m, 7, ByVideo: true),
                new("Dr. Arjun Mehta", "General Medicine", 0m, 0),
            ],
        });

        var (cardiologist, physician) = (rates.Doctors.Single(d => d.Name == "Dr. Vikram Rao"), rates.Doctors.Single(d => d.Name == "Dr. Arjun Mehta"));
        cardiologist.Fee.Payable.ShouldBe(900m);
        cardiologist.Video!.Payable.ShouldBe(900m);
        cardiologist.FreeFollowUpDays.ShouldBe(7);
        physician.Fee.Payable.ShouldBe(500m);
        physician.Video.ShouldBeNull();
        physician.FreeFollowUpDays.ShouldBe(0);

        // By department, then name.
        rates.Doctors.Select(d => d.Department).ShouldBe(["Cardiology", "General Medicine"]);
    }

    [Fact]
    public void A_follow_up_is_not_priced_because_it_is_free_within_the_doctors_days_and_a_new_consultation_after_them()
    {
        var rates = RateListBuilder.Build(Input());

        var listed = rates.Sections.SelectMany(s => s.Lines).Select(l => l.Code).ToList();
        listed.ShouldNotContain("CONS-FU");
        listed.ShouldNotContain("CONS-NEW");
        listed.ShouldNotContain("CONS-TELE");
        listed.ShouldContain("CONS-ER");
    }

    [Fact]
    public void A_room_is_listed_by_the_day_with_its_gst_nursing_visits_and_multiplier()
    {
        var plan = new RateListPlan(Guid.CreateVersion7(), "General", 0m, new Dictionary<Guid, decimal> { [DeluxeBed.Id] = 1.5m });
        var rates = RateListBuilder.Build(Input() with
        {
            Plan = plan,
            Rooms =
            [
                new RoomClass(GeneralBed.Id, false, Nursing.Id, Visit.Id, ["General Ward — Male", "General Ward — Female"]),
                new RoomClass(DeluxeBed.Id, false, Nursing.Id, Visit.Id, ["Deluxe Suites"]),
                new RoomClass(IcuBed.Id, true, Nursing.Id, IcuVisit.Id, ["Intensive Care Unit"]),
            ],
        });

        var (general, deluxe, icu) = (rates.Rooms[0], rates.Rooms[1], rates.Rooms[2]);
        general.Day.Payable.ShouldBe(1_500m);
        general.Wards.ShouldBe(["General Ward — Male", "General Ward — Female"]);

        // ₹6,500 a day is over ₹5,000: 5% GST on the room, as its day is charged.
        var day = Charged(6_500m, DeluxeBed, room: RoomCharge.Room);
        (deluxe.Day.GstRate, deluxe.Day.Payable).ShouldBe((day.TaxRate, day.NetAmount));
        deluxe.Day.Payable.ShouldBe(6_825m);

        // Nursing and visits in it cost its multiple; the bed itself does not.
        (deluxe.Multiplier, deluxe.Nursing!.Payable, deluxe.Visit!.Payable).ShouldBe((1.5m, 900m, 1_050m));

        // Intensive care is exempt however much it costs, and its visits are the ICU visit's.
        (icu.IntensiveCare, icu.Day.GstRate, icu.Day.Payable, icu.Visit!.Code).ShouldBe((true, 0m, 8_000m, "VISIT-DR-ICU"));

        // The nursing and visit services priced with the rooms are not listed again.
        rates.Sections.SelectMany(s => s.Lines).Select(l => l.Code).ShouldNotContain("NURS-DAY");
        rates.Sections.Select(s => s.Category).ShouldNotContain(ServiceCategory.BedCharge);
    }

    [Fact]
    public void Without_wards_the_nursing_and_visit_services_are_listed_with_the_rest()
    {
        var codes = RateListBuilder.Build(Input()).Sections.SelectMany(s => s.Lines).Select(l => l.Code).ToList();

        codes.ShouldContain("NURS-DAY");
        codes.ShouldContain("VISIT-DR");
    }

    [Fact]
    public void Registration_is_listed_only_where_the_hospital_charges_it()
    {
        RateListBuilder.Build(Input()).Registration!.Payable.ShouldBe(100m);
        RateListBuilder.Build(Input() with { RegistrationFee = false }).Registration.ShouldBeNull();
    }

    [Fact]
    public void A_package_is_listed_at_its_own_price_with_the_plans_concession()
    {
        var plan = new RateListPlan(Guid.CreateVersion7(), "General", 5m, new Dictionary<Guid, decimal>());
        var rates = RateListBuilder.Build(Input() with
        {
            Plan = plan,
            Packages = [new RateListPackage("PKG-NVD", "Normal delivery — 2 days", PackageItem.Id, 30_000m, 2, PackageUse.InPatient)],
        });

        var package = rates.Packages.Single();
        (package.Price.Code, package.Price.Name, package.Price.Payable, package.LengthOfStayDays).ShouldBe(("PKG-NVD", "Normal delivery — 2 days", 28_500m, 2));
        rates.Sections.Select(s => s.Category).ShouldNotContain(ServiceCategory.Package);
    }

    [Fact]
    public void A_service_priced_at_the_counter_is_marked_and_a_local_name_is_kept()
    {
        var rates = RateListBuilder.Build(Input());

        var medicines = rates.Sections.Single(s => s.Category == ServiceCategory.Consumable).Lines.Single();
        (medicines.MayVary, medicines.Payable).ShouldBe((true, 0m));
        rates.Sections.Single(s => s.Category == ServiceCategory.Laboratory).Lines.Single().LocalName.ShouldBe("முழு இரத்த எண்ணிக்கை");
    }

    [Fact]
    public void Sections_follow_the_category_order_and_services_their_names()
    {
        var rates = RateListBuilder.Build(Input() with { Items = [.. Catalogue, Item("LAB-ALB", "Albumin", ServiceCategory.Laboratory, 200m)] });

        rates.Sections.Select(s => s.Category).ShouldBe(
        [
            ServiceCategory.Consultation, ServiceCategory.Laboratory, ServiceCategory.Procedure, ServiceCategory.Nursing, ServiceCategory.DoctorVisit,
            ServiceCategory.Consumable, ServiceCategory.Miscellaneous,
        ]);
        rates.Sections.Single(s => s.Category == ServiceCategory.Laboratory).Lines.Select(l => l.Name).ShouldBe(["Albumin", "Complete blood count"]);
    }

    [Theory]
    [InlineData("Tamil Nadu", null, PatientLanguages.Tamil)]
    [InlineData(" tamil nadu ", null, PatientLanguages.Tamil)]
    [InlineData("Puducherry", null, PatientLanguages.Tamil)]
    [InlineData("Uttar Pradesh", null, PatientLanguages.Hindi)]
    [InlineData("Delhi", null, PatientLanguages.Hindi)]
    [InlineData("Maharashtra", null, null)]
    [InlineData("Kerala", "33ABCDE1234F1Z5", null)] // the address decides
    [InlineData(null, "33ABCDE1234F1Z5", PatientLanguages.Tamil)] // no state: the GSTIN's
    [InlineData("", "09ABCDE1234F1Z5", PatientLanguages.Hindi)]
    [InlineData(null, null, null)]
    public void The_local_language_is_the_states_when_hc360_writes_it(string? state, string? gstin, string? language) =>
        RateListLanguage.For(state, gstin).ShouldBe(language);

    private static RateListInput Input() => new() { AsOn = Today, Items = Catalogue };

    private static RateListItem Item(
        string code, string name, ServiceCategory category, decimal price, decimal taxRate = 0m, bool discountAllowed = true, bool priceEditable = false,
        string? localName = null) =>
        new(Guid.CreateVersion7(), code, name, localName, category, price, taxRate, discountAllowed, priceEditable);

    // The charge a bill would carry for one of the service at this price.
    private static Charge Charged(decimal unitPrice, RateListItem item, decimal concession = 0m, RoomCharge? room = null) =>
        TestData.Charge(unitPrice, category: item.Category, taxRate: item.TaxRate, discountAllowed: item.IsDiscountAllowed, tariffDiscountPercent: concession, room: room);
}
