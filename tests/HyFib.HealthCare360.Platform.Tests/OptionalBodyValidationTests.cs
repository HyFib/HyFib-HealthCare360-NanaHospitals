using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

    /// <summary>
    /// Through a real server, as the staff app sends it: a cancel without a note has no body and so no Content-Type. It
    /// must reach its endpoint, not the API's catch-all (ServerComposition), which answers any route nothing else takes
    /// with a 404, as it did when the route's body made routing ask for JSON.
    /// </summary>
    [Fact]
    public async Task A_cancel_sent_without_a_note_reaches_its_endpoint_and_one_with_a_note_is_checked()
    {
        await using var app = await CancelServerAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var path = $"/api/v1/pharmacy/terminal-sales/{Guid.NewGuid()}/cancel";

        using var bare = await http.PostAsync(path, content: null, Ct);
        bare.StatusCode.ShouldBe(HttpStatusCode.OK, await bare.Content.ReadAsStringAsync(Ct));
        (await bare.Content.ReadFromJsonAsync<string>(Ct)).ShouldBe("no note");

        using var noted = await http.PostAsJsonAsync(path, new { note = "The terminal's screen shows the idle menu" }, Ct);
        (await noted.Content.ReadFromJsonAsync<string>(Ct)).ShouldBe("The terminal's screen shows the idle menu");

        using var tooLong = await http.PostAsJsonAsync(path, new { note = new string('n', 201) }, Ct);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await tooLong.Content.ReadAsStringAsync(Ct));
        problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).ShouldBe(["note"]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A cancel route as the counters map it, beside the API's catch-all, on a loopback port.</summary>
    private static async Task<WebApplication> CancelServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Services.AddProblemDetails();
        builder.Services.AddScoped<IValidator<CounterTerminalCancel>, CounterTerminalCancelValidator>();
        var app = builder.Build();
        app.MapPost("/api/v1/pharmacy/terminal-sales/{id:guid}/cancel", (Guid id, CounterTerminalCancel? request) => TypedResults.Ok(request?.Note ?? "no note"))
            .WithOptionalValidation<CounterTerminalCancel>();
        app.Map("/api/{**rest}", () => Results.Problem(title: "Not found", statusCode: StatusCodes.Status404NotFound));
        await app.StartAsync(Ct);
        return app;
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
