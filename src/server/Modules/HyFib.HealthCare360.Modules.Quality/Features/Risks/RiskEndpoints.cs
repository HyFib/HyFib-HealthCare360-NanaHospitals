using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Quality.Features.Risks;

/// <summary>/api/v1/quality/risks — facility risk register with 5 × 5 likelihood × impact scoring (IRMS-05).</summary>
internal static class RiskEndpoints
{
    private static readonly Dictionary<string, Expression<Func<Risk, object?>>> SortKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["score"] = r => r.Score,
        ["residualScore"] = r => r.ResidualScore,
        ["reviewDate"] = r => r.ReviewDate,
        ["number"] = r => r.RiskNumber,
        ["title"] = r => r.Title,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/risks").WithTags("Quality").RequirePermission(QualityPermissions.RisksManage);

        group.MapGet("/", List);
        group.MapGet("/matrix", Matrix);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create).WithValidation<SaveRiskRequest>();
        group.MapPut("/{id:guid}", Update).WithValidation<SaveRiskRequest>();
        group.MapPost("/{id:guid}/review", Review).WithValidation<ReviewRiskRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        RiskCategory? category,
        RiskRating? rating,
        RiskStatus? status,
        Guid? departmentId,
        bool? reviewOverdue,
        HospitalDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.LocalToday();
        var risks = db.Set<Risk>().AsNoTracking();
        risks = status is not null ? risks.Where(r => r.Status == status) : risks.Where(r => r.Status != RiskStatus.Closed);
        if (category is not null)
        {
            risks = risks.Where(r => r.Category == category);
        }

        if (rating is not null)
        {
            risks = risks.Where(r => r.Rating == rating);
        }

        if (departmentId is not null)
        {
            risks = risks.Where(r => r.DepartmentId == departmentId);
        }

        if (reviewOverdue == true)
        {
            risks = risks.Where(r => r.ReviewDate < today);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            risks = risks.Where(r => EF.Functions.ILike(r.Title, like) || r.RiskNumber == term.ToUpper());
        }

        var result = await risks.OrderByKey(page, SortKeys, r => r.Score, defaultDescending: true).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(r => new RiskListItem(
            r.Id, r.RiskNumber, r.Title, r.Category, r.DepartmentId, r.Likelihood, r.Impact, r.Score, r.Rating, r.ResidualScore, r.ResidualRating,
            r.Status, r.OwnerUserId, r.ReviewDate, r.IsReviewOverdue(today))));
    }

    /// <summary>Heat map of open risks (inherent, or residual with <c>?residual=true</c>).</summary>
    private static async Task<IResult> Matrix(bool? residual, HospitalDbContext db, CancellationToken ct)
    {
        var risks = await db.Set<Risk>().AsNoTracking()
            .Where(r => r.Status != RiskStatus.Closed)
            .Select(r => new { r.Likelihood, r.Impact, r.ResidualLikelihood, r.ResidualImpact })
            .ToListAsync(ct);
        var points = risks
            .Select(r => residual == true && r.ResidualLikelihood is { } rl && r.ResidualImpact is { } ri ? (L: rl, I: ri) : (L: r.Likelihood, I: r.Impact))
            .ToList();

        var cells = new List<RiskMatrixCell>();
        for (var likelihood = RiskMatrix.MaxLevel; likelihood >= RiskMatrix.MinLevel; likelihood--)
        {
            for (var impact = RiskMatrix.MinLevel; impact <= RiskMatrix.MaxLevel; impact++)
            {
                var score = RiskMatrix.Score(likelihood, impact);
                cells.Add(new RiskMatrixCell(likelihood, impact, score, RiskMatrix.Rate(score), points.Count(p => p.L == likelihood && p.I == impact)));
            }
        }

        return TypedResults.Ok(cells);
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var r = await db.Set<Risk>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
        {
            return Error.NotFound(nameof(Risk), id).ToProblem();
        }

        var owner = r.OwnerUserId is { } ownerId ? await db.Set<User>().Where(u => u.Id == ownerId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        return TypedResults.Ok(new RiskDetail(
            r.Id, r.RiskNumber, r.Title, r.Description, r.Category, r.DepartmentId, r.Likelihood, r.Impact, r.Score, r.Rating, r.ResidualLikelihood,
            r.ResidualImpact, r.ResidualScore, r.ResidualRating, r.ExistingControls, r.MitigationPlan, r.OwnerUserId, owner, r.ReviewDate,
            r.LastReviewedAt, r.LastReviewNote, r.Status, r.IsReviewOverdue(time.LocalToday()), r.Version));
    }

    private static async Task<IResult> Create(SaveRiskRequest request, HospitalDbContext db, INumberSeries numbers, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var risk = new Risk { FacilityId = facilityId };
        var applied = await ApplyAsync(risk, request, db, ct);
        if (applied.IsFailure)
        {
            return applied.Error!.ToProblem();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        risk.RiskNumber = await numbers.NextAsync(QualityNumberSeries.Risk, facilityId, ct);
        db.Add(risk);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/v1/quality/risks/{risk.Id}", new { risk.Id, risk.RiskNumber, risk.Score, risk.Rating });
    }

    private static async Task<IResult> Update(Guid id, SaveRiskRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var risk = await db.Set<Risk>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (risk is null)
        {
            return Error.NotFound(nameof(Risk), id).ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(risk, version);
        }

        var applied = await ApplyAsync(risk, request, db, ct);
        if (applied.IsFailure)
        {
            return applied.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Review(Guid id, ReviewRiskRequest request, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var risk = await db.Set<Risk>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (risk is null)
        {
            return Error.NotFound(nameof(Risk), id).ToProblem();
        }

        db.SetExpectedVersion(risk, request.Version);
        var result = risk.Assess(request.Likelihood, request.Impact, request.ResidualLikelihood, request.ResidualImpact);
        if (result.IsSuccess)
        {
            result = risk.RecordReview(request.NextReviewDate, request.Note, time.LocalToday(), time.GetUtcNow());
        }

        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        if (request.ExistingControls is not null)
        {
            risk.ExistingControls = string.IsNullOrWhiteSpace(request.ExistingControls) ? null : request.ExistingControls.Trim();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Result> ApplyAsync(Risk risk, SaveRiskRequest request, HospitalDbContext db, CancellationToken ct)
    {
        if (request.OwnerUserId is { } ownerId && !await db.Set<User>().AnyAsync(u => u.Id == ownerId && u.IsActive && u.Type == UserType.Staff, ct))
        {
            return Error.Field("ownerUserId", "Choose an active staff member as risk owner.");
        }

        if (request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId, ct))
        {
            return Error.Field("departmentId", "Choose a department of this facility.");
        }

        var assessed = risk.Assess(request.Likelihood, request.Impact, request.ResidualLikelihood, request.ResidualImpact);
        if (assessed.IsFailure)
        {
            return assessed;
        }

        risk.Title = request.Title.Trim();
        risk.Description = Clean(request.Description);
        risk.Category = request.Category;
        risk.DepartmentId = request.DepartmentId;
        risk.ExistingControls = Clean(request.ExistingControls);
        risk.MitigationPlan = Clean(request.MitigationPlan);
        risk.OwnerUserId = request.OwnerUserId;
        risk.ReviewDate = request.ReviewDate;
        risk.Status = request.Status;
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
