using System.Windows;
using System.Windows.Input;

namespace ProPdfReader;

public partial class NoteEditorWindow : Window
{
    public NoteEditorWindow(string selectedText, string initialText = "", bool isEditing = false)
    {
        InitializeComponent();
        Title = isEditing ? "Edit note" : "Add note";
        SelectedTextPreview.Text = selectedText;
        NoteTextBox.Text = initialText;
        NoteTextBox.CaretIndex = NoteTextBox.Text.Length;
        Loaded += (_, _) => NoteTextBox.Focus();
        UpdateSaveState();
    }

    public string NoteText => NoteTextBox.Text.Trim();

    private void NoteTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateSaveState();
    }

    private void UpdateSaveState()
    {
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NoteTextBox.Text);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NoteTextBox.Text))
        {
            return;
        }

        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter && SaveButton.IsEnabled)
        {
            DialogResult = true;
            e.Handled = true;
        }
    }
}
