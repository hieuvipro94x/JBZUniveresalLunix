using System.Windows;
using JBZUniveresalLunix.Models;

namespace JBZUniveresalLunix.Views;

public partial class FaultConfirmationWindow : Window
{
    private readonly Func<int, PinRecord?>? _pinResolver;
    private readonly string? _requiredDiscardPassword;
    private bool _authorized;

    public FaultConfirmationWindow(
        IReadOnlyList<FaultDetail> faults,
        string footer,
        Func<int, PinRecord?>? pinResolver = null,
        string? requiredDiscardPassword = null,
        string? windowHeader = null)
    {
        InitializeComponent();

        _pinResolver = pinResolver;
        _requiredDiscardPassword = requiredDiscardPassword;
        if (!string.IsNullOrWhiteSpace(windowHeader))
        {
            Title = windowHeader;
            WindowHeaderText.Text = windowHeader;
        }

        if (requiredDiscardPassword is not null)
        {
            DiscardPasswordPanel.Visibility = Visibility.Visible;
            Loaded += (_, _) => DiscardPasswordBox.Focus();
            Closing += PreventUnauthorizedClose;
        }

        FaultDetail? primaryFault = faults
            .OrderBy(fault => FaultTypeCatalog.Priority(fault.Type))
            .FirstOrDefault();

        IReadOnlyList<OperatorFaultDisplay> displays = faults
            .Select(FaultDisplayFormatter.FormatOperator)
            .ToArray();

        string summary = primaryFault is null
            ? "KIỂM TRA SẢN PHẨM"
            : BuildShortSummary(primaryFault, _pinResolver);
        ApplyCompactSummary(summary);

        FaultItemsControl.ItemsSource = displays;
        FooterText.Text = footer ?? string.Empty;
    }

    private void ApplyCompactSummary(string summary)
    {
        string[] lines = summary
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        FaultTypeText.Text = lines.Length > 0 ? lines[0] : "LỖI SẢN PHẨM";
        SummaryText.Text = lines.Length > 1
            ? string.Join(Environment.NewLine, lines.Skip(1))
            : "VUI LÒNG KIỂM TRA SẢN PHẨM";
    }

    private static string BuildShortSummary(
        FaultDetail fault,
        Func<int, PinRecord?>? pinResolver)
    {
        if (fault.Type == ProductFaultType.SystemDeviceError)
            return "LỖI GIAO TIẾP THIẾT BỊ\n\nVUI LÒNG KHỞI ĐỘNG LẠI";

        if (fault.Type is not (ProductFaultType.WrongWiring or ProductFaultType.ShortCircuit))
            return FaultDisplayFormatter.FormatOperator(fault).Title;

        PinRecord? actualFrom = ResolveActualPin(fault.ActualSourceIo, pinResolver);
        PinRecord? actualTo = ResolveActualPin(fault.ActualTargetIo, pinResolver);

        // Popup vận hành phải ưu tiên tên dây thật trong THT.
        // Chỉ khi IO không có WireName/cấu hình trong THT mới hiện IO(n).
        string from = FormatCompactEndpoint(
            actualFrom,
            fault.ActualSourceIo,
            fallbackWireName: fault.WireName);

        string to = FormatCompactEndpoint(
            actualTo,
            fault.ActualTargetIo,
            fallbackWireName: null);

        bool fromMappedByName = HasWireName(actualFrom);
        bool toMappedByName = HasWireName(actualTo);

        if (fault.Type == ProductFaultType.ShortCircuit)
        {
            // Hai network đã cấu hình bị nối/chập với nhau:
            //   LỖI CHẬP MẠCH
            //   BG1 CHẬP VỚI BF2
            //
            // Nếu một đầu không có tên dây trong THT:
            //   BG1 CHẬP VỚI IO(12)
            return $"LỖI CHẬP MẠCH\n\n{from} CHẬP VỚI {to}";
        }

        // WRONG WIRING:
        // - Cả hai đầu có tên dây THT:
        //     BG1 NỐI NHẦM BF2
        // - Chỉ một đầu có tên dây THT:
        //     BG1 NỐI IO(12)
        //   Luôn đưa đầu có tên dây lên trước để người vận hành dễ hiểu.
        if (fromMappedByName && toMappedByName)
            return $"LỖI SAI DÂY\n\n{from} NỐI NHẦM {to}";

        if (fromMappedByName)
            return $"LỖI SAI DÂY\n\n{from} NỐI {to}";

        if (toMappedByName)
            return $"LỖI SAI DÂY\n\n{to} NỐI {from}";

        // Resolver/model có thể không được truyền vào popup. Khi đó vẫn tận dụng
        // WireName đã capture trong FaultDetail cho đầu nguồn nếu có.
        if (!string.IsNullOrWhiteSpace(fault.WireName))
        {
            string known = fault.WireName.Trim();
            int? otherIo = fault.ActualTargetIo ?? fault.ActualSourceIo;
            if (otherIo is int io && io > 0)
                return $"LỖI SAI DÂY\n\n{known} NỐI IO({io})";
        }

        return $"LỖI SAI DÂY\n\n{from} NỐI {to}";
    }

    private static bool HasWireName(PinRecord? pin) =>
        pin is not null && !string.IsNullOrWhiteSpace(pin.WireName);

    private static string FormatCompactEndpoint(
        PinRecord? pin,
        int? io,
        string? fallbackWireName)
    {
        if (pin is not null && !string.IsNullOrWhiteSpace(pin.WireName))
            return pin.WireName.Trim();

        if (!string.IsNullOrWhiteSpace(fallbackWireName))
            return fallbackWireName.Trim();

        return io is int value && value > 0
            ? $"IO({value})"
            : "IO(?)";
    }

    private static PinRecord? ResolveActualPin(
        int? io,
        Func<int, PinRecord?>? pinResolver)
    {
        if (pinResolver is null || io is not int value || value <= 0)
            return null;

        return pinResolver(value);
    }

    private static string FormatMappedEndpoint(PinRecord pin, int? io)
    {
        string wire = !string.IsNullOrWhiteSpace(pin.WireName)
            ? pin.WireName.Trim()
            : io is int value && value > 0
                ? $"IO {value}"
                : "DÂY";

        string housing = FormatHousing(pin.Connector);

        return string.IsNullOrWhiteSpace(housing)
            ? wire
            : $"{wire} [{housing}]";
    }

    private static string FormatFallbackEndpoint(
        string? wireName,
        string? connector,
        int? io)
    {
        string wire = !string.IsNullOrWhiteSpace(wireName)
            ? wireName.Trim()
            : io is int value && value > 0
                ? $"IO {value}"
                : "DÂY";

        string housing = FormatHousing(connector);

        return string.IsNullOrWhiteSpace(housing)
            ? wire
            : $"{wire} [{housing}]";
    }

    private static string FormatHousing(string? connector)
    {
        if (string.IsNullOrWhiteSpace(connector))
            return string.Empty;

        string value = connector.Trim();

        // THT lưu "1", "2", ...
        if (int.TryParse(value, out int directNumber) && directNumber > 0)
            return $"HOUSING {directNumber}";

        // Chuẩn hóa mọi kiểu tên connector thường gặp trong THT:
        // 1 / 2 / 3 / 4
        // HOLDER 1 / HOLDER1 / HOLDER-1
        // CONNECTOR 1 / CN1 / HOUSING 1
        // => luôn hiển thị HOUSING 1 / 2 / 3 / 4...
        // Chỉ đổi cách HIỂN THỊ, tuyệt đối không sửa dữ liệu model/THT.
        string[] prefixes = ["HOUSING", "HOLDER", "CONNECTOR", "CN"];
        foreach (string prefix in prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string suffix = value[prefix.Length..]
                .Trim()
                .TrimStart('-', '_', ':')
                .Trim();

            if (int.TryParse(suffix, out int number) && number > 0)
                return $"HOUSING {number}";

            if (prefix.Equals("HOUSING", StringComparison.OrdinalIgnoreCase))
                return value.ToUpperInvariant();
        }

        // ID đặc biệt: giữ nguyên ID thật, chỉ thêm nhãn HOUSING.
        return $"HOUSING {value}";
    }

    private static string ResolveFallbackKnownName(FaultDetail fault)
    {
        if (!string.IsNullOrWhiteSpace(fault.WireName))
        {
            string connector = !string.IsNullOrWhiteSpace(fault.ActualConnectorFrom)
                ? fault.ActualConnectorFrom
                : fault.ActualConnectorTo;

            return FormatFallbackEndpoint(fault.WireName, connector, null);
        }

        return "DÂY";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_requiredDiscardPassword is not null)
        {
            if (string.IsNullOrEmpty(_requiredDiscardPassword))
            {
                DiscardPasswordErrorText.Text =
                    "Chưa cài Mật khẩu thùng lỗi trong Cài đặt Production.";
                DiscardPasswordErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (!JBZUniveresalLunix.Services.AdminAuthenticationService.Verify(
                    _requiredDiscardPassword,
                    DiscardPasswordBox.Password))
            {
                DiscardPasswordErrorText.Text = "Mật khẩu xử lý hàng lỗi không đúng.";
                DiscardPasswordErrorText.Visibility = Visibility.Visible;
                DiscardPasswordBox.SelectAll();
                DiscardPasswordBox.Focus();
                return;
            }
        }

        _authorized = true;
        DialogResult = true;
        Close();
    }

    private void PreventUnauthorizedClose(
        object? sender,
        System.ComponentModel.CancelEventArgs e)
    {
        if (!_authorized && IsVisible)
            e.Cancel = true;
    }
}
