using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

internal sealed record TemplateSeed(string Code, string Name, Modality Modality, string? BodyPart, string? Technique, string Findings, string Impression);

/// <summary>
/// Normal-study reporting templates for the common examinations (IMG-03). The radiologist starts from the template and
/// edits only what differs, which speeds reporting and keeps the report structure consistent. "__" marks a measurement
/// to fill in.
/// </summary>
internal static class RadiologyTemplates
{
    public static IReadOnlyList<TemplateSeed> All { get; } =
    [
        new("TPL-DX-CHEST", "Chest X-ray PA — normal", Modality.DX, "Chest",
            "Single frontal (PA) erect view of the chest.",
            "The trachea is central. The cardiac size is within normal limits (cardiothoracic ratio < 0.5). Both hila are normal in size and position. "
            + "Both lung fields are clear with no focal consolidation, collapse, cavity or mass. No pleural effusion or pneumothorax. "
            + "The costophrenic and cardiophrenic angles are clear. Both domes of the diaphragm are normal in position and contour. "
            + "The visualised bony thorax and soft tissues are unremarkable.",
            "No significant abnormality detected in the chest radiograph."),

        new("TPL-DX-LSSPINE", "X-ray lumbosacral spine — normal", Modality.DX, "Lumbosacral spine",
            "AP and lateral views of the lumbosacral spine.",
            "The normal lumbar lordosis is maintained. Vertebral body heights and alignment are normal. The intervertebral disc spaces are preserved. "
            + "No osteophytes, spondylolisthesis or pars defect. Pedicles and posterior elements are intact. Both sacroiliac joints are normal. "
            + "Paravertebral soft tissues are unremarkable.",
            "Normal radiograph of the lumbosacral spine."),

        new("TPL-US-ABDOMEN", "Ultrasound whole abdomen — normal", Modality.US, "Abdomen",
            "Real-time B-mode ultrasound of the abdomen and pelvis with a curvilinear transducer.",
            "Liver: normal in size (__ cm) and echotexture; no focal lesion; intrahepatic biliary radicles are not dilated; portal vein normal in calibre.\n"
            + "Gall bladder: well distended; wall not thickened; no calculus or sludge. CBD: normal in calibre.\n"
            + "Pancreas: normal in size and echotexture where visualised.\n"
            + "Spleen: normal in size (__ cm) and echotexture.\n"
            + "Kidneys: normal in size (right __ cm, left __ cm) with maintained corticomedullary differentiation; no calculus or hydronephrosis.\n"
            + "Urinary bladder: well distended with a normal wall; no calculus.\n"
            + "No free fluid in the abdomen or pelvis. No significant lymphadenopathy.",
            "No significant abnormality detected on ultrasound of the abdomen."),

        new("TPL-US-PELVIS-F", "Ultrasound pelvis (female) — normal", Modality.US, "Pelvis",
            "Transabdominal ultrasound of the pelvis with a full bladder.",
            "Uterus: anteverted, normal in size (__ x __ x __ cm) with homogeneous myometrium; no focal lesion. Endometrial thickness __ mm.\n"
            + "Ovaries: both normal in size and echotexture (right __ x __ cm, left __ x __ cm); no cyst or mass.\n"
            + "No adnexal mass. No free fluid in the pouch of Douglas. Urinary bladder is well distended with a normal wall.",
            "Normal pelvic ultrasound."),

        new("TPL-CT-HEAD", "CT brain (plain) — normal", Modality.CT, "Head",
            "Non-contrast axial CT of the brain from the skull base to the vertex, reviewed in brain and bone windows.",
            "No intra-axial or extra-axial haemorrhage. No acute infarct or mass lesion. Grey-white matter differentiation is preserved. "
            + "The ventricles and basal cisterns are normal in size and configuration. No midline shift. The posterior fossa structures are normal. "
            + "The calvarium and skull base are intact. Visualised paranasal sinuses and mastoid air cells are clear.",
            "No acute intracranial abnormality."),

        new("TPL-CT-ABDOMEN", "CECT abdomen and pelvis — normal", Modality.CT, "Abdomen",
            "Contrast-enhanced axial CT of the abdomen and pelvis in the portal venous phase after intravenous iodinated contrast; multiplanar reconstructions reviewed.",
            "The liver is normal in size with homogeneous enhancement and no focal lesion. The gall bladder, biliary tree and pancreas are normal. "
            + "The spleen and both adrenal glands are normal. Both kidneys enhance and excrete contrast normally with no calculus, mass or hydronephrosis. "
            + "Bowel loops are normal in calibre without wall thickening. No collection or free fluid. No significant lymphadenopathy. "
            + "The urinary bladder and pelvic organs are unremarkable. The aorta and IVC are normal in calibre. Visualised bones and lung bases are unremarkable.",
            "No significant abnormality detected on contrast-enhanced CT of the abdomen and pelvis."),

        new("TPL-MR-BRAIN", "MRI brain (plain) — normal", Modality.MR, "Brain",
            "Multiplanar, multisequence MRI of the brain: T1, T2, FLAIR, DWI/ADC and SWI.",
            "The brain parenchyma shows normal signal intensity. No restricted diffusion to suggest an acute infarct. No haemorrhage or blooming on SWI. "
            + "No mass lesion or midline shift. The ventricles, sulci and cisterns are normal for age. The corpus callosum, brainstem and cerebellum are normal. "
            + "The pituitary gland and sellar region are unremarkable. Major intracranial flow voids are preserved. Orbits, paranasal sinuses and mastoids are clear.",
            "Normal MRI of the brain."),

        new("TPL-MR-LSSPINE", "MRI lumbosacral spine", Modality.MR, "Lumbosacral spine",
            "Sagittal T1, T2 and STIR with axial T2 images of the lumbosacral spine.",
            "Lumbar lordosis is maintained. Vertebral bodies are normal in height and marrow signal. Discs show normal hydration and height.\n"
            + "L3-L4: no disc bulge or canal stenosis.\n"
            + "L4-L5: no disc bulge; neural foramina are patent.\n"
            + "L5-S1: no disc bulge; traversing and exiting nerve roots are free.\n"
            + "The conus medullaris ends at L1 with normal signal. Facet joints and ligamenta flava are unremarkable. Paravertebral soft tissues are normal.",
            "No disc herniation, canal stenosis or nerve root compression in the lumbosacral spine."),

        new("TPL-MG-BIRADS", "Mammography (bilateral) — BI-RADS", Modality.MG, "Breast",
            "Bilateral digital mammography, craniocaudal (CC) and mediolateral oblique (MLO) views.",
            "Breast composition: ACR category __.\n"
            + "Right breast: no mass, architectural distortion, asymmetry or suspicious microcalcification.\n"
            + "Left breast: no mass, architectural distortion, asymmetry or suspicious microcalcification.\n"
            + "Skin and nipple-areolar complexes are normal. No axillary lymphadenopathy.",
            "BI-RADS 1: negative. Routine screening mammography is advised as appropriate for age and risk."),
    ];
}
