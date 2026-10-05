using System.Runtime.InteropServices;
using System.Text;

// Native libraries are loaded from System32 only, never from the application or current directory (DLL planting).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// Sends printer-language data straight to the spooler (ZPL or TSPL for label printers, ESC/POS for thermal
/// receipt printers), bypassing the page renderer. Binary jobs are passed as <c>base64:…</c>.
/// </summary>
internal static partial class RawPrinter
{
    public static void Send(string printerName, string data)
    {
        var bytes = data.StartsWith("base64:", StringComparison.Ordinal)
            ? Convert.FromBase64String(data[7..])
            : Encoding.Latin1.GetBytes(data);
        if (bytes.Length is 0 or > 4 * 1024 * 1024)
        {
            throw new BridgeException("bad_request", "The print job is empty or too large.");
        }

        if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw new BridgeException("printer_not_found", $"The printer '{printerName}' could not be opened.");
        }

        try
        {
            var document = new NativeMethods.DocInfo { DocumentName = "HC360 label", DataType = "RAW" };
            if (!NativeMethods.StartDocPrinter(handle, 1, ref document))
            {
                throw new BridgeException("print_failed", "The printer refused the job.");
            }

            try
            {
                NativeMethods.StartPagePrinter(handle);
                var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    if (!NativeMethods.WritePrinter(handle, pinned.AddrOfPinnedObject(), bytes.Length, out var written) || written != bytes.Length)
                    {
                        throw new BridgeException("print_failed", "The job could not be sent to the printer.");
                    }
                }
                finally
                {
                    pinned.Free();
                }

                NativeMethods.EndPagePrinter(handle);
            }
            finally
            {
                NativeMethods.EndDocPrinter(handle);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(handle);
        }
    }

    private static partial class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DocInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string DocumentName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? OutputFile;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string DataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClosePrinter(IntPtr handle);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartDocPrinter(IntPtr handle, int level, ref DocInfo document);

        [DllImport("winspool.drv", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndDocPrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StartPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WritePrinter(IntPtr handle, IntPtr buffer, int count, out int written);
    }
}
