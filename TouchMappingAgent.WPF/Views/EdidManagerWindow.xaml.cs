using System.IO;
using System.Windows;
using Microsoft.Win32;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.ViewModels;
using DragEventArgs = System.Windows.DragEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace TouchMappingAgent.WPF.Views;

/// <summary>
/// Diagnostic window for monitor EDIDs: which ports report what, which collide, and how to
/// substitute a template where a range extender delivers nothing usable.
/// </summary>
public partial class EdidManagerWindow : Window
{
    private readonly EdidManagerViewModel _viewModel;

    /// <summary>Initializes the window with its ViewModel.</summary>
    /// <param name="viewModel">ViewModel driving the window.</param>
    public EdidManagerWindow(EdidManagerViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshAsync();
    }

    /// <summary>
    /// Accepts a dragged EDID file. Only the extensions the store can actually read are
    /// allowed, so a mis-drop shows a "no" cursor rather than a failure message afterwards.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsAcceptableDrop(e) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!IsAcceptableDrop(e))
            return;

        var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop)!;
        _viewModel.AcceptDroppedFile(files[0]);
        e.Handled = true;
    }

    private static bool IsAcceptableDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            return false;

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return false;

        var extension = Path.GetExtension(files[0]);
        return Evolved.EdidManager.Templates.EdidTemplateStore.SupportedExtensions
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private void OnBrowseTemplate(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationSource.Instance[LocalizationKeys.EdidUi_PickTemplateTitle],
            Filter = LocalizationSource.Instance[LocalizationKeys.EdidUi_PickTemplateFilter],
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(_viewModel.TemplateDirectory) &&
            Directory.Exists(_viewModel.TemplateDirectory))
        {
            dialog.InitialDirectory = _viewModel.TemplateDirectory;
        }

        if (dialog.ShowDialog(this) == true)
            _viewModel.AcceptDroppedFile(dialog.FileName);
    }
}

