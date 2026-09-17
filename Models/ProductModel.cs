namespace JBZUniveresalLunix.Models;

public sealed record PinRecord(
    string Connector,
    string WireName,
    int IoNumber,
    string PinNumber,
    string SpliceName = "",
    string Section = "",
    string Color = "",
    string ConnectorPinCount = "",
    string PinType = "",
    string WireConnection = "",
    int OriginalOrder = 0);

public sealed record ConnectorPin(
    string LocalPinNumber,
    int PhysicalIo,
    string WireName,
    PinRecord Pin);

public sealed record ConnectorDefinition(
    string ConnectorId,
    int? DeclaredPinCount,
    IReadOnlyList<ConnectorPin> Pins);

/// <summary>
/// Một mạng dây theo đúng thứ tự xuất hiện trong file THT.
/// Htdrv gốc dùng I/O đầu tiên của mỗi network làm điểm kích (source),
/// các I/O còn lại là điểm phải trả A0 ACTIVE khi dây thông.
/// </summary>
public sealed record WireNet(
    string Name,
    IReadOnlyList<int> IoNumbers,
    IReadOnlyList<PinRecord> Pins)
{
    public int SourceIo => IoNumbers.Count > 0 ? IoNumbers[0] : 0;

    public IReadOnlyList<int> ExpectedActiveIo { get; } =
        IoNumbers.Count <= 1
            ? Array.Empty<int>()
            : IoNumbers.Skip(1).Distinct().ToArray();

    public bool IsSplice => IoNumbers.Count > 2;

    public string IoText => string.Join(" ↔ ", IoNumbers);
}


/// <summary>
/// Một nhánh CLIP trong THT. Quy tắc V12.4 theo cấu hình thực tế:
/// - AO/A0 là đầu chung của tất cả CLIP.
/// - aN chỉ là TÊN/THỨ TỰ của nhánh CLIP.
/// - Cột I/O trên chính row aN mới là I/O được cấu hình cho đầu còn lại.
/// Không được suy diễn a1=IO1, a2=IO2... nếu cột I/O khai báo khác.
/// </summary>
public sealed record ClipBranch(
    string Name,
    int BranchNumber,
    int TargetIo,
    PinRecord ClipPin,
    PinRecord? TargetPin)
{
    public string NetName => $"CLIP:{Name.ToUpperInvariant()}";
}

/// <summary>
/// Topology CLIP: mọi a1/a2/a3... có một đầu nối chung A0; đầu còn lại
/// của từng aN đi tới đúng I/O ghi trong cột I/O của row aN trong file THT.
/// </summary>
public sealed class ClipTopology
{
    public PinRecord CommonPin { get; }
    public IReadOnlyList<ClipBranch> Branches { get; }
    public int CommonIo => CommonPin.IoNumber;

    public ClipTopology(PinRecord commonPin, IReadOnlyList<ClipBranch> branches)
    {
        CommonPin = commonPin;
        Branches = branches;
    }

    public bool IsSpecialPin(PinRecord pin) =>
        ReferenceEquals(pin, CommonPin) ||
        Branches.Any(branch => ReferenceEquals(pin, branch.ClipPin));
}

public sealed class ProductModel
{
    public string ModelName { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string VehicleType { get; set; } = "";
    public string CustomerCode { get; set; } = "";

    // Các trường Part table gốc dùng trong ALL6.xls/history/label.
    public string Eco { get; set; } = "";
    public string Nco { get; set; } = "";
    public string Alc { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public long SourceLength { get; set; }
    public DateTime? SourceModifiedAt { get; set; }

    /// <summary>
    /// Additional static label values read from the Part table in the THT file.
    /// Keys are case-insensitive so a new THT column can be used by a template
    /// without adding another hard-coded renderer branch.
    /// </summary>
    public Dictionary<string, string> LabelVariables { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Label definition carried by this loaded THT model. Raw EPL found in the
    /// THT is preferred over an external profile template.
    /// </summary>
    public LabelTemplateDefinition LabelTemplate { get; set; } = new();

    public List<PinRecord> Pins { get; set; } = [];
    public List<WireNet> Nets { get; set; } = [];
    public List<ConnectorDefinition> Connectors { get; set; } = [];
    public List<string> TopologyWarnings { get; set; } = [];
    public List<ResistanceStep> ResistanceSteps { get; set; } = [];

    /// <summary>
    /// Hai I/O được khai báo bằng Connector=_DISCARD trong THT. Đây là một
    /// cặp tiếp điểm thường mở của thùng hàng lỗi, không thuộc topology sản phẩm.
    /// </summary>
    public List<int> DiscardContactIo { get; set; } = [];

    public bool HasDiscardInterlock => DiscardContactIo.Count == 2;

    /// <summary>
    /// I/O đặc biệt không đủ thông tin để dựng topology. AO/aN hợp lệ sẽ được
    /// đưa vào ClipTopology, chỉ dữ liệu special bị thiếu/malformed mới nằm đây.
    /// </summary>
    public HashSet<int> IgnoredIo { get; set; } = [];

    /// <summary>Topology cụm CLIP A0/AO + a1/a2/a3... nếu model có khai báo.</summary>
    public ClipTopology? Clip { get; set; }

    /// <summary>
    /// THT hợp lệ nhưng chưa khai báo bất kỳ chân/topology nào. Htdrv dùng loại
    /// file này như một màn hình quan sát để tìm I/O bằng đầu dò và dựng model.
    /// Đây không phải model Production và không được phép tạo PASS/FAIL.
    /// </summary>
    public bool IsIoMappingTemplate { get; set; }

    public int MaxIo
    {
        get
        {
            int pinMax = Pins.Count == 0 ? 0 : Pins.Max(x => x.IoNumber);
            int clipTargetMax = Clip is not null && Clip.Branches.Count > 0
                ? Clip.Branches.Max(branch => branch.TargetIo)
                : 0;
            int discardMax = DiscardContactIo.Count == 0
                ? 0
                : DiscardContactIo.Max();
            return Math.Max(Math.Max(pinMax, clipTargetMax), discardMax);
        }
    }
}

public sealed record LabelTemplateDefinition(
    string RawTemplate = "",
    int Copies = 1,
    string ProfileId = "",
    string BarcodeTemplate = "");

public enum LabelPrintMode
{
    NeedsOriginalTrace = 0,
    RawEpl = 1,
    RawZpl = 2,
    StoredForm = 3,
    ExternalTemplate = 4,
    ExternalHelper = 5
}

public sealed record LabelProfile(
    string Id,
    LabelPrintMode Mode,
    string TemplatePath = "",
    string StoredFormName = "",
    string ExternalHelperPath = "",
    string ExternalHelperArgument = "",
    string ExternalPrintFile = "",
    string EncodingName = "us-ascii",
    int Copies = 1,
    string VerificationStatus = "VERIFIED",
    string InlineTemplate = "");

public sealed record ResistanceStep(
    string Name,
    int Channel,
    double MinOhm,
    double MaxOhm,
    string RouteA = "",
    string RouteB = "");
