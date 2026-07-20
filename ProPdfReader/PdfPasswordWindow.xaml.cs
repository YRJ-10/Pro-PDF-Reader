using System.Windows;
using System.Windows.Controls;

namespace ProPdfReader;

public partial class PdfPasswordWindow : Window
{
    public PdfPasswordWindow(string fileName, bool previousAttemptFailed)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        FileNameText.Text = fileName;
        ErrorText.Visibility = previousAttemptFailed ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Password => PasswordInput.Password;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PasswordInput.Focus();
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UnlockButton.IsEnabled = PasswordInput.SecurePassword.Length > 0;
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
