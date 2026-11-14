using FluentValidation;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>Push a card or UPI amount to the counter's terminal.</summary>
/// <param name="TerminalId">The terminal wired to the counter, as its label names it.</param>
/// <param name="PatientId">The registered patient paying, if any: a walk-in customer has none.</param>
public sealed record StartTerminalSaleRequest(decimal Amount, string TerminalId, CounterPaymentMode Mode, Guid? PatientId);

/// <summary>
/// A card/UPI terminal's id as its label and the POS settings name it: letters, digits, '-' or '_', up to 32. The
/// terminal providers take no other, so a typed id is checked against it where it comes in.
/// </summary>
public static class TerminalIds
{
    public const int MaxLength = 32;

    public static IRuleBuilderOptions<T, string> TerminalId<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Enter the terminal id on its label.")
            .MaximumLength(MaxLength)
            .Matches("^[A-Za-z0-9_-]*$").WithMessage("Use the terminal id on its label: letters, digits, '-' or '_'.");
}

public sealed class StartTerminalSaleRequestValidator : AbstractValidator<StartTerminalSaleRequest>
{
    public StartTerminalSaleRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0m).WithMessage("Enter an amount greater than zero.")
            .LessThanOrEqualTo(10_000_000m).WithMessage("The amount is too large.")
            .PrecisionScale(18, 2, true).WithMessage("Use at most 2 decimal places.");
        RuleFor(x => x.TerminalId).TerminalId();
        RuleFor(x => x.Mode).Must(mode => mode is CounterPaymentMode.Card or CounterPaymentMode.Upi).WithMessage("Terminal sales are card or UPI payments.");
    }
}

/// <summary>
/// The terminal sales of a counter that bills its own sales (<see cref="ICounterTerminal"/>), mapped by the counter's
/// module under its own path and permissions: <c>POST terminal-sales</c> pushes the amount, <c>GET terminal-sales/{id}</c>
/// follows it, <c>POST terminal-sales/{id}/cancel</c> cancels it on the terminal, and <c>GET terminal-sales/unused</c>
/// lists the approved ones no payment records yet.
/// </summary>
public static class CounterTerminalEndpoints
{
    public static void MapCounterTerminalSales(this IEndpointRouteBuilder group, string counter, params string[] permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(counter);
        var sales = group.MapGroup("/terminal-sales");
        sales.MapPost("/", async (StartTerminalSaleRequest r, ICounterTerminal terminal, CancellationToken ct) =>
                (await terminal.StartAsync(new CounterTerminalStart(counter, r.Amount, r.TerminalId, r.Mode, r.PatientId), ct)).ToOk())
            .RequireAnyPermission(permissions).WithValidation<StartTerminalSaleRequest>();
        sales.MapGet("/unused", async (ICounterTerminal terminal, CancellationToken ct) => TypedResults.Ok(await terminal.UnusedAsync(counter, ct)))
            .RequireAnyPermission(permissions);
        sales.MapGet("/{id:guid}", async (Guid id, ICounterTerminal terminal, CancellationToken ct) => (await terminal.RefreshAsync(counter, id, ct)).ToOk())
            .RequireAnyPermission(permissions);
        sales.MapPost("/{id:guid}/cancel", async (Guid id, CounterTerminalCancel? request, ICounterTerminal terminal, CancellationToken ct) =>
                (await terminal.CancelAsync(counter, id, request?.Note, ct)).ToOk())
            .RequireAnyPermission(permissions);
    }
}
