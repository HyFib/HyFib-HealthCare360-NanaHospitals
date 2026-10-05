using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;

/// <summary>What the ClaML file says it is (<c>&lt;Title name="ICD-10" version="2019"&gt;…&lt;/Title&gt;</c>).</summary>
public sealed record ClamlTitle(string? Name, string? Version, string? Date, string? Text);

public sealed record ClassificationChapter(string Number, string Start, string End, string Title, int Sequence);

/// <summary>A block; <see cref="Code"/> is its range ("E10-E14", or "C50" for a block of one category).</summary>
public sealed record ClassificationBlock(string Code, string Start, string End, string Title, string Chapter, string? Parent, bool HoldsCategories, int Sequence);

/// <summary>
/// A code of the classification: a category from the file, or one made by adding a shared fourth or fifth character
/// (<see cref="FromModifier"/>).
/// </summary>
public sealed record ClassificationCode(
    string Code,
    string Title,
    string Chapter,
    string? Block,
    bool IsBillable,
    IcdUsage? Usage,
    string? IncludedTerms,
    bool FromModifier);

public sealed record ClamlClassification(
    ClamlTitle Title,
    IReadOnlyList<ClassificationChapter> Chapters,
    IReadOnlyList<ClassificationBlock> Blocks,
    IReadOnlyList<ClassificationCode> Codes,
    int CategoriesInFile,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads the WHO ICD-10 classification in ClaML (Classification Markup Language, EN ISO 13120), as WHO distributes it
/// (<c>icd102019en.xml</c>).
/// <list type="bullet">
/// <item><b>Safely.</b> The file's DOCTYPE is skipped: the DTD is neither fetched nor applied, no entity outside the
/// file is resolved (an undeclared entity stops the import), and the reader's size limit applies. Elements are read one
/// at a time from a stream, so the whole document is never held as a tree.</item>
/// <item><b>Chapters, blocks and categories</b> with their preferred titles. A block of blocks (C00-C97 holding
/// C00-C75 holding C00-C14) is kept with its parent; a code is filed under its nearest block.</item>
/// <item><b>Inclusion terms</b> become search words. <b>Dagger and asterisk</b> codes are marked from the class's
/// <c>usage</c> and the file's <c>UsageKinds</c>.</item>
/// <item><b>Shared characters.</b> A <c>ModifiedBy</c> on a class applies to it and every class below it, unless one
/// of them names it in <c>ExcludeModifier</c>; <c>all="false"</c> limits it to the listed <c>ValidModifierClass</c>es.
/// The characters are added to the codes at the bottom of the file's hierarchy: a dotted code adds the fourth character
/// (E11 + .0 → E11.0), any other adds the next one (M54.1 + 6 → M54.16, M45 + 6 → M45.6). Modifier classes may hold
/// further modifier classes (F10 + .2 → F10.2 → F10.20). A made code's title is its parent's followed by the
/// character's own ("Type 2 diabetes mellitus, with coma").</item>
/// <item><b>Codable</b> (<see cref="ClassificationCode.IsBillable"/>): a code is codable unless it has fourth-character
/// subdivisions, from the file or from a dotted shared list. WHO lists fifth characters and the chapter XIII site codes
/// for optional use, so the code above them stays codable (M54.5 as well as M54.56).</item>
/// </list>
/// </summary>
public static partial class ClamlReader
{
    private const int MaxTitleLength = 300;
    private const int MaxDepth = 24;

    public static ClamlClassification Read(Stream xml, CatalogueFileLimits limits)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(limits);
        var raw = Load(xml, limits);
        return new Builder(raw, limits).Build();
    }

    // ------------------------------------------------------------------------------------------------- reading the XML

    private static Raw Load(Stream xml, CatalogueFileLimits limits)
    {
        var settings = new XmlReaderSettings
        {
            // WHO's file starts with <!DOCTYPE ClaML SYSTEM "ClaML.dtd">. The DTD is skipped, never fetched or applied, and
            // nothing outside the file is ever read: there is no resolver, and an entity the file did not declare stops it.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = limits.MaxUnzippedBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };

        var raw = new Raw();
        try
        {
            using var reader = XmlReader.Create(xml, settings);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "ClaML")
            {
                throw new CatalogueFileException(
                    $"This is not a ClaML file: it starts with <{Shorten(reader.LocalName)}> instead of <ClaML>. {CatalogueFile.AcceptedFiles}");
            }

            if (reader.IsEmptyElement)
            {
                return raw;
            }

            reader.ReadStartElement();
            while (reader.NodeType is not (XmlNodeType.EndElement or XmlNodeType.None))
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "Title":
                        raw.Title = ReadTitle(Next(reader));
                        break;
                    case "UsageKinds":
                        ReadUsageKinds(Next(reader), raw);
                        break;
                    case "Modifier":
                        AddModifier(Next(reader), raw);
                        break;
                    case "ModifierClass":
                        AddModifierClass(Next(reader), raw, limits);
                        break;
                    case "Class":
                        AddClass(Next(reader), raw, limits);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new CatalogueFileException($"The file is not valid XML: {Shorten(ex.Message, 200)}", ex);
        }

        return raw;
    }

    private static XElement Next(XmlReader reader) => (XElement)XNode.ReadFrom(reader);

    private static ClamlTitle ReadTitle(XElement title) =>
        new(Attribute(title, "name"), Attribute(title, "version"), Attribute(title, "date"), CatalogueText.Clean(title.Value) is { Length: > 0 } text ? CatalogueText.Fit(text, 200) : null);

    /// <summary><c>&lt;UsageKind name="dagger" mark="†"/&gt;</c>: which usage names mean dagger and asterisk, and their marks.</summary>
    private static void ReadUsageKinds(XElement kinds, Raw raw)
    {
        foreach (var kind in Children(kinds, "UsageKind"))
        {
            if (Attribute(kind, "name") is not { } name)
            {
                continue;
            }

            var mark = Attribute(kind, "mark");
            var usage = mark switch
            {
                "†" or "+" => IcdUsage.Dagger,
                "*" => IcdUsage.Asterisk,
                _ => UsageFromName(name),
            };
            raw.UsageKinds[name] = (usage, mark);
        }
    }

    private static void AddModifier(XElement element, Raw raw)
    {
        if (Attribute(element, "code") is not { } code)
        {
            raw.Warnings.Add("A modifier without a code was skipped.");
            return;
        }

        raw.Modifiers.TryAdd(code, new RawModifier(code, Children(element, "SubClass").Select(s => Attribute(s, "code")).OfType<string>().ToList()));
    }

    private static void AddModifierClass(XElement element, Raw raw, CatalogueFileLimits limits)
    {
        if (++raw.ModifierClassCount > limits.MaxModifierClasses)
        {
            throw new CatalogueFileException($"The file has more than {limits.MaxModifierClasses:N0} modifier classes, more than any ICD-10 edition.");
        }

        var modifier = Attribute(element, "modifier");
        var code = Attribute(element, "code");
        if (modifier is null || code is null)
        {
            raw.Warnings.Add("A modifier class without a modifier or code was skipped.");
            return;
        }

        var modifierClass = new RawModifierClass(
            modifier,
            code,
            UsageOf(Attribute(element, "usage"), raw),
            Attribute(Child(element, "SuperClass"), "code"),
            Children(element, "SubClass").Select(s => Attribute(s, "code")).OfType<string>().ToList(),
            PreferredTitle(element, raw),
            Inclusions(element, raw),
            raw.ModifierClassCount);
        if (raw.ModifierClasses.TryAdd((modifier, code), modifierClass))
        {
            raw.ModifierClassOrder.Add(modifierClass);
        }
    }

    private static void AddClass(XElement element, Raw raw, CatalogueFileLimits limits)
    {
        if (++raw.ClassCount > limits.MaxClasses)
        {
            throw new CatalogueFileException($"The file has more than {limits.MaxClasses:N0} classes, more than any ICD-10 edition.");
        }

        var (code, markedUsage) = CleanCode(Attribute(element, "code"));
        var kind = Attribute(element, "kind")?.ToLowerInvariant();
        if (code is null || kind is null)
        {
            raw.Warnings.Add("A class without a code or kind was skipped.");
            return;
        }

        var modifiedBy = Children(element, "ModifiedBy")
            .Select((m, index) => Attribute(m, "code") is { } modifier
                ? new ModifierUse(
                    modifier,
                    !string.Equals(Attribute(m, "all"), "false", StringComparison.OrdinalIgnoreCase),
                    Children(m, "ValidModifierClass").Select(v => Attribute(v, "code")).OfType<string>().ToImmutableHashSet(StringComparer.Ordinal),
                    int.TryParse(Attribute(m, "position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) ? position : null,
                    raw.ClassCount * 16 + index)
                : null)
            .OfType<ModifierUse>()
            .ToList();

        var rawClass = new RawClass(
            code,
            kind,
            UsageOf(Attribute(element, "usage"), raw) ?? markedUsage,
            CleanCode(Attribute(Child(element, "SuperClass"), "code")).Code,
            Children(element, "SubClass").Select(s => CleanCode(Attribute(s, "code")).Code).OfType<string>().ToList(),
            modifiedBy,
            Children(element, "ExcludeModifier").Select(e => Attribute(e, "code")).OfType<string>().ToList(),
            PreferredTitle(element, raw),
            Inclusions(element, raw),
            raw.ClassCount);
        if (raw.Classes.TryAdd(code, rawClass))
        {
            raw.ClassOrder.Add(rawClass);
        }
        else
        {
            raw.Warnings.Add($"{code} appears more than once in the file; the first is used.");
        }
    }

    /// <summary>The <c>preferred</c> rubric's label (the title), or <c>preferredLong</c> when a class has only that.</summary>
    private static string? PreferredTitle(XElement element, Raw raw)
    {
        var rubrics = Children(element, "Rubric").ToList();
        var preferred = rubrics.FirstOrDefault(r => Attribute(r, "kind") == "preferred") ?? rubrics.FirstOrDefault(r => Attribute(r, "kind") == "preferredLong");
        return preferred is null ? null : LabelOf(preferred, raw);
    }

    private static List<string> Inclusions(XElement element, Raw raw) =>
        Children(element, "Rubric")
            .Where(r => Attribute(r, "kind") == "inclusion")
            .Select(r => LabelOf(r, raw))
            .OfType<string>()
            .Where(text => text.Length > 0)
            .ToList();

    /// <summary>The English label of a rubric, or its first label when it has no English one.</summary>
    private static string? LabelOf(XElement rubric, Raw raw)
    {
        var labels = Children(rubric, "Label").ToList();
        var label = labels.FirstOrDefault(l => string.Equals((string?)l.Attribute(XNamespace.Xml + "lang"), "en", StringComparison.OrdinalIgnoreCase))
                    ?? labels.FirstOrDefault();
        if (label is null)
        {
            return null;
        }

        var text = new StringBuilder();
        AppendText(label, text, raw);
        return CatalogueText.Clean(text.ToString());
    }

    /// <summary>
    /// A label's text: its own text and every element's, with a space between elements (fragments, list items) and a
    /// reference to another code shown as the printed classification shows it ("(G01*)").
    /// </summary>
    private static void AppendText(XElement element, StringBuilder text, Raw raw)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText value:
                    text.Append(value.Value);
                    break;
                case XElement { Name.LocalName: "Reference" } reference:
                    var target = CatalogueText.Clean(reference.Value);
                    var mark = Attribute(reference, "usage") is { } usage && raw.UsageKinds.TryGetValue(usage, out var kind) ? kind.Mark : null;
                    var bracketed = Attribute(reference, "class")?.Contains("bracket", StringComparison.OrdinalIgnoreCase) == true;
                    text.Append(bracketed ? $" ({target}{mark})" : $"{target}{mark}");
                    break;
                case XElement child:
                    text.Append(' ');
                    AppendText(child, text, raw);
                    text.Append(' ');
                    break;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------- building the tree

    private sealed class Builder(Raw raw, CatalogueFileLimits limits)
    {
        private readonly Dictionary<string, List<RawClass>> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Modifier, string? Parent), List<RawModifierClass>> _modifierChildren = [];
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
        private readonly List<Node> _order = [];
        private readonly List<(string Number, string Title, int Sequence)> _chapters = [];
        private readonly List<ClassificationBlock> _blocks = [];
        private readonly HashSet<string> _blockCodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _missingModifiers = new(StringComparer.Ordinal);
        private int _categories;
        private int _invalidCategories;
        private int _cutTitles;

        public ClamlClassification Build()
        {
            IndexClasses();
            IndexModifierClasses();
            var chapters = raw.ClassOrder.Where(c => c.Kind == "chapter").ToList();
            if (chapters.Count == 0)
            {
                throw new CatalogueFileException($"The file has no chapters, so it is not an ICD-10 ClaML file. {CatalogueFile.AcceptedFiles}");
            }

            foreach (var chapter in chapters)
            {
                Visit(chapter, null, null, Modifiers.None, 0);
            }

            var orphans = raw.ClassOrder.Count(c => !_visited.Contains(c.Code));
            if (orphans > 0)
            {
                raw.Warnings.Add($"{orphans} classes are not under any chapter and were skipped.");
            }

            if (_invalidCategories > Math.Max(10, _categories / 50))
            {
                throw new CatalogueFileException(
                    $"{_invalidCategories} of the file's categories are not ICD-10 codes. Is it the WHO ICD-10 file, and not ICD-11 or another classification?");
            }

            if (_categories < limits.MinIcd10Categories)
            {
                throw new CatalogueFileException(
                    $"The file has only {_categories:N0} {(_categories == 1 ? "category" : "categories")}; WHO's ICD-10 file has about 12,000. Check that it is the complete WHO file.");
            }

            if (_cutTitles > 0)
            {
                raw.Warnings.Add($"{_cutTitles} titles longer than {MaxTitleLength} characters were shortened.");
            }

            var codes = _order.Select(n => new ClassificationCode(
                    n.Code, n.Title, n.Chapter, n.Block, !n.HasFourthCharacterSubdivisions, n.Usage, n.IncludedTerms, n.FromModifier))
                .ToList();
            return new ClamlClassification(raw.Title, Chapters(), _blocks, codes, _categories, raw.Warnings.ToList());
        }

        private List<ClassificationChapter> Chapters() =>
            _chapters.Select(c =>
                {
                    var blocks = _blocks.Where(b => b.Chapter == c.Number).ToList();
                    var categories = _order.Where(n => n.Chapter == c.Number && n.Code.Length == 3).Select(n => n.Code).ToList();
                    var starts = blocks.Select(b => b.Start).Concat(categories).ToList();
                    var ends = blocks.Select(b => b.End).Concat(categories).ToList();
                    return new ClassificationChapter(
                        c.Number,
                        starts.Count == 0 ? string.Empty : starts.Min(StringComparer.Ordinal)!,
                        ends.Count == 0 ? string.Empty : ends.Max(StringComparer.Ordinal)!,
                        c.Title,
                        c.Sequence);
                })
                .ToList();

        /// <summary>Children by parent: the parent's SuperClass link, else a SubClass list naming the class; in the parent's SubClass order.</summary>
        private void IndexClasses()
        {
            var listedBy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parent in raw.ClassOrder)
            {
                foreach (var sub in parent.SubClasses)
                {
                    listedBy.TryAdd(sub, parent.Code);
                }
            }

            foreach (var child in raw.ClassOrder.Where(c => c.Kind != "chapter"))
            {
                var parent = child.SuperClass ?? listedBy.GetValueOrDefault(child.Code);
                if (parent is not null && raw.Classes.ContainsKey(parent))
                {
                    (_children.TryGetValue(parent, out var list) ? list : _children[parent] = []).Add(child);
                }
            }

            foreach (var (parentCode, list) in _children)
            {
                var order = raw.Classes[parentCode].SubClasses.Select((code, index) => (code, index))
                    .GroupBy(x => x.code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().index, StringComparer.Ordinal);
                list.Sort((a, b) =>
                {
                    var byList = order.GetValueOrDefault(a.Code, int.MaxValue).CompareTo(order.GetValueOrDefault(b.Code, int.MaxValue));
                    return byList != 0 ? byList : a.Sequence.CompareTo(b.Sequence);
                });
            }
        }

        private void IndexModifierClasses()
        {
            foreach (var modifierClass in raw.ModifierClassOrder)
            {
                // A modifier class directly under its modifier is top level; one under another class of the same modifier nests.
                var parent = modifierClass.SuperClass is { } super && super != modifierClass.Modifier && super != modifierClass.Code
                             && raw.ModifierClasses.ContainsKey((modifierClass.Modifier, super))
                    ? super
                    : null;
                var key = (modifierClass.Modifier, parent);
                (_modifierChildren.TryGetValue(key, out var list) ? list : _modifierChildren[key] = []).Add(modifierClass);
            }

            foreach (var ((modifier, parent), list) in _modifierChildren)
            {
                var listed = parent is null
                    ? raw.Modifiers.GetValueOrDefault(modifier)?.SubClasses ?? []
                    : raw.ModifierClasses[(modifier, parent)].SubClasses;
                var order = listed.Select((code, index) => (code, index)).GroupBy(x => x.code, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().index, StringComparer.Ordinal);
                list.Sort((a, b) =>
                {
                    var byList = order.GetValueOrDefault(a.Code, int.MaxValue).CompareTo(order.GetValueOrDefault(b.Code, int.MaxValue));
                    return byList != 0 ? byList : a.Sequence.CompareTo(b.Sequence);
                });
            }
        }

        private List<RawClass> ChildrenOf(string code) => _children.GetValueOrDefault(code) ?? [];

        private List<RawModifierClass> ModifierChildren(string modifier, string? parent) => _modifierChildren.GetValueOrDefault((modifier, parent)) ?? [];

        private void Visit(RawClass node, string? chapter, ClassificationBlock? block, Modifiers inherited, int depth)
        {
            if (depth > MaxDepth || !_visited.Add(node.Code))
            {
                return;
            }

            var modifiers = inherited.With(node);
            var children = ChildrenOf(node.Code);
            switch (node.Kind)
            {
                case "chapter":
                    chapter = node.Code;
                    _chapters.Add((node.Code, Title(node.Title, node.Code), node.Sequence));
                    break;
                case "block":
                    if (BlockRange().Match(node.Code) is { Success: true } range)
                    {
                        var start = range.Groups[1].Value;
                        var end = range.Groups[2].Success ? range.Groups[2].Value : start;
                        var code = start == end ? start : $"{start}-{end}";
                        if (_blockCodes.Add(code))
                        {
                            block = new ClassificationBlock(code, start, end, Title(node.Title, code), chapter ?? string.Empty, block?.Code,
                                children.Any(c => c.Kind == "category"), node.Sequence);
                            _blocks.Add(block);
                        }
                    }
                    else
                    {
                        raw.Warnings.Add($"The block {Shorten(node.Code)} has no range HC360 recognises; its codes are filed under the block above it.");
                    }

                    break;
                case "category":
                    AddCategory(node, chapter ?? string.Empty, block?.Code, modifiers, depth);
                    return;
                default:
                    raw.Warnings.Add($"{Shorten(node.Code)} is of kind '{Shorten(node.Kind)}', which HC360 does not import.");
                    break;
            }

            foreach (var child in children)
            {
                Visit(child, chapter, block, modifiers, depth + 1);
            }
        }

        private Node? AddCategory(RawClass category, string chapter, string? block, Modifiers modifiers, int depth)
        {
            _visited.Add(category.Code);
            _categories++;
            if (!CodeReleases.IsIcd10Code(category.Code))
            {
                _invalidCategories++;
                raw.Warnings.Add($"{Shorten(category.Code)} is not an ICD-10 code and was skipped.");
                return null;
            }

            var node = new Node(category.Code, Title(category.Title, category.Code), chapter, block, category.Usage, Terms(category.Inclusions), FromModifier: false);
            if (!Register(node))
            {
                return null;
            }

            foreach (var child in ChildrenOf(category.Code).Where(c => c.Kind == "category"))
            {
                if (depth < MaxDepth && !_visited.Contains(child.Code) && AddCategory(child, chapter, block, modifiers.With(child), depth + 1) is not null)
                {
                    node.AddChild(fourthCharacter: !node.Code.Contains('.', StringComparison.Ordinal));
                }
            }

            if (node.ChildCount == 0)
            {
                Expand(node, modifiers);
            }

            return node;
        }

        /// <summary>Adds the shared characters to a code at the bottom of the file's hierarchy, one modifier after another.</summary>
        private void Expand(Node leaf, Modifiers modifiers)
        {
            List<Node> current = [leaf];
            foreach (var use in modifiers.InOrder(this))
            {
                var tops = ModifierChildren(use.Code, null);
                if (tops.Count == 0)
                {
                    if (_missingModifiers.Add(use.Code))
                    {
                        raw.Warnings.Add($"The modifier {Shorten(use.Code)} is used but not defined; its characters were not added.");
                    }

                    continue;
                }

                var next = new List<Node>();
                foreach (var node in current)
                {
                    // A list of fourth characters (.0, .1…) does not apply below the fourth character.
                    if (IsDotted(use.Code) && node.Code.Contains('.', StringComparison.Ordinal))
                    {
                        next.Add(node);
                        continue;
                    }

                    var leaves = new List<Node>();
                    foreach (var top in tops)
                    {
                        Generate(node, node, top, use, leaves, 0);
                    }

                    next.AddRange(leaves.Count == 0 ? [node] : leaves);
                }

                current = next;
            }
        }

        private void Generate(Node category, Node parent, RawModifierClass modifierClass, ModifierUse use, List<Node> leaves, int depth)
        {
            if (depth > 4 || !use.Allows(modifierClass, this))
            {
                return;
            }

            var dotted = modifierClass.Code.StartsWith('.');
            var code = Compose(dotted ? category.Code : parent.Code, modifierClass.Code);
            if (!CodeReleases.IsIcd10Code(code))
            {
                raw.Warnings.Add($"{Shorten(parent.Code)} with {Shorten(modifierClass.Code)} does not make an ICD-10 code and was skipped.");
                return;
            }

            var node = new Node(code, Title(ComposeTitle(parent.Title, modifierClass.Title), code), parent.Chapter, parent.Block,
                modifierClass.Usage ?? parent.Usage, Terms(modifierClass.Inclusions), FromModifier: true);
            if (!Register(node))
            {
                return;
            }

            parent.AddChild(fourthCharacter: dotted && !parent.Code.Contains('.', StringComparison.Ordinal));
            foreach (var child in ModifierChildren(modifierClass.Modifier, modifierClass.Code))
            {
                Generate(category, node, child, use, leaves, depth + 1);
            }

            if (node.ChildCount == 0)
            {
                leaves.Add(node);
            }
        }

        private bool Register(Node node)
        {
            if (!_nodes.TryAdd(node.Code, node))
            {
                raw.Warnings.Add($"{node.Code} is listed twice; the first is kept.");
                return false;
            }

            if (_nodes.Count > limits.MaxCodes)
            {
                throw new CatalogueFileException($"The file makes more than {limits.MaxCodes:N0} codes, more than any ICD-10 edition.");
            }

            _order.Add(node);
            return true;
        }

        /// <summary>True when every top-level class of the modifier is a fourth character (".0").</summary>
        internal bool IsDotted(string modifier) => ModifierChildren(modifier, null) is { Count: > 0 } tops && tops.All(t => t.Code.StartsWith('.'));

        internal bool HasValidDescendant(RawModifierClass modifierClass, IImmutableSet<string> valid, int depth = 0) =>
            depth < 4 && ModifierChildren(modifierClass.Modifier, modifierClass.Code).Any(c => valid.Contains(c.Code) || HasValidDescendant(c, valid, depth + 1));

        private string Title(string? title, string code)
        {
            var text = string.IsNullOrWhiteSpace(title) ? code : title;
            if (text.Length > MaxTitleLength)
            {
                _cutTitles++;
            }

            return CatalogueText.Fit(text, MaxTitleLength);
        }

        private static string? Terms(List<string> inclusions) =>
            inclusions.Count == 0
                ? null
                : CatalogueText.Fit(string.Join("; ", inclusions.Distinct(StringComparer.OrdinalIgnoreCase)), IcdCode.MaxIncludedTermsLength);
    }

    /// <summary>A code being built; it counts its children rather than keeping them.</summary>
    private sealed class Node(string code, string title, string chapter, string? block, IcdUsage? usage, string? includedTerms, bool FromModifier)
    {
        public string Code { get; } = code;
        public string Title { get; } = title;
        public string Chapter { get; } = chapter;
        public string? Block { get; } = block;
        public IcdUsage? Usage { get; } = usage;
        public string? IncludedTerms { get; } = includedTerms;
        public bool FromModifier { get; } = FromModifier;
        public int ChildCount { get; private set; }

        public bool HasFourthCharacterSubdivisions { get; private set; }

        public void AddChild(bool fourthCharacter)
        {
            ChildCount++;
            HasFourthCharacterSubdivisions |= fourthCharacter;
        }
    }

    /// <summary>The modifiers in force for a class: inherited from the classes above it, less those it excludes.</summary>
    private sealed record Modifiers(ImmutableDictionary<string, ModifierUse> Uses)
    {
        public static Modifiers None { get; } = new(ImmutableDictionary.Create<string, ModifierUse>(StringComparer.Ordinal));

        public Modifiers With(RawClass node)
        {
            if (node.ExcludeModifiers.Count == 0 && node.ModifiedBy.Count == 0)
            {
                return this;
            }

            var uses = Uses.RemoveRange(node.ExcludeModifiers);
            foreach (var use in node.ModifiedBy)
            {
                uses = uses.SetItem(use.Code, use);
            }

            return new Modifiers(uses);
        }

        /// <summary>By the file's <c>position</c> where given, otherwise fourth characters before fifth, then in the file's order.</summary>
        public IEnumerable<ModifierUse> InOrder(Builder builder) =>
            Uses.Values
                .OrderBy(u => u.Position is null ? 1 : 0)
                .ThenBy(u => u.Position ?? 0)
                .ThenBy(u => builder.IsDotted(u.Code) ? 0 : 1)
                .ThenBy(u => u.Sequence);
    }

    private sealed record ModifierUse(string Code, bool All, IImmutableSet<string> Valid, int? Position, int Sequence)
    {
        /// <summary>Every class of the modifier, or (all="false") the listed ones and the classes that lead to them.</summary>
        public bool Allows(RawModifierClass modifierClass, Builder builder) =>
            All || Valid.Contains(modifierClass.Code) || builder.HasValidDescendant(modifierClass, Valid);
    }

    // ------------------------------------------------------------------------------------------------- raw file content

    private sealed class Raw
    {
        public ClamlTitle Title { get; set; } = new(null, null, null, null);

        public Dictionary<string, (IcdUsage? Usage, string? Mark)> UsageKinds { get; } = new(StringComparer.Ordinal)
        {
            // ClaML's usual names, used when the file has no UsageKinds of its own.
            ["dagger"] = (IcdUsage.Dagger, "†"),
            ["aster"] = (IcdUsage.Asterisk, "*"),
        };

        public Dictionary<string, RawClass> Classes { get; } = new(StringComparer.Ordinal);
        public List<RawClass> ClassOrder { get; } = [];
        public Dictionary<string, RawModifier> Modifiers { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Modifier, string Code), RawModifierClass> ModifierClasses { get; } = [];
        public List<RawModifierClass> ModifierClassOrder { get; } = [];
        public WarningList Warnings { get; } = new();
        public int ClassCount { get; set; }
        public int ModifierClassCount { get; set; }
    }

    private sealed record RawClass(
        string Code,
        string Kind,
        IcdUsage? Usage,
        string? SuperClass,
        List<string> SubClasses,
        List<ModifierUse> ModifiedBy,
        List<string> ExcludeModifiers,
        string? Title,
        List<string> Inclusions,
        int Sequence);

    private sealed record RawModifier(string Code, List<string> SubClasses);

    private sealed record RawModifierClass(
        string Modifier,
        string Code,
        IcdUsage? Usage,
        string? SuperClass,
        List<string> SubClasses,
        string? Title,
        List<string> Inclusions,
        int Sequence);

    /// <summary>The first warnings of a kind are kept word for word; after twenty, they are only counted.</summary>
    private sealed class WarningList
    {
        private const int Kept = 20;
        private readonly List<string> _messages = [];
        private int _more;

        public void Add(string message)
        {
            if (_messages.Count < Kept)
            {
                _messages.Add(message);
            }
            else
            {
                _more++;
            }
        }

        public List<string> ToList() => _more == 0 ? [.. _messages] : [.. _messages, $"…and {_more} more warnings like these."];
    }

    // ------------------------------------------------------------------------------------------------- helpers

    /// <summary>
    /// A modifier class's characters added to a code: after the third character there is always a dot
    /// (E11 + .0 → E11.0, M45 + 6 → M45.6); after it, characters follow on (M54.1 + 6 → M54.16).
    /// </summary>
    internal static string Compose(string code, string characters)
    {
        var suffix = characters.Trim().TrimStart('.');
        return code.Contains('.', StringComparison.Ordinal) ? code + suffix : code + "." + suffix;
    }

    /// <summary>"Type 2 diabetes mellitus" and "With coma" → "Type 2 diabetes mellitus, with coma" (acronyms keep their capitals).</summary>
    internal static string ComposeTitle(string parent, string? characters)
    {
        if (string.IsNullOrWhiteSpace(characters))
        {
            return parent;
        }

        var text = characters.Trim();
        if (text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]))
        {
            text = char.ToLowerInvariant(text[0]) + text[1..];
        }

        return $"{parent}, {text}";
    }

    /// <summary>A class code as stored: upper case, without a dagger or asterisk the file may have written into it.</summary>
    private static (string? Code, IcdUsage? Usage) CleanCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (null, null);
        }

        var text = code.Trim();
        IcdUsage? usage = text.EndsWith('†') || text.EndsWith('+') ? IcdUsage.Dagger : text.EndsWith('*') ? IcdUsage.Asterisk : null;
        return (text.TrimEnd('†', '+', '*', '!').ToUpperInvariant(), usage);
    }

    private static IcdUsage? UsageOf(string? name, Raw raw) =>
        name is null ? null : raw.UsageKinds.TryGetValue(name, out var kind) ? kind.Usage : UsageFromName(name);

    private static IcdUsage? UsageFromName(string name) =>
        name.Contains("dagger", StringComparison.OrdinalIgnoreCase) ? IcdUsage.Dagger
        : name.Contains("aster", StringComparison.OrdinalIgnoreCase) ? IcdUsage.Asterisk
        : null;

    private static string? Attribute(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value is { } value && value.Trim().Length > 0 ? value.Trim() : null;

    private static XElement? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement element, string name) => element.Elements().Where(e => e.Name.LocalName == name);

    /// <summary>File text quoted in a message, kept short.</summary>
    private static string Shorten(string? text, int max = 40) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex(@"^([A-Z][0-9]{2})(?:-([A-Z][0-9]{2}))?$")]
    private static partial Regex BlockRange();
}
