namespace JBZUniveresalLunix.Models;

public sealed class ProductionSettings
{
    // ============================================================
    // THIẾT BỊ / I/O
    // ============================================================

    /// <summary>Runtime chỉ dùng Universal Tester New qua UART; config legacy được normalize về JbzSerial.</summary>
    public BoardMode BoardMode { get; set; } = BoardMode.JbzSerial;
    /// <summary>Windows COM for the JBZ UART board; empty means discover an available board.</summary>
    public string BoardPortName { get; set; } = string.Empty;


    /// <summary>
    /// Card vật lý cuối cùng mà firmware nhận ở byte thứ ba của START_SCAN.
    /// Trace Htdrv trong project xác nhận command xx=4 quét 256 I/O, vì vậy
    /// mỗi scan-unit = 64 I/O = 2 port nội bộ x 32 I/O. Khi Start Card lớn hơn
    /// 1, giá trị này là StartCardNumber + ExpansionCardCount - 1.
    /// </summary>
    public int CardCount { get; set; } = 2;

    public int IoConfirm1 { get; set; } = 1;

    public int IoConfirmN { get; set; } = 1;

    public int UsbDelay { get; set; } = 1;
    /// <summary>
    /// Card vật lý đầu tiên của dải được lắp. Firmware vẫn quét từ card 1 tới
    /// card cuối của dải; decoder bỏ vùng đứng trước và ánh xạ Start Card thành IO1.
    /// </summary>
    public int StartCardNumber { get; set; } = 1;

    public bool UseTestPointer { get; set; } = true;

    /// <summary>
    /// Maintenance-only mode. When enabled, production test is locked and relay
    /// commands are available only through the existing board transport owner.
    /// </summary>
    public bool ManualModeEnabled { get; set; }

    /// <summary>
    /// Trường compatibility cho cấu hình V12.9.2 trở về trước. Từ V12.9.5
    /// Production luôn dùng Master state machine tự động; Normalize() luôn ép true.
    /// Không còn checkbox/nút Master thủ công trên TestView.
    /// </summary>
    public bool AutoMasterSequence { get; set; } = true;

    /// <summary>
    /// Số điểm lỗi dây duy nhất phải phát hiện trên MASTER NG. Giá trị này là
    /// fallback/default; cấu hình theo từng model nằm trong MasterFaultCountsByModel.
    /// </summary>
    public int MasterFaultRequiredCount { get; set; } = 2;

    /// <summary>
    /// Cấu hình Số lỗi Master theo mã hàng/model. Key ưu tiên PartNumber; nếu THT
    /// chưa có PartNumber thì dùng tên model/tên file.
    /// </summary>
    public Dictionary<string, int> MasterFaultCountsByModel { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Legacy compatibility: số COM cũ của máy kín nước (5 = COM5).</summary>
    public int WaterproofSerialPort { get; set; }

    /// <summary>Máy leak UART/RS232 riêng, không liên quan transport JBZ UART.</summary>
    public WaterProofMachineSettings WaterProofMachine { get; set; } = new();

    /// <summary>
    /// Cấu hình leak theo từng file/model THT. Key là tên file không có phần mở rộng.
    /// Model không có key hoặc Enabled=false sẽ bỏ qua hoàn toàn bước leak.
    /// </summary>
    public Dictionary<string, WaterProofModelSettings> WaterProofProfilesByModel { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cấu hình 10 kênh điện trở riêng theo file/model THT.</summary>
    public Dictionary<string, ResistanceChannelSetting[]> ResistanceProfilesByModel { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Số card mở rộng 64 I/O người vận hành cấu hình: 1..10.
    /// Mỗi card có 2 port nội bộ x 32 I/O; operator không cấu hình số port.
    /// START_SCAN xx dùng chính số card/scan-unit này.
    /// </summary>
    public int ExpansionCardCount { get; set; } = 2;

    // ============================================================
    // THÔNG TIN PRODUCTION
    // ============================================================

    // Giá trị LOTNO dành cho sản phẩm PASS kế tiếp. Người vận hành có thể đặt
    // LOTNO bắt đầu trong màn Cài đặt; lịch sử test và tem cùng dùng giá trị này.
    // Chỉ sau khi in tem PASS thành công, phần mềm mới tăng +1 và lưu lại.
    public long LotNo { get; set; } = 2000;

    // Ngày làm việc gắn với LOTNO, lưu theo ISO yyyy-MM-dd. Sang ngày mới,
    // LOT kế tiếp trở về LOTNO bắt đầu riêng của mã hàng; daily PASS về 0.
    public string LotNoDate { get; set; } = string.Empty;

    /// <summary>
    /// LOT kế tiếp và ngày sản xuất được lưu riêng theo mã hàng. Key ưu tiên
    /// PartNumber; LotNo/LotNoDate phía trên chỉ là giá trị tương thích đang
    /// hiển thị cho mã hàng được chọn.
    /// </summary>
    public Dictionary<string, ProductLotSettings> LotSettingsByProduct { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Giữ trường cũ để đọc các file cấu hình V11.4 trở về trước.
    public string Lot { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string DeviceNumber { get; set; } = string.Empty;

    public string OperatorCompany { get; set; } = string.Empty;

    public string ProductionLine { get; set; } = string.Empty;

    public double TemperatureTolerance { get; set; }

    public int MinimumErrorLogValue { get; set; }

    public bool AutoSaveErrors { get; set; }

    /// <summary>
    /// Enables the main diagnostic/protocol log. History and crash reports
    /// are persisted independently.
    /// </summary>
    public bool EnableSystemLogs { get; set; }

    // ============================================================
    // THỜI GIAN
    // ============================================================

    /// <summary>Legacy config compatibility only. Runtime uses ProductionTimingPolicy.DefaultIoScanIntervalMs.</summary>
    public int IoScanIntervalMs { get; set; } = 2;

    /// <summary>Legacy config only. Runtime no longer uses a separate OPEN confirmation delay.</summary>
    public int OpenCircuitConfirmMs { get; set; } = 150;

    /// <summary>Legacy config compatibility only. Runtime uses ProductionTimingPolicy.DefaultShortCircuitConfirmMs.</summary>
    public int ShortCircuitConfirmMs { get; set; }

    /// <summary>Legacy config compatibility only. Runtime uses ProductionTimingPolicy.DefaultWrongConnectionConfirmMs.</summary>
    public int WrongConnectionConfirmMs { get; set; } = 100;

    /// <summary>Legacy config compatibility only. Runtime uses ProductionTimingPolicy.DefaultProductSettleTimeMs.</summary>
    public int ProductSettleTimeMs { get; set; } = 200;

    /// <summary>Legacy config compatibility only. Runtime uses ProductionTimingPolicy.DefaultJigContactUnstableWindowMs.</summary>
    public int JigContactUnstableWindowMs { get; set; } = 1000;

    /// <summary>Chu kỳ bảo trì Probe Pin dùng chung; counter vẫn tách theo model.</summary>
    public long ProbeReplacementThreshold { get; set; } = 200_000;

    // Compatibility cho config cũ. Normalize đồng bộ về ShortCircuitConfirmMs.
    public int ShortConfirmMs { get; set; } = 0;

    /// <summary>Thời gian OUTPUT mở/đẩy JIG giữ ON trước khi cưỡng bức OFF.</summary>
    public int Relay1JigPulseMs { get; set; } = 120;

    /// <summary>Thời gian OUTPUT MARKING giữ ON trước khi cưỡng bức OFF.</summary>
    public int Relay2MarkingPulseMs { get; set; } = 120;

    /// <summary>Bật/tắt OUTPUT JIG trong chuỗi PASS/FAIL/Master.</summary>
    public bool JigEjectRelayEnabled { get; set; } = true;

    /// <summary>Bật/tắt OUTPUT MARKING trong chuỗi PASS. FAIL/Master không bao giờ dùng MARKING.</summary>
    public bool PassMarkingRelayEnabled { get; set; } = true;

    /// <summary>PASS sequence: false = MARKING trước JIG; true = JIG trước MARKING.</summary>
    public bool PassJigRelayFirst { get; set; }

    /// <summary>Trường tương thích file cfg cũ; runtime Universal Tester New bỏ qua hoàn toàn.</summary>
    public int RelayWiringMode { get; set; }

    /// <summary>Relay vật lý 1 của Universal Tester New = firmware OUTPUT channel 0. Cố định, không cho đổi mapping.</summary>
    public int JigRelayChannel { get; set; } = 0;

    /// <summary>Relay vật lý 5 của Universal Tester New = firmware OUTPUT channel 4. Cố định, không cho đổi mapping.</summary>
    public int MarkingRelayChannel { get; set; } = 4;

    /// <summary>Trường tương thích cfg cũ; luôn biểu diễn relay vật lý 1 mở JIG.</summary>
    public int FaultJigRelayNumber { get; set; } = 1;

    /// <summary>Khoảng chờ an toàn sau OUTPUT MARKING OFF trước OUTPUT JIG ON trong chu trình PASS.</summary>
    public int PassMarkingToJigDelayMs { get; set; } = 120;

    /// <summary>Firmware production PASS: first PASSPEN timing parameter (trace default 300 ms).</summary>
    public int PassPenCloseMs { get; set; } = 300;

    /// <summary>Firmware production PASS: second PASSPEN timing parameter (trace default 200 ms).</summary>
    public int PassPenReleaseMs { get; set; } = 200;

    /// <summary>Firmware removal: first UNCONNECT timing parameter (trace default 400 ms).</summary>
    public int UnconnectCloseMs { get; set; } = 400;

    /// <summary>Firmware removal: second UNCONNECT timing parameter (trace default 200 ms).</summary>
    public int UnconnectReleaseMs { get; set; } = 200;

    /// <summary>Compatibility V15.1 trở về trước: "R1,R2". V15.2 UI không còn dùng trực tiếp.</summary>
    public string StampDelay { get; set; } = "250,250";

    public int OversizeWaitSeconds { get; set; }

    public int ShieldDelay { get; set; } = 1;

    public int ResistanceDelayMs { get; set; }

    // ============================================================
    // GIAO DIỆN
    // ============================================================

    public int ItemHeight { get; set; } = 31;

    public int ScrollDelay { get; set; } = 15;

    public int PageDelay { get; set; } = 30;

    public bool ShowTitle { get; set; } = true;

    public bool ShowConnector { get; set; }

    // ============================================================
    // MODEL
    // ============================================================

    public string LastThtPath { get; set; } = string.Empty;
    public string LastThtPartKey { get; set; } = string.Empty;

    // ============================================================
    // ĐO ĐIỆN TRỞ R1-R10
    // ============================================================

    public ResistanceChannelSetting[] ResistanceChannels { get; set; } =
    [
        new()
        {
            Enabled = false,
            Name = "R1",
            Channel = 1,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R2",
            Channel = 2,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R3",
            Channel = 3,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R4",
            Channel = 4,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R5",
            Channel = 5,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R6",
            Channel = 6,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R7",
            Channel = 7,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R8",
            Channel = 8,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R9",
            Channel = 9,
            MinOhm = 0,
            MaxOhm = 0
        },
        new()
        {
            Enabled = false,
            Name = "R10",
            Channel = 10,
            MinOhm = 0,
            MaxOhm = 0
        }
    ];

    // ============================================================
    // LỊCH SỬ / IN TEM
    // ============================================================

    /// <summary>In tem tự động ngay khi sản phẩm PASS.</summary>
    public bool AutoPrintLabelOnPass { get; set; } = true;

    /// <summary>Thư mục tương đối chứa DB/export lịch sử.</summary>
    public string HistoryDirectory { get; set; } = "Data";

    // ============================================================
    // MÁY IN TEM
    // ============================================================

    public LabelSettings Label { get; set; } = new();
}

public sealed class ProductLotSettings
{
    /// <summary>
    /// LOTNO bắt đầu do operator đặt một lần cho mã hàng. -1 chỉ xuất hiện khi
    /// đọc cấu hình cũ chưa có field này và sẽ được migrate từ LotNo hiện tại.
    /// </summary>
    public long StartLotNo { get; set; } = -1;

    /// <summary>LOT kế tiếp thực tế dành cho giao dịch in tem an toàn.</summary>
    public long LotNo { get; set; }

    public string LotNoDate { get; set; } = string.Empty;
}

public sealed class ResistanceChannelSetting
{
    public bool Enabled { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Channel { get; set; }

    public double MinOhm { get; set; }

    public double MaxOhm { get; set; }
}

public sealed class LabelSettings
{
    public const string LargeTemplate = "TEM_TO";
    public const string SmallTemplate = "TEM_BE";
    public const string SmallQrTemplate = "TEM_BE_QR";

    /// <summary>
    /// Tên máy in được cài trong Windows.
    /// Có thể để trống nếu gửi EPL trực tiếp qua COM.
    /// </summary>
    public string PrinterName { get; set; } = string.Empty;

    /// <summary>
    /// Cổng COM của máy in tem, ví dụ COM3.
    /// </summary>
    public string PrinterCom { get; set; } = string.Empty;

    public int WidthMm { get; set; } = 90;

    public int HeightMm { get; set; } = 15;

    public string FormatName { get; set; } = "KS91";

    public int BaudRate { get; set; } = 9600;

    public int WriteTimeoutMs { get; set; } = 3000;

    public int Copies { get; set; } = 1;

    /// <summary>Chọn một trong ba mẫu lệnh in tích hợp trong EXE.</summary>
    public string TemplateType { get; set; } = LargeTemplate;

    /// <summary>Explicit profile/template selection. No part-number inference is allowed.</summary>
    public string TemplatePath { get; set; } = string.Empty;

    /// <summary>UTF-8/Base64 overrides stored in the single canonical CFG.</summary>
    public string LargeTemplateOverrideBase64 { get; set; } = string.Empty;
    public string SmallTemplateOverrideBase64 { get; set; } = string.Empty;
    public string SmallQrTemplateOverrideBase64 { get; set; } = string.Empty;

    public string EncodingName { get; set; } = "us-ascii";

    /// <summary>Optional legacy raw destination such as LPT1.</summary>
    public string RawDestination { get; set; } = string.Empty;

    public string ExternalHelperPath { get; set; } = string.Empty;

    public string ExternalHelperArgument { get; set; } = string.Empty;

    public string ExternalPrintFile { get; set; } = "print.txt";
}
