using System.Windows;

namespace TouchMappingAgent.WPF.Views;

/// <summary>
/// Shows the product banner, copyright and developer credit. Replaces a plain MessageBox so the
/// logo actually has somewhere to be shown at a legible size.
/// </summary>
public partial class AboutWindow : Window
{
    /// <summary>Initializes a new instance of <see cref="AboutWindow"/>.</summary>
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
