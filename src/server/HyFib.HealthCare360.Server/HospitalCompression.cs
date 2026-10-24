using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// Responses are compressed, Brotli first, at the fastest level: on a ward's WiFi the bytes cost more than the CPU, and
/// a list's largest page stays under 100 KB as sent (SRS PERF-05). Over HTTPS too: the token-bearing sign-in responses,
/// the one place compression could leak a secret (BREACH), are left out in Program.cs.
/// </summary>
internal static class HospitalCompression
{
    public static IServiceCollection AddHospitalCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(compression =>
        {
            compression.EnableForHttps = true;
            compression.Providers.Add<BrotliCompressionProvider>();
            compression.Providers.Add<GzipCompressionProvider>();
            compression.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json", "application/fhir+json", "image/svg+xml"]);
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }
}
