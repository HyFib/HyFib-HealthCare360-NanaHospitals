using HyFib.HealthCare360.Platform.Hosting;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// The first administrator's one-time password, written to the data directory when no password was configured, so the
/// installer can show it once. The file is deleted as soon as the administrator sets a password of their own, so a
/// password in clear text does not stay on the server's disk.
/// </summary>
internal static class InitialAdminPassword
{
    public const string UserName = "admin";

    private const string FileName = "initial-admin-password.txt";

    public static string PathIn(DataDirectories directories) => Path.Combine(directories.Root, FileName);

    /// <summary>Deletes the file; true when there was one and it is gone.</summary>
    public static bool Delete(DataDirectories directories)
    {
        var path = PathIn(directories);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
