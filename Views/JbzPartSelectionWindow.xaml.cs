using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using JBZUniveresalLunix.Models;
using JBZUniveresalLunix.Services;
using JBZUniveresalLunix.ViewModels;

namespace JBZUniveresalLunix.Views;

public partial class JbzPartSelectionWindow : Window
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _uploadCts;
    private bool _preparing;

    public JbzPartSelectionWindow(MainViewModel main)
    {
        _main = main;
        InitializeComponent();
        PartBox.Focus();
    }

    private void PartBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; UploadButton_Click(sender, e); }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_uploadCts is not null || _preparing) return;
        _preparing = true;
        string part = PartBox.Text.Trim();
        JbzPartFiles files;
        try
        {
            files = JbzPartFileResolver.Resolve(part);
            // Prove the model payload is compilable before asking the operator to upload.
            _ = await Task.Run(() => JbzModelCompiler.Compile(files.ModelPath));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JbzModelCompileException)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "KHÔNG TÌM THẤY MÃ HÀNG", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"MODEL_PREFLIGHT_FAIL part={part}: {ex}");
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "LỖI ĐỌC MÃ HÀNG", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }
        finally { _preparing = false; }

        FilesText.Text = $"MODEL: {files.ModelPath}\nSETUP: {files.SetupPath}";
        if (!_main.Test.IsBoardIdentityVerified)
        {
            StatusText.Text = "Bo UART chưa kết nối; chưa thể nạp mã hàng.";
            MessageBox.Show(this, StatusText.Text, "BO CHƯA KẾT NỐI", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
                $"BẮT BUỘC NẠP LẠI TOÀN BỘ mã hàng {files.PartNumber} xuống bo UART?\n\n{files.ModelPath}\n{files.SetupPath}",
                "XÁC NHẬN NẠP MODEL", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _uploadCts = new CancellationTokenSource();
        PartBox.IsEnabled = false; UploadButton.IsEnabled = false;
        CancelButton.Content = "HỦY NẠP";
        StatusText.Text = "Đang chuẩn bị nạp lại toàn bộ model xuống bo...";
        UploadProgress.Value = 0; PercentText.Text = "0%";
        var progress = new Progress<JbzModelUploadProgress>(value =>
        {
            UploadProgress.Value = value.Percent;
            PercentText.Text = $"{value.Percent}%";
            StatusText.Text = value.Percent == 100
                ? "Đã nạp lại toàn bộ model và gửi RESET xuống bo."
                : $"Đang nạp {value.Completed}/{value.Total}: {value.Command}";
        });
        bool loaded = false;
        try
        {
            ProductModel? model = await _main.LoadModelAsync(files.ModelPath, progress, _uploadCts.Token);
            loaded = model is not null;
            if (!loaded) StatusText.Text = "Chưa hoàn tất nạp mã hàng.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _main.Test.IsDeviceFault
                ? "Đã hủy khi đang nạp. Cần kết nối lại bo trước khi thử tiếp."
                : "Đã hủy trước khi nạp; mã hàng hiện tại được giữ nguyên.";
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"MODEL_UPLOAD_FAIL part={files.PartNumber}: {ex}");
            StatusText.Text = $"Nạp model thất bại: {ex.Message}";
            MessageBox.Show(this, StatusText.Text, "LỖI NẠP MODEL", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _uploadCts.Dispose(); _uploadCts = null;
            PartBox.IsEnabled = true; UploadButton.IsEnabled = true;
            CancelButton.Content = "ĐÓNG";
        }
        if (loaded)
        {
            UploadProgress.Value = 100; PercentText.Text = "100%";
            StatusText.Text = $"Đã nạp lại toàn bộ mã hàng {files.PartNumber} xuống bo.";
            DialogResult = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_preparing) { StatusText.Text = "Đang kiểm tra file mã hàng..."; return; }
        if (_uploadCts is not null)
        {
            _uploadCts.Cancel();
            StatusText.Text = "Đang hủy lệnh nạp...";
        }
        else DialogResult = false;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_preparing) { e.Cancel = true; StatusText.Text = "Đang kiểm tra file mã hàng..."; return; }
        if (_uploadCts is null) return;
        _uploadCts.Cancel();
        StatusText.Text = "Đang hủy lệnh nạp...";
        e.Cancel = true;
    }
}
