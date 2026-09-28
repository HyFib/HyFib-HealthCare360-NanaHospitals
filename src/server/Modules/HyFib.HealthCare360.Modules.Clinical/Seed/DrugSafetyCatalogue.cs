using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Seed;

/// <summary>
/// Starter content for the configurable prescribing-safety tables (ERX-02): clinically common drug–drug interactions
/// seen in Indian OP/IP prescribing, and maximum adult daily doses (mg) of frequently prescribed medicines, from standard
/// references (BNF, product labels, national guidelines). Pharmacy and therapeutics committees review and extend both
/// tables through the API; nothing here is generated.
/// </summary>
internal static class DrugSafetyCatalogue
{
    internal sealed record InteractionSeed(string DrugA, string DrugB, InteractionSeverity Severity, string Effect, string Management);

    internal sealed record DoseLimitSeed(string GenericName, decimal MaxDailyDoseMg, string? Route, string? Notes = null);

    private const InteractionSeverity Major = InteractionSeverity.Major;
    private const InteractionSeverity Moderate = InteractionSeverity.Moderate;
    private const InteractionSeverity Minor = InteractionSeverity.Minor;

    private const string NsaidBleeding = "NSAIDs add to the bleeding risk and can raise the INR; gastrointestinal ulceration.";
    private const string AvoidNsaid = "Avoid; prefer paracetamol for pain. If unavoidable, monitor INR and give gastroprotection.";
    private const string NitrateHypotension = "Profound, potentially fatal hypotension (additive nitric-oxide vasodilation).";
    private const string NitrateManagement = "Contraindicated. Do not give nitrates within 24 h of sildenafil or 48 h of tadalafil.";

    public static IReadOnlyList<InteractionSeed> Interactions { get; } =
    [
        // Major
        new("warfarin", "aspirin", Major, "Additive bleeding risk (anticoagulant plus antiplatelet); serious gastrointestinal bleeding.",
            "Avoid unless specifically indicated (for example a mechanical valve); use the lowest aspirin dose with PPI cover and monitor INR."),
        new("warfarin", "ibuprofen", Major, NsaidBleeding, AvoidNsaid),
        new("warfarin", "diclofenac", Major, NsaidBleeding, AvoidNsaid),
        new("warfarin", "fluconazole", Major, "Fluconazole inhibits warfarin metabolism (CYP2C9); the INR can rise sharply.",
            "Avoid, or reduce the warfarin dose and check the INR within 3–5 days."),
        new("warfarin", "metronidazole", Major, "Metronidazole inhibits warfarin metabolism; marked INR rise and bleeding.",
            "Avoid; if needed reduce the warfarin dose and monitor the INR closely."),
        new("warfarin", "amiodarone", Major, "Amiodarone increases the effect of warfarin for weeks to months.",
            "Reduce the warfarin dose by 30–50% and monitor the INR weekly."),
        new("warfarin", "rifampicin", Major, "Rifampicin induces warfarin metabolism; loss of anticoagulation.",
            "Monitor the INR closely; large dose increases may be needed, and reduce again when rifampicin stops."),
        new("simvastatin", "clarithromycin", Major, "CYP3A4 inhibition raises simvastatin levels; myopathy and rhabdomyolysis.",
            "Contraindicated: pause simvastatin during the course or use azithromycin."),
        new("simvastatin", "amiodarone", Major, "Raised simvastatin exposure; myopathy and rhabdomyolysis.", "Do not exceed simvastatin 20 mg a day; consider another statin."),
        new("sildenafil", "nitroglycerin", Major, NitrateHypotension, NitrateManagement),
        new("sildenafil", "isosorbide", Major, NitrateHypotension, NitrateManagement),
        new("tadalafil", "nitroglycerin", Major, NitrateHypotension, NitrateManagement),
        new("tadalafil", "isosorbide", Major, NitrateHypotension, NitrateManagement),
        new("spironolactone", "potassium chloride", Major, "Potassium-sparing diuretic plus potassium supplement: hyperkalaemia.",
            "Avoid unless hypokalaemia is documented; monitor potassium and renal function."),
        new("methotrexate", "trimethoprim", Major, "Additive antifolate effect: bone-marrow suppression and pancytopenia.",
            "Avoid trimethoprim/co-trimoxazole with methotrexate; choose another antibiotic."),
        new("clarithromycin", "colchicine", Major, "Colchicine toxicity (CYP3A4 and P-glycoprotein inhibition), potentially fatal.",
            "Avoid; if essential reduce the colchicine dose, and never combine in renal or hepatic impairment."),
        new("tramadol", "sertraline", Major, "Serotonin syndrome and a lowered seizure threshold.",
            "Avoid, or use the lowest doses and watch for agitation, tremor, hyperthermia and seizures."),
        new("tramadol", "fluoxetine", Major, "Serotonin syndrome; fluoxetine also blocks tramadol activation (CYP2D6).", "Avoid; choose another analgesic."),
        new("tramadol", "escitalopram", Major, "Serotonin syndrome and a lowered seizure threshold.", "Avoid, or use the lowest doses with close monitoring."),
        new("linezolid", "sertraline", Major, "Linezolid is a monoamine-oxidase inhibitor: serotonin syndrome.", "Avoid; if unavoidable monitor closely for serotonin toxicity."),
        new("tramadol", "alprazolam", Major, "Opioid plus benzodiazepine: additive CNS and respiratory depression.",
            "Avoid; if needed use the lowest doses for the shortest time and counsel the patient and family."),
        new("morphine", "midazolam", Major, "Opioid plus benzodiazepine: additive CNS and respiratory depression.",
            "Use only with monitoring of respiration and sedation; titrate carefully."),
        new("digoxin", "amiodarone", Major, "Amiodarone raises digoxin levels: bradycardia, heart block and arrhythmias.",
            "Halve the digoxin dose and monitor levels and heart rate."),
        new("digoxin", "verapamil", Major, "Raised digoxin levels and additive AV-nodal block.", "Reduce the digoxin dose; monitor heart rate and levels."),
        new("metoprolol", "verapamil", Major, "Additive negative inotropic and AV-nodal effects: bradycardia, heart block, hypotension.",
            "Avoid, especially intravenous verapamil; use only with specialist monitoring."),
        new("atenolol", "verapamil", Major, "Additive negative inotropic and AV-nodal effects: bradycardia, heart block, hypotension.",
            "Avoid, especially intravenous verapamil; use only with specialist monitoring."),
        new("theophylline", "ciprofloxacin", Major, "Ciprofloxacin inhibits theophylline clearance: seizures and arrhythmias.",
            "Avoid, or reduce the theophylline dose and monitor levels."),
        new("tizanidine", "ciprofloxacin", Major, "Ciprofloxacin (CYP1A2) greatly increases tizanidine: hypotension and sedation.", "Contraindicated."),
        new("lithium", "ibuprofen", Major, "NSAIDs reduce lithium excretion: lithium toxicity.", "Avoid; if needed monitor lithium levels closely."),
        new("lithium", "hydrochlorothiazide", Major, "Thiazides reduce lithium clearance by about 25%: toxicity.", "Avoid, or reduce the lithium dose and monitor levels."),
        new("rifampicin", "ethinylestradiol", Major, "Rifampicin induces oestrogen metabolism: contraceptive failure.",
            "Use additional non-hormonal contraception during and for 28 days after rifampicin."),
        new("carbamazepine", "ethinylestradiol", Major, "Enzyme induction: contraceptive failure.", "Use a non-hormonal method and counsel the patient."),
        new("allopurinol", "azathioprine", Major, "Allopurinol blocks azathioprine metabolism: severe bone-marrow suppression.",
            "Avoid, or reduce azathioprine to a quarter of the dose with close blood-count monitoring."),
        new("valproate", "meropenem", Major, "Carbapenems sharply lower valproate levels: breakthrough seizures.", "Avoid; use another antibiotic or anticonvulsant."),
        new("amiodarone", "levofloxacin", Major, "Additive QT prolongation: torsades de pointes.", "Avoid; if needed monitor the ECG and electrolytes."),
        new("haloperidol", "metoclopramide", Major, "Additive dopamine blockade: extrapyramidal reactions and neuroleptic malignant syndrome.", "Avoid the combination."),

        // Moderate
        new("clopidogrel", "omeprazole", Moderate, "Omeprazole (CYP2C19 inhibitor) reduces clopidogrel activation and its antiplatelet effect.",
            "Prefer pantoprazole when a PPI is needed."),
        new("clopidogrel", "esomeprazole", Moderate, "Esomeprazole (CYP2C19 inhibitor) reduces clopidogrel activation and its antiplatelet effect.",
            "Prefer pantoprazole when a PPI is needed."),
        new("clopidogrel", "aspirin", Moderate, "Dual antiplatelet therapy increases bleeding risk.",
            "Intended after ACS or stenting: confirm the indication and planned duration, consider PPI cover."),
        new("atorvastatin", "clarithromycin", Moderate, "Raised atorvastatin levels; myopathy risk.", "Limit atorvastatin to 20 mg a day during the course, or pause it."),
        new("simvastatin", "amlodipine", Moderate, "Amlodipine increases simvastatin exposure; myopathy risk.", "Do not exceed simvastatin 20 mg a day."),
        new("enalapril", "spironolactone", Moderate, "ACE inhibitor plus potassium-sparing diuretic: hyperkalaemia.",
            "Monitor potassium and creatinine; avoid in renal impairment."),
        new("ramipril", "potassium chloride", Moderate, "ACE inhibitor plus potassium supplement: hyperkalaemia.", "Avoid routine potassium supplements; monitor potassium."),
        new("telmisartan", "potassium chloride", Moderate, "Angiotensin-receptor blocker plus potassium supplement: hyperkalaemia.", "Monitor potassium."),
        new("enalapril", "ibuprofen", Moderate, "NSAIDs blunt the antihypertensive effect and increase the risk of acute kidney injury.",
            "Avoid prolonged use; monitor blood pressure and renal function."),
        new("aspirin", "ibuprofen", Moderate, "Ibuprofen can block the antiplatelet effect of low-dose aspirin; additive gastrointestinal bleeding.",
            "Give aspirin at least 30 minutes before ibuprofen, or use paracetamol."),
        new("prednisolone", "ibuprofen", Moderate, "Additive gastrointestinal ulceration and bleeding.", "Give gastroprotection; avoid long-term combination."),
        new("methotrexate", "ibuprofen", Moderate, "NSAIDs reduce methotrexate clearance (important at higher doses).",
            "Avoid with high-dose methotrexate; monitor blood counts and renal function."),
        new("glimepiride", "fluconazole", Moderate, "Fluconazole raises sulfonylurea levels: hypoglycaemia.", "Monitor blood glucose; consider a dose reduction."),
        new("levothyroxine", "calcium carbonate", Moderate, "Calcium reduces levothyroxine absorption.", "Separate the doses by at least 4 hours."),
        new("levothyroxine", "ferrous", Moderate, "Iron reduces levothyroxine absorption.", "Separate the doses by at least 4 hours."),
        new("ciprofloxacin", "aluminium hydroxide", Moderate, "Antacids chelate ciprofloxacin and reduce its absorption.",
            "Give ciprofloxacin 2 hours before or 6 hours after the antacid."),
        new("doxycycline", "ferrous", Moderate, "Iron reduces doxycycline absorption.", "Separate the doses by 2–3 hours."),
        new("azithromycin", "hydroxychloroquine", Moderate, "Additive QT prolongation.", "Check the ECG and electrolytes in patients at risk."),
        new("ondansetron", "domperidone", Moderate, "Additive QT prolongation.", "Avoid in patients with QT risk factors; use one antiemetic."),
        new("furosemide", "gentamicin", Moderate, "Additive ototoxicity and nephrotoxicity.", "Monitor renal function, aminoglycoside levels and hearing."),
        new("phenytoin", "fluconazole", Moderate, "Fluconazole increases phenytoin levels: toxicity.", "Monitor phenytoin levels and for ataxia or nystagmus."),
        new("isoniazid", "phenytoin", Moderate, "Isoniazid inhibits phenytoin metabolism.", "Monitor phenytoin levels."),

        // Minor
        new("paracetamol", "warfarin", Minor, "Regular paracetamol above 2 g a day can raise the INR.", "Occasional use is fine; monitor the INR with regular use."),
        new("atorvastatin", "amlodipine", Minor, "Small increase in atorvastatin exposure.", "No dose change is usually needed."),
    ];

    public static IReadOnlyList<DoseLimitSeed> DoseLimits { get; } =
    [
        new("paracetamol", 4000m, "oral", "Consider 3 g/day in low body weight, chronic alcohol use or liver disease."),
        new("ibuprofen", 2400m, "oral"),
        new("diclofenac", 150m, "oral"),
        new("aceclofenac", 200m, "oral"),
        new("naproxen", 1250m, "oral"),
        new("mefenamic acid", 1500m, "oral"),
        new("aspirin", 4000m, "oral", "Analgesic dosing; antiplatelet dose is 75–150 mg."),
        new("nimesulide", 200m, "oral", "Not for children under 12 years."),
        new("etoricoxib", 120m, "oral"),
        new("tramadol", 400m, "oral"),
        new("codeine", 240m, "oral"),
        new("metformin", 2550m, "oral"),
        new("glimepiride", 8m, "oral"),
        new("gliclazide", 320m, "oral", "Immediate-release tablets; modified-release maximum is 120 mg."),
        new("amlodipine", 10m, "oral"),
        new("atenolol", 100m, "oral"),
        new("metoprolol", 400m, "oral"),
        new("enalapril", 40m, "oral"),
        new("ramipril", 10m, "oral"),
        new("losartan", 100m, "oral"),
        new("telmisartan", 80m, "oral"),
        new("atorvastatin", 80m, "oral"),
        new("rosuvastatin", 40m, "oral"),
        new("simvastatin", 40m, "oral", "80 mg only for patients already stable on it."),
        new("ondansetron", 24m, "oral"),
        new("domperidone", 30m, "oral", "Cardiac risk above 30 mg/day; use for up to 1 week."),
        new("amoxicillin", 3000m, "oral"),
        new("azithromycin", 1000m, "oral", "Single 1 g dose for specific infections; usual 500 mg/day."),
        new("ciprofloxacin", 1500m, "oral"),
        new("levofloxacin", 750m, "oral"),
        new("doxycycline", 200m, "oral"),
        new("metronidazole", 2400m, "oral"),
        new("fluconazole", 800m, "oral"),
        new("allopurinol", 900m, "oral"),
        new("colchicine", 2m, "oral", "No more than 6 mg per course; reduce in renal impairment."),
        new("spironolactone", 400m, "oral"),
        new("amitriptyline", 150m, "oral"),
        new("sertraline", 200m, "oral"),
        new("fluoxetine", 80m, "oral"),
        new("escitalopram", 20m, "oral", "10 mg maximum above 65 years."),
        new("alprazolam", 4m, "oral"),
        new("gabapentin", 3600m, "oral"),
        new("pregabalin", 600m, "oral"),
        new("levothyroxine", 0.3m, "oral", "300 micrograms."),
        new("montelukast", 10m, "oral"),
        new("methotrexate", 25m, "oral", "Weekly dosing for arthritis/psoriasis; daily schedules are flagged separately."),
        new("isoniazid", 300m, "oral"),
        new("rifampicin", 600m, "oral"),
    ];
}
