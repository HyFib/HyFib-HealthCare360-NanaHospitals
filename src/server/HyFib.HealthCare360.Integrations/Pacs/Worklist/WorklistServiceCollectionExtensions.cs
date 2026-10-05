using FellowOakDicom;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

internal static class WorklistServiceCollectionExtensions
{
    /// <summary>
    /// The DICOM modality worklist server: fo-dicom (its services resolved from HC360's container, with dataset and PDU
    /// logging off so no patient data reaches the log), the shared gateway and status, the administrator's echo test and
    /// the hosted service that runs the listener.
    /// </summary>
    public static IServiceCollection AddModalityWorklist(this IServiceCollection services)
    {
        services.AddFellowOakDicom(configureServiceOptions: options =>
        {
            options.LogDimseDatasets = false;
            options.LogDataPDUs = false;
        });
        services.AddSingleton<ModalityWorklistState>();
        services.AddSingleton<ModalityWorklistGateway>();
        services.AddScoped<IWorklistFacilities, WorklistFacilities>();
        services.AddScoped<ModalityWorklistSelfTest>();
        services.AddHostedService<ModalityWorklistHostedService>();
        return services;
    }
}
