using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace JBZUniveresalLunix.Services;

public class KeysightVisaService : IDisposable
{
    const int VI_SUCCESS = 0;
    const uint VI_TMO_INFINITE = 0xFFFFFFFF;
    const uint VI_ATTR_TMO_VALUE = 0x3FFF001A;

    readonly object _sync = new();
    uint _resourceManager;
    uint _session;

    public virtual bool IsConnected => _session != 0;
    public string ConnectedResource { get; private set; } = string.Empty;
    public virtual string InstrumentId { get; private set; } = string.Empty;
    public string LastRawResistanceResponse { get; private set; } = string.Empty;

    [DllImport("visa32.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int viOpenDefaultRM(out uint session);
    [DllImport("visa32.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    static extern int viOpen(uint resourceManager, string resourceName, uint accessMode, uint timeoutMs, out uint session);
    [DllImport("visa32.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    static extern int viFindRsrc(uint resourceManager, string expression, out uint findList, out uint count, StringBuilder description);
    [DllImport("visa32.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    static extern int viFindNext(uint findList, StringBuilder description);
    [DllImport("visa32.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int viSetAttribute(uint session, uint attribute, uint value);
    [DllImport("visa32.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int viClose(uint session);
    [DllImport("visa32.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int viWrite(uint session, byte[] data, uint count, out uint written);
    [DllImport("visa32.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int viRead(uint session, byte[] data, uint count, out uint read);

    static void Ensure(int status, string api)
    {
        if (status < VI_SUCCESS)
            throw new InvalidOperationException($"{api} lỗi VISA: 0x{status:X8}");
    }

    void EnsureResourceManager()
    {
        if (_resourceManager != 0) return;
        Ensure(viOpenDefaultRM(out _resourceManager), "viOpenDefaultRM");
    }

    public IReadOnlyList<string> DiscoverUsbInstruments()
    {
        lock (_sync)
        {
            EnsureResourceManager();
            var result = new List<string>();
            var desc = new StringBuilder(512);
            uint findList = 0;
            uint count = 0;

            var status = viFindRsrc(_resourceManager, "USB?*INSTR", out findList, out count, desc);
            if (status < VI_SUCCESS)
                return result;

            try
            {
                if (count > 0 && desc.Length > 0)
                    result.Add(desc.ToString());

                for (var i = 1u; i < count; i++)
                {
                    desc.Clear();
                    desc.EnsureCapacity(512);
                    if (viFindNext(findList, desc) < VI_SUCCESS)
                        break;
                    if (desc.Length > 0)
                        result.Add(desc.ToString());
                }
            }
            finally
            {
                if (findList != 0) viClose(findList);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public virtual string Connect(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
            throw new ArgumentException("VISA Resource rỗng.", nameof(resource));

        lock (_sync)
        {
            DisposeInstrumentSession();
            EnsureResourceManager();
            Ensure(viOpen(_resourceManager, resource.Trim(), 0, 3000, out _session), "viOpen");
            try
            {
                // Đặt timeout rõ ràng để lỗi thiết bị không treo chu kỳ sản xuất.
                _ = viSetAttribute(_session, VI_ATTR_TMO_VALUE, 3000);
                InstrumentId = Query("*IDN?");
                ConnectedResource = resource.Trim();
                return InstrumentId;
            }
            catch
            {
                DisposeInstrumentSession();
                throw;
            }
        }
    }

    /// <summary>
    /// Chỉ gọi khi model thực sự có bước đo điện trở. Ưu tiên resource cấu hình;
    /// nếu rỗng hoặc lỗi thì tự tìm USBTMC và chọn thiết bị trả IDN Keysight/34461A.
    /// </summary>
    public virtual string ConnectAutomatic(string? preferredResource = null)
    {
        if (IsConnected) return InstrumentId;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredResource))
            candidates.Add(preferredResource.Trim());
        candidates.AddRange(DiscoverUsbInstruments());
        AsyncFileLogService.Current.Test(
            $"[AUTO-R] VISA resource found count={candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count()}");

        Exception? lastError = null;
        foreach (var resource in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                AsyncFileLogService.Current.Test($"[AUTO-R] VISA try resource={resource}");
                var idn = Connect(resource);
                AsyncFileLogService.Current.Test($"[AUTO-R] IDN = {idn}");
                if (idn.Contains("KEYSIGHT", StringComparison.OrdinalIgnoreCase) ||
                    idn.Contains("34461", StringComparison.OrdinalIgnoreCase) ||
                    idn.Contains("AGILENT", StringComparison.OrdinalIgnoreCase))
                {
                    AsyncFileLogService.Current.Test("[AUTO-R] Keysight ready");
                    return idn;
                }

                // Không giữ session của một USB instrument không phải đồng hồ cần đo.
                DisposeInstrumentSession();
            }
            catch (Exception ex)
            {
                lastError = ex;
                DisposeInstrumentSession();
            }
        }

        throw new InvalidOperationException(
            "Không tự tìm/kết nối được đồng hồ Keysight 34461A qua VISA/USB." +
            (lastError is null ? string.Empty : $" Lỗi cuối: {lastError.Message}"));
    }

    public virtual string Query(string command)
    {
        lock (_sync)
        {
            if (!IsConnected) throw new InvalidOperationException("Chưa kết nối Keysight");
            var tx = Encoding.ASCII.GetBytes(command.Trim() + "\n");
            Ensure(viWrite(_session, tx, (uint)tx.Length, out var written), "viWrite");
            if (written != tx.Length) throw new IOException($"VISA ghi thiếu byte: {written}/{tx.Length}");
            var rx = new byte[1024];
            Ensure(viRead(_session, rx, (uint)rx.Length, out var read), "viRead");
            return Encoding.ASCII.GetString(rx, 0, (int)read).Trim();
        }
    }

    public virtual double MeasureResistance(string command = ":MEASURE:RES?")
    {
        var raw = Query(command);
        LastRawResistanceResponse = raw;
        if (!double.TryParse(raw.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new FormatException("Keysight trả dữ liệu không hợp lệ: " + raw);
        AsyncFileLogService.Current.Test(
            FormattableString.Invariant($"[RES] Keysight raw response = {raw}; Parsed canonical = {value:0.##########} Ohm"));
        return value;
    }

    void DisposeInstrumentSession()
    {
        if (_session != 0) viClose(_session);
        _session = 0;
        ConnectedResource = string.Empty;
        InstrumentId = string.Empty;
    }

    void DisposeSessions()
    {
        DisposeInstrumentSession();
        if (_resourceManager != 0) viClose(_resourceManager);
        _resourceManager = 0;
    }

    public void Dispose()
    {
        lock (_sync) DisposeSessions();
    }
}
