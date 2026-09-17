using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using JBZUniveresalLunix.Services;

namespace JBZUniveresalLunix.Core;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _run;
    private readonly Func<bool>? _can;
    private bool _busy;

    public AsyncRelayCommand(
        Func<Task> run,
        Func<bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    public bool CanExecute(object? parameter)
    {
        return !_busy &&
               (_can?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _busy = true;
        RaiseCanExecuteChanged();

        try
        {
            await _run();
        }
        catch (OperationCanceledException)
        {
            // Hủy thao tác do đóng cửa sổ/đổi mode không phải lỗi ứng dụng.
        }
        catch (Exception ex)
        {
            // SAFE OFFLINE MODE: ICommand.Execute là async void. Nếu exception
            // thoát khỏi đây WPF Dispatcher có thể kết thúc toàn bộ ứng dụng.
            // Chặn tại biên command, ghi log và báo rõ để app tiếp tục chạy.
            AsyncFileLogService.Current.Error($"COMMAND ERROR: {ex}");
            MessageBox.Show(
                string.IsNullOrWhiteSpace(ex.Message)
                    ? "Thao tác không thực hiện được. Hãy kiểm tra kết nối thiết bị và thử lại."
                    : ex.Message,
                "Không thể thực hiện thao tác",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(
            this,
            EventArgs.Empty
        );
    }

    public void Changed()
    {
        RaiseCanExecuteChanged();
    }
}