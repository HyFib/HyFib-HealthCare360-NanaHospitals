using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Seed;

/// <summary>
/// Starter speciality templates (GM, PED, ORTH, OBG, CARD, ENT) and order sets. They hold documentation prompts,
/// examination headings and standard investigation bundles written by clinicians' conventions — not generated text —
/// and only pre-fill a draft that the doctor edits. Default orders reference service codes seeded by Diagnostics and the
/// platform; codes that do not exist yet are skipped and added on a later start.
/// </summary>
internal static class TemplateCatalogue
{
    internal sealed record MedicationSeed(string DrugName, string GenericName, string Strength, string DosageForm, string Route, string Dose,
        DoseFrequency Frequency, int DurationDays, string Instructions);

    internal sealed record TemplateSeed(
        string Code,
        string Name,
        string Specialty,
        int DisplayOrder,
        string? HistoryPrompt,
        string? Examination,
        string? Plan,
        string? FollowUpAdvice,
        int? FollowUpDays,
        string[] Complaints,
        string[] OrderCodes,
        MedicationSeed[] Medications);

    internal sealed record OrderSetSeed(string Code, string Name, string? Specialty, string Description, string[] ServiceCodes);

    private const string GeneralExamination =
        "General: conscious, oriented. Pallor / icterus / cyanosis / clubbing / lymphadenopathy / oedema: \n" +
        "CVS: S1 S2 heard; murmurs: \nRS: air entry; added sounds: \nP/A: soft / tender; organomegaly: \nCNS: focal deficit: ";

    public static IReadOnlyList<TemplateSeed> Templates { get; } =
    [
        new("GM-OPD", "General Medicine — OP consultation", "GM", 10,
            "Onset, duration and progression of symptoms; associated symptoms; past history of diabetes, hypertension, asthma, TB, heart disease; current medicines; allergies.",
            GeneralExamination, "Investigations as ordered. Review with reports.", "Return earlier if symptoms worsen.", 7,
            ["Fever", "Cough", "Cold", "Headache", "Body ache", "Vomiting", "Loose stools", "Abdominal pain", "Burning micturition", "Giddiness", "Fatigue", "Breathlessness"],
            [], []),
        new("GM-FEVER", "General Medicine — Acute febrile illness", "GM", 20,
            "Fever: duration, grade, pattern, chills or rigors. Rash, bleeding, jaundice, burning micturition, cough, loose stools. Travel, mosquito exposure, similar illness in contacts.",
            GeneralExamination + "\nLook for rash, bleeding spots, hepatosplenomegaly and neck stiffness.",
            "Adequate oral fluids and rest. Avoid NSAIDs until dengue is excluded.",
            "Return immediately for persistent vomiting, bleeding, breathlessness, drowsiness or reduced urine output.", 3,
            ["Fever", "Chills", "Body ache", "Headache", "Rash", "Vomiting", "Joint pain"],
            ["LAB-CBC", "LAB-ESR", "LAB-CRP", "LAB-URINE-RE"],
            [new("Paracetamol 500 mg tablet", "Paracetamol", "500 mg", "Tablet", "Oral", "1 tab", DoseFrequency.TDS, 3,
                "After food, for fever or body ache. Not more than 4 tablets in 24 hours.")]),
        new("GM-DM", "General Medicine — Diabetes review", "GM", 30,
            "Duration of diabetes, current medicines and adherence, home glucose readings, hypoglycaemia episodes, diet and activity, symptoms of eye, foot, kidney or heart complications.",
            "Weight, BMI, blood pressure. Feet: pulses, sensation (monofilament), skin and ulcers. Fundus examination if due.",
            "Diet and exercise counselling. Individualised HbA1c target (usually below 7%).", "Bring the glucose diary at the next visit.", 30,
            ["Routine review", "Increased thirst", "Frequent urination", "Tingling of feet", "Weight loss", "Blurred vision"],
            ["LAB-FBS", "LAB-PPBS", "LAB-HBA1C", "LAB-KFT", "LAB-LIPID", "LAB-URINE-RE"], []),
        new("GM-HTN", "General Medicine — Hypertension review", "GM", 40,
            "Home BP readings, adherence, salt intake, headache, chest pain, breathlessness, palpitations; diabetes, kidney disease, smoking.",
            "BP in both arms (seated, after 5 minutes' rest), pulse, weight and BMI; heart, lungs and fundus as indicated.",
            "Low-salt diet, activity and weight advice.", "Keep a home BP record.", 30,
            ["Routine review", "Headache", "Giddiness", "Chest pain", "Breathlessness"],
            ["LAB-KFT", "LAB-ELECTROLYTES", "LAB-LIPID", "LAB-URINE-RE", "CARD-ECG"], []),
        new("PED-OPD", "Paediatrics — OP consultation", "PED", 10,
            "Onset and duration; feeding, activity and urine output; immunisation status; birth and developmental history; sick contacts.",
            "Weight, length/height and head circumference (under 2 years) plotted on WHO growth charts. Activity, hydration, pallor, icterus; chest, abdomen, ears and throat.",
            "Doses by body weight. Oral rehydration and continued feeding where relevant.",
            "Return immediately if the child stops feeding, is very sleepy, breathes fast, has convulsions or keeps vomiting.", 3,
            ["Fever", "Cough", "Cold", "Loose stools", "Vomiting", "Poor feeding", "Rash", "Ear pain"],
            [], []),
        new("PED-WELL", "Paediatrics — Well-baby and immunisation visit", "PED", 20,
            "Feeding (breastfeeding / complementary feeding), sleep, milestones, reactions to previous vaccines.",
            "Growth parameters on WHO charts; developmental milestones; general examination.",
            "Vaccines due as per the National Immunisation Schedule / IAP schedule.", "Next visit as per the immunisation schedule.", 28,
            ["Immunisation", "Growth monitoring", "Feeding advice"],
            [], []),
        new("ORTH-PAIN", "Orthopaedics — Joint or back pain", "ORTH", 10,
            "Site, onset (injury or insidious), duration, character and radiation; aggravating and relieving factors; morning stiffness, night pain, weakness or numbness.",
            "Inspection, palpation, range of movement, special tests; distal neurovascular status. Spine: straight-leg raise and neurological examination of the limbs.",
            "Activity modification; physiotherapy referral as needed.", "Return earlier for weakness, numbness or bladder/bowel symptoms.", 14,
            ["Knee pain", "Low back pain", "Neck pain", "Shoulder pain", "Joint swelling", "Stiffness"],
            [], []),
        new("ORTH-INJURY", "Orthopaedics — Acute injury", "ORTH", 20,
            "Mechanism and time of injury, first aid, loss of consciousness, other injuries, tetanus immunisation status.",
            "ABC first. Local: swelling, deformity, tenderness, crepitus, open wound. Distal pulses, capillary refill, sensation and movement.",
            "Immobilise, elevate and relieve pain; X-ray of the injured part; tetanus prophylaxis if indicated.",
            "Keep the limb elevated; return for increasing pain, numbness or colour change of the fingers/toes.", 7,
            ["Fall", "Road traffic accident", "Swelling", "Deformity", "Unable to bear weight"],
            [], []),
        new("OBG-ANC", "Obstetrics — Antenatal visit", "OBG", 10,
            "LMP and EDD; gravida, para, living, abortions; previous pregnancy complications; current symptoms; fetal movements.",
            "Weight, BP, pallor, pedal oedema. Fundal height, lie and presentation, fetal heart rate.",
            "Iron–folic acid and calcium supplementation as per national guidelines; diet counselling.",
            "Come immediately for bleeding, leaking, reduced fetal movements, severe headache, blurred vision or convulsions.", 28,
            ["Routine antenatal visit", "Missed period", "Vomiting", "Abdominal pain", "Bleeding per vaginum", "Reduced fetal movements", "Swelling of feet"],
            ["LAB-CBC", "LAB-URINE-RE", "LAB-FBS", "LAB-TFT", "RAD-USG-PELVIS"], []),
        new("OBG-GYN", "Gynaecology — OP consultation", "OBG", 20,
            "Menstrual history (cycle, duration, flow, LMP), obstetric history, contraception, discharge, urinary symptoms, last Pap smear.",
            "General and breast examination; abdomen; speculum and bimanual examination as indicated (with a chaperone).",
            "Investigations as ordered.", "Review with reports.", 14,
            ["Irregular periods", "Heavy bleeding", "Painful periods", "White discharge", "Lower abdominal pain", "Infertility", "Menopausal symptoms"],
            ["LAB-CBC", "RAD-USG-PELVIS"], []),
        new("CARD-CHEST", "Cardiology — Chest pain evaluation", "CARD", 10,
            "Character, site, radiation and duration of pain; relation to exertion; sweating, breathlessness, palpitations, syncope. Risk factors: diabetes, hypertension, smoking, dyslipidaemia, family history.",
            "Pulse rate and rhythm, BP in both arms, JVP, heart sounds and murmurs, lung bases, pedal oedema.",
            "ECG within 10 minutes of arrival for acute chest pain. Emergency referral for ongoing pain, ECG changes or instability.",
            "Come immediately for chest pain at rest, pain lasting more than 15 minutes, breathlessness or fainting.", 7,
            ["Chest pain", "Breathlessness", "Palpitations", "Giddiness", "Syncope", "Swelling of feet"],
            ["CARD-ECG", "LAB-CBC", "LAB-LIPID", "LAB-FBS", "CARD-ECHO"], []),
        new("CARD-FU", "Cardiology — Follow-up (CAD / heart failure)", "CARD", 20,
            "Functional class (NYHA), adherence, weight change, salt and fluid intake, bleeding on antiplatelets or anticoagulants.",
            "Weight, BP, pulse, JVP, basal crepitations, pedal oedema.",
            "Salt and fluid advice; daily weight for heart failure.", "Report weight gain above 2 kg in 3 days or increasing breathlessness.", 30,
            ["Routine review", "Breathlessness on exertion", "Chest discomfort", "Swelling of feet", "Palpitations"],
            ["LAB-KFT", "LAB-ELECTROLYTES", "CARD-ECG"], []),
        new("ENT-OPD", "ENT — OP consultation", "ENT", 10,
            "Onset, duration and side; discharge, bleeding, fever; allergy symptoms; change in hearing or voice; smoking.",
            "Otoscopy (both ears), tuning-fork tests, anterior rhinoscopy, oral cavity and oropharynx, indirect laryngoscopy if indicated, neck nodes.",
            "Investigations as ordered.", "Keep ears dry; return for fever, swelling behind the ear or giddiness.", 7,
            ["Ear pain", "Ear discharge", "Hearing loss", "Tinnitus", "Nasal block", "Sneezing", "Sore throat", "Hoarseness", "Vertigo"],
            [], []),
    ];

    public static IReadOnlyList<OrderSetSeed> OrderSets { get; } =
    [
        new("FEVER-WORKUP", "Fever work-up", "GM", "Initial investigations for acute fever.", ["LAB-CBC", "LAB-ESR", "LAB-CRP", "LAB-URINE-RE", "LAB-LFT"]),
        new("DM-REVIEW", "Diabetes review", "GM", "Periodic diabetes control and complication screen.",
            ["LAB-FBS", "LAB-PPBS", "LAB-HBA1C", "LAB-KFT", "LAB-LIPID", "LAB-URINE-RE"]),
        new("CHEST-PAIN", "Chest pain work-up", "CARD", "Initial cardiac evaluation of chest pain.", ["CARD-ECG", "LAB-CBC", "LAB-ELECTROLYTES", "LAB-KFT", "CARD-ECHO"]),
        new("PRE-OP", "Pre-operative panel", null, "Routine pre-anaesthetic investigations.",
            ["LAB-CBC", "LAB-FBS", "LAB-KFT", "LAB-ELECTROLYTES", "LAB-URINE-RE", "CARD-ECG", "RAD-XR-CHEST"]),
        new("ANC-FIRST", "First antenatal visit", "OBG", "Booking-visit investigations.", ["LAB-CBC", "LAB-URINE-RE", "LAB-FBS", "LAB-TFT", "RAD-USG-PELVIS"]),
    ];
}
