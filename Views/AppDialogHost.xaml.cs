using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cleaner;

/// <summary>
/// Встроенный диалог поверх содержимого окна. Cleaner не открывает отдельных окон
/// и системных сообщений: любое подтверждение показывается внутри главного окна.
/// </summary>
public partial class AppDialogHost : UserControl
{
    private TaskCompletionSource<bool>? _completion;

    public AppDialogHost()
    {
        InitializeComponent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Сообщение с одной кнопкой.</summary>
    public Task ShowMessageAsync(string title, string message, string closeText = "Понятно")
    {
        return OpenAsync(title, message, closeText, null);
    }

    /// <summary>Вопрос с подтверждением; возвращает true, если пользователь согласился.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Продолжить", string cancelText = "Отмена")
    {
        return OpenAsync(title, message, confirmText, cancelText);
    }

    private Task<bool> OpenAsync(string title, string message, string confirmText, string? cancelText)
    {
        _completion?.TrySetResult(false);

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText ?? string.Empty;
        CancelButton.Visibility = cancelText is null ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus();
        ConfirmButton.Focus();
        return _completion.Task;
    }

    private void Close(bool result)
    {
        Visibility = Visibility.Collapsed;
        var completion = _completion;
        _completion = null;
        completion?.TrySetResult(result);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Close(true);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false);

    /// <summary>Enter подтверждает, Escape отменяет — как в обычном диалоге.</summary>
    public bool HandleKey(Key key)
    {
        if (!IsOpen)
        {
            return false;
        }

        switch (key)
        {
            case Key.Escape:
                Close(CancelButton.Visibility != Visibility.Visible);
                return true;
            case Key.Enter:
                Close(true);
                return true;
            default:
                return false;
        }
    }
}
