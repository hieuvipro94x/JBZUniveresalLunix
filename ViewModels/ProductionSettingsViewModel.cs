using System.Collections.ObjectModel;
using System.IO;
using JBZUniveresalLunix.Core;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Services;

namespace JBZUniveresalLunix.ViewModels;

public sealed class ProductionSettingsViewModel : ObservableObject
{
    private int _masterFaultRequiredCount;
    private readonly TestViewModel? _test;
    private readonly string _modelPath;
    private readonly string _lotProductKey;
    private bool _manualRuntimeActive;
    private string _manualRelay0Status = "OFF";
    private string _manualRelay1Status = "OFF";
    private string _manualPowerStatus = "OFF";
    private string _manualStatus = "Manual OFF";
    private int _selectedManualResistanceChannel;
    private bool _manualResistanceRunning;
    private string _manualResistanceStatus = "Chọn TẤT CẢ hoặc một CH để đo";
    private bool _manualWaterProofRunning;
    private string _manualWaterProofStatus = "Chọn COM và bấm CHẠY TEST";
    private readonly double?[] _manualWaterProofPressBaseline = new double?[3];

    public ProductionSettings Settings { get; }
    public ObservableCollection<ResistanceChannelEditor> ResistanceChannels { get; }
    public ObservableCollection<ResistanceResult> ManualResistanceResults { get; } = new();
    public ObservableCollection<WaterProofChannelResult> ManualWaterProofResults { get; } = new();
    public WaterProofModelSettings WaterProof { get; }
    public IReadOnlyList<string> WaterProofConnectorOptions { get; }
    public string WaterProofModelKey =>
        ProductionConfigService.GetMasterModelKeyFromPath(_modelPath);
    public IReadOnlyList<ChannelOption> ChannelOptions { get; } =
    [
        new(0, "Không dùng"),
        new(1, "CH1"),
        new(2, "CH2"),
        new(3, "CH3"),
        new(4, "CH4"),
        new(5, "CH5"),
        new(6, "CH6"),
        new(7, "CH7"),
        new(8, "CH8"),
        new(9, "CH9"),
        new(10, "CH10")
    ];
    public IReadOnlyList<ChannelOption> ManualResistanceOptions { get; } =
    [
        new(0, "TẤT CẢ CH ĐÃ BẬT"),
        new(1, "CH1"),
        new(2, "CH2"),
        new(3, "CH3"),
        new(4, "CH4"),
        new(5, "CH5"),
        new(6, "CH6"),
        new(7, "CH7"),
        new(8, "CH8"),
        new(9, "CH9"),
        new(10, "CH10")
    ];

    public string MasterModelKey =>
        ProductionConfigService.GetMasterModelKeyFromPath(_modelPath);

    public int MasterFaultRequiredCount
    {
        get => _masterFaultRequiredCount;
        set => Set(ref _masterFaultRequiredCount, Math.Clamp(value, 0, 99));
    }

    public bool IsManualPanelVisible => true;

    public bool ManualRuntimeActive
    {
        get => _manualRuntimeActive;
        private set
        {
            if (Set(ref _manualRuntimeActive, value))
                RefreshManualCommands();
        }
    }

    public string ManualRelay0Status
    {
        get => _manualRelay0Status;
        private set => Set(ref _manualRelay0Status, value);
    }

    public string ManualRelay1Status
    {
        get => _manualRelay1Status;
        private set => Set(ref _manualRelay1Status, value);
    }

    public string ManualPowerStatus
    {
        get => _manualPowerStatus;
        private set => Set(ref _manualPowerStatus, value);
    }

    public string ManualStatus
    {
        get => _manualStatus;
        private set => Set(ref _manualStatus, value);
    }

    public int SelectedManualResistanceChannel
    {
        get => _selectedManualResistanceChannel;
        set => Set(ref _selectedManualResistanceChannel, Math.Clamp(
            value,
            ResistanceMeasurementPlan.DisabledChannel,
            ResistanceMeasurementPlan.MaxChannel));
    }

    public string ManualResistanceStatus
    {
        get => _manualResistanceStatus;
        private set => Set(ref _manualResistanceStatus, value);
    }

    public string ManualWaterProofStatus
    {
        get => _manualWaterProofStatus;
        private set => Set(ref _manualWaterProofStatus, value);
    }

    public AsyncRelayCommand ManualRelay0OnCommand { get; }
    public AsyncRelayCommand ManualRelay0OffCommand { get; }
    public AsyncRelayCommand ManualRelay1OnCommand { get; }
    public AsyncRelayCommand ManualRelay1OffCommand { get; }
    public AsyncRelayCommand ManualResetCommand { get; }
    public AsyncRelayCommand ManualMeasureResistanceCommand { get; }
    public AsyncRelayCommand ManualWaterProofTestCommand { get; }

    public ProductionSettingsViewModel(TestViewModel? test = null)
    {
        _test = test;
        Settings = ProductionConfigService.Load();
        _modelPath = test?.CurrentModelPath ?? Settings.LastThtPath;
        _lotProductKey = ProductionConfigService.GetLotProductKey(
            test?.PartNumber,
            _modelPath,
            test?.ModelName);
        ProductLotSettings productLot = ProductionConfigService.GetOrCreateProductLot(
            Settings,
            _lotProductKey,
            migrateCurrentLot: true);
        // Trường trên màn Cài đặt là LOTNO bắt đầu, không phải LOT kế tiếp đã
        // tăng trong quá trình sản xuất.
        Settings.LotNo = productLot.StartLotNo;
        Settings.LotNoDate = productLot.LotNoDate;
        if (!string.IsNullOrWhiteSpace(_modelPath))
            Settings.LastThtPath = _modelPath;
        Settings.ManualModeEnabled = false;
        _manualRuntimeActive = test?.IsManualModeActive == true;
        _manualStatus = "Sẵn sàng thao tác tay - không cần lưu cài đặt";
        ResistanceChannels = new ObservableCollection<ResistanceChannelEditor>(
            ProductionConfigService.GetResistanceProfileForPath(Settings, _modelPath).Select((setting, index) =>
                new ResistanceChannelEditor(setting, index + 1)));
        WaterProof = ProductionConfigService.GetWaterProofProfileForPath(
            Settings, _modelPath);
        ResetManualWaterProofResults(WaterProof);
        WaterProofConnectorOptions = LoadWaterProofConnectorOptions(test, _modelPath);
        _masterFaultRequiredCount = ProductionConfigService.GetMasterFaultRequiredCountForPath(
            Settings, _modelPath);

        ManualRelay0OnCommand = new AsyncRelayCommand(
            async () => await RunManualRelayCommandAsync(JbzBoardTransportAdapter.JigOutputChannel, true),
            CanUseManualControls);
        ManualRelay0OffCommand = new AsyncRelayCommand(
            async () => await RunManualRelayCommandAsync(JbzBoardTransportAdapter.JigOutputChannel, false),
            CanUseManualControls);
        ManualRelay1OnCommand = new AsyncRelayCommand(
            async () => await RunManualRelayCommandAsync(JbzBoardTransportAdapter.MarkingOutputChannel, true),
            CanUseManualControls);
        ManualRelay1OffCommand = new AsyncRelayCommand(
            async () => await RunManualRelayCommandAsync(JbzBoardTransportAdapter.MarkingOutputChannel, false),
            CanUseManualControls);
        ManualResetCommand = new AsyncRelayCommand(
            RunManualResetAsync,
            CanUseManualControls);
        ManualMeasureResistanceCommand = new AsyncRelayCommand(
            RunManualResistanceAsync,
            CanUseManualResistance);
        ManualWaterProofTestCommand = new AsyncRelayCommand(
            RunManualWaterProofAsync,
            CanUseManualWaterProof);
    }

    private static IReadOnlyList<string> LoadWaterProofConnectorOptions(
        TestViewModel? test,
        string? thtPath)
    {
        IReadOnlyList<string> current = test?.CurrentConnectorIds ?? [];
        if (current.Count > 0)
            return current;

        if (string.IsNullOrWhiteSpace(thtPath) || !File.Exists(thtPath))
            return [];

        try
        {
            return new JbzModelParser().Load(thtPath.Trim()).Connectors
                .Select(connector => connector.ConnectorId)
                .Where(connector => !string.IsNullOrWhiteSpace(connector))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AsyncFileLogService.Current.Error(
                $"Không đọc được danh sách connector THT cho cấu hình Leak: {ex.Message}");
            return [];
        }
    }

    public void SetManualRuntimeActive(bool active)
    {
        ManualRuntimeActive = active;
        ApplyManualOutputStatus();
        ManualStatus = active
            ? $"MANUAL - OUT1 JIG={ManualRelay0Status} • OUT2 MARK={ManualRelay1Status} • OUT5 POWER={ManualPowerStatus}"
            : "Manual OFF";
    }

    private bool CanUseManualControls() =>
        _test is not null &&
        !_test.IsDeviceFault &&
        !_manualResistanceRunning &&
        !_manualWaterProofRunning &&
        (_test.IsManualModeActive || _test.CanEnterManualMode);

    private bool CanUseManualResistance() => CanUseManualControls();

    private bool CanUseManualWaterProof() => CanUseManualControls();

    private async Task RunManualWaterProofAsync()
    {
        if (_test is null)
            return;

        string portName = Settings.WaterProofMachine.PortName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(portName))
            throw new InvalidOperationException("Hãy chọn cổng COM UART/RS232 của máy Leak trước khi chạy test.");
        if (!string.IsNullOrWhiteSpace(Settings.Label.PrinterCom) &&
            string.Equals(portName, Settings.Label.PrinterCom.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{portName} đang được cấu hình cho máy in tem. Máy Leak phải dùng một cổng COM riêng.");
        }
        if (WaterProof.EnabledChannelCount == 0)
            throw new InvalidOperationException("Hãy bật ít nhất một kênh CH1/CH2/CH3 trước khi chạy test.");
        if (WaterProof.PressTimeMs is < 1 or > 300000 ||
            WaterProof.WaitTimeMs is < 1 or > 300000)
        {
            throw new InvalidOperationException("Thời gian tạo áp/giữ áp phải từ 1 đến 300000 ms.");
        }

        var machine = new WaterProofMachineSettings
        {
            PortName = portName,
            BaudRate = WaterProofMachineSettings.DefaultBaudRate,
            AutoConnect = Settings.WaterProofMachine.AutoConnect,
            ReadTimeoutMs = Settings.WaterProofMachine.ReadTimeoutMs,
            WriteTimeoutMs = Settings.WaterProofMachine.WriteTimeoutMs
        };
        WaterProofModelSettings profile = WaterProof.Clone();

        _manualWaterProofRunning = true;
        ResetManualWaterProofResults(profile);
        ManualWaterProofStatus = $"ĐANG KẾT NỐI {portName} • 115200 8N1...";
        RefreshManualCommands();

        try
        {
            WaterProofRunResult result = await _test.TestManualWaterProofAsync(
                machine,
                profile,
                progress => UpdateManualWaterProofProgress(portName, progress));
            ApplyManualWaterProofResult(result);
            string channels = string.Join(" • ", result.Channels
                .Where(channel => channel.Enabled)
                .Select(channel => $"CH{channel.Channel} {(channel.Passed ? "PASS" : "FAIL")} Δ{channel.Leak:0.###}"));
            ManualWaterProofStatus =
                $"TEST {(result.Passed ? "PASS" : "FAIL")} • {channels}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ManualWaterProofStatus = $"{portName} ĐANG BỊ CHIẾM DỤNG";
            throw new InvalidOperationException(
                $"Không mở được {portName}: cổng đang bị chương trình khác chiếm dụng.",
                ex);
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"Manual Leak test failed: {ex}");
            ManualWaterProofStatus = $"TEST LEAK LỖI • {ex.Message}";
            throw;
        }
        finally
        {
            _manualWaterProofRunning = false;
            ManualRuntimeActive = _test.IsManualModeActive;
            RefreshManualCommands();
        }
    }

    private void UpdateManualWaterProofProgress(string portName, WaterProofProgress progress)
    {
        string stage = progress.Stage switch
        {
            WaterProofStage.Pressurizing => "ĐANG TẠO ÁP",
            WaterProofStage.Waiting => "ĐANG ĐO ĐỘ RÒ",
            WaterProofStage.Evaluating => "ĐANG ĐÁNH GIÁ",
            _ => "ĐANG TEST"
        };
        string values = progress.Values.Count == 0
            ? string.Empty
            : " • " + string.Join(" / ", progress.Values.Select(value =>
                value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        ManualWaterProofStatus = $"{stage} {portName}{values}";

        if (progress.Stage is not (WaterProofStage.Pressurizing or WaterProofStage.Waiting))
            return;

        int count = Math.Min(3, progress.Values.Count);
        for (int index = 0; index < count; index++)
        {
            WaterProofChannelResult row = ManualWaterProofResults[index];
            if (!row.Enabled)
                continue;

            double current = progress.Values[index];
            row.LiveMachineValue = current;
            if (progress.Stage == WaterProofStage.Pressurizing)
            {
                _manualWaterProofPressBaseline[index] = current;
                row.PressPressure = current;
                row.FirstResultPressure = current;
                row.Leak = 0.0;
            }
            else
            {
                row.WaitPressure = current;
                row.SecondResultPressure = current;
                if (_manualWaterProofPressBaseline[index] is double baseline)
                    row.Leak = Math.Abs(baseline - current);
            }
        }
    }

    private void ResetManualWaterProofResults(WaterProofModelSettings profile)
    {
        Array.Clear(_manualWaterProofPressBaseline, 0, _manualWaterProofPressBaseline.Length);
        ManualWaterProofResults.Clear();
        for (int channel = 1; channel <= 3; channel++)
        {
            ManualWaterProofResults.Add(new WaterProofChannelResult
            {
                Channel = channel,
                Enabled = profile.IsChannelEnabled(channel),
                Connector = profile.ConnectorForChannel(channel),
                LeakLimit = profile.LeakLimit
            });
        }
    }

    private void ApplyManualWaterProofResult(WaterProofRunResult result)
    {
        foreach (WaterProofChannelMeasurement measurement in result.Channels)
        {
            if (!measurement.Enabled)
                continue;

            WaterProofChannelResult? row = ManualWaterProofResults.FirstOrDefault(
                item => item.Channel == measurement.Channel);
            if (row is null)
                continue;

            row.PressPressure = measurement.FirstPressure;
            row.WaitPressure = measurement.SecondPressure;
            row.FirstResultPressure = measurement.FirstPressure;
            row.SecondResultPressure = measurement.SecondPressure;
            row.Leak = measurement.Leak;
            row.LiveMachineValue = measurement.Leak;
            row.Passed = measurement.Passed;
            row.IsMeasured = measurement.Enabled;
        }
    }

    private async Task RunManualRelayCommandAsync(int relay, bool turnOn)
    {
        if (_test is null)
            return;

        string role = relay == JbzBoardTransportAdapter.JigOutputChannel
            ? "OUT1 / JIG"
            : "OUT2 / MARK";
        ManualStatus = $"Đang {(turnOn ? "BẬT" : "TẮT")} {role}...";

        try
        {
            await _test.SetManualRelayAsync(relay, turnOn);
            ManualRuntimeActive = _test.IsManualModeActive;
            ApplyManualOutputStatus();
            ManualStatus =
                $"MANUAL - OUT1 JIG={ManualRelay0Status} • OUT2 MARK={ManualRelay1Status} • OUT5 POWER={ManualPowerStatus}";
            RefreshManualCommands();
        }
        catch
        {
            ApplyManualOutputStatus();
            ManualStatus = "MANUAL FAULT - kiểm tra DeviceFault";
            RefreshManualCommands();
            throw;
        }
    }

    private void ApplyManualOutputStatus()
    {
        bool jigOn = _test?.ManualJigRelayOn == true;
        bool markingOn = _test?.ManualMarkingRelayOn == true;
        ManualRelay0Status = jigOn ? "ON" : "OFF";
        ManualRelay1Status = markingOn ? "ON" : "OFF";
        ManualPowerStatus = jigOn || markingOn ? "ON" : "OFF";
    }

    private async Task RunManualResetAsync()
    {
        if (_test is null)
            return;

        ManualStatus = "Đang reset manual...";
        try
        {
            await _test.ResetManualOutputsAsync();
            ManualRuntimeActive = _test.IsManualModeActive;
            ApplyManualOutputStatus();
            ManualStatus = "MANUAL - reset complete, cả 2 relay OFF";
            RefreshManualCommands();
        }
        catch
        {
            ApplyManualOutputStatus();
            ManualStatus = "MANUAL FAULT - kiểm tra DeviceFault";
            RefreshManualCommands();
            throw;
        }
    }

    private async Task RunManualResistanceAsync()
    {
        if (_test is null)
            return;

        var snapshot = new ProductionSettings
        {
            ResistanceChannels = ResistanceChannels
                .Select(editor => editor.ToSetting())
                .ToArray()
        };
        List<ResistanceStep> steps = ResistanceMeasurementPlan.BuildManualSteps(
            snapshot,
            SelectedManualResistanceChannel);
        if (steps.Count == 0)
        {
            throw new InvalidOperationException(SelectedManualResistanceChannel == 0
                ? "Chưa bật kênh điện trở nào. Hãy tích BẬT cho ít nhất một dòng R."
                : $"CH{SelectedManualResistanceChannel} chưa được gán cho dòng R nào.");
        }

        _manualResistanceRunning = true;
        ManualResistanceResults.Clear();
        for (int index = 0; index < steps.Count; index++)
        {
            ResistanceStep step = steps[index];
            ManualResistanceResults.Add(new ResistanceResult
            {
                Name = step.Name,
                Channel = step.Channel,
                MinOhm = step.MinOhm,
                MaxOhm = step.MaxOhm,
                MeasurementStatus = index == 0 ? "ĐANG ĐO" : "CHỜ ĐO"
            });
        }
        ManualResistanceStatus =
            $"ĐANG ĐO {string.Join(", ", steps.Select(step => $"{step.Name}/CH{step.Channel}"))}...";
        RefreshManualCommands();

        try
        {
            IReadOnlyList<ResistanceResult> results =
                await _test.MeasureManualResistanceAsync(
                    steps,
                    UpdateManualResistanceResult);
            foreach (ResistanceResult result in results)
                UpdateManualResistanceResult(result);

            int passed = results.Count(result => result.Passed);
            ManualResistanceStatus =
                $"HOÀN THÀNH {results.Count} KÊNH • PASS {passed} • FAIL {results.Count - passed}";
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"Manual resistance measurement failed: {ex}");
            for (int index = 0; index < ManualResistanceResults.Count; index++)
            {
                ResistanceResult current = ManualResistanceResults[index];
                if (current.ResultText != "ĐANG ĐO")
                    continue;

                ManualResistanceResults[index] = new ResistanceResult
                {
                    Name = current.Name,
                    Channel = current.Channel,
                    MinOhm = current.MinOhm,
                    MaxOhm = current.MaxOhm,
                    MeasurementStatus = "LỖI"
                };
            }
            ManualResistanceStatus = "MẤT KẾT NỐI MÁY TEST - VUI LÒNG KHỞI ĐỘNG LẠI";
            throw;
        }
        finally
        {
            _manualResistanceRunning = false;
            ManualRuntimeActive = _test.IsManualModeActive;
            RefreshManualCommands();
        }
    }

    private void UpdateManualResistanceResult(ResistanceResult update)
    {
        int index = -1;
        for (int candidate = 0; candidate < ManualResistanceResults.Count; candidate++)
        {
            ResistanceResult current = ManualResistanceResults[candidate];
            if (current.Channel == update.Channel &&
                string.Equals(current.Name, update.Name, StringComparison.OrdinalIgnoreCase))
            {
                index = candidate;
                break;
            }
        }

        if (index >= 0)
            ManualResistanceResults[index] = update;
        else
            ManualResistanceResults.Add(update);

        if (update.ResultText == "ĐANG ĐO")
        {
            ManualResistanceStatus =
                $"ĐANG ĐO {update.Name}/CH{update.Channel} • chờ giá trị ổn định...";
        }
    }

    public void Save()
    {
        Settings.ExpansionCardCount = Math.Clamp(
            Settings.ExpansionCardCount,
            1,
            BoardCapacity.MaxExpansionCardCount);
        Settings.StartCardNumber = Math.Clamp(
            Settings.StartCardNumber,
            1,
            BoardCapacity.MaxExpansionCardCount);
        Settings.ExpansionCardCount = Math.Min(
            Settings.ExpansionCardCount,
            BoardCapacity.MaxExpansionCardCount - Settings.StartCardNumber + 1);
        Settings.CardCount = BoardCapacity.FromSettings(Settings).ScanCardCount;
        Settings.ResistanceChannels = ResistanceChannels
            .Select(editor => editor.ToSetting())
            .ToArray();
        ProductionConfigService.SetResistanceProfileForPath(
            Settings, _modelPath, Settings.ResistanceChannels);
        Settings.AutoMasterSequence = true;
        // Manual là thao tác runtime tức thời, không phải cấu hình cần lưu.
        Settings.ManualModeEnabled = false;
        ProductionConfigService.SetMasterFaultRequiredCountForPath(
            Settings, _modelPath, MasterFaultRequiredCount);
        ProductionConfigService.SetWaterProofProfileForPath(
            Settings, _modelPath, WaterProof);
        ProductionConfigService.SetProductLot(
            Settings,
            _lotProductKey,
            Settings.LotNo,
            DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        ProductionConfigService.Save(Settings);
    }

    private void RefreshManualCommands()
    {
        ManualRelay0OnCommand?.RaiseCanExecuteChanged();
        ManualRelay0OffCommand?.RaiseCanExecuteChanged();
        ManualRelay1OnCommand?.RaiseCanExecuteChanged();
        ManualRelay1OffCommand?.RaiseCanExecuteChanged();
        ManualResetCommand?.RaiseCanExecuteChanged();
        ManualMeasureResistanceCommand?.RaiseCanExecuteChanged();
        ManualWaterProofTestCommand?.RaiseCanExecuteChanged();
    }
}

public sealed record ChannelOption(int Value, string Display);

public sealed class ResistanceChannelEditor : ObservableObject
{
    private bool _enabled;
    private int _channelSelection;
    private double _minOhm;
    private double _maxOhm;

    public string Name { get; }
    public string Label { get; }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public int ChannelSelection
    {
        get => _channelSelection;
        set => Set(ref _channelSelection, Math.Clamp(
            value,
            0,
            ResistanceMeasurementPlan.MaxChannel));
    }

    public double MinOhm
    {
        get => _minOhm;
        set => Set(ref _minOhm, value);
    }

    public double MaxOhm
    {
        get => _maxOhm;
        set => Set(ref _maxOhm, value);
    }

    public ResistanceChannelEditor(ResistanceChannelSetting setting, int ordinal)
    {
        Name = $"R{ordinal}";
        Label = Name;
        _enabled = setting.Enabled;
        _channelSelection = Math.Clamp(
            setting.Channel,
            0,
            ResistanceMeasurementPlan.MaxChannel);
        _minOhm = setting.MinOhm;
        _maxOhm = setting.MaxOhm;
    }

    public ResistanceChannelSetting ToSetting() => new()
    {
        Enabled = Enabled,
        Name = Name,
        Channel = ChannelSelection,
        MinOhm = MinOhm,
        MaxOhm = MaxOhm
    };
}
