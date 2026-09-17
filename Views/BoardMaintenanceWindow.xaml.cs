using System.ComponentModel;
using System.Windows;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Services;
using WinForms = System.Windows.Forms;

namespace JBZUniveresalLunix.Views;

public partial class BoardMaintenanceWindow : Window
{
    private readonly JbzBoardTransportAdapter _board;
    private JbzFirmwareImage? _image;
    private bool _running;

    public BoardMaintenanceWindow(JbzBoardTransportAdapter board)
    {
        _board = board;
        InitializeComponent();
        Closing += OnClosing;
    }

    private void SelectHex_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog { Filter = "Intel HEX (*.hex)|*.hex", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
        try
        {
            _image = JbzIntelHexParser.Load(dialog.FileName, requireProvenImageRange: true);
            FirmwarePathText.Text = dialog.FileName;
            ImageInfoText.Text = $"{_image.TotalBytes:N0} byte | {_image.Blocks.Count} packet | " +
                $"0x{_image.FirstAddress:X8}..0x{_image.LastAddressExclusive - 1:X8} | SHA-256 {_image.Sha256}";
            UpdateButton.IsEnabled = true;
            StatusText.Text = "HEX hợp lệ và nằm đúng vùng địa chỉ đã được trace xác nhận.";
        }
        catch (Exception ex)
        {
            _image = null; UpdateButton.IsEnabled = false; StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "HEX KHÔNG HỢP LỆ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void UpdateFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (_image is null || _running) return;
        if (!AdminAuthenticationService.VerifyBoardMaintenance(AdminPassword.Password))
        {
            MessageBox.Show(this, "Sai mật khẩu Admin.", "KHÔNG ĐƯỢC PHÉP", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        if (MessageBox.Show(this,
                "Xác nhận nạp firmware Universal Tester New? Sau khi nạp, phần mềm sẽ đọc lại *IDN? để xác nhận đúng phiên bản firmware đang chạy. Không ngắt nguồn hoặc cáp UART trong quá trình nạp.",
                "XÁC NHẬN NẠP FIRMWARE", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _running = true; UpdateButton.IsEnabled = false; AdminPassword.IsEnabled = false;
        try
        {
            var progress = new Progress<JbzFirmwareProgress>(item =>
            {
                FirmwareProgress.Value = item.Percent; StatusText.Text = item.Status;
                LogText.AppendText($"{DateTime.Now:HH:mm:ss.fff} {item.Stage} {item.Status}\r\n"); LogText.ScrollToEnd();
            });
            string idn = await _board.UpdateFirmwareAsync(_image, progress);
            string version = JbzProtocolParser.ExtractFirmwareVersion(idn);
            StatusText.Text = version.Length == 0
                ? $"Hoàn tất, bo đã trả IDN mới: {idn}"
                : $"Hoàn tất - Firmware đang chạy: V{version} ({idn})";
            MessageBox.Show(this, StatusText.Text, "NẠP FIRMWARE THÀNH CÔNG", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"FIRMWARE UPDATE FAILED: {ex}");
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "NẠP FIRMWARE THẤT BẠI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _running = false; AdminPassword.IsEnabled = true; UpdateButton.IsEnabled = _image is not null; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_running) return; e.Cancel = true;
        MessageBox.Show(this, "Không thể đóng cửa sổ khi bo đang ghi flash.", "ĐANG NẠP FIRMWARE", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
