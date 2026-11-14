using FluentValidation;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS SEC-06: a write whose body is optional (a terminal sale's cancel, whose note only a sale with a lost answer needs)
/// still has its body checked when it comes. A request without one goes on to the handler, and a request whose body is
/// required is refused without one, as before.
/// </summary>
public sealed class OptionalBodyValidationTests
{
    private static readonly object Handled = new();

    [Fact]
    public async Task A_cancel_without_a_note_goes_on_to_the_handler()
    {
        var (result, handled) = await Run(new OptionalBodyValidationFilter<CounterTerminalCancel>(), null);

        handled.ShouldBeTrue();
        result.ShouldBeSameAs(Handled);
    }

    [Fact]
    public async Task A_cancel_with_a_note_that_fits_goes_on_to_the_handler()
    {
        var (_, handled) = await Run(new OptionalBodyValidationFilter<CounterTerminalCancel>(), new CounterTerminalCancel(new string('n', 200)));

        handled.ShouldBeTrue();
    }

    [Fact]
    public async Task A_note_longer_than_the_screen_takes_is_refused_by_name()
    {
        var (result, handled) = await Run(new OptionalBodyValidationFilter<CounterTerminalCancel>(), new CounterTerminalCancel(new string('n', 201)));

        handled.ShouldBeFalse();
        result.ShouldBeOfType<ValidationProblem>().ProblemDetails.Errors.Keys.ShouldBe(["note"]);
    }

    [Fact]
    public async Task A_note_with_control_characters_is_refused() =>
        (await Run(new OptionalBodyValidationFilter<CounterTerminalCancel>(), new CounterTerminalCancel("checked\u0007 the screen"))).Handled.ShouldBeFalse();

    [Fact]
    public async Task A_body_that_is_required_is_still_refused_when_it_is_left_out()
    {
        var (result, handled) = await Run(new ValidationFilter<CounterTerminalCancel>(), null);

        handled.ShouldBeFalse();
        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task An_optional_body_without_a_validator_fails_closed_even_when_it_is_left_out()
    {
        var context = new DefaultEndpointFilterInvocationContext(
            new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() }, Guid.NewGuid(), null);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await new OptionalBodyValidationFilter<CounterTerminalCancel>().InvokeAsync(context, _ => ValueTask.FromResult<object?>(Handled)));
    }

    private static async Task<(object? Result, bool Handled)> Run(IEndpointFilter filter, CounterTerminalCancel? body)
    {
        var services = new ServiceCollection().AddScoped<IValidator<CounterTerminalCancel>, CounterTerminalCancelValidator>().BuildServiceProvider();
        var context = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext { RequestServices = services }, Guid.NewGuid(), body);
        var handled = false;

        var result = await filter.InvokeAsync(context, _ =>
        {
            handled = true;
            return ValueTask.FromResult<object?>(Handled);
        });

        return (result, handled);
    }
}
