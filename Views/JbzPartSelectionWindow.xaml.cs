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
    private const string DefaultTitle = "NHẬP MÃ HÀNG";

    private readonly MainViewModel _main;
    private CancellationTokenSource? _uploadCts;
    private bool _preparing;
    private bool _allowClose;
    private bool _closeRequested;

    public JbzPartSelectionWindow(MainViewModel main)
    {
        _main = main;
        InitializeComponent();

        ContentRendered += (_, _) =>
        {
            PartBox.Focus();
            PartBox.SelectAll();
        };
    }

    private async void PartBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        await LoadPartAsync();
    }

    private async Task LoadPartAsync()
    {
        if (_uploadCts is not null || _preparing)
            return;

        string part = PartBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(part))
        {
            MessageBox.Show(
                this,
                "Vui lòng nhập mã hàng.",
                "CHƯA NHẬP MÃ HÀNG",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            PartBox.Focus();
            return;
        }

        _preparing = true;
        PartBox.IsEnabled = false;
        Cursor = Cursors.Wait;
        Title = "ĐANG KIỂM TRA MÃ HÀNG...";

        JbzPartFiles files;
        try
        {
            files = JbzPartFileResolver.Resolve(part);

            // Kiểm tra model có thể biên dịch trước khi gửi bất kỳ lệnh nào xuống bo.
            _ = await Task.Run(() => JbzModelCompiler.Compile(files.ModelPath));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JbzModelCompileException)
        {
            AsyncFileLogService.Current.Error($"MODEL_PREFLIGHT_FAIL part={part}: {ex}");

            MessageBox.Show(
                this,
                $"Không thể sử dụng mã hàng {part}. Vui lòng kiểm tra lại mã hàng.",
                "KHÔNG TÌM THẤY MÃ HÀNG",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            RestoreInput();
            return;
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error($"MODEL_PREFLIGHT_FAIL part={part}: {ex}");

            MessageBox.Show(
                this,
                "Không thể đọc mã hàng. Vui lòng kiểm tra lại.",
                "LỖI ĐỌC MÃ HÀNG",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            RestoreInput();
            return;
        }
        finally
        {
            _preparing = false;
        }

        if (!_main.Test.IsBoardIdentityVerified)
        {
            MessageBox.Show(
                this,
                "Bo UART chưa kết nối; chưa thể nạp mã hàng.",
                "BO CHƯA KẾT NỐI",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            RestoreInput();
            return;
        }

        // Chỉ hỏi khi đúng mã/model hiện tại đã được nạp trước đó.
        // Mã mới: bấm Enter là nạp ngay, không có hộp xác nhận trung gian.
        if (IsCurrentModel(files))
        {
            MessageBoxResult updateAgain = MessageBox.Show(
                this,
                $"Mã hàng {files.PartNumber} đang được sử dụng.\nBạn có muốn cập nhật lại xuống bo không?",
                "CẬP NHẬT LẠI MÃ HÀNG",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (updateAgain != MessageBoxResult.Yes)
            {
                RestoreInput();
                return;
            }
        }

        _uploadCts = new CancellationTokenSource();
        PartBox.IsEnabled = false;
        Cursor = Cursors.Wait;
        Title = $"ĐANG NẠP {files.PartNumber}...";

        var progress = new Progress<JbzModelUploadProgress>(value =>
        {
            Title = $"ĐANG NẠP {files.PartNumber} - {value.Percent}%";
        });

        bool loaded = false;
        try
        {
            ProductModel? model = await _main.LoadModelAsync(
                files.ModelPath,
                progress,
                _uploadCts.Token);

            loaded = model is not null;
        }
        catch (OperationCanceledException)
        {
            if (!_closeRequested)
            {
                MessageBox.Show(
                    this,
                    "Đã hủy nạp mã hàng.",
                    "ĐÃ HỦY",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AsyncFileLogService.Current.Error(
                $"MODEL_UPLOAD_FAIL part={files.PartNumber}: {ex}");

            MessageBox.Show(
                this,
                $"Không thể nạp mã hàng {files.PartNumber} xuống bo.",
                "LỖI NẠP MODEL",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _uploadCts.Dispose();
            _uploadCts = null;
        }

        if (_closeRequested)
        {
            _allowClose = true;
            Close();
            return;
        }

        if (loaded)
        {
            DialogResult = true;
            return;
        }

        RestoreInput();
    }

    private bool IsCurrentModel(JbzPartFiles files)
    {
        string currentPart = _main.Test.PartNumber?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentPart) &&
            string.Equals(currentPart, files.PartNumber, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? currentPath = _main.Test.CurrentModelPath;
        if (string.IsNullOrWhiteSpace(currentPath))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(currentPath),
                Path.GetFullPath(files.ModelPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(
                currentPath,
                files.ModelPath,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RestoreInput()
    {
        Title = DefaultTitle;
        Cursor = Cursors.Arrow;
        PartBox.IsEnabled = true;
        PartBox.Focus();
        PartBox.SelectAll();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        if (_preparing)
        {
            e.Cancel = true;
            return;
        }

        if (_uploadCts is null)
            return;

        // Không còn nút HỦY riêng. Bấm X trong lúc nạp sẽ hủy an toàn rồi đóng.
        _closeRequested = true;
        _uploadCts.Cancel();
        e.Cancel = true;
    }
}
