using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Services;
using JBZUniveresalLunix.ViewModels;

namespace JBZUniveresalLunix.Views;

/// <summary>
/// V12.9: trang Cài đặt nhúng trực tiếp trong MainWindow. Không tạo Window,
/// không xuất hiện thêm mục Alt+Tab và không mở một shell ứng dụng thứ hai.
/// </summary>
public partial class ProductionSettingsPage : UserControl
{
    private readonly MainViewModel? _main;
    private readonly ProductionSettingsViewModel _vm;
    private int _released;
    private int _portRefreshGeneration;
    private int _printerConnectionGeneration;
    private bool _printerPortSelectionInitialized;
    private bool _suppressPrinterPortSelection;

    public event EventHandler? SettingsSaved;
    public event EventHandler? RequestClose;

    public ProductionSettingsPage()
        : this(null)
    {
    }

    public ProductionSettingsPage(MainViewModel? main)
        : this(main, new ProductionSettingsViewModel(main?.Test))
    {
    }

    public ProductionSettingsPage(
        MainViewModel? main,
        ProductionSettingsViewModel viewModel)
    {
        _main = main;
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _vm;
        InitializeComboBoxItems();
        Loaded += ProductionSettingsPage_Loaded;
    }

    private async void ProductionSettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ProductionSettingsPage_Loaded;
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (IsReleased)
            return;

        await RefreshPortsAsync();
        if (IsReleased)
            return;
        _printerPortSelectionInitialized = true;
    }

    public void ReleasePageResources()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;

        Interlocked.Increment(ref _portRefreshGeneration);
        Interlocked.Increment(ref _printerConnectionGeneration);
        Loaded -= ProductionSettingsPage_Loaded;
        DataContext = null;
        SettingsSaved = null;
        RequestClose = null;
    }

    private bool IsReleased => Volatile.Read(ref _released) != 0;

    private Window? HostWindow => Window.GetWindow(this) ?? Application.Current?.MainWindow;

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double viewportWidth = SettingsScrollViewer?.ViewportWidth ?? 0;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = Math.Max(
                320,
                e.NewSize.Width - SystemParameters.VerticalScrollBarWidth);
        }

        UpdatePanelWidths(viewportWidth);
    }

    private void SettingsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.ViewportWidthChange) > 0.1 &&
            double.IsFinite(e.ViewportWidth) &&
            e.ViewportWidth > 0)
        {
            UpdatePanelWidths(e.ViewportWidth);
        }
    }

    private void UpdatePanelWidths(double viewportWidth)
    {
        // WPF có thể báo PositiveInfinity trong lượt đo ScrollViewer đầu tiên.
        // Width của FrameworkElement chỉ nhận số hữu hạn không âm.
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            return;

        if (IoSettingsPanel is null || RelaySettingsPanel is null || LabelSettingsPanel is null)
        {
            return;
        }

        // Ưu tiên ba nhóm chức năng trên cùng một hàng từ màn hình 1280 trở lên
        // theo chiều rộng thực của viewport. Không dùng Width của UserControl
        // vì phần thanh cuộn dọc sẽ làm mất viền phải của panel cuối.
        // Tỷ lệ 28/28/44 vẫn dành nhiều chỗ nhất cho TEM / ĐIỆN TRỞ.
        double available = Math.Max(
            320,
            viewportWidth - SettingsPanelsHost.Margin.Left - SettingsPanelsHost.Margin.Right);
        if (available >= 1160)
        {
            double content = available - 18;
            IoSettingsPanel.Width = Math.Floor(content * 0.28);
            RelaySettingsPanel.Width = Math.Floor(content * 0.28);
            LabelSettingsPanel.Width = Math.Max(
                470,
                content - IoSettingsPanel.Width - RelaySettingsPanel.Width);
            return;
        }

        if (available >= 760)
        {
            double halfPanel = Math.Floor((available - 12) / 2);
            IoSettingsPanel.Width = halfPanel;
            RelaySettingsPanel.Width = halfPanel;
            LabelSettingsPanel.Width = available - 6;
            return;
        }

        // Màn hình rất hẹp: mỗi panel một hàng. Giữ 500px tối thiểu để form
        // TEM không ép mất chữ; ScrollViewer chỉ hiện cuộn ngang ở trường hợp
        // ngoại lệ này thay vì cắt hẳn mép phải.
        double singlePanel = Math.Max(500, available - 6);
        IoSettingsPanel.Width = singlePanel;
        RelaySettingsPanel.Width = singlePanel;
        LabelSettingsPanel.Width = singlePanel;
    }

    private void InitializeComboBoxItems()
    {
        CardIoComboBox.ItemsSource = Enumerable
            .Range(1, BoardCapacity.MaxExpansionCardCount)
            .Select(n => new CardIoOption(
                n,
                n.ToString()))
            .ToArray();
        StartCardComboBox.ItemsSource = Enumerable
            .Range(1, BoardCapacity.MaxExpansionCardCount)
            .ToArray();

        if (_vm.Settings.ExpansionCardCount <= 0)
        {
            _vm.Settings.ExpansionCardCount =
                Math.Clamp(_vm.Settings.CardCount, 1, BoardCapacity.MaxExpansionCardCount);
        }

        _vm.Settings.ExpansionCardCount = Math.Clamp(
            _vm.Settings.ExpansionCardCount,
            1,
            BoardCapacity.MaxExpansionCardCount);
        _vm.Settings.StartCardNumber = Math.Clamp(
            _vm.Settings.StartCardNumber,
            1,
            BoardCapacity.MaxExpansionCardCount);

        BoardCapacity capacity = BoardCapacity.FromSettings(_vm.Settings);
        _vm.Settings.CardCount = capacity.ScanCardCount;
        RefreshTotalIoCapacity();

        IoConfirm1ComboBox.ItemsSource = Enumerable.Range(0, 128).ToArray();
        IoConfirmNComboBox.ItemsSource = Enumerable.Range(0, 32).ToArray();
        // Universal Tester New relay mapping is fixed by hardware/firmware:
        // Relay 1 = OUT0, Relay 5 = OUT4. No user-selectable relay channel list.
    }

    private void CardIoComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshTotalIoCapacity();

    private void RefreshTotalIoCapacity()
    {
        if (TotalIoCapacityText is null)
            return;

        int count = CardIoComboBox?.SelectedValue is int selected
            ? selected
            : _vm.Settings.ExpansionCardCount;
        count = Math.Clamp(count, 1, BoardCapacity.MaxExpansionCardCount);
        int start = StartCardComboBox?.SelectedItem is int selectedStart
            ? selectedStart
            : _vm.Settings.StartCardNumber;
        start = Math.Clamp(start, 1, BoardCapacity.MaxExpansionCardCount);
        int end = start + count - 1;
        TotalIoCapacityText.Text = end <= BoardCapacity.MaxExpansionCardCount
            ? $"{count * BoardCapacity.IoPerExpansionCard} IO • card {start}-{end}"
            : $"VƯỢT GIỚI HẠN CARD {BoardCapacity.MaxExpansionCardCount}";
    }

    private async void RefreshPrinterPorts_Click(object sender, RoutedEventArgs e) =>
        await RefreshPortsAsync();

    private async void PrinterComComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_printerPortSelectionInitialized || _suppressPrinterPortSelection || IsReleased)
            return;

        int generation = Interlocked.Increment(ref _printerConnectionGeneration);
        try
        {
            PrinterComComboBox
                .GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)
                ?.UpdateSource();

            string portName = _vm.Settings.Label.PrinterCom?.Trim() ?? string.Empty;
            // Giống phần mềm gốc: lựa chọn COM có hiệu lực ngay và được lưu để
            // lần PASS kế tiếp dùng đúng cổng, không cần một nút KẾT NỐI riêng.
            try
            {
                ProductionConfigService.Save(_vm.Settings);
            }
            catch (Exception saveError)
            {
                AsyncFileLogService.Current.Error(
                    $"Cannot persist automatic label printer port {portName}: {saveError}");
            }

            if (_main is null)
                return;

            if (string.IsNullOrWhiteSpace(portName))
            {
                await _main.Test.DisconnectLabelPrinterAsync();
                return;
            }

            LabelPrinterConnectionResult result =
                await _main.Test.ConnectLabelPrinterAsync(_vm.Settings.Label);
            if (!result.Connected &&
                !IsReleased &&
                generation == Volatile.Read(ref _printerConnectionGeneration))
            {
                AsyncFileLogService.Current.Error(
                    $"Automatic label printer selection failed on {portName}: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"Automatic label printer selection failed: {ex}");
        }
    }

    private void EditLabelTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string configured = _vm.Settings.Label.TemplatePath?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string path = ResolveConfiguredLabelTemplatePath();
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            EditBuiltInLabelTemplate();
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"Open label template editor failed: {ex}");
            ShowMessage("Chưa mở được mẫu tem. Vui lòng thử lại.", "CHỈNH TEM", MessageBoxImage.Warning);
        }
    }

    private void EditBuiltInLabelTemplate()
    {
        string templateType = LabelProfileResolver.NormalizeTemplateType(_vm.Settings.Label.TemplateType);
        string reference = BuiltInLabelTemplateStore.ReferenceFor(templateType);
        string defaultTemplate = BuiltInLabelTemplateStore.Load(reference);
        string savedOverride = BuiltInLabelTemplateStore.LoadOverride(_vm.Settings.Label, templateType);

        var editor = new Window
        {
            Title = $"CHỈNH TEM {templateType}",
            Owner = HostWindow,
            Width = 900,
            Height = 680,
            MinWidth = 640,
            MinHeight = 420,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var note = new TextBlock
        {
            Text = "Nội dung sửa sẽ được lưu cùng Cài đặt. Dùng KHÔI PHỤC MẶC ĐỊNH để bỏ bản tùy chỉnh.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(note, 0);
        layout.Children.Add(note);

        var textBox = new TextBox
        {
            Text = string.IsNullOrEmpty(savedOverride) ? defaultTemplate : savedOverride,
            AcceptsReturn = true,
            AcceptsTab = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 14
        };
        Grid.SetRow(textBox, 1);
        layout.Children.Add(textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var resetButton = new Button { Content = "KHÔI PHỤC MẶC ĐỊNH", MinWidth = 170, Margin = new Thickness(4) };
        var cancelButton = new Button { Content = "HỦY", MinWidth = 90, Margin = new Thickness(4), IsCancel = true };
        var saveButton = new Button { Content = "ÁP DỤNG", MinWidth = 110, Margin = new Thickness(4), IsDefault = true };
        resetButton.Click += (_, _) => textBox.Text = defaultTemplate;
        cancelButton.Click += (_, _) => editor.DialogResult = false;
        saveButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(textBox.Text))
            {
                MessageBox.Show(editor, "Mẫu tem không được để trống.", "CHỈNH TEM", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            editor.DialogResult = true;
        };
        buttons.Children.Add(resetButton);
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(saveButton);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        editor.Content = layout;

        if (editor.ShowDialog() != true)
            return;

        if (string.Equals(textBox.Text, defaultTemplate, StringComparison.Ordinal))
            BuiltInLabelTemplateStore.ClearOverride(_vm.Settings.Label, templateType);
        else
            BuiltInLabelTemplateStore.SaveOverride(_vm.Settings.Label, templateType, textBox.Text);

        ShowMessage(
            "Đã áp dụng mẫu tem. Bấm LƯU CÀI ĐẶT để hoàn tất.",
            "CHỈNH TEM",
            MessageBoxImage.Information);
    }

    private void PreviewLabel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LabelPrintRequest request = BuildSettingsLabelRequest("PREVIEW");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding = Encoding.GetEncoding(
                request.Profile.EncodingName,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            string extension = request.Profile.Mode == LabelPrintMode.RawZpl ? ".zpl" : ".txt";
            string directory = Path.Combine(Path.GetTempPath(), "JBZUniveresalLunix", "LabelPreview");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"PREVIEW_{SafeFileName(request.Profile.Id)}_LOT{request.Data.LotNo}{extension}");
            File.WriteAllBytes(path, encoding.GetBytes(request.Payload));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"Label preview failed: {ex}");
            ShowMessage("Chưa xem trước được tem. Vui lòng kiểm tra mẫu tem.", "XEM TRƯỚC TEM", MessageBoxImage.Warning);
        }
    }

    private async void TestPrintLabel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LabelPrintRequest request = BuildSettingsLabelRequest("TEST-PRINT");
            if (_main is null)
                throw new InvalidOperationException("Trang Cài đặt chưa được nối với chương trình chính.");
            LabelPrintTransportResult result = await _main.Test.PrintSettingsLabelAsync(request);
            ShowMessage(
                result.Printed
                    ? "Đã in thử tem. Không tăng LOT hoặc sản lượng."
                    : "Chưa in thử được tem. Hãy rút/cắm lại cáp và chọn lại cổng COM.",
                "IN THỬ TEM",
                result.Printed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"Test label print failed: {ex}");
            ShowMessage(
                "Chưa in thử được tem. Hãy rút/cắm lại cáp và chọn lại cổng COM.",
                "IN THỬ TEM",
                MessageBoxImage.Warning);
        }
    }

    private LabelPrintRequest BuildSettingsLabelRequest(string purpose)
    {
        string thtPath = _vm.Settings.LastThtPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(thtPath) || !File.Exists(thtPath))
            throw new FileNotFoundException("Chưa có file THT hiện tại để dựng dữ liệu tem.", thtPath);

        ProductModel model = new JbzModelParser().Load(thtPath);
        DateTime now = DateTime.Now;
        var history = new TestHistoryRecord
        {
            Finished = now,
            PartName = model.ProductName,
            PartNumber = model.PartNumber,
            Eco = model.Eco,
            Nco = model.Nco,
            Alc = model.Alc,
            LotNo = Math.Max(0, _vm.Settings.LotNo),
            ModelName = model.ModelName,
            ModelFile = model.SourcePath,
            CycleId = purpose + "-" + now.ToString("yyyyMMddHHmmssfff")
        };
        return LabelPrintRequest.Capture(history, model, _vm.Settings.Label);
    }

    private string ResolveConfiguredLabelTemplatePath()
    {
        string configured = _vm.Settings.Label.TemplatePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Template tích hợp phải được chỉnh bằng trình soạn thảo trong ứng dụng.");

        string path = Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured));
        if (!File.Exists(path))
            throw new FileNotFoundException("Không tìm thấy file template label.", path);
        return path;
    }

    private static string SafeFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new((value ?? string.Empty).Where(character => !invalid.Contains(character)).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "UNRESOLVED" : safe;
    }

    private async Task RefreshPortsAsync()
    {
        int generation = Interlocked.Increment(ref _portRefreshGeneration);
        try
        {
            string[] ports = await Task.Run(() => SerialPort.GetPortNames()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(GetComPortNumber)
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray());

            if (IsReleased || generation != Volatile.Read(ref _portRefreshGeneration))
                return;

            string savedPort = _vm.Settings.Label.PrinterCom?.Trim() ?? string.Empty;
            List<ComPortOption> options = ports
                .Select(x => new ComPortOption(x, x))
                .ToList();

            if (!string.IsNullOrWhiteSpace(savedPort) &&
                options.All(x => !string.Equals(x.PortName, savedPort, StringComparison.OrdinalIgnoreCase)))
            {
                options.Insert(0, new ComPortOption(savedPort, savedPort));
            }

            options.Insert(0, new ComPortOption(string.Empty, "Không dùng COM / dùng Windows printer"));
            _suppressPrinterPortSelection = true;
            try
            {
                PrinterComComboBox.ItemsSource = options;
                PrinterComComboBox.SelectedValue = savedPort;
            }
            finally
            {
                _suppressPrinterPortSelection = false;
            }

            string savedWaterProofPort = _vm.Settings.WaterProofMachine.PortName?.Trim() ?? string.Empty;
            WaterProofComComboBox.ItemsSource = ports;
            WaterProofComComboBox.Text = savedWaterProofPort;
        }
        catch (Exception ex)
        {
            if (IsReleased || generation != Volatile.Read(ref _portRefreshGeneration))
                return;

            _suppressPrinterPortSelection = true;
            try
            {
                PrinterComComboBox.ItemsSource = new[] { new ComPortOption(string.Empty, "Không dùng COM") };
                PrinterComComboBox.SelectedIndex = 0;
            }
            finally
            {
                _suppressPrinterPortSelection = false;
            }
            WaterProofComComboBox.ItemsSource = Array.Empty<string>();
            AsyncFileLogService.Current.Error($"COM port enumeration failed: {ex}");
            ShowMessage(
                "Chưa đọc được danh sách cổng kết nối. Vui lòng thử lại.",
                "Cổng COM",
                MessageBoxImage.Warning);
        }
    }

    private async void RefreshWaterProofPorts_Click(object sender, RoutedEventArgs e) =>
        await RefreshPortsAsync();

    private static int GetComPortNumber(string portName)
    {
        Match m = Regex.Match(portName, @"^COM(\d+)$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out int number)
            ? number
            : int.MaxValue;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PrinterComComboBox
                .GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedValueProperty)
                ?.UpdateSource();
            WaterProofComComboBox
                .GetBindingExpression(ComboBox.TextProperty)
                ?.UpdateSource();

            if (!ValidateSettings(out string error))
            {
                ShowMessage(error, "Cấu hình chưa hợp lệ", MessageBoxImage.Warning);
                return;
            }

            // Đồng bộ CardCount compatibility từ BoardCapacity ngay trước save.
            BoardCapacity capacity = BoardCapacity.FromSettings(_vm.Settings);
            _vm.Settings.CardCount = capacity.ScanCardCount;
            _vm.Save();

            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"Save production settings failed: {ex}");
            ShowMessage("Chưa lưu được Cài đặt. Vui lòng thử lại.", "CHƯA LƯU ĐƯỢC", MessageBoxImage.Error);
        }
    }

    public void SetManualRuntimeActive(bool active) =>
        _vm.SetManualRuntimeActive(active);

    public async Task ReleaseManualOutputsAsync()
    {
        if (_main?.Test.IsManualModeActive == true)
            await _main.Test.ResetManualOutputsAsync();
        _vm.SetManualRuntimeActive(false);
    }

    private bool ValidateSettings(out string error)
    {
        if (_vm.Settings.LotNo < 0)
        {
            error = "LOTNO phải là số nguyên từ 0 trở lên.";
            return false;
        }

        if (_vm.MasterFaultRequiredCount is < 0 or > 99)
        {
            error = "Số lỗi Master phải từ 0 đến 99. 0 = bỏ kiểm tra Master.";
            return false;
        }

        if (_vm.Settings.ExpansionCardCount is < 1 or > BoardCapacity.MaxExpansionCardCount)
        {
            error = $"Card mở rộng phải từ 1 đến {BoardCapacity.MaxExpansionCardCount}.";
            return false;
        }

        if (_vm.Settings.StartCardNumber is < 1 or > BoardCapacity.MaxExpansionCardCount ||
            _vm.Settings.StartCardNumber + _vm.Settings.ExpansionCardCount - 1 > BoardCapacity.MaxExpansionCardCount)
        {
            error = $"Card bắt đầu + số card mở rộng không được vượt card {BoardCapacity.MaxExpansionCardCount}.";
            return false;
        }

        BoardCapacity capacity = BoardCapacity.FromSettings(_vm.Settings);
        if (!capacity.IsRangeWithinSystem)
        {
            error =
                "Cấu hình card vượt phạm vi phần cứng hiện tại.\n\n" +
                $"Card mở rộng: {capacity.ExpansionCardCount}\n" +
                $"Tổng IO: {capacity.TotalIoCapacity}\n" +
                $"Giới hạn card: {BoardCapacity.MaxExpansionCardCount}.";
            return false;
        }

        if (_vm.Settings.IoConfirm1 is < 0 or > 127 || _vm.Settings.IoConfirmN is < 0 or > 31)
        {
            error = "Xác nhận IO1 phải 0..127 và IOn phải 0..31.";
            return false;
        }

        if (_vm.Settings.UsbDelay is < 1 or > 16)
        {
            error = "USB Delay phải từ 1 đến 16 ms.";
            return false;
        }

        if (_vm.Settings.PassPenCloseMs is < 50 or > 5000 || _vm.Settings.PassPenReleaseMs is < 50 or > 5000)
        {
            error = "PASSPEN đóng/nhả phải từ 50 đến 5000 ms.";
            return false;
        }

        if (_vm.Settings.UnconnectCloseMs is < 50 or > 5000 || _vm.Settings.UnconnectReleaseMs is < 50 or > 5000)
        {
            error = "UNCONNECT đóng/nhả phải từ 50 đến 5000 ms.";
            return false;
        }

        if (_vm.Settings.Relay1JigPulseMs is < 50 or > 5000)
        {
            error = "Thời gian pulse OUTPUT mở JIG phải từ 50 đến 5000 ms.";
            return false;
        }

        if (_vm.Settings.Relay2MarkingPulseMs is < 50 or > 5000)
        {
            error = "Thời gian pulse OUTPUT MARKING phải từ 50 đến 5000 ms.";
            return false;
        }

        if (_vm.Settings.PassMarkingToJigDelayMs is < 0 or > 5000)
        {
            error = "Delay PASS từ OUTPUT MARKING sang OUTPUT JIG phải từ 0 đến 5000 ms.";
            return false;
        }

        // Fixed physical mapping: Relay 1 = OUT0 (JIG), Relay 5 = OUT4 (MARKING).
        _vm.Settings.JigRelayChannel = JbzBoardTransportAdapter.Relay1OutputChannel;
        _vm.Settings.MarkingRelayChannel = JbzBoardTransportAdapter.Relay5OutputChannel;
        _vm.Settings.FaultJigRelayNumber = 1;
        _vm.Settings.StampDelay = $"{_vm.Settings.Relay1JigPulseMs},{_vm.Settings.Relay2MarkingPulseMs}";

        if (_vm.Settings.Label.WidthMm <= 0 || _vm.Settings.Label.HeightMm <= 0)
        {
            error = "Kích thước tem phải lớn hơn 0 mm.";
            return false;
        }

        if (_vm.Settings.Label.BaudRate <= 0 ||
            _vm.Settings.Label.WriteTimeoutMs < 100 ||
            _vm.Settings.Label.Copies is < 1 or > 20)
        {
            error = "Cấu hình máy in không hợp lệ: BaudRate > 0, timeout >= 100 ms, số bản in 1..20.";
            return false;
        }

        if (_vm.WaterProof.Enabled)
        {
            if (string.IsNullOrWhiteSpace(_vm.Settings.WaterProofMachine.PortName))
            {
                error = "Model đang bật kiểm tra kín nước nhưng chưa cấu hình COM UART/RS232.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_vm.Settings.Label.PrinterCom) &&
                string.Equals(
                    _vm.Settings.WaterProofMachine.PortName.Trim(),
                    _vm.Settings.Label.PrinterCom.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Máy Leak và máy in tem không được dùng chung một cổng COM.";
                return false;
            }

            if (_vm.WaterProof.EnabledChannelCount == 0)
            {
                error = "Kiểm tra kín nước phải chọn ít nhất một kênh CH1/CH2/CH3.";
                return false;
            }

            (string Channel, bool Enabled, string Connector)[] connectorMappings =
            [
                ("CH1", _vm.WaterProof.Channel1Enabled, _vm.WaterProof.Channel1Connector),
                ("CH2", _vm.WaterProof.Channel2Enabled, _vm.WaterProof.Channel2Connector),
                ("CH3", _vm.WaterProof.Channel3Enabled, _vm.WaterProof.Channel3Connector)
            ];
            foreach ((string channel, bool enabled, string connector) in connectorMappings)
            {
                if (!enabled)
                    continue;

                if (string.IsNullOrWhiteSpace(connector))
                {
                    error = $"{channel}: phải chọn connector THT trước khi bật kiểm tra kín nước.";
                    return false;
                }

                if (_vm.WaterProofConnectorOptions.Count == 0)
                {
                    error = "Không đọc được connector từ file THT hiện tại. Không thể bật kiểm tra kín nước.";
                    return false;
                }

                if (!_vm.WaterProofConnectorOptions.Contains(
                        connector.Trim(),
                        StringComparer.OrdinalIgnoreCase))
                {
                    error = $"{channel}: connector '{connector}' không tồn tại trong file THT hiện tại.";
                    return false;
                }
            }

            if (_vm.WaterProof.PressMin < 0 || _vm.WaterProof.LeakLimit < 0)
            {
                error = "Áp tối thiểu và độ sụt tối đa không được âm.";
                return false;
            }

            if (_vm.WaterProof.PressTimeMs is < 1 or > 300000 ||
                _vm.WaterProof.WaitTimeMs is < 1 or > 300000)
            {
                error = "Thời gian tạo áp/giữ áp phải từ 1 đến 300000 ms.";
                return false;
            }
        }

        foreach (ResistanceChannelEditor channel in _vm.ResistanceChannels)
        {
            if (channel.ChannelSelection is < ResistanceMeasurementPlan.DisabledChannel or
                > ResistanceMeasurementPlan.MaxChannel)
            {
                error = $"Kênh của {channel.Name} phải nằm trong khoảng 0 đến 10.";
                return false;
            }

            if (!double.IsFinite(channel.MinOhm) ||
                !double.IsFinite(channel.MaxOhm) ||
                channel.MinOhm < 0 ||
                channel.MaxOhm < channel.MinOhm)
            {
                error = $"{channel.Name}: Min Ω phải <= Max Ω và không được âm.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        await ReleaseManualOutputsAsync();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void ShowMessage(string message, string title, MessageBoxImage image)
    {
        if (IsReleased)
            return;

        Window? owner = HostWindow;
        if (owner is not null)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
    }

    private sealed record CardIoOption(int ExpansionCardCount, string Display);
    private sealed record ComPortOption(string PortName, string Display);
}
