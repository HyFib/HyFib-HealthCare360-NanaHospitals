using FellowOakDicom;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>How one answer is presented to the modality that asked.</summary>
/// <param name="Modality">The modality code to send (see <see cref="WorklistMatcher.PresentedModality"/>).</param>
/// <param name="StationAeTitle">The station to send for an entry with none assigned: the one the modality asked for, if any.</param>
internal sealed record WorklistPresentation(string CharacterSet, string Modality, string? StationAeTitle);

/// <summary>
/// The DICOM side of the modality worklist: the matching keys of a C-FIND identifier, and each worklist entry as a C-FIND
/// answer. An answer holds exactly the attributes the modality asked for (plus the character set): known ones with
/// HC360's values, the rest empty, as DICOM expects. Values are made to fit their DICOM types; identifiers are never
/// shortened: an accession number longer than 16 characters is sent empty, and in full as the Filler Order Number.
/// </summary>
internal static class WorklistDicom
{
    /// <summary>The longest value of a DICOM PN component group and of LO.</summary>
    private const int MaxLongString = 64;

    private const int MaxShortString = ModalityWorklistItem.MaxShortStringLength;

    /// <summary>The matching keys the server honours, read from the identifier (the first item of the step sequence).</summary>
    public static WorklistKeys Keys(DicomDataset? identifier)
    {
        if (identifier is null)
        {
            return WorklistKeys.Any;
        }

        var step = identifier.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var sequence) && sequence.Items.Count > 0
            ? sequence.Items[0]
            : null;
        return new WorklistKeys(
            Text(identifier, DicomTag.PatientName),
            Text(identifier, DicomTag.PatientID),
            Text(identifier, DicomTag.AccessionNumber),
            Text(identifier, DicomTag.RequestedProcedureID),
            step is null ? null : Text(step, DicomTag.Modality),
            step is null ? null : Text(step, DicomTag.ScheduledStationAETitle),
            step is null ? null : DicomRange.ParseDate(Text(step, DicomTag.ScheduledProcedureStepStartDate)),
            step is null ? null : DicomRange.ParseTime(Text(step, DicomTag.ScheduledProcedureStepStartTime)));
    }

    /// <summary>The answer for one entry: the attributes the identifier asked for, filled from the entry.</summary>
    public static DicomDataset Response(ModalityWorklistItem item, DicomDataset? identifier, WorklistPresentation presentation)
    {
        var full = Full(item, presentation);
        var answer = new DicomDataset();
        if (presentation.CharacterSet.Length > 0)
        {
            answer.Add(DicomTag.SpecificCharacterSet, presentation.CharacterSet);
        }

        if (identifier is null)
        {
            return answer;
        }

        foreach (var requested in identifier)
        {
            if (requested.Tag.Group == 0x0000 || requested.Tag == DicomTag.SpecificCharacterSet || requested.Tag.IsPrivate)
            {
                continue;
            }

            if (requested is DicomSequence sequence)
            {
                answer.Add(Filter(sequence, full));
            }
            else if (full.TryGetString(requested.Tag, out var value))
            {
                answer.AddOrUpdate(requested.Tag, value);
            }
            else
            {
                Empty(answer, requested);
            }
        }

        return answer;
    }

    /// <summary>Every attribute HC360 can answer, as DICOM values.</summary>
    private static DicomDataset Full(ModalityWorklistItem item, WorklistPresentation presentation)
    {
        var step = new DicomDataset();
        Set(step, DicomTag.Modality, presentation.Modality);
        Set(step, DicomTag.ScheduledStationAETitle, item.ScheduledStationAeTitle ?? presentation.StationAeTitle);
        Set(step, DicomTag.ScheduledProcedureStepStartDate, item.ScheduledStepStartDate);
        Set(step, DicomTag.ScheduledProcedureStepStartTime, item.ScheduledStepStartTime);
        Set(step, DicomTag.ScheduledPerformingPhysicianName, null);
        Set(step, DicomTag.ScheduledProcedureStepDescription, LongString(item.RequestedProcedureDescription));
        Set(step, DicomTag.ScheduledProcedureStepID, ShortString(item.ScheduledProcedureStepId));
        Set(step, DicomTag.ScheduledStationName, null);
        Set(step, DicomTag.ScheduledProcedureStepLocation, ShortString(item.ScheduledStepLocation));
        Set(step, DicomTag.ScheduledProcedureStepStatus, item.ScheduledStepStatus);

        var full = new DicomDataset();
        Set(full, DicomTag.AccessionNumber, item.AccessionFitsDicom ? item.AccessionNumber : null);
        Set(full, DicomTag.ReferringPhysicianName, PersonName(item.ReferringPhysicianName));
        Set(full, DicomTag.PatientName, PersonName(item.PatientName));
        Set(full, DicomTag.PatientID, LongString(item.PatientId));
        Set(full, DicomTag.PatientBirthDate, item.PatientBirthDate);
        Set(full, DicomTag.PatientSex, item.PatientSex);
        Set(full, DicomTag.StudyInstanceUID, item.StudyInstanceUid);
        Set(full, DicomTag.RequestedProcedureDescription, LongString(item.RequestedProcedureDescription));
        Set(full, DicomTag.RequestedProcedureID, ShortString(item.RequestedProcedureId));
        Set(full, DicomTag.RequestedProcedurePriority, item.RequestedProcedurePriority);
        Set(full, DicomTag.PlacerOrderNumberImagingServiceRequest, LongString(item.PlacerOrderNumber));
        Set(full, DicomTag.FillerOrderNumberImagingServiceRequest, LongString(item.AccessionNumber));
        full.Add(new DicomSequence(DicomTag.ReferencedStudySequence));
        full.Add(new DicomSequence(DicomTag.ReferencedPatientSequence));
        full.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, step));
        return full;
    }

    /// <summary>A sequence the identifier asked for: the step sequence with the attributes asked for in its item (all of
    /// them when the item is empty or missing); other sequences empty.</summary>
    private static DicomSequence Filter(DicomSequence requested, DicomDataset full)
    {
        if (!full.TryGetSequence(requested.Tag, out var ours) || ours.Items.Count == 0)
        {
            return new DicomSequence(requested.Tag);
        }

        var source = ours.Items[0];
        var keys = requested.Items.FirstOrDefault();
        if (keys is null || !keys.Any())
        {
            return new DicomSequence(requested.Tag, source.Clone());
        }

        var item = new DicomDataset();
        foreach (var key in keys)
        {
            if (key.Tag.IsPrivate || key.Tag == DicomTag.SpecificCharacterSet)
            {
                continue;
            }

            if (key is DicomSequence)
            {
                item.Add(new DicomSequence(key.Tag));
            }
            else if (source.TryGetString(key.Tag, out var value))
            {
                item.AddOrUpdate(key.Tag, value);
            }
            else
            {
                Empty(item, key);
            }
        }

        return new DicomSequence(requested.Tag, item);
    }

    /// <summary>
    /// An attribute HC360 has no value for, returned empty (DICOM's "zero length"): the requested element itself when it
    /// is empty, as most keys are; otherwise (a matching key HC360 does not support) an empty one of the same type.
    /// </summary>
    private static void Empty(DicomDataset dataset, DicomItem requested)
    {
        if (requested is not DicomElement element)
        {
            return;
        }

        if (element.Buffer.Size == 0)
        {
            dataset.AddOrUpdate(element);
        }
        else if (element is DicomStringElement)
        {
            dataset.AddOrUpdate(element.ValueRepresentation, element.Tag, Array.Empty<string>());
        }
    }

    private static void Set(DicomDataset dataset, DicomTag tag, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            dataset.AddOrUpdate(tag, Array.Empty<string>());
        }
        else
        {
            dataset.AddOrUpdate(tag, value);
        }
    }

    private static string? Text(DicomDataset dataset, DicomTag tag) =>
        dataset.TryGetString(tag, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>A short string (SH) that fits as it is, or nothing: an identifier is never cut short.</summary>
    private static string? ShortString(string? value) =>
        value is { Length: > 0 and <= MaxShortString } && !value.Contains('\\', StringComparison.Ordinal) ? value : null;

    /// <summary>Descriptive text (LO) without backslashes, within 64 characters.</summary>
    private static string? LongString(string? value)
    {
        var text = value?.Replace('\\', '/').Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= MaxLongString ? text : text[..MaxLongString];
    }

    /// <summary>
    /// A person name within DICOM's 64 characters: the middle and then the given name are left out of a longer name, so
    /// the family name always stays whole when it can.
    /// </summary>
    internal static string? PersonName(string? value)
    {
        var name = value?.Replace('\\', ' ').Split('=')[0].Trim().TrimEnd('^');
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var components = name.Split('^').ToList();
        while (string.Join('^', components).Length > MaxLongString && components.Count > 1)
        {
            components.RemoveAt(components.Count - 1);
        }

        var fitted = string.Join('^', components);
        return fitted.Length <= MaxLongString ? fitted : fitted[..MaxLongString];
    }
}
