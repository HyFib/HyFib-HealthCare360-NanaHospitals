using System.Net;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The outside heartbeat is sent only while the server's own web host answers its readiness over HTTP, so a wedged host
/// stops it. Run against a real Kestrel with HC360's kind of certificate: a healthy host passes, one that answers 503 or
/// does not answer in time fails, and a listener with any other certificate is not trusted.
/// </summary>
public sealed class ServerReadinessProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_host_that_answers_ready_passes()
    {
        using var own = Certificate("own");
        await using var app = await ServeAsync(own.ServerCertificate, _ => Task.CompletedTask);
        using var probe = new LoopbackReadinessProbe(PortOf(app), () => own.ServerCertificate);

        (await probe.CheckAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_host_that_answers_unavailable_fails_saying_so()
    {
        using var own = Certificate("own");
        await using var app = await ServeAsync(own.ServerCertificate, context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        });
        using var probe = new LoopbackReadinessProbe(PortOf(app), () => own.ServerCertificate);

        (await probe.CheckAsync(Ct)).ShouldNotBeNull().ShouldContain("503");
    }

    [Fact]
    public async Task A_wedged_host_that_never_answers_fails_within_the_timeout()
    {
        using var own = Certificate("own");
        await using var app = await ServeAsync(own.ServerCertificate, async context => await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted));
        using var probe = new LoopbackReadinessProbe(PortOf(app), () => own.ServerCertificate, TimeSpan.FromMilliseconds(500));

        (await probe.CheckAsync(Ct)).ShouldNotBeNull().ShouldContain("did not answer");
    }

    [Fact]
    public async Task A_listener_with_another_certificate_than_the_servers_own_is_not_trusted()
    {
        using var own = Certificate("own");
        using var other = Certificate("other");
        await using var app = await ServeAsync(other.ServerCertificate, _ => Task.CompletedTask);
        using var probe = new LoopbackReadinessProbe(PortOf(app), () => own.ServerCertificate);

        (await probe.CheckAsync(Ct)).ShouldNotBeNull().ShouldContain("could not be reached");
    }

    [Fact]
    public async Task Nothing_listening_fails()
    {
        using var own = Certificate("own");
        int port;
        await using (var app = await ServeAsync(own.ServerCertificate, _ => Task.CompletedTask))
        {
            port = PortOf(app);
        }

        using var probe = new LoopbackReadinessProbe(port, () => own.ServerCertificate, TimeSpan.FromSeconds(2));

        (await probe.CheckAsync(Ct)).ShouldNotBeNull();
    }

    [Fact]
    public void Without_a_certificate_of_its_own_nothing_is_trusted()
    {
        using var own = Certificate("own");

        LoopbackReadinessProbe.IsOwn(own.ServerCertificate, null).ShouldBeFalse();
        LoopbackReadinessProbe.IsOwn(null, own.ServerCertificate).ShouldBeFalse();
        LoopbackReadinessProbe.IsOwn(own.ServerCertificate, own.ServerCertificate).ShouldBeTrue();
    }

    private LanCertificateManager Certificate(string machine)
    {
        var options = new PlatformOptions { DataDirectory = Path.Combine(_root, machine) };
        var certificates = new LanCertificateManager(new DataDirectories(options), options, new EphemeralDataProtectionProvider(), NullLogger<LanCertificateManager>.Instance);
        certificates.EnsureCertificates();
        return certificates;
    }

    private static async Task<WebApplication> ServeAsync(X509Certificate2 certificate, RequestDelegate ready)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet("/health/ready", ready);
        await app.StartAsync(Ct);
        return app;
    }

    private static int PortOf(WebApplication app) => new Uri(app.Urls.Single()).Port;
}
