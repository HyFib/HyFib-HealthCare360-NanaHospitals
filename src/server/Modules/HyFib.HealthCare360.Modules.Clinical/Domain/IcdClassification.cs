namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public sealed record IcdChapter(string Number, string Start, string End, string Title);

public sealed record IcdBlock(string Start, string End, string Title)
{
    public string Range => Start == End ? Start : $"{Start}-{End}";
}

/// <summary>
/// ICD-10 (WHO 2019) chapter and block structure. Used to file every catalogue code under its chapter/block and to
/// group the morbidity index by block (MRD-06). Lookup works on the three-character category, so it also
/// classifies codes cached later from the online ICD-10 service.
/// </summary>
public static class IcdClassification
{
    public static IReadOnlyList<IcdChapter> Chapters { get; } =
    [
        new("I", "A00", "B99", "Certain infectious and parasitic diseases"),
        new("II", "C00", "D48", "Neoplasms"),
        new("III", "D50", "D89", "Diseases of the blood and blood-forming organs and certain disorders involving the immune mechanism"),
        new("IV", "E00", "E90", "Endocrine, nutritional and metabolic diseases"),
        new("V", "F00", "F99", "Mental and behavioural disorders"),
        new("VI", "G00", "G99", "Diseases of the nervous system"),
        new("VII", "H00", "H59", "Diseases of the eye and adnexa"),
        new("VIII", "H60", "H95", "Diseases of the ear and mastoid process"),
        new("IX", "I00", "I99", "Diseases of the circulatory system"),
        new("X", "J00", "J99", "Diseases of the respiratory system"),
        new("XI", "K00", "K93", "Diseases of the digestive system"),
        new("XII", "L00", "L99", "Diseases of the skin and subcutaneous tissue"),
        new("XIII", "M00", "M99", "Diseases of the musculoskeletal system and connective tissue"),
        new("XIV", "N00", "N99", "Diseases of the genitourinary system"),
        new("XV", "O00", "O99", "Pregnancy, childbirth and the puerperium"),
        new("XVI", "P00", "P96", "Certain conditions originating in the perinatal period"),
        new("XVII", "Q00", "Q99", "Congenital malformations, deformations and chromosomal abnormalities"),
        new("XVIII", "R00", "R99", "Symptoms, signs and abnormal clinical and laboratory findings, not elsewhere classified"),
        new("XIX", "S00", "T98", "Injury, poisoning and certain other consequences of external causes"),
        new("XX", "V01", "Y98", "External causes of morbidity and mortality"),
        new("XXI", "Z00", "Z99", "Factors influencing health status and contact with health services"),
        new("XXII", "U00", "U85", "Codes for special purposes"),
    ];

    public static IReadOnlyList<IcdBlock> Blocks { get; } =
    [
        // I
        new("A00", "A09", "Intestinal infectious diseases"),
        new("A15", "A19", "Tuberculosis"),
        new("A20", "A28", "Certain zoonotic bacterial diseases"),
        new("A30", "A49", "Other bacterial diseases"),
        new("A50", "A64", "Infections with a predominantly sexual mode of transmission"),
        new("A65", "A69", "Other spirochaetal diseases"),
        new("A70", "A74", "Other diseases caused by chlamydiae"),
        new("A75", "A79", "Rickettsioses"),
        new("A80", "A89", "Viral infections of the central nervous system"),
        new("A90", "A99", "Arthropod-borne viral fevers and viral haemorrhagic fevers"),
        new("B00", "B09", "Viral infections characterized by skin and mucous membrane lesions"),
        new("B15", "B19", "Viral hepatitis"),
        new("B20", "B24", "Human immunodeficiency virus [HIV] disease"),
        new("B25", "B34", "Other viral diseases"),
        new("B35", "B49", "Mycoses"),
        new("B50", "B64", "Protozoal diseases"),
        new("B65", "B83", "Helminthiases"),
        new("B85", "B89", "Pediculosis, acariasis and other infestations"),
        new("B90", "B94", "Sequelae of infectious and parasitic diseases"),
        new("B95", "B98", "Bacterial, viral and other infectious agents"),
        new("B99", "B99", "Other infectious diseases"),

        // II
        new("C00", "C14", "Malignant neoplasms of lip, oral cavity and pharynx"),
        new("C15", "C26", "Malignant neoplasms of digestive organs"),
        new("C30", "C39", "Malignant neoplasms of respiratory and intrathoracic organs"),
        new("C40", "C41", "Malignant neoplasms of bone and articular cartilage"),
        new("C43", "C44", "Melanoma and other malignant neoplasms of skin"),
        new("C45", "C49", "Malignant neoplasms of mesothelial and soft tissue"),
        new("C50", "C50", "Malignant neoplasm of breast"),
        new("C51", "C58", "Malignant neoplasms of female genital organs"),
        new("C60", "C63", "Malignant neoplasms of male genital organs"),
        new("C64", "C68", "Malignant neoplasms of urinary tract"),
        new("C69", "C72", "Malignant neoplasms of eye, brain and other parts of central nervous system"),
        new("C73", "C75", "Malignant neoplasms of thyroid and other endocrine glands"),
        new("C76", "C80", "Malignant neoplasms of ill-defined, secondary and unspecified sites"),
        new("C81", "C96", "Malignant neoplasms of lymphoid, haematopoietic and related tissue"),
        new("C97", "C97", "Malignant neoplasms of independent (primary) multiple sites"),
        new("D00", "D09", "In situ neoplasms"),
        new("D10", "D36", "Benign neoplasms"),
        new("D37", "D48", "Neoplasms of uncertain or unknown behaviour"),

        // III
        new("D50", "D53", "Nutritional anaemias"),
        new("D55", "D59", "Haemolytic anaemias"),
        new("D60", "D64", "Aplastic and other anaemias"),
        new("D65", "D69", "Coagulation defects, purpura and other haemorrhagic conditions"),
        new("D70", "D77", "Other diseases of blood and blood-forming organs"),
        new("D80", "D89", "Certain disorders involving the immune mechanism"),

        // IV
        new("E00", "E07", "Disorders of thyroid gland"),
        new("E10", "E14", "Diabetes mellitus"),
        new("E15", "E16", "Other disorders of glucose regulation and pancreatic internal secretion"),
        new("E20", "E35", "Disorders of other endocrine glands"),
        new("E40", "E46", "Malnutrition"),
        new("E50", "E64", "Other nutritional deficiencies"),
        new("E65", "E68", "Obesity and other hyperalimentation"),
        new("E70", "E90", "Metabolic disorders"),

        // V
        new("F00", "F09", "Organic, including symptomatic, mental disorders"),
        new("F10", "F19", "Mental and behavioural disorders due to psychoactive substance use"),
        new("F20", "F29", "Schizophrenia, schizotypal and delusional disorders"),
        new("F30", "F39", "Mood [affective] disorders"),
        new("F40", "F48", "Neurotic, stress-related and somatoform disorders"),
        new("F50", "F59", "Behavioural syndromes associated with physiological disturbances and physical factors"),
        new("F60", "F69", "Disorders of adult personality and behaviour"),
        new("F70", "F79", "Mental retardation"),
        new("F80", "F89", "Disorders of psychological development"),
        new("F90", "F98", "Behavioural and emotional disorders with onset usually occurring in childhood and adolescence"),
        new("F99", "F99", "Unspecified mental disorder"),

        // VI
        new("G00", "G09", "Inflammatory diseases of the central nervous system"),
        new("G10", "G14", "Systemic atrophies primarily affecting the central nervous system"),
        new("G20", "G26", "Extrapyramidal and movement disorders"),
        new("G30", "G32", "Other degenerative diseases of the nervous system"),
        new("G35", "G37", "Demyelinating diseases of the central nervous system"),
        new("G40", "G47", "Episodic and paroxysmal disorders"),
        new("G50", "G59", "Nerve, nerve root and plexus disorders"),
        new("G60", "G64", "Polyneuropathies and other disorders of the peripheral nervous system"),
        new("G70", "G73", "Diseases of myoneural junction and muscle"),
        new("G80", "G83", "Cerebral palsy and other paralytic syndromes"),
        new("G90", "G99", "Other disorders of the nervous system"),

        // VII
        new("H00", "H06", "Disorders of eyelid, lacrimal system and orbit"),
        new("H10", "H13", "Disorders of conjunctiva"),
        new("H15", "H22", "Disorders of sclera, cornea, iris and ciliary body"),
        new("H25", "H28", "Disorders of lens"),
        new("H30", "H36", "Disorders of choroid and retina"),
        new("H40", "H42", "Glaucoma"),
        new("H43", "H45", "Disorders of vitreous body and globe"),
        new("H46", "H48", "Disorders of optic nerve and visual pathways"),
        new("H49", "H52", "Disorders of ocular muscles, binocular movement, accommodation and refraction"),
        new("H53", "H54", "Visual disturbances and blindness"),
        new("H55", "H59", "Other disorders of eye and adnexa"),

        // VIII
        new("H60", "H62", "Diseases of external ear"),
        new("H65", "H75", "Diseases of middle ear and mastoid"),
        new("H80", "H83", "Diseases of inner ear"),
        new("H90", "H95", "Other disorders of ear"),

        // IX
        new("I00", "I02", "Acute rheumatic fever"),
        new("I05", "I09", "Chronic rheumatic heart diseases"),
        new("I10", "I15", "Hypertensive diseases"),
        new("I20", "I25", "Ischaemic heart diseases"),
        new("I26", "I28", "Pulmonary heart disease and diseases of pulmonary circulation"),
        new("I30", "I52", "Other forms of heart disease"),
        new("I60", "I69", "Cerebrovascular diseases"),
        new("I70", "I79", "Diseases of arteries, arterioles and capillaries"),
        new("I80", "I89", "Diseases of veins, lymphatic vessels and lymph nodes, not elsewhere classified"),
        new("I95", "I99", "Other and unspecified disorders of the circulatory system"),

        // X
        new("J00", "J06", "Acute upper respiratory infections"),
        new("J09", "J18", "Influenza and pneumonia"),
        new("J20", "J22", "Other acute lower respiratory infections"),
        new("J30", "J39", "Other diseases of upper respiratory tract"),
        new("J40", "J47", "Chronic lower respiratory diseases"),
        new("J60", "J70", "Lung diseases due to external agents"),
        new("J80", "J84", "Other respiratory diseases principally affecting the interstitium"),
        new("J85", "J86", "Suppurative and necrotic conditions of lower respiratory tract"),
        new("J90", "J94", "Other diseases of pleura"),
        new("J95", "J99", "Other diseases of the respiratory system"),

        // XI
        new("K00", "K14", "Diseases of oral cavity, salivary glands and jaws"),
        new("K20", "K31", "Diseases of oesophagus, stomach and duodenum"),
        new("K35", "K38", "Diseases of appendix"),
        new("K40", "K46", "Hernia"),
        new("K50", "K52", "Noninfective enteritis and colitis"),
        new("K55", "K64", "Other diseases of intestines"),
        new("K65", "K67", "Diseases of peritoneum"),
        new("K70", "K77", "Diseases of liver"),
        new("K80", "K87", "Disorders of gallbladder, biliary tract and pancreas"),
        new("K90", "K93", "Other diseases of the digestive system"),

        // XII
        new("L00", "L08", "Infections of the skin and subcutaneous tissue"),
        new("L10", "L14", "Bullous disorders"),
        new("L20", "L30", "Dermatitis and eczema"),
        new("L40", "L45", "Papulosquamous disorders"),
        new("L50", "L54", "Urticaria and erythema"),
        new("L55", "L59", "Radiation-related disorders of the skin and subcutaneous tissue"),
        new("L60", "L75", "Disorders of skin appendages"),
        new("L80", "L99", "Other disorders of the skin and subcutaneous tissue"),

        // XIII
        new("M00", "M25", "Arthropathies"),
        new("M30", "M36", "Systemic connective tissue disorders"),
        new("M40", "M54", "Dorsopathies"),
        new("M60", "M79", "Soft tissue disorders"),
        new("M80", "M94", "Osteopathies and chondropathies"),
        new("M95", "M99", "Other disorders of the musculoskeletal system and connective tissue"),

        // XIV
        new("N00", "N08", "Glomerular diseases"),
        new("N10", "N16", "Renal tubulo-interstitial diseases"),
        new("N17", "N19", "Renal failure"),
        new("N20", "N23", "Urolithiasis"),
        new("N25", "N29", "Other disorders of kidney and ureter"),
        new("N30", "N39", "Other diseases of urinary system"),
        new("N40", "N51", "Diseases of male genital organs"),
        new("N60", "N64", "Disorders of breast"),
        new("N70", "N77", "Inflammatory diseases of female pelvic organs"),
        new("N80", "N98", "Noninflammatory disorders of female genital tract"),
        new("N99", "N99", "Other disorders of the genitourinary system"),

        // XV
        new("O00", "O08", "Pregnancy with abortive outcome"),
        new("O10", "O16", "Oedema, proteinuria and hypertensive disorders in pregnancy, childbirth and the puerperium"),
        new("O20", "O29", "Other maternal disorders predominantly related to pregnancy"),
        new("O30", "O48", "Maternal care related to the fetus and amniotic cavity and possible delivery problems"),
        new("O60", "O75", "Complications of labour and delivery"),
        new("O80", "O84", "Delivery"),
        new("O85", "O92", "Complications predominantly related to the puerperium"),
        new("O94", "O99", "Other obstetric conditions, not elsewhere classified"),

        // XVI
        new("P00", "P04", "Fetus and newborn affected by maternal factors and by complications of pregnancy, labour and delivery"),
        new("P05", "P08", "Disorders related to length of gestation and fetal growth"),
        new("P10", "P15", "Birth trauma"),
        new("P20", "P29", "Respiratory and cardiovascular disorders specific to the perinatal period"),
        new("P35", "P39", "Infections specific to the perinatal period"),
        new("P50", "P61", "Haemorrhagic and haematological disorders of fetus and newborn"),
        new("P70", "P74", "Transitory endocrine and metabolic disorders specific to fetus and newborn"),
        new("P75", "P78", "Digestive system disorders of fetus and newborn"),
        new("P80", "P83", "Conditions involving the integument and temperature regulation of fetus and newborn"),
        new("P90", "P96", "Other disorders originating in the perinatal period"),

        // XVII
        new("Q00", "Q07", "Congenital malformations of the nervous system"),
        new("Q10", "Q18", "Congenital malformations of eye, ear, face and neck"),
        new("Q20", "Q28", "Congenital malformations of the circulatory system"),
        new("Q30", "Q34", "Congenital malformations of the respiratory system"),
        new("Q35", "Q37", "Cleft lip and cleft palate"),
        new("Q38", "Q45", "Other congenital malformations of the digestive system"),
        new("Q50", "Q56", "Congenital malformations of genital organs"),
        new("Q60", "Q64", "Congenital malformations of the urinary system"),
        new("Q65", "Q79", "Congenital malformations and deformations of the musculoskeletal system"),
        new("Q80", "Q89", "Other congenital malformations"),
        new("Q90", "Q99", "Chromosomal abnormalities, not elsewhere classified"),

        // XVIII
        new("R00", "R09", "Symptoms and signs involving the circulatory and respiratory systems"),
        new("R10", "R19", "Symptoms and signs involving the digestive system and abdomen"),
        new("R20", "R23", "Symptoms and signs involving the skin and subcutaneous tissue"),
        new("R25", "R29", "Symptoms and signs involving the nervous and musculoskeletal systems"),
        new("R30", "R39", "Symptoms and signs involving the urinary system"),
        new("R40", "R46", "Symptoms and signs involving cognition, perception, emotional state and behaviour"),
        new("R47", "R49", "Symptoms and signs involving speech and voice"),
        new("R50", "R69", "General symptoms and signs"),
        new("R70", "R79", "Abnormal findings on examination of blood, without diagnosis"),
        new("R80", "R82", "Abnormal findings on examination of urine, without diagnosis"),
        new("R83", "R89", "Abnormal findings on examination of other body fluids, substances and tissues, without diagnosis"),
        new("R90", "R94", "Abnormal findings on diagnostic imaging and in function studies, without diagnosis"),
        new("R95", "R99", "Ill-defined and unknown causes of mortality"),

        // XIX
        new("S00", "S09", "Injuries to the head"),
        new("S10", "S19", "Injuries to the neck"),
        new("S20", "S29", "Injuries to the thorax"),
        new("S30", "S39", "Injuries to the abdomen, lower back, lumbar spine and pelvis"),
        new("S40", "S49", "Injuries to the shoulder and upper arm"),
        new("S50", "S59", "Injuries to the elbow and forearm"),
        new("S60", "S69", "Injuries to the wrist and hand"),
        new("S70", "S79", "Injuries to the hip and thigh"),
        new("S80", "S89", "Injuries to the knee and lower leg"),
        new("S90", "S99", "Injuries to the ankle and foot"),
        new("T00", "T07", "Injuries involving multiple body regions"),
        new("T08", "T14", "Injuries to unspecified part of trunk, limb or body region"),
        new("T15", "T19", "Effects of foreign body entering through natural orifice"),
        new("T20", "T32", "Burns and corrosions"),
        new("T33", "T35", "Frostbite"),
        new("T36", "T50", "Poisoning by drugs, medicaments and biological substances"),
        new("T51", "T65", "Toxic effects of substances chiefly nonmedicinal as to source"),
        new("T66", "T78", "Other and unspecified effects of external causes"),
        new("T79", "T79", "Certain early complications of trauma"),
        new("T80", "T88", "Complications of surgical and medical care, not elsewhere classified"),
        new("T90", "T98", "Sequelae of injuries, of poisoning and of other consequences of external causes"),

        // XX
        new("V01", "V99", "Transport accidents"),
        new("W00", "X59", "Other external causes of accidental injury"),
        new("X60", "X84", "Intentional self-harm"),
        new("X85", "Y09", "Assault"),
        new("Y10", "Y34", "Event of undetermined intent"),
        new("Y35", "Y36", "Legal intervention and operations of war"),
        new("Y40", "Y84", "Complications of medical and surgical care"),
        new("Y85", "Y89", "Sequelae of external causes of morbidity and mortality"),
        new("Y90", "Y98", "Supplementary factors related to causes of morbidity and mortality classified elsewhere"),

        // XXI
        new("Z00", "Z13", "Persons encountering health services for examination and investigation"),
        new("Z20", "Z29", "Persons with potential health hazards related to communicable diseases"),
        new("Z30", "Z39", "Persons encountering health services in circumstances related to reproduction"),
        new("Z40", "Z54", "Persons encountering health services for specific procedures and health care"),
        new("Z55", "Z65", "Persons with potential health hazards related to socioeconomic and psychosocial circumstances"),
        new("Z70", "Z76", "Persons encountering health services in other circumstances"),
        new("Z80", "Z99", "Persons with potential health hazards related to family and personal history and certain conditions influencing health status"),

        // XXII
        new("U00", "U49", "Provisional assignment of new diseases of uncertain etiology or emergency use"),
        new("U82", "U85", "Resistance to antimicrobial and antineoplastic drugs"),
    ];

    /// <summary>Three-character category of a code ("E11.9" → "E11"); null when the text is not an ICD-10 code.</summary>
    public static string? CategoryOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim().ToUpperInvariant();
        return trimmed.Length >= 3 && char.IsAsciiLetterUpper(trimmed[0]) && char.IsAsciiDigit(trimmed[1]) && char.IsAsciiDigit(trimmed[2])
            ? trimmed[..3]
            : null;
    }

    public static IcdChapter? ChapterOf(string? code) =>
        CategoryOf(code) is { } category ? Chapters.FirstOrDefault(c => InRange(category, c.Start, c.End)) : null;

    public static IcdBlock? BlockOf(string? code) =>
        CategoryOf(code) is { } category ? Blocks.FirstOrDefault(b => InRange(category, b.Start, b.End)) : null;

    private static bool InRange(string category, string start, string end) =>
        string.CompareOrdinal(category, start) >= 0 && string.CompareOrdinal(category, end) <= 0;
}
