using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

namespace HyFib.HealthCare360.Modules.SupportServices.Seed;

/// <summary>Reference data seeded for a new installation (hospitals edit prices, sets and menus afterwards).</summary>
internal static class SeedCatalog
{
    public sealed record ServiceSeed(string Code, string Name, decimal Price);

    public sealed record DietSeed(string Code, string Name, DietCategory Category, string? ChargeCode, MealSlot Slots, string Description);

    public sealed record SetSeed(string Code, string Name, string Category, string? Department, SterilizationMethod Method, int ShelfLifeDays, (string Name, int Quantity)[] Items);

    /// <param name="HsnSac">Goods only; food and drink made in the canteen is billed under <see cref="MenuItem.DefaultSac"/>.</param>
    public sealed record MenuSeed(
        string Code, string Name, MenuCategory Category, decimal Price, bool IsVeg, decimal GstRate = MenuItem.StandardGstRate, bool TrackStock = false,
        string? HsnSac = null);

    /// <summary>Diet charges posted per admission per day when a facility enables diet billing.</summary>
    public static readonly ServiceSeed[] DietServices =
    [
        new("DIET-STD", "Diet charges — standard (per day)", 300m),
        new("DIET-THER", "Diet charges — therapeutic (per day)", 450m),
        new("DIET-ENT", "Enteral / tube feed (per day)", 400m),
    ];

    /// <summary>Ambulance fares: the base fare by vehicle kind covers the first 10 km; each kilometre beyond is AMB-KM.</summary>
    public static readonly ServiceSeed[] AmbulanceServices =
    [
        new("AMB-BLS", "Ambulance — basic life support (first 10 km)", 1000m),
        new("AMB-ALS", "Ambulance — advanced life support (first 10 km)", 2500m),
        new("AMB-PTS", "Patient transport vehicle (first 10 km)", 800m),
        new("AMB-HEARSE", "Hearse (first 10 km)", 1200m),
        new("AMB-KM", "Ambulance — each km beyond the first 10", 25m),
    ];

    /// <summary>A starting linen list with the washes each piece usually lasts (hospitals adjust it to their linen).</summary>
    public static readonly (string Code, string Name, int LifeWashes)[] Linen =
    [
        ("BEDSHEET", "Bed sheet", 150),
        ("DRAWSHEET", "Draw sheet", 150),
        ("PILLOWCOVER", "Pillow cover", 150),
        ("GOWN", "Patient gown", 100),
        ("OTGOWN", "OT gown", 75),
        ("TOWEL", "Towel", 100),
        ("BLANKET", "Blanket", 50),
    ];

    public static readonly DietSeed[] Diets =
    [
        new("NORMAL", "Normal diet", DietCategory.Regular, "DIET-STD", MealSlots.Standard, "Regular hospital diet."),
        new("SOFT", "Soft diet", DietCategory.ModifiedConsistency, "DIET-STD", MealSlots.Standard, "Soft, easily chewed foods (post-operative, elderly, dental problems)."),
        new("SEMI-SOLID", "Semi-solid diet", DietCategory.ModifiedConsistency, "DIET-STD", MealSlots.All, "Mashed and pureed foods."),
        new("LIQUID", "Full liquid diet", DietCategory.ModifiedConsistency, "DIET-THER", MealSlots.All, "Milk, soups, juices and kanji every 2–3 hours."),
        new("CLEAR-LIQUID", "Clear liquid diet", DietCategory.ModifiedConsistency, "DIET-THER", MealSlots.All, "Clear fluids only (post-operative day 1, before procedures)."),
        new("DIABETIC", "Diabetic diet", DietCategory.Therapeutic, "DIET-THER", MealSlots.All, "Controlled carbohydrate, small frequent meals, no added sugar."),
        new("RENAL", "Renal diet", DietCategory.Therapeutic, "DIET-THER", MealSlots.Standard, "Protein, potassium, phosphorus and fluid restricted as advised."),
        new("CARDIAC", "Cardiac / low-salt diet", DietCategory.Therapeutic, "DIET-THER", MealSlots.Standard, "Low salt, low saturated fat."),
        new("HIGH-PROTEIN", "High-protein diet", DietCategory.Therapeutic, "DIET-THER", MealSlots.All, "Burns, wounds, malnutrition and recovery."),
        new("LOW-FAT", "Low-fat diet", DietCategory.Therapeutic, "DIET-THER", MealSlots.Standard, "Pancreatitis, gall bladder and liver disease."),
        new("BLAND", "Bland diet", DietCategory.Therapeutic, "DIET-STD", MealSlots.Standard, "Non-spicy, non-irritant (gastritis, peptic ulcer)."),
        new("RT-FEED", "Ryle's tube feed", DietCategory.Enteral, "DIET-ENT", MealSlots.All, "Blenderised or formula feed through a nasogastric tube."),
        new("NPO", "Nil by mouth (NPO)", DietCategory.NilByMouth, null, MealSlots.Standard, "Nothing by mouth (before surgery or procedures)."),
    ];

    public static readonly SetSeed[] InstrumentSets =
    [
        new("MAJOR-TRAY", "Major surgical tray", "Surgical", "GS", SterilizationMethod.Steam, 7,
        [
            ("Scalpel handle No. 3", 2), ("Scalpel handle No. 4", 1), ("Mayo scissors", 1), ("Metzenbaum scissors", 1), ("Suture scissors", 1),
            ("Toothed tissue forceps", 2), ("Plain dissecting forceps", 2), ("Artery forceps, curved", 6), ("Artery forceps, straight", 6),
            ("Allis tissue forceps", 4), ("Babcock forceps", 2), ("Needle holder", 2), ("Langenbeck retractor", 2), ("Deaver retractor", 1),
            ("Towel clip", 6), ("Sponge-holding forceps", 2), ("Kidney tray", 1), ("Gallipot", 2),
        ]),
        new("MINOR-TRAY", "Minor surgical tray", "Surgical", "GS", SterilizationMethod.Steam, 7,
        [
            ("Scalpel handle No. 3", 1), ("Suture scissors", 1), ("Toothed tissue forceps", 1), ("Plain dissecting forceps", 1), ("Artery forceps", 4),
            ("Needle holder", 1), ("Towel clip", 2), ("Sponge-holding forceps", 1), ("Gallipot", 1),
        ]),
        new("LSCS-SET", "LSCS (caesarean section) set", "Obstetric", "OBG", SterilizationMethod.Steam, 7,
        [
            ("Scalpel handle No. 4", 2), ("Mayo scissors", 1), ("Suture scissors", 1), ("Toothed tissue forceps", 2), ("Green-Armytage forceps", 4),
            ("Artery forceps", 8), ("Allis tissue forceps", 4), ("Doyen's retractor", 1), ("Needle holder", 2), ("Sponge-holding forceps", 2),
            ("Towel clip", 4), ("Kidney tray", 1), ("Cord clamp", 2),
        ]),
        new("DELIVERY-SET", "Normal delivery set", "Obstetric", "OBG", SterilizationMethod.Steam, 7,
        [
            ("Episiotomy scissors", 1), ("Cord-cutting scissors", 1), ("Artery forceps", 2), ("Sponge-holding forceps", 1), ("Needle holder", 1),
            ("Toothed tissue forceps", 1), ("Kidney tray", 1), ("Gallipot", 1),
        ]),
        new("DRESSING-SET", "Dressing set", "Ward", null, SterilizationMethod.Steam, 7,
        [
            ("Plain dissecting forceps", 1), ("Artery forceps", 1), ("Scissors", 1), ("Gallipot", 1), ("Kidney tray", 1),
        ]),
        new("SUTURE-SET", "Suturing set", "Emergency", "EMER", SterilizationMethod.Steam, 7,
        [
            ("Needle holder", 1), ("Toothed tissue forceps", 1), ("Suture scissors", 1), ("Artery forceps", 2), ("Gallipot", 1),
        ]),
        new("CATH-SET", "Urinary catheterisation set", "Ward", null, SterilizationMethod.Steam, 7,
        [
            ("Kidney tray", 1), ("Gallipot", 1), ("Sponge-holding forceps", 1), ("Plain dissecting forceps", 1),
        ]),
        new("LAPARO-SET", "Laparoscopy instrument set", "Surgical", "GS", SterilizationMethod.Plasma, 180,
        [
            ("Trocar 10 mm", 2), ("Trocar 5 mm", 2), ("Veress needle", 1), ("Maryland dissector", 1), ("Laparoscopic scissors", 1),
            ("Atraumatic grasper", 2), ("Clip applicator", 1), ("Suction-irrigation cannula", 1),
        ]),
    ];

    public static readonly MenuSeed[] Menu =
    [
        new("IDLI", "Idli (2 pcs) with sambar", MenuCategory.Breakfast, 30m, true),
        new("PLAIN-DOSA", "Plain dosa", MenuCategory.Breakfast, 40m, true),
        new("MASALA-DOSA", "Masala dosa", MenuCategory.Breakfast, 50m, true),
        new("VADA", "Medu vada (2 pcs)", MenuCategory.Breakfast, 30m, true),
        new("UPMA", "Upma", MenuCategory.Breakfast, 30m, true),
        new("POHA", "Poha", MenuCategory.Breakfast, 30m, true),
        new("POORI", "Poori bhaji", MenuCategory.Breakfast, 45m, true),
        new("SI-MEALS", "South Indian meals", MenuCategory.Meals, 80m, true),
        new("NI-THALI", "North Indian thali", MenuCategory.Meals, 90m, true),
        new("CURD-RICE", "Curd rice", MenuCategory.Meals, 40m, true),
        new("CHAPATI", "Chapati (2) with sabzi", MenuCategory.Meals, 50m, true),
        new("VEG-BIRYANI", "Veg biryani", MenuCategory.Meals, 90m, true),
        new("EGG-CURRY", "Egg curry with rice", MenuCategory.Meals, 90m, false),
        new("CHICKEN-BIRYANI", "Chicken biryani", MenuCategory.Meals, 140m, false),
        new("SAMOSA", "Samosa", MenuCategory.Snacks, 15m, true),
        new("BONDA", "Aloo bonda", MenuCategory.Snacks, 15m, true),
        new("VEG-PUFF", "Veg puff", MenuCategory.Snacks, 20m, true),
        new("SANDWICH", "Veg sandwich", MenuCategory.Snacks, 40m, true),
        new("TEA", "Tea", MenuCategory.Beverages, 10m, true),
        new("COFFEE", "Filter coffee", MenuCategory.Beverages, 15m, true),
        new("MILK", "Hot milk", MenuCategory.Beverages, 15m, true),
        new("LIME-JUICE", "Fresh lime juice", MenuCategory.Juices, 25m, true),
        new("BUTTERMILK", "Buttermilk", MenuCategory.Juices, 15m, true),
        new("TENDER-COCONUT", "Tender coconut", MenuCategory.Juices, 50m, true, GstRate: 0m, HsnSac: "080119"),
        new("KESARI", "Rava kesari", MenuCategory.Sweets, 25m, true),
        new("GULAB-JAMUN", "Gulab jamun (2 pcs)", MenuCategory.Sweets, 30m, true),
        new("WATER-1L", "Packaged drinking water 1 L", MenuCategory.Packaged, 20m, true, GstRate: 18m, TrackStock: true, HsnSac: "220190"),
        new("BISCUITS", "Biscuit pack", MenuCategory.Packaged, 10m, true, GstRate: 18m, TrackStock: true, HsnSac: "190531"),
    ];
}
