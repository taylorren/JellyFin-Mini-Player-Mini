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
using NAudio.Dsp;
using NAudio.Wave;
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
    private Canvas? _drawingCanvas;
    private Canvas? _eqCanvas;
    private Popup? _configPopup;
    private Popup? _menuPopup;
    private FrameworkElement? _innerCircle;
    private Path? _bandPath;
    private DropShadowEffect? _eqGlowEffect;
    private readonly List<Line> _eqSpikes = new();
    private AppConfig _config = new();
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _eqTimer;

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
        _innerCircle = FindName("InnerCircle") as FrameworkElement;
        _bandPath = FindName("BandPath") as Path;
        _eqGlowEffect = FindName("EqGlowEffect") as DropShadowEffect;
        
        // Initialize tray icon
        var iconImage = FindResource("AppIcon") as ImageSource;
        if (iconImage is not null)
        {
            _trayService.InitializeTrayIcon(iconImage);
        }
        
        if (_drawingCanvas is not null)
        {
            _drawingCanvas.Loaded += (_, _) =>
            {
                InitializeEqSpikes();
                UpdateBandGeometry();
                UpdateEqGeometry();
            };
            _drawingCanvas.SizeChanged += (_, _) =>
            {
                UpdateBandGeometry();
                UpdateEqGeometry();
            };
        }

        if (_innerCircle is not null)
        {
            _innerCircle.SizeChanged += (_, _) => UpdateBandGeometry();
        }
        
        // Subscribe to audio service events
        if (_audioService != null)
        {
            _audioService.EqValuesUpdated += OnEqValuesUpdated;
        }
        
        Loaded += OnLoaded;
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += PollTimerOnTick;

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
            UpdateEqGeometry();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null && _audioService != null && _pollTimer != null && _eqTimer != null)
        {
            await _viewModel.LoadConfigAsync();
            _pollTimer.Start();
            InitializeEqSpikes();
            UpdateEqGeometry();
            _audioService.StartCapture();
            _eqTimer.Start();
            UpdateBandGeometry();
            _viewModel.OnLoaded();
            // UpdatePlayPauseIcon is now handled by the ViewModel
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayService?.Dispose();
        _audioService?.StopCapture();
        _viewModel?.OnClosed();
        base.OnClosed(e);
    }


    private void InitializeEqSpikes()
    {
        if (_eqCanvas is null)
        {
            return;
        }

        _eqCanvas.Children.Clear();
        _eqSpikes.Clear();

        var stroke = (Brush)FindResource("EqSpikeBrush");
        for (var i = 0; i < 48; i++)  // Using constant directly since we removed the field
        {
            var line = new Line
            {
                Stroke = stroke,
                StrokeThickness = 5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _eqCanvas.Children.Add(line);
            _eqSpikes.Add(line);
        }
    }

    private void UpdateEqGeometry()
    {
        if (_eqSpikes.Count == 0 || _audioService == null)
        {
            return;
        }

        var progressRing = ProgressRing;
        if (progressRing == null)
        {
            return;
        }

        var ringWidth = progressRing.ActualWidth > 0 ? progressRing.ActualWidth : progressRing.Width;
        var ringHeight = progressRing.ActualHeight > 0 ? progressRing.ActualHeight : progressRing.Height;
        if (ringWidth <= 0 || ringHeight <= 0)
        {
            return;
        }

        var ringLeft = Canvas.GetLeft(progressRing);
        var ringTop = Canvas.GetTop(progressRing);
        if (double.IsNaN(ringLeft))
        {
            ringLeft = 0;
        }

        if (double.IsNaN(ringTop))
        {
            ringTop = 0;
        }

        var ringRadius = Math.Min(ringWidth, ringHeight) / 2.0;
        var centerX = ringLeft + ringWidth / 2.0;
        var centerY = ringTop + ringHeight / 2.0;
        var baseRadius = ringRadius + progressRing.StrokeThickness / 2.0 + 4;
        var minSpike = Math.Max(3, ringRadius * 0.04);
        var maxSpike = Math.Max(8, ringRadius * 0.14);

        // Get current EQ values from the service
        var eqValues = _audioService.GetCurrentEqValues();

        // Batch updates to reduce rendering overhead
        var count = Math.Min(Math.Min(_eqSpikes.Count, eqValues.Length), 48); // Use the known constant
        var totalIntensity = 0.0;
        
        for (var i = 0; i < count; i++)
        {
            var angle = (Math.PI * 2.0 * i) / count;
            var sin = Math.Sin(angle);
            var cos = Math.Cos(angle);
            
            var intensity = eqValues[i];
            totalIntensity += intensity;

            // Exponential scaling for more visual impact on high peaks
            var scaledIntensity = Math.Pow(intensity, 1.2);
            var spikeLength = minSpike + scaledIntensity * (maxSpike - minSpike);

            var x1 = centerX + cos * baseRadius;
            var y1 = centerY + sin * baseRadius;
            var x2 = centerX + cos * (baseRadius + spikeLength);
            var y2 = centerY + sin * (baseRadius + spikeLength);

            var line = _eqSpikes[i];
            // Only update if values have changed significantly to reduce rendering overhead
            if (Math.Abs(line.X1 - x1) > 0.1 || Math.Abs(line.Y1 - y1) > 0.1 || 
                Math.Abs(line.X2 - x2) > 0.1 || Math.Abs(line.Y2 - y2) > 0.1)
            {
                line.X1 = x1;
                line.Y1 = y1;
                line.X2 = x2;
                line.Y2 = y2;
            }
            
            // Enhance visual appearance: color gradient and dynamic thickness
            // Base color is Orange (#FF8A2A) to Light Orange (#FFC166)
            var r = (byte)255;
            var g = (byte)(138 + (193 - 138) * intensity);
            var b = (byte)(42 + (102 - 42) * intensity);
            
            line.StrokeThickness = 3 + intensity * 6; // Dynamic thickness
            line.Opacity = 0.6 + intensity * 0.4;    // More opaque when active
            
            // Reuse or update brush efficiently
            if (line.Stroke is not SolidColorBrush scb || scb.Color.G != g)
            {
                line.Stroke = new SolidColorBrush(Color.FromRgb(r, g, b));
            }
        }

        // Update the overall canvas glow based on average intensity
        if (_eqGlowEffect != null)
        {
            var avgIntensity = totalIntensity / count;
            _eqGlowEffect.BlurRadius = 15 + avgIntensity * 30;
            _eqGlowEffect.Opacity = 0.4 + avgIntensity * 0.6;
            
            // Color shifts slightly towards lighter orange at high intensity
            var gr = (byte)255;
            var gg = (byte)(138 + (193 - 138) * avgIntensity);
            var gb = (byte)(42 + (102 - 42) * avgIntensity);
            _eqGlowEffect.Color = Color.FromRgb(gr, gg, gb);
        }
    }

    private void UpdateBandGeometry()
    {
        if (_bandPath is null || _innerCircle is null)
        {
            return;
        }

        var innerLeft = Canvas.GetLeft(_innerCircle);
        var innerTop = Canvas.GetTop(_innerCircle);
        if (double.IsNaN(innerLeft))
        {
            innerLeft = 0;
        }

        if (double.IsNaN(innerTop))
        {
            innerTop = 0;
        }

        var innerWidth = _innerCircle.ActualWidth > 0 ? _innerCircle.ActualWidth : _innerCircle.Width;
        var innerHeight = _innerCircle.ActualHeight > 0 ? _innerCircle.ActualHeight : _innerCircle.Height;
        if (innerWidth <= 0 || innerHeight <= 0)
        {
            return;
        }

        const double widthPaddingRatio = 12.0 / 520.0;
        const double heightRatio = 140.0 / 520.0;

        var rectWidth = Math.Max(0, innerWidth * (1.0 - widthPaddingRatio));
        var rectHeight = Math.Max(0, innerHeight * heightRatio);

        var rectX = innerLeft + (innerWidth - rectWidth) / 2.0;
        var rectY = innerTop + (innerHeight - rectHeight) / 2.0;
        var rectRadius = rectHeight / 2.0;

        var rectGeom = new RectangleGeometry(new Rect(rectX, rectY, rectWidth, rectHeight), rectRadius, rectRadius);

        var centerX = innerLeft + innerWidth / 2.0;
        var centerY = innerTop + innerHeight / 2.0;
        var ellipseGeom = new EllipseGeometry(new Point(centerX, centerY), innerWidth / 2.0, innerHeight / 2.0);

        // Only update if the geometry has changed significantly to reduce rendering overhead
        var newGeometry = Geometry.Combine(rectGeom, ellipseGeom, GeometryCombineMode.Intersect, null);
        
        // Check if the new geometry is different from the current one before updating
        if (_bandPath.Data == null || !_bandPath.Data.Equals(newGeometry))
        {
            _bandPath.Data = newGeometry;
        }
    }

    private async void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        // This will now be handled by the ViewModel's SaveConfigCommand
        if (_viewModel != null)
        {
            await _viewModel.SaveConfigAsync();
        }
    }

    private async void FetchButton_OnClick(object sender, RoutedEventArgs e)
    {
        // This will now be handled by the ViewModel's FetchCommand
        _viewModel?.FetchExecute();
    }

    private async void PollTimerOnTick(object? sender, EventArgs e)
    {
        _viewModel?.FetchExecute();
    }

    private async void PrevButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.PrevExecute();
    }

    private async void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.PlayPauseExecute();
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
            if (current is ButtonBase or TextBoxBase or Popup)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }


    private async void NextButton_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.NextExecute();
    }






    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_configPopup is not null)
        {
            _configPopup.IsOpen = false;
        }
    }
}