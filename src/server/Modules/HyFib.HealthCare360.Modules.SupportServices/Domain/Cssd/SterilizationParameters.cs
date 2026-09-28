namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public sealed record ParameterCheck(bool IsWithinRange, string? Message)
{
    public static ParameterCheck Ok { get; } = new(true, null);
}

/// <summary>
/// Minimum validated exposure per method (typical AAMI/EN 285 and manufacturer values): steam 121 °C for 15 min,
/// 126 °C for 10 min or 134 °C for 3 min, never above 138 °C; ethylene oxide 37–63 °C for at least 60 min; hydrogen
/// peroxide plasma 40–60 °C for at least 24 min. Sites keep their own validated sterilizer programmes; these bounds only
/// catch cycles that cannot have sterilized, which then fail automatically.
/// </summary>
public static class SterilizationParameters
{
    public const decimal SteamMaxC = 138m;

    public static ParameterCheck Check(SterilizationMethod method, decimal temperatureC, int exposureMinutes) => method switch
    {
        SterilizationMethod.Steam => Steam(temperatureC, exposureMinutes),
        SterilizationMethod.EthyleneOxide => Range("Ethylene oxide", temperatureC, exposureMinutes, 37m, 63m, 60),
        SterilizationMethod.Plasma => Range("Plasma", temperatureC, exposureMinutes, 40m, 60m, 24),
        _ => new ParameterCheck(false, "Unknown sterilization method."),
    };

    /// <summary>Minimum steam exposure at a temperature, or null when the temperature cannot sterilize.</summary>
    public static int? MinimumSteamMinutes(decimal temperatureC) => temperatureC switch
    {
        > SteamMaxC => null,
        >= 134m => 3,
        >= 126m => 10,
        >= 121m => 15,
        _ => null,
    };

    private static ParameterCheck Steam(decimal temperatureC, int exposureMinutes)
    {
        if (temperatureC > SteamMaxC)
        {
            return new ParameterCheck(false, $"Steam temperature {temperatureC} °C is above the {SteamMaxC} °C limit.");
        }

        return MinimumSteamMinutes(temperatureC) switch
        {
            null => new ParameterCheck(false, $"Steam at {temperatureC} °C is below the 121 °C sterilizing temperature."),
            { } minimum when exposureMinutes < minimum => new ParameterCheck(false, $"Steam at {temperatureC} °C needs at least {minimum} minutes (recorded {exposureMinutes})."),
            _ => ParameterCheck.Ok,
        };
    }

    private static ParameterCheck Range(string name, decimal temperatureC, int exposureMinutes, decimal minC, decimal maxC, int minMinutes)
    {
        if (temperatureC < minC || temperatureC > maxC)
        {
            return new ParameterCheck(false, $"{name} cycles run at {minC}–{maxC} °C (recorded {temperatureC} °C).");
        }

        return exposureMinutes < minMinutes
            ? new ParameterCheck(false, $"{name} cycles need at least {minMinutes} minutes of exposure (recorded {exposureMinutes}).")
            : ParameterCheck.Ok;
    }
}
