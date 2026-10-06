namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>What HC360 Desktop does with a page the staff app opens in a new window.</summary>
internal enum NewWindowAction
{
    /// <summary>Nothing: the address is not usable, or the window is locked.</summary>
    Refuse,

    /// <summary>Another site: the system browser, after the usual confirmation.</summary>
    OpenExternally,

    /// <summary>The same view navigates to it (kiosks and display boards keep one full-screen window).</summary>
    OpenHere,

    /// <summary>Another HC360 window, beside the one that asked (<see cref="StaffWindow"/>).</summary>
    OpenWindow,
}

/// <summary>
/// Links with <c>target="_blank"</c> and <c>window.open</c> (SRS TEL-04): the chart or the consultation notes opened
/// from a video call must not replace the call, so a page of the server opens in another HC360 window, as it would in
/// a new browser tab. Pages of other sites open in the system browser, as before.
/// </summary>
internal static class NewWindowPolicy
{
    public static NewWindowAction Decide(string address, Uri server, bool kiosk, bool locked)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var target))
        {
            return NewWindowAction.Refuse;
        }

        if (!IsSameOrigin(target, server))
        {
            return NewWindowAction.OpenExternally;
        }

        if (locked)
        {
            return NewWindowAction.Refuse;
        }

        return kiosk ? NewWindowAction.OpenHere : NewWindowAction.OpenWindow;
    }

    public static bool IsSameOrigin(Uri uri, Uri server) =>
        Uri.Compare(
            uri, server, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
}
