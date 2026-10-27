using HyFib.HealthCare360.Platform.Files;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class StoredFileNameTests
{
    [Theory]
    [InlineData("discharge-summary.pdf", "application/pdf", "discharge-summary.pdf")]
    [InlineData("Scan.JPEG", "image/jpeg", "Scan.JPEG")]
    [InlineData("x-ray.jpg", "image/jpeg", "x-ray.jpg")]
    [InlineData("ecg", "application/dicom", "ecg.dcm")]
    [InlineData("0412-ravi.SCP", EncryptedFileStore.ScpEcgContentType, "0412-ravi.SCP")]
    public void A_name_that_fits_what_the_file_is_is_kept(string uploaded, string contentType, string stored) =>
        EncryptedFileStore.FileNameFor(uploaded, contentType).ShouldBe(stored);

    [Theory]
    [InlineData("results.hta", "application/pdf", "results.pdf")]
    [InlineData("results.html", "application/pdf", "results.pdf")]
    [InlineData("invoice.pdf.exe", "application/pdf", "invoice.pdf.pdf")]
    [InlineData("photo.png", "image/jpeg", "photo.jpg")]
    [InlineData(@"C:\fakepath\..\run.bat", "image/png", "run.png")]
    [InlineData("/home/ravi/../scan.pdf", "application/pdf", "scan.pdf")]
    [InlineData("C:report.pdf", "application/pdf", "report.pdf")]
    [InlineData(@"C:\scans\", "application/pdf", "document.pdf")]
    [InlineData("ECG 10:30.pdf", "application/pdf", "ECG 1030.pdf")]
    [InlineData("scan?<1>.png", "image/png", "scan1.png")]
    [InlineData(".hta", "image/webp", "document.webp")]
    [InlineData("tracing.exe", EncryptedFileStore.ScpEcgContentType, "tracing.scp")]
    [InlineData(null, "application/pdf", "document.pdf")]
    public void A_name_that_claims_to_be_something_else_takes_the_real_type(string? uploaded, string contentType, string stored) =>
        EncryptedFileStore.FileNameFor(uploaded, contentType).ShouldBe(stored);
}
