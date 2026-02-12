# Technical Highlights

## Architecture

RoundSoundMimic is built as a modern .NET WPF application targeting .NET 10.0, utilizing the MVVM (Model-View-ViewModel) pattern with dependency injection via Microsoft.Extensions.DependencyInjection.

## Key Technologies

- **WPF (Windows Presentation Foundation)**: For rich, hardware-accelerated UI with custom graphics
- **NAudio**: Audio capture and real-time frequency analysis for EQ visualization
- **Jellyfin API**: RESTful integration for media server connectivity
- **System Tray Integration**: Native Windows tray icon with balloon notifications

## Core Features Implementation

### Circular Progress Ring
- Custom geometry-based progress indicator using WPF's `StrokeDashArray`
- Mathematically precise circular progress calculation
- Dynamic tooltip with playback time information
- Smooth animations and real-time updates

### Audio Processing Pipeline
- Real-time audio capture from system output
- FFT-based frequency analysis for equalizer visualization
- Thread-safe data flow between audio processing and UI threads
- Efficient buffering and processing for minimal latency

### Jellyfin Integration
- Asynchronous HTTP client for server communication
- Robust error handling and connection management
- Artwork downloading and caching with intelligent key generation
- Playback state monitoring and control commands

### Performance Optimizations
- Brush pooling system to reduce WPF rendering overhead
- Efficient UI property change notifications
- Background processing for non-blocking operations
- Memory-conscious artwork management

### UI/UX Enhancements
- Custom value converters for data binding transformations
- Format detection with visual color coding (lossless vs. lossy)
- Responsive design with system tray minimization
- Configurable user preferences with JSON persistence

## Development Features

- Automated build and launch for rapid development cycles
- Comprehensive performance monitoring and testing utilities
- Modular service architecture for maintainability
- Cross-thread communication using Dispatcher for UI safety