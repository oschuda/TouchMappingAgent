using System.Windows;

namespace TouchMappingAgent.WPF.Views;

/// <summary>
/// Operator handbook: why the agent exists, how to learn a touch assignment, what an EDID
/// collision means and what to do about a range extender that swallows DDC.
/// </summary>
public partial class HelpWindow : Window
{
    /// <summary>Initializes a new instance of <see cref="HelpWindow"/>.</summary>
    public HelpWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
