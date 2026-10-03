using System.IO.Compression;
using System.Text;

namespace HyFib.HealthCare360.Modules.Clinical.Tests.Catalogue;

/// <summary>
/// Small hand-made files in the shape of WHO's ICD-10 ClaML and the CMS ICD-10-PCS order file. They are not WHO or CMS
/// files: titles are shortened and the structure is chosen to exercise each rule (a DOCTYPE naming an external DTD,
/// shared fourth and fifth characters, nested modifier classes, a restricted modifier, an excluded modifier, dagger and
/// asterisk codes, references inside labels, nested blocks).
/// </summary>
internal static class CatalogueSamples
{
    public const string Claml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE ClaML SYSTEM "ClaML.dtd">
        <ClaML version="2.0.0">
          <Meta name="lang" value="en"/>
          <Identifier authority="WHO" uid="sample"/>
          <Title name="ICD-10" version="2019" date="2019-01-01">International Statistical Classification of Diseases and Related Health Problems 10th Revision</Title>
          <ClassKinds>
            <ClassKind name="chapter"/>
            <ClassKind name="block"/>
            <ClassKind name="category"/>
          </ClassKinds>
          <UsageKinds>
            <UsageKind name="aster" mark="*"/>
            <UsageKind name="dagger" mark="†"/>
          </UsageKinds>
          <RubricKinds>
            <RubricKind name="preferred"/>
            <RubricKind name="inclusion"/>
            <RubricKind name="exclusion"/>
          </RubricKinds>

          <Modifier code="S04E10_4">
            <SubClass code=".0"/>
            <SubClass code=".2"/>
            <SubClass code=".9"/>
            <Rubric kind="text"><Label xml:lang="en">The following fourth-character subdivisions are for use with categories E10-E14</Label></Rubric>
          </Modifier>
          <ModifierClass modifier="S04E10_4" code=".0">
            <SuperClass code="S04E10_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">With coma</Label></Rubric>
          </ModifierClass>
          <ModifierClass modifier="S04E10_4" code=".2" usage="dagger">
            <SuperClass code="S04E10_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">With kidney complications</Label></Rubric>
            <Rubric kind="inclusion"><Label xml:lang="en">Diabetic nephropathy (<Reference usage="aster">N08.3</Reference>)</Label></Rubric>
          </ModifierClass>
          <ModifierClass modifier="S04E10_4" code=".9">
            <SuperClass code="S04E10_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">Without complications</Label></Rubric>
          </ModifierClass>

          <Modifier code="S05F10_4">
            <SubClass code=".2"/>
            <SubClass code=".9"/>
          </Modifier>
          <ModifierClass modifier="S05F10_4" code=".2">
            <SuperClass code="S05F10_4"/>
            <SubClass code=".20"/>
            <SubClass code=".21"/>
            <Rubric kind="preferred"><Label xml:lang="en">Dependence syndrome</Label></Rubric>
          </ModifierClass>
          <ModifierClass modifier="S05F10_4" code=".20">
            <SuperClass code=".2"/>
            <Rubric kind="preferred"><Label xml:lang="en">Currently abstinent</Label></Rubric>
          </ModifierClass>
          <ModifierClass modifier="S05F10_4" code=".21">
            <SuperClass code=".2"/>
            <Rubric kind="preferred"><Label xml:lang="en">Currently abstinent, but in a protected environment</Label></Rubric>
          </ModifierClass>
          <ModifierClass modifier="S05F10_4" code=".9">
            <SuperClass code="S05F10_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">Unspecified mental and behavioural disorder</Label></Rubric>
          </ModifierClass>

          <Modifier code="S13M00_5">
            <SubClass code="0"/>
            <SubClass code="6"/>
          </Modifier>
          <ModifierClass modifier="S13M00_5" code="0">
            <SuperClass code="S13M00_5"/>
            <Rubric kind="preferred"><Label xml:lang="en">Multiple sites</Label></Rubric>
          </ModifierClass>
          <ModifierClass modifier="S13M00_5" code="6">
            <SuperClass code="S13M00_5"/>
            <Rubric kind="preferred"><Label xml:lang="en">Lumbar region</Label></Rubric>
          </ModifierClass>

          <Modifier code="S20W00_4">
            <SubClass code=".0"/>
            <SubClass code=".9"/>
          </Modifier>
          <ModifierClass modifier="S20W00_4" code=".0">
            <SuperClass code="S20W00_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">Home</Label></Rubric>
          </ModifierClass>
          <ModifierClass modifier="S20W00_4" code=".9">
            <SuperClass code="S20W00_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">Unspecified place</Label></Rubric>
          </ModifierClass>

          <Modifier code="S20V01_5">
            <SubClass code="0"/>
            <SubClass code="9"/>
          </Modifier>
          <ModifierClass modifier="S20V01_5" code="0">
            <SuperClass code="S20V01_5"/>
            <Rubric kind="preferred"><Label xml:lang="en">While engaged in sports activity</Label></Rubric>
          </ModifierClass>
          <ModifierClass modifier="S20V01_5" code="9">
            <SuperClass code="S20V01_5"/>
            <Rubric kind="preferred"><Label xml:lang="en">During unspecified activity</Label></Rubric>
          </ModifierClass>

          <Class code="I" kind="chapter">
            <SubClass code="A00-A09"/>
            <SubClass code="A15-A19"/>
            <Rubric kind="preferred"><Label xml:lang="en">Certain infectious and parasitic diseases</Label></Rubric>
          </Class>
          <Class code="A00-A09" kind="block">
            <SuperClass code="I"/>
            <SubClass code="A00"/>
            <Rubric kind="preferred"><Label xml:lang="en">Intestinal infectious diseases</Label></Rubric>
          </Class>
          <Class code="A00" kind="category">
            <SuperClass code="A00-A09"/>
            <SubClass code="A00.0"/>
            <SubClass code="A00.9"/>
            <Rubric kind="preferred"><Label xml:lang="en">Cholera</Label></Rubric>
          </Class>
          <Class code="A00.0" kind="category">
            <SuperClass code="A00"/>
            <Rubric kind="preferred"><Label xml:lang="en">Cholera due to Vibrio cholerae 01, biovar cholerae</Label></Rubric>
            <Rubric kind="inclusion"><Label xml:lang="en">Classical cholera</Label></Rubric>
            <Rubric kind="exclusion"><Label xml:lang="en">Not an inclusion term</Label></Rubric>
          </Class>
          <Class code="A00.9" kind="category">
            <SuperClass code="A00"/>
            <Rubric kind="preferred"><Label xml:lang="fr">Choléra, sans précision</Label><Label xml:lang="en">Cholera, unspecified</Label></Rubric>
          </Class>
          <Class code="A15-A19" kind="block">
            <SuperClass code="I"/>
            <SubClass code="A17"/>
            <Rubric kind="preferred"><Label xml:lang="en">Tuberculosis</Label></Rubric>
          </Class>
          <Class code="A17" kind="category">
            <SuperClass code="A15-A19"/>
            <SubClass code="A17.0"/>
            <Rubric kind="preferred"><Label xml:lang="en">Tuberculosis of nervous system</Label></Rubric>
          </Class>
          <Class code="A17.0" kind="category" usage="dagger">
            <SuperClass code="A17"/>
            <Rubric kind="preferred"><Label xml:lang="en">Tuberculous meningitis <Reference class="in brackets" usage="aster">G01</Reference></Label></Rubric>
            <Rubric kind="inclusion"><Label xml:lang="en"><Fragment>Tuberculosis of meninges</Fragment><Fragment>(cerebral)(spinal)</Fragment></Label></Rubric>
          </Class>

          <Class code="II" kind="chapter">
            <SubClass code="C00-C97"/>
            <Rubric kind="preferred"><Label xml:lang="en">Neoplasms</Label></Rubric>
          </Class>
          <Class code="C00-C97" kind="block">
            <SuperClass code="II"/>
            <SubClass code="C00-C14"/>
            <Rubric kind="preferred"><Label xml:lang="en">Malignant neoplasms</Label></Rubric>
          </Class>
          <Class code="C00-C14" kind="block">
            <SuperClass code="C00-C97"/>
            <SubClass code="C00"/>
            <Rubric kind="preferred"><Label xml:lang="en">Lip, oral cavity and pharynx</Label></Rubric>
          </Class>
          <Class code="C00" kind="category">
            <SuperClass code="C00-C14"/>
            <Rubric kind="preferred"><Label xml:lang="en">Malignant neoplasm of lip</Label></Rubric>
          </Class>

          <Class code="IV" kind="chapter">
            <SubClass code="E10-E14"/>
            <Rubric kind="preferred"><Label xml:lang="en">Endocrine, nutritional and metabolic diseases</Label></Rubric>
          </Class>
          <Class code="E10-E14" kind="block">
            <SuperClass code="IV"/>
            <SubClass code="E10"/>
            <SubClass code="E11"/>
            <SubClass code="E14"/>
            <Rubric kind="preferred"><Label xml:lang="en">Diabetes mellitus</Label></Rubric>
          </Class>
          <Class code="E10" kind="category">
            <SuperClass code="E10-E14"/>
            <ModifiedBy code="S04E10_4" all="true"/>
            <Rubric kind="preferred"><Label xml:lang="en">Type 1 diabetes mellitus</Label></Rubric>
            <Rubric kind="inclusion"><Label xml:lang="en">Juvenile-onset diabetes (mellitus)</Label></Rubric>
          </Class>
          <Class code="E11" kind="category">
            <SuperClass code="E10-E14"/>
            <ModifiedBy code="S04E10_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">Type 2 diabetes mellitus</Label></Rubric>
          </Class>
          <Class code="E14" kind="category">
            <SuperClass code="E10-E14"/>
            <ModifiedBy code="S04E10_4" all="false">
              <ValidModifierClass code=".0"/>
              <ValidModifierClass code=".9"/>
            </ModifiedBy>
            <Rubric kind="preferred"><Label xml:lang="en">Unspecified diabetes mellitus</Label></Rubric>
          </Class>

          <Class code="V" kind="chapter">
            <SubClass code="F10-F19"/>
            <Rubric kind="preferred"><Label xml:lang="en">Mental and behavioural disorders</Label></Rubric>
          </Class>
          <Class code="F10-F19" kind="block">
            <SuperClass code="V"/>
            <SubClass code="F10"/>
            <Rubric kind="preferred"><Label xml:lang="en">Mental and behavioural disorders due to psychoactive substance use</Label></Rubric>
          </Class>
          <Class code="F10" kind="category">
            <SuperClass code="F10-F19"/>
            <ModifiedBy code="S05F10_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">Mental and behavioural disorders due to use of alcohol</Label></Rubric>
          </Class>

          <Class code="VI" kind="chapter">
            <SubClass code="G00-G09"/>
            <Rubric kind="preferred"><Label xml:lang="en">Diseases of the nervous system</Label></Rubric>
          </Class>
          <Class code="G00-G09" kind="block">
            <SuperClass code="VI"/>
            <SubClass code="G01"/>
            <Rubric kind="preferred"><Label xml:lang="en">Inflammatory diseases of the central nervous system</Label></Rubric>
          </Class>
          <Class code="G01" kind="category" usage="aster">
            <SuperClass code="G00-G09"/>
            <Rubric kind="preferred"><Label xml:lang="en">Meningitis in bacterial diseases classified elsewhere</Label></Rubric>
          </Class>

          <Class code="XIII" kind="chapter">
            <SubClass code="M40-M54"/>
            <SubClass code="M95-M99"/>
            <ModifiedBy code="S13M00_5"/>
            <Rubric kind="preferred"><Label xml:lang="en">Diseases of the musculoskeletal system and connective tissue</Label></Rubric>
          </Class>
          <Class code="M40-M54" kind="block">
            <SuperClass code="XIII"/>
            <SubClass code="M45"/>
            <SubClass code="M54"/>
            <Rubric kind="preferred"><Label xml:lang="en">Dorsopathies</Label></Rubric>
          </Class>
          <Class code="M45" kind="category">
            <SuperClass code="M40-M54"/>
            <Rubric kind="preferred"><Label xml:lang="en">Ankylosing spondylitis</Label></Rubric>
          </Class>
          <Class code="M54" kind="category">
            <SuperClass code="M40-M54"/>
            <SubClass code="M54.5"/>
            <Rubric kind="preferred"><Label xml:lang="en">Dorsalgia</Label></Rubric>
          </Class>
          <Class code="M54.5" kind="category">
            <SuperClass code="M54"/>
            <Rubric kind="preferred"><Label xml:lang="en">Low back pain</Label></Rubric>
          </Class>
          <Class code="M95-M99" kind="block">
            <SuperClass code="XIII"/>
            <SubClass code="M99"/>
            <Rubric kind="preferred"><Label xml:lang="en">Other disorders of the musculoskeletal system and connective tissue</Label></Rubric>
          </Class>
          <Class code="M99" kind="category">
            <SuperClass code="M95-M99"/>
            <ExcludeModifier code="S13M00_5"/>
            <Rubric kind="preferred"><Label xml:lang="en">Biomechanical lesions, not elsewhere classified</Label></Rubric>
          </Class>

          <Class code="XX" kind="chapter">
            <SubClass code="W00-X59"/>
            <SubClass code="X85-Y09"/>
            <Rubric kind="preferred"><Label xml:lang="en">External causes of morbidity and mortality</Label></Rubric>
          </Class>
          <Class code="W00-X59" kind="block">
            <SuperClass code="XX"/>
            <SubClass code="W00"/>
            <ModifiedBy code="S20W00_4" position="1"/>
            <ModifiedBy code="S20V01_5" position="2"/>
            <Rubric kind="preferred"><Label xml:lang="en">Other external causes of accidental injury</Label></Rubric>
          </Class>
          <Class code="W00" kind="category">
            <SuperClass code="W00-X59"/>
            <Rubric kind="preferred"><Label xml:lang="en">Fall on same level involving ice and snow</Label></Rubric>
          </Class>
          <Class code="X85-Y09" kind="block">
            <SuperClass code="XX"/>
            <SubClass code="Y06"/>
            <ModifiedBy code="S20W00_4"/>
            <ModifiedBy code="S20V01_5"/>
            <Rubric kind="preferred"><Label xml:lang="en">Assault</Label></Rubric>
          </Class>
          <Class code="Y06" kind="category">
            <SuperClass code="X85-Y09"/>
            <SubClass code="Y06.0"/>
            <ExcludeModifier code="S20W00_4"/>
            <Rubric kind="preferred"><Label xml:lang="en">Neglect and abandonment</Label></Rubric>
          </Class>
          <Class code="Y06.0" kind="category">
            <SuperClass code="Y06"/>
            <Rubric kind="preferred"><Label xml:lang="en">By spouse or partner</Label></Rubric>
          </Class>
        </ClaML>
        """;

    public static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>A zip holding the given files, as WHO and CMS distribute theirs.</summary>
    public static MemoryStream Zip(params (string Name, string Content)[] files)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    /// <summary>One line of the order file: order number, code (7 wide), 1 for a valid code or 0 for a table heading, short and long title.</summary>
    public static string OrderLine(int order, string code, bool valid, string shortTitle, string longTitle) =>
        $"{order:00000} {code,-7} {(valid ? 1 : 0)} {shortTitle,-60} {longTitle}";

    public static string PcsOrderFile => string.Join("\r\n",
        OrderLine(1, "001", false, "Central Nervous System and Cranial Nerves, Bypass", "Central Nervous System and Cranial Nerves, Bypass"),
        OrderLine(2, "0016070", true, "Bypass Cereb Vent to Nasophar with Autol Sub, Open Approach",
            "Bypass Cerebral Ventricle to Nasopharynx with Autologous Tissue Substitute, Open Approach"),
        OrderLine(3, "0DT", false, "Gastrointestinal System, Resection", "Gastrointestinal System, Resection"),
        OrderLine(4, "0DTJ4ZZ", true, "Resection of Appendix, Perc Endo Approach", "Resection of Appendix, Percutaneous Endoscopic Approach"),
        OrderLine(5, "0DTJ0ZZ", true, "Resection of Appendix, Open Approach", "Resection of Appendix, Open Approach"),
        OrderLine(6, "BW03ZZZ", true, "Plain Radiography of Chest", "Plain Radiography of Chest")) + "\r\n";
}
