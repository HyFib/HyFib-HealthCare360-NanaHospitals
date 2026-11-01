using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Analytics.Features.Journeys;

/// <summary>
/// Where patients are in the hospital (the pipeline): one patient's journeys on their page, and the board of everyone in
/// the hospital now. Both need <c>visits.read</c>, which the front office, nurses and doctors hold with every other role
/// that sees patients: a journey is the patient's visits and stays, which that permission already shows. One patient's
/// journeys follow the restricted-record rules and are audited as a read of the record; the board, a list like today's
/// visits and the bed board, names a restricted patient by initials only (R-3).
/// </summary>
internal static class JourneyEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/patients/{patientId:guid}/journey", ForPatient)
            .WithTags("Patient journeys")
            .RequirePermission(PatientsPermissions.VisitsRead);

        app.MapGet("/api/v1/journeys/in-hospital", InHospital)
            .WithTags("Patient journeys")
            .RequirePermission(PatientsPermissions.VisitsRead);
    }

    private static async Task<IResult> ForPatient(
        Guid patientId, JourneyQueries journeys, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        // Operations are part of the clinical record, which a sensitive category (HIV, mental health, MTP, sexual violence)
        // closes to staff who do not hold it; the visit, stay and lab orders stay open to the desk and the departments.
        var chart = await guard.EnsureCanOpenChartAsync(patientId, ct);
        var result = await journeys.ForPatientAsync(patientId, includeSurgeries: chart.IsSuccess, ct);
        await audit.PatientRecordAccessedAsync(patientId, "Viewed where the patient is in their visit, stay, operations and lab orders", ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> InHospital(
        [AsParameters] PageQuery query, JourneyKind? kind, Guid? departmentId, Guid? wardId, JourneyQueries journeys, CancellationToken ct)
    {
        if (kind is JourneyKind.Surgery or JourneyKind.Lab)
        {
            return Error.Field("kind", "Show out-patients or in-patients.").ToProblem();
        }

        return TypedResults.Ok(await journeys.InHospitalAsync(new InHospitalFilter(kind, departmentId, wardId), query.ToRequest(), ct));
    }
}
