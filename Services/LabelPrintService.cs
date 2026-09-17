using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Services;

public sealed class LabelPrintService : IAsyncDisposable
{
    private readonly SemaphoreSlim _printGate = new(1, 1);
    private SerialPort? _printerPort;
    private string _connectedPort = string.Empty;
    private int _connectedBaudRate;

    public bool IsConnected => _printerPort?.IsOpen == true;

    public string ConnectedPort => IsConnected ? _connectedPort : string.Empty;

    public async Task<LabelPrinterConnectionResult> ConnectAsync(
        LabelSettings settings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string portName = settings.PrinterCom?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(portName))
            return new LabelPrinterConnectionResult(false, "Chưa chọn cổng COM máy in.");

        await _printGate.WaitAsync(ct);
        try
        {
            await EnsureComConnectedAsync(
                portName,
                settings.BaudRate,
                settings.WriteTimeoutMs,
                settings.EncodingName,
                ct);
            return new LabelPrinterConnectionResult(
                true,
                $"ĐÃ KẾT NỐI {portName} - {settings.BaudRate} baud");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ClosePrinterPort();
            AsyncFileLogService.Current.Error(
                $"Label printer connection failed on {portName}: {ex}");
            return new LabelPrinterConnectionResult(
                false,
                "Không kết nối được máy in. Hãy rút/cắm lại cáp và chọn lại cổng COM.");
        }
        finally
        {
            _printGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _printGate.WaitAsync(ct);
        try
        {
            ClosePrinterPort();
        }
        finally
        {
            _printGate.Release();
        }
    }

    public async Task<LabelPrintTransportResult> PrintPassLabelAsync(
        LabelPrintRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _printGate.WaitAsync(ct);
        try
        {
            return await PrintSerializedAsync(request, ct);
        }
        finally
        {
            _printGate.Release();
        }
    }

    private async Task<LabelPrintTransportResult> PrintSerializedAsync(
        LabelPrintRequest request,
        CancellationToken ct)
    {
        string transport = ResolveTransportName(request);
        AsyncFileLogService.Current.Application(
            $"[LABEL] Product={request.Data.PartNumber} Profile={request.Profile.Id} " +
            $"Template={TemplateSource(request)} Transport={transport} COM={request.PrinterCom} " +
            $"Copies={request.Copies} CycleId={request.CycleId}");
        Encoding encoding = ResolveEncoding(request.Profile.EncodingName);
        byte[] payload = EncodeStrict(request.Payload, encoding);

        if (request.Profile.Mode == LabelPrintMode.ExternalHelper)
            return await PrintWithExternalHelperAsync(request, payload, ct);

        if (!string.IsNullOrWhiteSpace(request.PrinterCom))
        {
            await EnsureComConnectedAsync(
                request.PrinterCom.Trim(),
                request.BaudRate,
                request.WriteTimeoutMs,
                request.Profile.EncodingName,
                ct);
            await WriteToConnectedComAsync(payload, request.Copies, ct);
            return new LabelPrintTransportResult(
                true,
                $"Printed {request.Copies} label(s) via {request.PrinterCom}.");
        }

        if (!string.IsNullOrWhiteSpace(request.PrinterName))
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < request.Copies; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    RawPrinter.Send(request.PrinterName.Trim(), payload);
                }
            }, ct);
            return new LabelPrintTransportResult(
                true,
                $"Printed {request.Copies} label(s) via Windows printer '{request.PrinterName}'.");
        }

        if (!string.IsNullOrWhiteSpace(request.RawDestination))
        {
            await PrintToRawDestinationAsync(request.RawDestination, payload, request.Copies, ct);
            return new LabelPrintTransportResult(
                true,
                $"Printed {request.Copies} label(s) via raw destination '{request.RawDestination}'.");
        }

        return new LabelPrintTransportResult(
            false,
            "No printer transport configured.");
    }

    private async Task EnsureComConnectedAsync(
        string portName,
        int baudRate,
        int writeTimeoutMs,
        string encodingName,
        CancellationToken ct)
    {
        if (_printerPort?.IsOpen == true &&
            string.Equals(_connectedPort, portName, StringComparison.OrdinalIgnoreCase) &&
            _connectedBaudRate == baudRate)
            return;

        ClosePrinterPort();
        Encoding encoding = ResolveEncoding(encodingName);
        var port = new SerialPort(
            portName,
            baudRate,
            Parity.None,
            8,
            StopBits.One)
        {
            Handshake = Handshake.None,
            Encoding = encoding,
            WriteTimeout = Math.Max(1, writeTimeoutMs),
            DtrEnable = false,
            RtsEnable = false
        };

        try
        {
            await Task.Run(port.Open, ct);
            _printerPort = port;
            _connectedPort = portName;
            _connectedBaudRate = baudRate;
            AsyncFileLogService.Current.Application(
                $"[LABEL] Printer connected COM={portName} Baud={baudRate}");
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    private async Task WriteToConnectedComAsync(byte[] payload, int copies, CancellationToken ct)
    {
        SerialPort port = _printerPort is { IsOpen: true }
            ? _printerPort
            : throw new InvalidOperationException("Kết nối COM máy in chưa sẵn sàng.");

        try
        {
            for (int i = 0; i < copies; i++)
            {
                ct.ThrowIfCancellationRequested();
                await port.BaseStream.WriteAsync(payload, ct);
                await port.BaseStream.FlushAsync(ct);
            }
        }
        catch
        {
            ClosePrinterPort();
            throw;
        }
    }

    private void ClosePrinterPort()
    {
        SerialPort? port = _printerPort;
        _printerPort = null;
        _connectedPort = string.Empty;
        _connectedBaudRate = 0;
        if (port is null)
            return;

        try { port.Close(); } catch { }
        port.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _printGate.WaitAsync();
        try
        {
            ClosePrinterPort();
        }
        finally
        {
            _printGate.Release();
            _printGate.Dispose();
        }
    }

    private static async Task PrintToRawDestinationAsync(
        string destination,
        byte[] payload,
        int copies,
        CancellationToken ct)
    {
        for (int i = 0; i < copies; i++)
        {
            await using var stream = new FileStream(
                destination.Trim(),
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
        }
    }

    private static async Task<LabelPrintTransportResult> PrintWithExternalHelperAsync(
        LabelPrintRequest request,
        byte[] payload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalHelperPath) ||
            !File.Exists(request.ExternalHelperPath))
            return new LabelPrintTransportResult(false, $"External label helper not found: {request.ExternalHelperPath}");

        string helperDirectory = Path.GetDirectoryName(request.ExternalHelperPath)!;
        string printFile = string.IsNullOrWhiteSpace(request.ExternalPrintFile)
            ? Path.Combine(helperDirectory, "print.txt")
            : Path.GetFullPath(Path.IsPathRooted(request.ExternalPrintFile)
                ? request.ExternalPrintFile
                : Path.Combine(helperDirectory, request.ExternalPrintFile));
        await File.WriteAllBytesAsync(printFile, payload, ct);

        string arguments = (request.ExternalHelperArgument ?? string.Empty)
            .Replace("{PRINT_FILE}", printFile, StringComparison.Ordinal);
        for (int copy = 0; copy < request.Copies; copy++)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = request.ExternalHelperPath,
                    Arguments = arguments,
                    WorkingDirectory = helperDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
                return new LabelPrintTransportResult(false, "External label helper did not start.");

            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                return new LabelPrintTransportResult(false, $"External label helper exited with code {process.ExitCode}.");
        }

        return new LabelPrintTransportResult(true, $"External helper completed. Print file: {printFile}");
    }

    private static string ResolveTransportName(LabelPrintRequest request) =>
        request.Profile.Mode == LabelPrintMode.ExternalHelper ? "ExternalHelper" :
        !string.IsNullOrWhiteSpace(request.PrinterCom) ? "COM" :
        !string.IsNullOrWhiteSpace(request.PrinterName) ? "WindowsRAW" :
        !string.IsNullOrWhiteSpace(request.RawDestination) ? "RawDestination" :
        "Unconfigured";

    private static string TemplateSource(LabelPrintRequest request) =>
        string.IsNullOrWhiteSpace(request.Profile.TemplatePath)
            ? "THT_EMBEDDED"
            : request.Profile.TemplatePath;

    private static Encoding ResolveEncoding(string encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(
                string.IsNullOrWhiteSpace(encodingName) ? "us-ascii" : encodingName,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"Unsupported label encoding '{encodingName}'.", ex);
        }
    }

    private static byte[] EncodeStrict(string payload, Encoding encoding)
    {
        try
        {
            return encoding.GetBytes(payload);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"Label payload contains characters unsupported by encoding '{encoding.WebName}'.", ex);
        }
    }

    private static class RawPrinter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class DocInfo1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName = "JBZ Label";
            [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDataType = "RAW";
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DocInfo1 di);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        public static void Send(string printerName, byte[] bytes)
        {
            if (!OpenPrinter(printerName, out IntPtr printer, IntPtr.Zero))
                throw new InvalidOperationException($"Cannot open Windows printer '{printerName}'. Win32={Marshal.GetLastWin32Error()}");

            IntPtr unmanaged = IntPtr.Zero;
            try
            {
                var info = new DocInfo1();
                if (StartDocPrinter(printer, 1, info) == 0)
                    throw new InvalidOperationException($"StartDocPrinter failed. Win32={Marshal.GetLastWin32Error()}");

                try
                {
                    if (!StartPagePrinter(printer))
                        throw new InvalidOperationException($"StartPagePrinter failed. Win32={Marshal.GetLastWin32Error()}");

                    try
                    {
                        unmanaged = Marshal.AllocCoTaskMem(bytes.Length);
                        Marshal.Copy(bytes, 0, unmanaged, bytes.Length);

                        if (!WritePrinter(printer, unmanaged, bytes.Length, out int written) || written != bytes.Length)
                            throw new InvalidOperationException($"WritePrinter failed. Win32={Marshal.GetLastWin32Error()}");
                    }
                    finally
                    {
                        EndPagePrinter(printer);
                    }
                }
                finally
                {
                    EndDocPrinter(printer);
                }
            }
            finally
            {
                if (unmanaged != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(unmanaged);
                ClosePrinter(printer);
            }
        }
    }
}
