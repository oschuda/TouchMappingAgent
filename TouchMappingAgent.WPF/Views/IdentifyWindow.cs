using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.Services;

// The project global-usings pull in System.Windows.Forms for the tray icon, which collides
// with the WPF types of the same name. Alias the WPF ones explicitly.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace TouchMappingAgent.WPF.Views;

/// <summary>
/// Full-screen prompt shown on ONE monitor during the learn step: "please touch THIS screen
/// now". The touch that arrives identifies the digitizer physically wired to that panel.
///
/// This is the only way the assignment can be established at all on the target installation.
/// Both DM7000s report an identical emulated EDID down to the serial number, and both PM1715
/// digitizers report no location path — so nothing in software can deduce which digitizer
/// belongs to which panel. A human touching the screen is the missing information, and this
/// window is how that information is collected exactly once.
///
/// Built in code rather than XAML so its positioning logic and the raw-input plumbing stay in
/// one file; there is no designer surface worth having for a single full-bleed prompt.
/// </summary>
public sealed class IdentifyWindow : Window
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly MonitorInfo _target;
    private RawTouchListener? _listener;
    private readonly TaskCompletionSource<string?> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TextBlock? _statusText;

    /// <summary>
    /// Creates the prompt for one monitor.
    /// </summary>
    /// <param name="target">The monitor to cover and ask the operator to touch.</param>
    public IdentifyWindow(MonitorInfo target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));

        Title = $"Bildschirm identifizieren – {target.DisplayName}";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x43));
        Cursor = Cursors.Hand;

        // WindowStartupLocation/Left/Top are WPF DIPs; the real positioning happens in
        // SourceInitialized via SetWindowPos with physical pixels (see MoveToTargetMonitor).
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = BuildContent(target);

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;

        // Any input on this window counts as "the operator acted here". Mouse and key are
        // accepted as an escape hatch for a screen whose digitizer is dead or unplugged —
        // without one, a broken touch panel would make the dialog impossible to dismiss.
        PreviewKeyDown += OnKeyDown;
    }

    /// <summary>
    /// Completes with the HID interface path of the digitizer that was touched, or null when
    /// the operator cancelled (Esc) or the window was closed without a touch.
    /// </summary>
    public Task<string?> TouchedDevicePathTask => _result.Task;

    /// <summary>
    /// True when raw input registration succeeded. If false, a touch cannot be attributed to a
    /// specific device and the caller must fall back to manual selection.
    /// </summary>
    public bool CanIdentifyDevices => _listener?.IsListening ?? false;

    private UIElement BuildContent(MonitorInfo target)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(48)
        };

        panel.Children.Add(new TextBlock
        {
            Text = LocalizationSource.Instance[LocalizationKeys.Identify_TouchNow],
            FontSize = 44,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 24)
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"{target.DisplayName}  ·  {target.ConnectorLabel}  ·  {target.Width}×{target.Height}",
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xC5, 0xE8)),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 40)
        });

        _statusText = new TextBlock
        {
            Text = LocalizationSource.Instance[LocalizationKeys.Identify_WaitingForTouch],
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD8, 0xE4)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_statusText);

        return panel;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        MoveToTargetMonitor(hwnd);

        try
        {
            _listener = new RawTouchListener(hwnd);
            _listener.TouchDetected += OnTouchDetected;

            if (!_listener.IsListening && _statusText != null)
            {
                _statusText.Text = LocalizationSource.Instance[LocalizationKeys.Identify_RawInputUnavailableDetail];
            }
        }
        catch (Exception)
        {
            // Leaves CanIdentifyDevices false; the caller falls back to manual selection.
            if (_statusText != null)
            {
                _statusText.Text = LocalizationSource.Instance[LocalizationKeys.Identify_RawInputUnavailable];
            }
        }
    }

    /// <summary>
    /// Positions the window over the target monitor using PHYSICAL pixels.
    ///
    /// Deliberately not via WPF's Left/Top/Width/Height: those are device-independent units,
    /// and MonitorInfo carries physical desktop coordinates straight from EnumDisplaySettings.
    /// On any DPI scaling other than 100% the two disagree, and the prompt would land on the
    /// wrong screen — which in this particular dialog means learning the wrong assignment.
    /// SetWindowPos takes physical pixels and sidesteps the conversion entirely.
    /// </summary>
    private void MoveToTargetMonitor(IntPtr hwnd)
    {
        if (_target.Width <= 0 || _target.Height <= 0)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        try
        {
            SetWindowPos(hwnd, IntPtr.Zero,
                _target.X, _target.Y, _target.Width, _target.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnTouchDetected(string devicePath)
    {
        if (_result.Task.IsCompleted)
            return;

        if (_statusText != null)
            _statusText.Text = LocalizationSource.Instance[LocalizationKeys.Identify_TouchDetected];

        _result.TrySetResult(devicePath);

        // Close on the dispatcher rather than inline: this runs inside the window procedure,
        // and destroying the window from within its own WM_INPUT handling is asking for
        // re-entrancy trouble.
        Dispatcher.BeginInvoke(new Action(Close));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        _result.TrySetResult(null);
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Whatever happened, the awaiting caller must be released.
        _result.TrySetResult(null);

        _listener?.Dispose();
        _listener = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
