using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Documents;

public sealed class ReportDocumentSourceTests
{
    [Fact]
    public void Lab_radiology_and_cardiology_reports_each_have_a_document_source_for_report_ready_messages()
    {
        var services = new ServiceCollection();

        new DiagnosticsModule().AddServices(services, new ConfigurationBuilder().Build());

        var sources = services.Where(d => d.ServiceType == typeof(INotificationDocumentSource)).ToList();
        sources.Select(d => d.ImplementationType).ShouldBe(
            [typeof(LabReportDocumentSource), typeof(RadiologyReportDocumentSource), typeof(CardiologyReportDocumentSource)], ignoreOrder: true);
        sources.ShouldAllBe(d => d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(HealthCheckupResultsBuilder) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void The_kinds_name_the_module_and_the_report()
    {
        LabReportDocumentSource.DocumentKind.ShouldBe("diagnostics.lab-report");
        RadiologyReportDocumentSource.DocumentKind.ShouldBe("diagnostics.radiology-report");
        CardiologyReportDocumentSource.DocumentKind.ShouldBe("diagnostics.cardiology-report");
    }
}
