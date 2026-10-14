using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Media.Effects;

namespace RoundSoundMimic;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private Services.IAudioProcessingService? _audioService;
    private Services.ITrayIconService? _trayService;
    private Services.VinylVisualizer? _visualizer;
    private Canvas? _drawingCanvas;
    private Canvas? _eqCanvas;
    private Popup? _configPopup;
    private Popup? _menuPopup;
    private AppConfig _config = new();
    private DispatcherTimer? _eqTimer;
    private System.Windows.Media.Animation.Storyboard? _vinylStoryboard;

    // Parameterless constructor for WPF XAML loader
    public MainWindow()
    {
        // For design-time support and when created directly by WPF
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
        {
            InitializeComponent();
            return;
        }

        // At runtime, get services from the application's service provider
        var serviceProvider = ((App)Application.Current).ServiceProvider;
        _viewModel = serviceProvider.GetRequiredService<MainViewModel>();
        _audioService = serviceProvider.GetRequiredService<Services.IAudioProcessingService>();
        _trayService = serviceProvider.GetRequiredService<Services.ITrayIconService>();
        
        InitializeComponent();
        
        // Set the data context for binding
        DataContext = _viewModel;
        
        _drawingCanvas = FindName("DrawingCanvas") as Canvas;
        _eqCanvas = FindName("EqCanvas") as Canvas;
        _configPopup = FindName("ConfigPopup") as Popup;
        _menuPopup = FindName("MenuPopup") as Popup;

        // Initialize tray icon
        var iconImage = FindResource("AppIcon") as ImageSource;
        if (iconImage is not null)
        {
            _trayService.InitializeTrayIcon(iconImage);
        }

        // Route the vinyl/EQ rendering to a dedicated visualizer service so the
        // window class stays focused on interaction wiring rather than drawing.
        _visualizer = new Services.VinylVisualizer();
        var eqSpikeBrush = FindResource("EqSpikeBrush") as Brush;
        _visualizer.Bind(
            _eqCanvas,
            FindName("ProgressRing") as Ellipse,
            FindName("InnerCircle") as FrameworkElement,
            FindName("BandPath") as Path,
            FindName("EqGlowEffect") as DropShadowEffect,
            eqSpikeBrush,
            _audioService);

        // The vinyl rotation is owned by code-behind so it can be paused whenever
        // playback stops or is paused (see UpdateVinylSpin).
        _vinylStoryboard = FindResource("AlbumArtRotationStoryboard") as System.Windows.Media.Animation.Storyboard;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        if (_drawingCanvas is not null)
        {
            _drawingCanvas.Loaded += (_, _) => _visualizer?.OnCanvasLoaded();
            _drawingCanvas.SizeChanged += (_, _) => _visualizer?.OnCanvasSizeChanged();
        }

        if (FindName("InnerCircle") is FrameworkElement innerCircle)
        {
            innerCircle.SizeChanged += (_, _) => _visualizer?.OnInnerCircleSizeChanged();
        }

        // Subscribe to audio service events
        if (_audioService != null)
        {
            _audioService.EqValuesUpdated += OnEqValuesUpdated;
        }

        Loaded += OnLoaded;

        _eqTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS for smoother animation
        };
        _eqTimer.Tick += (_, _) => _audioService?.UpdateEqAnimation();
    }

    private void OnEqValuesUpdated(double[] eqValues)
    {
        // Update the equalizer visualization on the UI thread
        Dispatcher.Invoke(() =>
        {
            _visualizer?.OnEqValuesUpdated();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null && _audioService != null && _eqTimer != null)
        {
            await _viewModel.LoadConfigAsync();
            _visualizer?.OnCanvasLoaded();
            _audioService.StartCapture();
            _eqTimer.Start();
            _viewModel.OnLoaded();

            // The vinyl rotation is now driven from code-behind so it can be paused
            // whenever playback stops or is paused.
            _vinylStoryboard?.Begin(this, true);
            UpdateVinylSpin();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayService?.Dispose();
        _audioService?.StopCapture();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel?.OnClosed();
        base.OnClosed(e);
    }




    private void Window_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_menuPopup is null)
        {
            return;
        }

        _menuPopup.IsOpen = true;
        e.Handled = true;
    }

    private void OpenConfigButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_configPopup is null)
        {
            return;
        }

        _configPopup.IsOpen = true;
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        Hide();
    }

    private void Window_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractiveElement(source))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private static bool IsInteractiveElement(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ButtonBase or TextBoxBase or Popup or Slider)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }








    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPlaying))
        {
            UpdateVinylSpin();
        }
    }

    private void UpdateVinylSpin()
    {
        if (_vinylStoryboard is null)
        {
            return;
        }

        if (_viewModel?.IsPlaying == true)
        {
            _vinylStoryboard.Resume(this);
        }
        else
        {
            _vinylStoryboard.Pause(this);
        }
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        // Never steal keys from the config popup (its text boxes need Space, etc.).
        if (_configPopup?.IsOpen == true || Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.PlayPauseExecute();
                e.Handled = true;
                break;
            case Key.Left:
                _viewModel.PrevExecute();
                e.Handled = true;
                break;
            case Key.Right:
                _viewModel.NextExecute();
                e.Handled = true;
                break;
            case Key.F:
                _viewModel.ToggleFavoriteExecute();
                e.Handled = true;
                break;
        }
    }
    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_configPopup is not null)
        {
            _configPopup.IsOpen = false;
        }
    }
}
