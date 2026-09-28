namespace HyFib.HealthCare360.Modules.Clinical.Seed;

/// <summary>
/// Common procedures with their ICD-10-PCS codes (public-domain CMS classification), seeded for coding at discharge and
/// insurance claims. Titles give the everyday Indian name followed by the PCS description so both coders and clinicians
/// find them. MRD maintains and extends the list through the API.
/// </summary>
internal static class ProcedureCodeCatalogue
{
    internal sealed record Entry(string Code, string Title, string Category, string? Keywords = null);

    public static IReadOnlyList<Entry> All { get; } =
    [
        // General surgery
        new("0DTJ4ZZ", "Laparoscopic appendicectomy (resection of appendix, percutaneous endoscopic)", "General surgery", "lap appendectomy"),
        new("0DTJ0ZZ", "Open appendicectomy (resection of appendix, open)", "General surgery", "appendectomy"),
        new("0FT44ZZ", "Laparoscopic cholecystectomy (resection of gallbladder, percutaneous endoscopic)", "General surgery", "lap chole"),
        new("0FT40ZZ", "Open cholecystectomy (resection of gallbladder, open)", "General surgery"),
        new("0YU50JZ", "Inguinal hernioplasty with mesh, right (supplement right inguinal region with synthetic substitute, open)", "General surgery", "hernia repair Lichtenstein"),
        new("0YU60JZ", "Inguinal hernioplasty with mesh, left (supplement left inguinal region with synthetic substitute, open)", "General surgery", "hernia repair Lichtenstein"),
        new("0YUA0JZ", "Bilateral inguinal hernioplasty with mesh (supplement bilateral inguinal region with synthetic substitute, open)", "General surgery", "hernia repair"),
        new("0YU54JZ", "Laparoscopic inguinal hernia repair, right (supplement right inguinal region with synthetic substitute, percutaneous endoscopic)", "General surgery", "TEP TAPP"),
        new("0WUF0JZ", "Ventral/umbilical hernioplasty with mesh (supplement abdominal wall with synthetic substitute, open)", "General surgery", "incisional hernia repair"),
        new("0WJG0ZZ", "Exploratory laparotomy (inspection of peritoneal cavity, open)", "General surgery", "laparotomy"),
        new("07TP0ZZ", "Splenectomy (resection of spleen, open)", "General surgery"),
        new("0GTK0ZZ", "Total thyroidectomy (resection of thyroid gland, open)", "General surgery"),
        new("0HTT0ZZ", "Mastectomy, right (resection of right breast, open)", "General surgery", "MRM"),
        new("0HTU0ZZ", "Mastectomy, left (resection of left breast, open)", "General surgery", "MRM"),
        new("0HBT0ZZ", "Excision of breast lump, right (excision of right breast, open)", "General surgery", "lumpectomy fibroadenoma"),
        new("0HBU0ZZ", "Excision of breast lump, left (excision of left breast, open)", "General surgery", "lumpectomy fibroadenoma"),
        new("0DH64UZ", "Percutaneous endoscopic gastrostomy (insertion of feeding device into stomach, percutaneous endoscopic)", "General surgery", "PEG"),
        new("0B110F4", "Tracheostomy (bypass trachea to cutaneous with tracheostomy device, open)", "General surgery"),
        new("0HQ1XZZ", "Suturing of facial laceration (repair face skin, external)", "General surgery", "suturing CLW"),
        new("0W9G3ZZ", "Abdominal paracentesis (drainage of peritoneal cavity, percutaneous)", "General surgery", "ascitic tap"),
        new("0W9930Z", "Intercostal drain insertion, right (drainage of right pleural cavity with drainage device, percutaneous)", "General surgery", "ICD chest tube"),
        new("0W9B30Z", "Intercostal drain insertion, left (drainage of left pleural cavity with drainage device, percutaneous)", "General surgery", "ICD chest tube"),
        new("0W993ZZ", "Therapeutic thoracocentesis, right (drainage of right pleural cavity, percutaneous)", "General surgery", "pleural tap"),
        new("0W9D3ZZ", "Pericardiocentesis (drainage of pericardial cavity, percutaneous)", "Cardiology", "pericardial tap"),

        // Obstetrics and gynaecology
        new("10D00Z1", "Lower segment caesarean section (extraction of products of conception, low, open)", "Obstetrics", "LSCS C-section"),
        new("10D00Z0", "Classical caesarean section (extraction of products of conception, high, open)", "Obstetrics", "C-section"),
        new("10E0XZZ", "Normal vaginal delivery (delivery of products of conception, external)", "Obstetrics", "NVD"),
        new("0W8NXZZ", "Episiotomy (division of female perineum, external)", "Obstetrics"),
        new("10D07Z3", "Low forceps delivery (extraction of products of conception, low forceps, via natural opening)", "Obstetrics", "forceps"),
        new("10D07Z6", "Vacuum-assisted delivery (extraction of products of conception, vacuum, via natural opening)", "Obstetrics", "ventouse"),
        new("10D17ZZ", "Manual removal of retained placenta (extraction of retained products of conception, via natural opening)", "Obstetrics", "MRP"),
        new("10A07Z6", "Suction evacuation / medical termination of pregnancy (abortion of products of conception, vacuum)", "Obstetrics", "MTP suction evacuation"),
        new("10903ZU", "Amniocentesis (drainage of amniotic fluid, diagnostic, percutaneous)", "Obstetrics"),
        new("0UDB7ZZ", "Dilatation and curettage (extraction of endometrium, via natural opening)", "Gynaecology", "D&C"),
        new("0UT90ZZ", "Total abdominal hysterectomy (resection of uterus, open)", "Gynaecology", "TAH"),
        new("0UT94ZZ", "Total laparoscopic hysterectomy (resection of uterus, percutaneous endoscopic)", "Gynaecology", "TLH"),
        new("0UT97ZZ", "Vaginal hysterectomy (resection of uterus, via natural opening)", "Gynaecology", "VH"),
        new("0UL74ZZ", "Laparoscopic tubal ligation (occlusion of bilateral fallopian tubes, percutaneous endoscopic)", "Gynaecology", "sterilisation tubectomy"),
        new("0UB04ZZ", "Laparoscopic ovarian cystectomy, right (excision of right ovary, percutaneous endoscopic)", "Gynaecology"),
        new("0UJD8ZZ", "Diagnostic hysteroscopy (inspection of uterus and cervix, endoscopic)", "Gynaecology"),
        new("0UH97HZ", "Intrauterine contraceptive device insertion (insertion of contraceptive device into uterus)", "Gynaecology", "IUCD Copper-T"),
        new("0UVC7ZZ", "Cervical cerclage (restriction of cervix, via natural opening)", "Obstetrics", "McDonald stitch"),

        // Orthopaedics
        new("0SRC0J9", "Total knee replacement, right, cemented (replacement of right knee joint with synthetic substitute)", "Orthopaedics", "TKR TKA"),
        new("0SRD0J9", "Total knee replacement, left, cemented (replacement of left knee joint with synthetic substitute)", "Orthopaedics", "TKR TKA"),
        new("0SR90J9", "Total hip replacement, right, cemented (replacement of right hip joint with synthetic substitute)", "Orthopaedics", "THR THA"),
        new("0SRB0J9", "Total hip replacement, left, cemented (replacement of left hip joint with synthetic substitute)", "Orthopaedics", "THR THA"),
        new("0SRR0J9", "Hemiarthroplasty, right hip, cemented (replacement of right hip joint, femoral surface)", "Orthopaedics", "bipolar hemiarthroplasty"),
        new("0SJC4ZZ", "Diagnostic knee arthroscopy, right (inspection of right knee joint, percutaneous endoscopic)", "Orthopaedics"),
        new("0PSHXZZ", "Closed reduction of distal radius fracture, right (reposition right radius, external)", "Orthopaedics", "CRPOP Colles"),
        new("01N50ZZ", "Carpal tunnel release (release median nerve, open)", "Orthopaedics", "CTR"),

        // Urology and nephrology
        new("0VT08ZZ", "Transurethral resection of prostate (resection of prostate, endoscopic)", "Urology", "TURP"),
        new("0TJB8ZZ", "Cystoscopy (inspection of bladder, endoscopic)", "Urology"),
        new("0TF68ZZ", "Ureteroscopic lithotripsy, right (fragmentation in right ureter, endoscopic)", "Urology", "URSL"),
        new("0T9B70Z", "Urinary catheterisation (drainage of bladder with drainage device)", "Urology", "Foley catheter"),
        new("0VTTXZZ", "Circumcision (resection of prepuce, external)", "Urology"),
        new("0VBQ0ZZ", "Vasectomy (excision of bilateral vas deferens, open)", "Urology", "NSV"),
        new("0TT00ZZ", "Nephrectomy, right (resection of right kidney, open)", "Urology"),
        new("031C0ZF", "Arteriovenous fistula for dialysis, left radiocephalic (bypass left radial artery to lower arm vein, open)", "Nephrology", "AV fistula AVF"),
        new("5A1D70Z", "Haemodialysis, intermittent (performance of urinary filtration, less than 6 hours per day)", "Nephrology", "hemodialysis HD"),
        new("0TB03ZX", "Percutaneous renal biopsy, right (excision of right kidney, percutaneous, diagnostic)", "Nephrology", "kidney biopsy"),

        // Cardiology
        new("B2111ZZ", "Coronary angiography (fluoroscopy of multiple coronary arteries, low osmolar contrast)", "Cardiology", "CAG"),
        new("027034Z", "PTCA with drug-eluting stent, one coronary artery (dilation of coronary artery, percutaneous)", "Cardiology", "angioplasty PCI stent"),
        new("02100Z9", "Coronary artery bypass graft, LIMA to one artery (bypass coronary artery from left internal mammary, open)", "Cardiology", "CABG"),
        new("0JH606Z", "Permanent pacemaker implantation, dual chamber (insertion of pacemaker into chest subcutaneous tissue)", "Cardiology", "PPI DDD pacemaker"),
        new("4A02X4Z", "Electrocardiogram (measurement of cardiac electrical activity, external)", "Cardiology", "ECG EKG"),
        new("5A2204Z", "Cardioversion / defibrillation (restoration of cardiac rhythm, single)", "Cardiology", "DC shock"),
        new("5A12012", "Cardiopulmonary resuscitation (performance of cardiac output, single, manual)", "Critical care", "CPR"),
        new("3E03317", "Thrombolysis (introduction of other thrombolytic into peripheral vein)", "Cardiology", "streptokinase tenecteplase"),

        // Gastroenterology
        new("0DJ08ZZ", "Upper GI endoscopy (inspection of upper intestinal tract, endoscopic)", "Gastroenterology", "UGIE OGD EGD"),
        new("0DJD8ZZ", "Colonoscopy (inspection of lower intestinal tract, endoscopic)", "Gastroenterology"),
        new("0DB68ZX", "Endoscopic gastric biopsy (excision of stomach, endoscopic, diagnostic)", "Gastroenterology"),
        new("0DH67UZ", "Nasogastric tube insertion (insertion of feeding device into stomach, via natural opening)", "Gastroenterology", "Ryle tube NG tube"),
        new("0FB03ZX", "Percutaneous liver biopsy (excision of liver, percutaneous, diagnostic)", "Gastroenterology"),

        // Respiratory and critical care
        new("0BJ08ZZ", "Bronchoscopy (inspection of tracheobronchial tree, endoscopic)", "Pulmonology"),
        new("0BH17EZ", "Endotracheal intubation (insertion of endotracheal airway into trachea)", "Critical care", "ET tube intubation"),
        new("5A1935Z", "Mechanical ventilation, less than 24 consecutive hours", "Critical care", "ventilator"),
        new("5A1945Z", "Mechanical ventilation, 24 to 96 consecutive hours", "Critical care", "ventilator"),
        new("5A1955Z", "Mechanical ventilation, more than 96 consecutive hours", "Critical care", "ventilator"),
        new("05HM33Z", "Central venous catheter, right internal jugular (insertion of infusion device, percutaneous)", "Critical care", "CVC central line"),

        // Neurosurgery and neurology
        new("009U3ZX", "Diagnostic lumbar puncture (drainage of spinal canal, percutaneous, diagnostic)", "Neurology", "LP CSF"),
        new("00C40ZZ", "Craniotomy and evacuation of subdural haematoma (extirpation of matter from subdural space, open)", "Neurosurgery", "SDH evacuation"),

        // ENT, ophthalmology and dental
        new("0CTPXZZ", "Tonsillectomy (resection of tonsils, external)", "ENT"),
        new("0CTQXZZ", "Adenoidectomy (resection of adenoids, external)", "ENT"),
        new("08RJ3JZ", "Cataract surgery with intraocular lens, right eye (replacement of right lens with synthetic substitute)", "Ophthalmology", "phaco IOL SICS"),
        new("08RK3JZ", "Cataract surgery with intraocular lens, left eye (replacement of left lens with synthetic substitute)", "Ophthalmology", "phaco IOL SICS"),
        new("0CDXXZ0", "Dental extraction, single lower tooth (extraction of lower tooth, single, external)", "Dental", "tooth extraction"),

        // Transfusion, infusions and other
        new("30233N1", "Packed red cell transfusion (transfusion of nonautologous red blood cells into peripheral vein)", "Transfusion", "PRBC blood transfusion"),
        new("3E03305", "Chemotherapy infusion (introduction of other antineoplastic into peripheral vein)", "Oncology", "chemo"),
        new("3E03329", "Intravenous antibiotic administration (introduction of other anti-infective into peripheral vein)", "Medicine", "IV antibiotics"),
        new("6A601ZZ", "Neonatal phototherapy, multiple sessions (phototherapy of skin, multiple)", "Paediatrics", "phototherapy jaundice"),
        new("07DR3ZX", "Bone marrow biopsy, iliac (extraction of iliac bone marrow, percutaneous, diagnostic)", "Haematology", "BMA BMB"),

        // Imaging
        new("BW03ZZZ", "Chest X-ray (plain radiography of chest)", "Imaging", "CXR"),
        new("BW40ZZZ", "Ultrasonography of abdomen", "Imaging", "USG abdomen"),
        new("B020ZZZ", "CT scan of brain (computerized tomography of brain)", "Imaging", "CT head"),
        new("B030ZZZ", "MRI of brain (magnetic resonance imaging of brain)", "Imaging", "MRI head"),
        new("BH02ZZZ", "Bilateral mammography (plain radiography of bilateral breasts)", "Imaging", "mammogram"),
    ];
}
