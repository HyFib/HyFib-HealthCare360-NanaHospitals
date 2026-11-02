using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// What in a module charges a service by itself, day after day: a ward's beds, a stay in progress in one of them, a diet.
/// Taking the service out of use would make billing refuse every such charge, so the services master refuses it while
/// anything still charges by it, naming what does. Each module that posts such charges registers one.
/// </summary>
/// <remarks>
/// A charge a module looks up by code and posts only while the service is in use (the nursing day, the consultant's
/// visit) is not one of them: for those, taking the service out of use is how a hospital stops charging it.
/// </remarks>
public interface IServiceChargeSource
{
    /// <summary>
    /// What charges the service by itself, in words for the person taking it out of use ("the ward General ward (male)
    /// charges its beds' days by it"); empty when nothing does. Reads every facility.
    /// </summary>
    Task<IReadOnlyList<string>> UsesAsync(Guid serviceItemId, CancellationToken cancellationToken);
}

/// <summary>Every module's <see cref="IServiceChargeSource"/>, asked together by the services master.</summary>
public sealed class ServiceChargeSources(IEnumerable<IServiceChargeSource> sources)
{
    public const string InUseCode = "service.charged_by_itself";

    /// <summary>What charges the service by itself, from every module's source, in the order the modules registered.</summary>
    public async Task<IReadOnlyList<string>> UsesAsync(Guid serviceItemId, CancellationToken cancellationToken)
    {
        List<string> uses = [];
        foreach (var source in sources)
        {
            uses.AddRange(await source.UsesAsync(serviceItemId, cancellationToken));
        }

        return uses;
    }

    /// <summary>The refusal to take a service out of use while <paramref name="uses"/> charge by it.</summary>
    public static Error InUse(string serviceCode, IReadOnlyList<string> uses)
    {
        ArgumentNullException.ThrowIfNull(uses);
        return Error.Conflict(InUseCode,
            $"{serviceCode} can't be taken out of use while it is charged by itself: {string.Join("; ", uses)}. Billing would refuse those " +
            "charges, and the days would go unbilled. Point them to another service first, or take it out of use once they end.");
    }
}
