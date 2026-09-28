using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Seed;

/// <summary>One catalogue row. Reorder levels are base units; pack MRP/rate are per purchase unit and only used for demo stock.</summary>
internal sealed record SeedItem(
    string Code,
    string Name,
    string? Generic,
    ItemType Type,
    string Category,
    string? Strength,
    string? Form,
    string PackSize,
    string BaseUnit,
    string PurchaseUnit,
    decimal Factor,
    string Hsn,
    decimal Gst,
    DrugSchedule Schedule,
    bool Controlled,
    bool ColdChain,
    VedClass Ved,
    decimal ReorderLevel,
    decimal ReorderQuantity,
    decimal PackMrp,
    decimal PackRate);

/// <summary>
/// Reference catalogue of common Indian hospital items (generic names; brands are added by each hospital).
/// Schedules follow the Drugs &amp; Cosmetics Rules (H1 per the 2013 notification; tramadol and alprazolam are
/// also NDPS psychotropics). GST rates follow the September 2025 rationalisation (most medicines, medical
/// devices, gauze, gloves and implants at 5%); the hospital's tax adviser should confirm them before go-live.
/// </summary>
internal static class ItemCatalogSeed
{
    public static IReadOnlyList<(string Code, string Name, ItemType Type)> Categories { get; } =
    [
        ("TAB", "Tablets & Capsules", ItemType.Drug),
        ("INJ", "Injections & Vaccines", ItemType.Drug),
        ("IVF", "IV Fluids", ItemType.Drug),
        ("SYR", "Syrups & Oral Liquids", ItemType.Drug),
        ("INH", "Inhalers & Respules", ItemType.Drug),
        ("TOP", "Topicals & Antiseptics", ItemType.Drug),
        ("CONS", "Medical Consumables", ItemType.Consumable),
        ("SURG", "Surgical Items", ItemType.Surgical),
        ("SUT", "Sutures", ItemType.Surgical),
        ("IMPL", "Implants", ItemType.Implant),
        ("LIN", "Linen", ItemType.Linen),
        ("GEN", "General Stores", ItemType.General),
        ("SPR", "Equipment Spares", ItemType.Spare),
    ];

    private const ItemType Drug = ItemType.Drug;
    private const DrugSchedule Otc = DrugSchedule.Otc;
    private const DrugSchedule H = DrugSchedule.H;
    private const DrugSchedule H1 = DrugSchedule.H1;
    private const VedClass V = VedClass.Vital;
    private const VedClass E = VedClass.Essential;
    private const VedClass D = VedClass.Desirable;

    public static IReadOnlyList<SeedItem> Items { get; } =
    [
        // ---- tablets and capsules (sold loose; stock in tablets)
        new("DRG-PCM500", "Paracetamol 500 mg Tablet", "Paracetamol", Drug, "TAB", "500 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, Otc, false, false, E, 500, 2000, 20.00m, 11.50m),
        new("DRG-PCM650", "Paracetamol 650 mg Tablet", "Paracetamol", Drug, "TAB", "650 mg", "Tablet", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, Otc, false, false, E, 600, 3000, 33.75m, 19.50m),
        new("DRG-AMOX500", "Amoxicillin 500 mg Capsule", "Amoxicillin", Drug, "TAB", "500 mg", "Capsule", "Strip of 10", "CAP", "STRIP", 10, "300420", 5, H, false, false, V, 300, 1000, 98.00m, 58.00m),
        new("DRG-AZI500", "Azithromycin 500 mg Tablet", "Azithromycin", Drug, "TAB", "500 mg", "Tablet", "Strip of 3", "TAB", "STRIP", 3, "300420", 5, H, false, false, V, 90, 300, 71.00m, 40.00m),
        new("DRG-CEFIX200", "Cefixime 200 mg Tablet", "Cefixime", Drug, "TAB", "200 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300420", 5, H1, false, false, V, 200, 600, 109.00m, 62.00m),
        new("DRG-LEVO500", "Levofloxacin 500 mg Tablet", "Levofloxacin", Drug, "TAB", "500 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H1, false, false, V, 100, 300, 95.00m, 52.00m),
        new("DRG-METRO400", "Metronidazole 400 mg Tablet", "Metronidazole", Drug, "TAB", "400 mg", "Tablet", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, H, false, false, E, 150, 600, 25.00m, 13.00m),
        new("DRG-PANTO40", "Pantoprazole 40 mg Tablet", "Pantoprazole", Drug, "TAB", "40 mg", "Tablet (gastro-resistant)", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, H, false, false, E, 450, 1500, 159.00m, 85.00m),
        new("DRG-RABE20", "Rabeprazole 20 mg Tablet", "Rabeprazole", Drug, "TAB", "20 mg", "Tablet (gastro-resistant)", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H, false, false, D, 100, 300, 110.00m, 60.00m),
        new("DRG-FAMO20", "Famotidine 20 mg Tablet", "Famotidine", Drug, "TAB", "20 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H, false, false, D, 100, 300, 18.00m, 9.50m),
        new("DRG-OND4", "Ondansetron 4 mg Tablet", "Ondansetron", Drug, "TAB", "4 mg", "Orally disintegrating tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H, false, false, E, 150, 500, 56.00m, 30.00m),
        new("DRG-DOMP10", "Domperidone 10 mg Tablet", "Domperidone", Drug, "TAB", "10 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H, false, false, D, 100, 300, 34.00m, 17.00m),
        new("DRG-MET500", "Metformin 500 mg Tablet", "Metformin", Drug, "TAB", "500 mg", "Tablet", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, H, false, false, E, 600, 3000, 30.00m, 15.50m),
        new("DRG-GLIM1", "Glimepiride 1 mg Tablet", "Glimepiride", Drug, "TAB", "1 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H, false, false, E, 300, 1000, 60.00m, 31.00m),
        new("DRG-AMLO5", "Amlodipine 5 mg Tablet", "Amlodipine", Drug, "TAB", "5 mg", "Tablet", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, H, false, false, E, 450, 1500, 42.00m, 22.00m),
        new("DRG-TELMI40", "Telmisartan 40 mg Tablet", "Telmisartan", Drug, "TAB", "40 mg", "Tablet", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, H, false, false, E, 450, 1500, 105.00m, 55.00m),
        new("DRG-LOSAR50", "Losartan 50 mg Tablet", "Losartan", Drug, "TAB", "50 mg", "Tablet", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, H, false, false, E, 300, 900, 65.00m, 34.00m),
        new("DRG-ATEN50", "Atenolol 50 mg Tablet", "Atenolol", Drug, "TAB", "50 mg", "Tablet", "Strip of 14", "TAB", "STRIP", 14, "300490", 5, H, false, false, E, 280, 840, 32.00m, 16.00m),
        new("DRG-ATOR10", "Atorvastatin 10 mg Tablet", "Atorvastatin", Drug, "TAB", "10 mg", "Tablet", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, H, false, false, E, 450, 1500, 90.00m, 47.00m),
        new("DRG-ASP75", "Aspirin 75 mg Tablet", "Acetylsalicylic acid", Drug, "TAB", "75 mg", "Tablet (gastro-resistant)", "Strip of 14", "TAB", "STRIP", 14, "300490", 5, H, false, false, V, 280, 1400, 5.50m, 2.80m),
        new("DRG-CLOP75", "Clopidogrel 75 mg Tablet", "Clopidogrel", Drug, "TAB", "75 mg", "Tablet", "Strip of 15", "TAB", "STRIP", 15, "300490", 5, H, false, false, V, 300, 900, 93.00m, 49.00m),
        new("DRG-MONT10", "Montelukast 10 mg Tablet", "Montelukast", Drug, "TAB", "10 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H, false, false, D, 150, 500, 125.00m, 64.00m),
        new("DRG-CETI10", "Cetirizine 10 mg Tablet", "Cetirizine", Drug, "TAB", "10 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, Otc, false, false, D, 200, 1000, 18.50m, 8.00m),
        new("DRG-DICLO50", "Diclofenac 50 mg Tablet", "Diclofenac", Drug, "TAB", "50 mg", "Tablet (enteric-coated)", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H, false, false, D, 200, 600, 21.00m, 10.00m),
        new("DRG-PRED10", "Prednisolone 10 mg Tablet", "Prednisolone", Drug, "TAB", "10 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300432", 5, H, false, false, E, 100, 300, 15.00m, 7.50m),
        new("DRG-TRAM50", "Tramadol 50 mg Capsule", "Tramadol", Drug, "TAB", "50 mg", "Capsule", "Strip of 10", "CAP", "STRIP", 10, "300490", 5, H1, true, false, E, 50, 200, 60.00m, 31.00m),
        new("DRG-ALPZ025", "Alprazolam 0.25 mg Tablet", "Alprazolam", Drug, "TAB", "0.25 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, H1, true, false, D, 50, 200, 25.00m, 12.00m),
        new("DRG-VITD60K", "Cholecalciferol 60000 IU Capsule", "Cholecalciferol", Drug, "TAB", "60000 IU", "Soft gelatin capsule", "Strip of 4", "CAP", "STRIP", 4, "300450", 5, H, false, false, D, 40, 200, 140.00m, 70.00m),
        new("DRG-FOLIC5", "Folic Acid 5 mg Tablet", "Folic acid", Drug, "TAB", "5 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300450", 5, Otc, false, false, E, 200, 1000, 8.50m, 3.90m),
        new("DRG-FEFOL", "Ferrous Sulphate + Folic Acid Tablet", "Ferrous sulphate + Folic acid", Drug, "TAB", "100 mg + 0.5 mg", "Tablet", "Strip of 10", "TAB", "STRIP", 10, "300490", 5, Otc, false, false, E, 300, 1000, 12.00m, 5.50m),
        new("DRG-ORS", "Oral Rehydration Salts (WHO formula) 21.8 g", "Oral rehydration salts", Drug, "TAB", "21.8 g", "Powder for oral solution", "Box of 25 sachets", "SACHET", "BOX", 25, "300490", 5, Otc, false, false, V, 100, 500, 525.00m, 300.00m),

        // ---- injections and vaccines (stock in vials/ampoules)
        new("DRG-CEFTRI1G", "Ceftriaxone 1 g Injection", "Ceftriaxone", Drug, "INJ", "1 g", "Powder for injection", "Vial with water for injection", "VIAL", "VIAL", 1, "300420", 5, H1, false, false, V, 100, 300, 62.00m, 32.00m),
        new("DRG-MEROP1G", "Meropenem 1 g Injection", "Meropenem", Drug, "INJ", "1 g", "Powder for injection", "Vial", "VIAL", "VIAL", 1, "300420", 5, H1, false, false, V, 40, 100, 1450.00m, 820.00m),
        new("DRG-PANTOIV", "Pantoprazole 40 mg Injection", "Pantoprazole", Drug, "INJ", "40 mg", "Powder for injection", "Vial", "VIAL", "VIAL", 1, "300490", 5, H, false, false, E, 100, 300, 58.00m, 29.00m),
        new("DRG-ONDINJ", "Ondansetron 2 mg/ml Injection (2 ml)", "Ondansetron", Drug, "INJ", "2 mg/ml", "Injection", "Box of 5 ampoules", "AMP", "BOX", 5, "300490", 5, H, false, false, E, 100, 300, 70.00m, 36.00m),
        new("DRG-DICLOINJ", "Diclofenac 75 mg/3 ml Injection", "Diclofenac", Drug, "INJ", "25 mg/ml", "Injection", "Box of 5 ampoules", "AMP", "BOX", 5, "300490", 5, H, false, false, D, 50, 200, 60.00m, 28.00m),
        new("DRG-DEXA8", "Dexamethasone 8 mg/2 ml Injection", "Dexamethasone", Drug, "INJ", "4 mg/ml", "Injection", "Vial", "VIAL", "VIAL", 1, "300432", 5, H, false, false, V, 60, 200, 22.00m, 10.00m),
        new("DRG-ADR1", "Adrenaline 1 mg/ml Injection", "Adrenaline", Drug, "INJ", "1 mg/ml", "Injection", "Box of 10 ampoules", "AMP", "BOX", 10, "300439", 5, H, false, false, V, 50, 100, 150.00m, 75.00m),
        new("DRG-HEP5000", "Heparin 5000 IU/ml Injection (5 ml)", "Heparin sodium", Drug, "INJ", "5000 IU/ml", "Injection", "Vial", "VIAL", "VIAL", 1, "300490", 5, H, false, false, V, 30, 100, 420.00m, 230.00m),
        new("DRG-INSR", "Human Insulin Regular 40 IU/ml (10 ml)", "Insulin human (regular)", Drug, "INJ", "40 IU/ml", "Injection", "Vial", "VIAL", "VIAL", 1, "300431", 5, H, false, true, V, 30, 100, 165.00m, 95.00m),
        new("DRG-INS3070", "Human Insulin 30/70 40 IU/ml (10 ml)", "Insulin human (biphasic 30/70)", Drug, "INJ", "40 IU/ml", "Injection", "Vial", "VIAL", "VIAL", 1, "300431", 5, H, false, true, V, 30, 100, 165.00m, 95.00m),
        new("DRG-MORPH10", "Morphine 10 mg/ml Injection", "Morphine sulphate", Drug, "INJ", "10 mg/ml", "Injection", "Ampoule", "AMP", "AMP", 1, "300449", 5, DrugSchedule.X, true, false, V, 10, 50, 26.00m, 14.00m),
        new("DRG-MIDAZ1", "Midazolam 1 mg/ml Injection (5 ml)", "Midazolam", Drug, "INJ", "1 mg/ml", "Injection", "Vial", "VIAL", "VIAL", 1, "300490", 5, H1, false, false, V, 20, 60, 45.00m, 22.00m),
        new("DRG-TT05", "Tetanus Toxoid 0.5 ml Injection", "Tetanus toxoid (adsorbed)", Drug, "INJ", "0.5 ml", "Injection", "Box of 10 ampoules", "AMP", "BOX", 10, "300241", 5, H, false, true, V, 20, 100, 180.00m, 95.00m),

        // ---- IV fluids (stock in bottles, bought by the case)
        new("DRG-NS500", "Sodium Chloride 0.9% IV Infusion 500 ml (Normal Saline)", "Sodium chloride", Drug, "IVF", "0.9% w/v", "IV infusion", "Case of 20 bottles", "BTL", "CASE", 20, "300490", 5, H, false, false, V, 200, 600, 700.00m, 380.00m),
        new("DRG-RL500", "Ringer Lactate IV Infusion 500 ml", "Compound sodium lactate", Drug, "IVF", "500 ml", "IV infusion", "Case of 20 bottles", "BTL", "CASE", 20, "300490", 5, H, false, false, V, 200, 600, 760.00m, 400.00m),
        new("DRG-DNS500", "Dextrose 5% + Sodium Chloride 0.9% IV 500 ml (DNS)", "Dextrose with sodium chloride", Drug, "IVF", "5% + 0.9%", "IV infusion", "Case of 20 bottles", "BTL", "CASE", 20, "300490", 5, H, false, false, V, 150, 400, 760.00m, 400.00m),
        new("DRG-D5500", "Dextrose 5% IV Infusion 500 ml", "Dextrose", Drug, "IVF", "5% w/v", "IV infusion", "Case of 20 bottles", "BTL", "CASE", 20, "300490", 5, H, false, false, E, 100, 400, 720.00m, 380.00m),

        // ---- oral liquids, inhalers, topicals
        new("DRG-PCMSUS", "Paracetamol 120 mg/5 ml Suspension (60 ml)", "Paracetamol", Drug, "SYR", "120 mg/5 ml", "Oral suspension", "Bottle of 60 ml", "BTL", "BTL", 1, "300490", 5, Otc, false, false, E, 50, 200, 32.00m, 17.00m),
        new("DRG-AMOXSYR", "Amoxicillin 125 mg/5 ml Dry Syrup (60 ml)", "Amoxicillin", Drug, "SYR", "125 mg/5 ml", "Powder for oral suspension", "Bottle of 60 ml", "BTL", "BTL", 1, "300420", 5, H, false, false, E, 40, 150, 48.00m, 25.00m),
        new("DRG-LACTU", "Lactulose 10 g/15 ml Solution (100 ml)", "Lactulose", Drug, "SYR", "10 g/15 ml", "Oral solution", "Bottle of 100 ml", "BTL", "BTL", 1, "300490", 5, Otc, false, false, D, 30, 100, 145.00m, 78.00m),
        new("DRG-SALBINH", "Salbutamol 100 mcg Inhaler (200 doses)", "Salbutamol", Drug, "INH", "100 mcg/dose", "Metered-dose inhaler", "Inhaler", "NOS", "NOS", 1, "300490", 5, H, false, false, V, 30, 100, 150.00m, 80.00m),
        new("DRG-BUDRES", "Budesonide 0.5 mg Respules (2 ml)", "Budesonide", Drug, "INH", "0.5 mg/2 ml", "Nebuliser suspension", "Pack of 5 respules", "RESPULE", "PACK", 5, "300432", 5, H, false, false, E, 50, 200, 165.00m, 88.00m),
        new("DRG-POVI5", "Povidone-Iodine 5% Solution (500 ml)", "Povidone-iodine", Drug, "TOP", "5% w/v", "Solution", "Bottle of 500 ml", "BTL", "BTL", 1, "300490", 5, Otc, false, false, V, 20, 60, 395.00m, 210.00m),
        new("DRG-SSD1", "Silver Sulfadiazine 1% Cream (50 g)", "Silver sulfadiazine", Drug, "TOP", "1% w/w", "Cream", "Tube of 50 g", "TUBE", "TUBE", 1, "300490", 5, H, false, false, E, 20, 60, 110.00m, 58.00m),
        new("DRG-LIGNO2", "Lignocaine 2% Jelly (30 g)", "Lidocaine hydrochloride", Drug, "TOP", "2% w/w", "Jelly", "Tube of 30 g", "TUBE", "TUBE", 1, "300490", 5, H, false, false, E, 20, 60, 90.00m, 45.00m),

        // ---- consumables
        new("CON-SYR2", "Disposable Syringe 2 ml with Needle", null, ItemType.Consumable, "CONS", "2 ml", null, "Box of 100", "PCS", "BOX", 100, "901831", 5, DrugSchedule.None, false, false, V, 500, 2000, 950.00m, 420.00m),
        new("CON-SYR5", "Disposable Syringe 5 ml with Needle", null, ItemType.Consumable, "CONS", "5 ml", null, "Box of 100", "PCS", "BOX", 100, "901831", 5, DrugSchedule.None, false, false, V, 500, 2000, 1100.00m, 480.00m),
        new("CON-IVC20", "IV Cannula 20G", null, ItemType.Consumable, "CONS", "20G", null, "Box of 50", "PCS", "BOX", 50, "901839", 5, DrugSchedule.None, false, false, V, 200, 500, 4250.00m, 1600.00m),
        new("CON-IVC22", "IV Cannula 22G", null, ItemType.Consumable, "CONS", "22G", null, "Box of 50", "PCS", "BOX", 50, "901839", 5, DrugSchedule.None, false, false, V, 150, 500, 4250.00m, 1600.00m),
        new("CON-IVSET", "IV Infusion Set (adult)", null, ItemType.Consumable, "CONS", null, null, "Box of 50", "PCS", "BOX", 50, "901839", 5, DrugSchedule.None, false, false, V, 200, 500, 2750.00m, 900.00m),
        new("CON-GLOVE7", "Surgical Gloves Sterile Size 7 (pair)", null, ItemType.Consumable, "CONS", "Size 7", null, "Box of 50 pairs", "PAIR", "BOX", 50, "401511", 5, DrugSchedule.None, false, false, E, 300, 1000, 2250.00m, 1000.00m),
        new("CON-EXGLOVE", "Examination Gloves Nitrile Medium", null, ItemType.Consumable, "CONS", "M", null, "Box of 100", "PCS", "BOX", 100, "401512", 5, DrugSchedule.None, false, false, E, 500, 2000, 650.00m, 330.00m),
        new("CON-GAUZE", "Gauze Swab 10 x 10 cm Sterile (8 ply)", null, ItemType.Consumable, "CONS", "10 x 10 cm", null, "Packet of 100", "PCS", "PKT", 100, "300590", 5, DrugSchedule.None, false, false, E, 1000, 5000, 450.00m, 210.00m),
        new("CON-BAND10", "Roller Bandage 10 cm x 3 m", null, ItemType.Consumable, "CONS", "10 cm", null, "Packet of 12", "PCS", "PKT", 12, "300590", 5, DrugSchedule.None, false, false, D, 120, 480, 216.00m, 96.00m),
        new("CON-FOLEY16", "Foley Catheter 16 Fr 2-way Silicone", null, ItemType.Consumable, "CONS", "16 Fr", null, "Box of 10", "PCS", "BOX", 10, "901839", 5, DrugSchedule.None, false, false, E, 50, 100, 3200.00m, 1500.00m),
        new("CON-UROBAG", "Urine Collection Bag 2000 ml", null, ItemType.Consumable, "CONS", "2000 ml", null, "Box of 50", "PCS", "BOX", 50, "901890", 5, DrugSchedule.None, false, false, D, 100, 300, 2250.00m, 850.00m),
        new("CON-MASK3P", "Surgical Mask 3-ply", null, ItemType.Consumable, "CONS", null, null, "Box of 50", "PCS", "BOX", 50, "630790", 5, DrugSchedule.None, false, false, E, 500, 2000, 250.00m, 90.00m),
        new("CON-ECGEL", "ECG Electrode Disposable (adult)", null, ItemType.Consumable, "CONS", null, null, "Packet of 50", "PCS", "PKT", 50, "901890", 5, DrugSchedule.None, false, false, D, 200, 1000, 600.00m, 260.00m),

        // ---- surgical items and sutures
        new("SUR-PG20", "Absorbable Suture Polyglactin 2-0, 70 cm, Round Body", null, ItemType.Surgical, "SUT", "2-0", null, "Box of 12 foils", "FOIL", "BOX", 12, "300610", 5, DrugSchedule.None, false, false, V, 36, 144, 5400.00m, 2600.00m),
        new("SUR-SILK30", "Silk Suture 3-0, 76 cm, Reverse Cutting", null, ItemType.Surgical, "SUT", "3-0", null, "Box of 12 foils", "FOIL", "BOX", 12, "300610", 5, DrugSchedule.None, false, false, E, 24, 96, 2100.00m, 950.00m),
        new("SUR-BLADE22", "Surgical Blade No. 22 (sterile)", null, ItemType.Surgical, "SURG", "No. 22", null, "Box of 100", "PCS", "BOX", 100, "901890", 5, DrugSchedule.None, false, false, E, 100, 500, 850.00m, 380.00m),

        // ---- implants (charged per patient at MRP)
        new("IMP-TKR", "Total Knee Replacement System (primary, cemented)", "Knee joint prosthesis", ItemType.Implant, "IMPL", null, null, "Set", "SET", "SET", 1, "902131", 5, DrugSchedule.None, false, false, E, 1, 2, 54720.00m, 38000.00m),
        new("IMP-DHS", "Dynamic Hip Screw 135° with 4-hole Plate", "Hip fracture fixation", ItemType.Implant, "IMPL", "135°", null, "Set", "SET", "SET", 1, "902110", 5, DrugSchedule.None, false, false, E, 1, 2, 18500.00m, 11200.00m),

        // ---- linen, general stores, spares
        new("LIN-BEDSHEET", "Bed Sheet Cotton White (single)", null, ItemType.Linen, "LIN", null, null, "Piece", "PCS", "PCS", 1, "630231", 5, DrugSchedule.None, false, false, E, 100, 300, 450.00m, 260.00m),
        new("LIN-PILLOW", "Pillow Cover Cotton White", null, ItemType.Linen, "LIN", null, null, "Piece", "PCS", "PCS", 1, "630231", 5, DrugSchedule.None, false, false, D, 100, 300, 120.00m, 65.00m),
        new("GEN-BMWYEL", "Biomedical Waste Bag Yellow (large)", null, ItemType.General, "GEN", "Large", null, "Packet of 100", "PCS", "PKT", 100, "392321", 18, DrugSchedule.None, false, false, E, 500, 2000, 900.00m, 520.00m),
        new("GEN-BMWRED", "Biomedical Waste Bag Red (large)", null, ItemType.General, "GEN", "Large", null, "Packet of 100", "PCS", "PKT", 100, "392321", 18, DrugSchedule.None, false, false, E, 300, 1000, 900.00m, 520.00m),
        new("SPR-SPO2", "SpO2 Sensor Adult Finger Clip (reusable)", null, ItemType.Spare, "SPR", "Adult", null, "Piece", "PCS", "PCS", 1, "901819", 5, DrugSchedule.None, false, false, E, 5, 10, 3800.00m, 2100.00m),
    ];
}
