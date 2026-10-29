// VERIFY ONLY: System.Windows (WindowsBase / PresentationCore / PresentationFramework) surface used by Mediator.Wpf.
#nullable disable
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace System.Windows.Threading
{
    public enum DispatcherPriority { Invalid = -1, Inactive = 0, SystemIdle = 1, ApplicationIdle = 2, ContextIdle = 3, Background = 4, Input = 5, Loaded = 6, Render = 7, DataBind = 8, Normal = 9, Send = 10 }

    public sealed class DispatcherOperation
    {
        internal DispatcherOperation() { }
    }

    public sealed class Dispatcher
    {
        private static readonly Dispatcher Instance = new();
        private Dispatcher() { }
        public static Dispatcher CurrentDispatcher => Instance;
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
        public DispatcherOperation BeginInvoke(Delegate method, params object[] args) { method.DynamicInvoke(args); return new DispatcherOperation(); }
        public DispatcherOperation BeginInvoke(Delegate method, DispatcherPriority priority, params object[] args) { method.DynamicInvoke(args); return new DispatcherOperation(); }
        public DispatcherOperation BeginInvoke(DispatcherPriority priority, Delegate method) { method.DynamicInvoke(); return new DispatcherOperation(); }
        public void Invoke(Action callback) => callback();
    }

    public abstract class DispatcherObject
    {
        public Dispatcher Dispatcher => Dispatcher.CurrentDispatcher;
        public bool CheckAccess() => true;
        public void VerifyAccess() { }
    }

    public class DispatcherTimer
    {
        public DispatcherTimer() { }
        public DispatcherTimer(DispatcherPriority priority) { }
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; set; }
        public event EventHandler Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
    }
}

namespace System.Windows
{
    public delegate void PropertyChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e);

    public readonly struct DependencyPropertyChangedEventArgs
    {
        public DependencyProperty Property { get; }
        public object OldValue { get; }
        public object NewValue { get; }
    }

    public class PropertyMetadata
    {
        public PropertyMetadata() { }
        public PropertyMetadata(object defaultValue) => DefaultValue = defaultValue;
        public PropertyMetadata(PropertyChangedCallback propertyChangedCallback) => PropertyChangedCallback = propertyChangedCallback;
        public PropertyMetadata(object defaultValue, PropertyChangedCallback propertyChangedCallback) { DefaultValue = defaultValue; PropertyChangedCallback = propertyChangedCallback; }
        public object DefaultValue { get; set; }
        public PropertyChangedCallback PropertyChangedCallback { get; set; }
    }

    public class UIPropertyMetadata : PropertyMetadata
    {
        public UIPropertyMetadata() { }
        public UIPropertyMetadata(object defaultValue) : base(defaultValue) { }
        public UIPropertyMetadata(object defaultValue, PropertyChangedCallback propertyChangedCallback) : base(defaultValue, propertyChangedCallback) { }
    }

    public class FrameworkPropertyMetadata : UIPropertyMetadata
    {
        public FrameworkPropertyMetadata() { }
        public FrameworkPropertyMetadata(object defaultValue) : base(defaultValue) { }
        public FrameworkPropertyMetadata(object defaultValue, PropertyChangedCallback propertyChangedCallback) : base(defaultValue, propertyChangedCallback) { }
    }

    public sealed class DependencyProperty
    {
        private DependencyProperty(string name, Type propertyType, Type ownerType, PropertyMetadata metadata)
        {
            Name = name;
            PropertyType = propertyType;
            OwnerType = ownerType;
            DefaultMetadata = metadata ?? new PropertyMetadata();
        }
        public string Name { get; }
        public Type PropertyType { get; }
        public Type OwnerType { get; }
        public PropertyMetadata DefaultMetadata { get; }
        public static DependencyProperty Register(string name, Type propertyType, Type ownerType) => new(name, propertyType, ownerType, null);
        public static DependencyProperty Register(string name, Type propertyType, Type ownerType, PropertyMetadata typeMetadata) => new(name, propertyType, ownerType, typeMetadata);
    }

    public class DependencyObject : DispatcherObject
    {
        private readonly Dictionary<DependencyProperty, object> _values = new();
        public object GetValue(DependencyProperty dp) => _values.TryGetValue(dp, out var v) ? v : dp.DefaultMetadata.DefaultValue;
        public void SetValue(DependencyProperty dp, object value) => _values[dp] = value;
        public void ClearValue(DependencyProperty dp) => _values.Remove(dp);
    }

    public abstract class Freezable : DependencyObject
    {
        public bool IsFrozen { get; private set; }
        public void Freeze() => IsFrozen = true;
    }

    public interface IInputElement { }

    public enum Visibility { Visible = 0, Hidden = 1, Collapsed = 2 }

    public class RoutedEventArgs : EventArgs
    {
        public RoutedEventArgs() { }
        public bool Handled { get; set; }
        public object Source { get; set; }
        public object OriginalSource { get; }
    }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);

    public readonly struct Size
    {
        public Size(double width, double height) { Width = width; Height = height; }
        public double Width { get; }
        public double Height { get; }
    }

    public struct Point
    {
        public Point(double x, double y) { X = x; Y = y; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    public struct Rect
    {
        public Rect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left => X;
        public double Top => Y;
        public double Right => X + Width;
        public double Bottom => Y + Height;
    }

    public class SizeChangedEventArgs : RoutedEventArgs
    {
        public Size NewSize { get; }
        public Size PreviousSize { get; }
        public bool WidthChanged { get; }
        public bool HeightChanged { get; }
    }
    public delegate void SizeChangedEventHandler(object sender, SizeChangedEventArgs e);

    public readonly struct DpiScale
    {
        public DpiScale(double dpiScaleX, double dpiScaleY) { DpiScaleX = dpiScaleX; DpiScaleY = dpiScaleY; }
        public double DpiScaleX { get; }
        public double DpiScaleY { get; }
        public double PixelsPerDip => DpiScaleY;
    }

    public sealed class DpiChangedEventArgs : RoutedEventArgs
    {
        public DpiScale OldDpi { get; }
        public DpiScale NewDpi { get; }
    }
    public delegate void DpiChangedEventHandler(object sender, DpiChangedEventArgs e);

    [Flags]
    public enum DragDropEffects { None = 0, Copy = 1, Move = 2, Link = 4, Scroll = -2147483648, All = -2147483645 }

    public interface IDataObject
    {
        object GetData(string format);
        bool GetDataPresent(string format);
        string[] GetFormats();
    }

    public sealed class DragEventArgs : RoutedEventArgs
    {
        public DragEventArgs(IDataObject data) => Data = data;
        public IDataObject Data { get; }
        public DragDropEffects Effects { get; set; }
        public DragDropEffects AllowedEffects { get; }
        public Point GetPosition(IInputElement relativeTo) => default;
    }
    public delegate void DragEventHandler(object sender, DragEventArgs e);

    public static class DataFormats
    {
        public static readonly string FileDrop = "FileDrop";
        public static readonly string Text = "Text";
        public static readonly string UnicodeText = "UnicodeText";
        public static readonly string Bitmap = "Bitmap";
    }

    public static class Clipboard
    {
        public static bool ContainsFileDropList() => false;
        public static StringCollection GetFileDropList() => new();
        public static bool ContainsData(string format) => false;
        public static object GetData(string format) => null;
        public static bool ContainsImage() => false;
        public static BitmapSource GetImage() => null;
        public static bool ContainsText() => false;
        public static string GetText() => "";
        public static void SetText(string text) { }
    }

    public struct GridLength
    {
        public GridLength(double value) { Value = value; }
        public double Value { get; }
    }

    public static class SystemParameters
    {
        public static Rect WorkArea => new(0, 0, 1920, 1040);
        public static bool ClientAreaAnimation => true;
        public static double PrimaryScreenWidth => 1920;
        public static double PrimaryScreenHeight => 1080;
    }

    public enum MessageBoxButton { OK = 0, OKCancel = 1, YesNoCancel = 3, YesNo = 4 }
    public enum MessageBoxImage { None = 0, Hand = 16, Stop = 16, Error = 16, Question = 32, Exclamation = 48, Warning = 48, Asterisk = 64, Information = 64 }
    public enum MessageBoxResult { None = 0, OK = 1, Cancel = 2, Yes = 6, No = 7 }

    public static class MessageBox
    {
        public static MessageBoxResult Show(string messageBoxText) => MessageBoxResult.OK;
        public static MessageBoxResult Show(string messageBoxText, string caption) => MessageBoxResult.OK;
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) => MessageBoxResult.OK;
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) => MessageBoxResult.OK;
    }

    public class ResourceDictionary : IDictionary
    {
        private readonly Hashtable _items = new();
        private Uri _source;

        public Collection<ResourceDictionary> MergedDictionaries { get; } = new();

        /// <summary>Real WPF loads the BAML here and throws IOException("Cannot locate resource …") when it is missing.</summary>
        public Uri Source
        {
            get => _source;
            set
            {
                var loaded = Mediator.Verify.WpfStub.StubHooks.LoadResource(value);
                foreach (DictionaryEntry e in loaded._items) _items[e.Key] = e.Value;
                _source = value;
            }
        }

        public object this[object key] { get => _items[key]; set => _items[key] = value; }
        public ICollection Keys => _items.Keys;
        public ICollection Values => _items.Values;
        public int Count => _items.Count;
        public bool IsFixedSize => false;
        public bool IsReadOnly => false;
        public bool IsSynchronized => false;
        public object SyncRoot => _items.SyncRoot;
        public void Add(object key, object value) => _items.Add(key, value);
        public void Clear() => _items.Clear();
        public bool Contains(object key) => _items.Contains(key);
        public void Remove(object key) => _items.Remove(key);
        public void CopyTo(Array array, int index) => _items.CopyTo(array, index);
        public IDictionaryEnumerator GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        internal object Find(object key)
        {
            if (_items.Contains(key)) return _items[key];
            for (int i = MergedDictionaries.Count - 1; i >= 0; i--)
                if (MergedDictionaries[i].Find(key) is { } v) return v;
            return null;
        }
    }

    public class Visual : DependencyObject { }

    public class UIElement : Visual, IInputElement
    {
        public static readonly DependencyProperty OpacityProperty = DependencyProperty.Register(nameof(Opacity), typeof(double), typeof(UIElement), new PropertyMetadata(1.0));
        public static readonly DependencyProperty VisibilityProperty = DependencyProperty.Register(nameof(Visibility), typeof(Visibility), typeof(UIElement), new PropertyMetadata(Visibility.Visible));
        public double Opacity { get => (double)GetValue(OpacityProperty); set => SetValue(OpacityProperty, value); }
        public Visibility Visibility { get => (Visibility)GetValue(VisibilityProperty); set => SetValue(VisibilityProperty, value); }
        public bool IsVisible { get; internal set; }
        public bool IsHitTestVisible { get; set; } = true;
        public bool AllowDrop { get; set; }
        public bool Focusable { get; set; }
        public bool IsKeyboardFocusWithin { get; }
        public Transform RenderTransform { get; set; }
        public Point RenderTransformOrigin { get; set; }
        public bool Focus() => true;
        public void BeginAnimation(DependencyProperty dp, AnimationTimeline animation) { }
        public void UpdateLayout() { }
        public event KeyEventHandler PreviewKeyDown;
        public event KeyEventHandler KeyDown;
        public event DragEventHandler PreviewDragEnter;
        public event DragEventHandler PreviewDragOver;
        public event DragEventHandler PreviewDragLeave;
        public event DragEventHandler PreviewDrop;
        public event DragEventHandler DragEnter;
        public event DragEventHandler DragOver;
        public event DragEventHandler DragLeave;
        public event DragEventHandler Drop;
    }

    public class FrameworkElement : UIElement
    {
        public object DataContext { get; set; }
        public double Width { get; set; } = double.NaN;
        public double Height { get; set; } = double.NaN;
        public double MinWidth { get; set; }
        public double MinHeight { get; set; }
        public double MaxWidth { get; set; } = double.PositiveInfinity;
        public double MaxHeight { get; set; } = double.PositiveInfinity;
        public double ActualWidth { get; }
        public double ActualHeight { get; }
        public string Name { get; set; } = "";
        public object Tag { get; set; }
        public object ToolTip { get; set; }
        public ResourceDictionary Resources { get; set; } = new();
        public bool IsLoaded { get; }
        public event RoutedEventHandler Loaded;
        public event RoutedEventHandler Unloaded;
        public event SizeChangedEventHandler SizeChanged;
        public void SetResourceReference(DependencyProperty dp, object name) => SetValue(dp, Application.Current?.TryFindResource(name));
        public object TryFindResource(object resourceKey) => Resources.Find(resourceKey) ?? Application.Current?.TryFindResource(resourceKey);
        public object FindResource(object resourceKey) => TryFindResource(resourceKey) ?? throw new ResourceReferenceKeyNotFoundException($"'{resourceKey}' resource not found.", resourceKey);
    }

    public class ResourceReferenceKeyNotFoundException : InvalidOperationException
    {
        public ResourceReferenceKeyNotFoundException() { }
        public ResourceReferenceKeyNotFoundException(string message, object resourceKey) : base(message) => Key = resourceKey;
        public object Key { get; }
    }

    public abstract class PresentationSource : DispatcherObject { }

    public enum ShutdownMode { OnLastWindowClose = 0, OnMainWindowClose = 1, OnExplicitShutdown = 2 }
    public enum SizeToContent { Manual = 0, Width = 1, Height = 2, WidthAndHeight = 3 }
    public enum WindowState { Normal = 0, Minimized = 1, Maximized = 2 }

    public class StartupEventArgs : EventArgs
    {
        public string[] Args { get; } = [];
    }
    public delegate void StartupEventHandler(object sender, StartupEventArgs e);

    public class ExitEventArgs : EventArgs
    {
        public int ApplicationExitCode { get; set; }
    }
    public delegate void ExitEventHandler(object sender, ExitEventArgs e);

    public class Application : DispatcherObject
    {
        public Application()
        {
            Current = this;
        }
        public static Application Current { get; private set; }
        public ResourceDictionary Resources { get; set; } = new();
        public Window MainWindow { get; set; }
        public ShutdownMode ShutdownMode { get; set; }
        public event StartupEventHandler Startup;
        public event ExitEventHandler Exit;
        public int Run() => 0;
        public void Shutdown() { }
        public void Shutdown(int exitCode) { }
        public object TryFindResource(object resourceKey) => Resources.Find(resourceKey);
        public object FindResource(object resourceKey) => TryFindResource(resourceKey) ?? throw new ResourceReferenceKeyNotFoundException($"'{resourceKey}' resource not found.", resourceKey);
        protected virtual void OnStartup(StartupEventArgs e) { }
        protected virtual void OnExit(ExitEventArgs e) { }
    }

    public class Window : System.Windows.Controls.ContentControl
    {
        public string Title { get; set; } = "";
        public double Left { get; set; }
        public double Top { get; set; }
        public bool Topmost { get; set; }
        public bool ShowInTaskbar { get; set; } = true;
        public bool IsActive { get; }
        public Window Owner { get; set; }
        public SizeToContent SizeToContent { get; set; }
        public WindowState WindowState { get; set; }
        public event EventHandler Activated;
        public event EventHandler Deactivated;
        public event EventHandler SourceInitialized;
        public event CancelEventHandler Closing;
        public event EventHandler Closed;
        public event DpiChangedEventHandler DpiChanged;
        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public bool Activate() => true;
        public void Close() { }
        public bool? ShowDialog() => null;
        protected virtual void OnSourceInitialized(EventArgs e) { }
        protected virtual void OnClosing(CancelEventArgs e) { }
        protected virtual void OnClosed(EventArgs e) { }
    }
}

namespace System.Windows.Controls
{
    public class Control : FrameworkElement
    {
        public Brush Background { get; set; }
        public Brush Foreground { get; set; }
        public Brush BorderBrush { get; set; }
        public double FontSize { get; set; }
    }

    public class ContentControl : Control
    {
        public object Content { get; set; }
    }

    public class UserControl : ContentControl { }

    public class ScrollViewer : ContentControl
    {
        public void ScrollToEnd() { }
    }

    public class TextBox : Control
    {
        public string Text { get; set; } = "";
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register("Text", typeof(string), typeof(TextBox));
        public int CaretIndex { get; set; }
        public bool AcceptsReturn { get; set; }
        public System.Windows.Data.BindingExpression GetBindingExpression(DependencyProperty dp) => null;
        public void SelectAll() { }
        public void Clear() => Text = "";
    }

    public class TextBlock : FrameworkElement
    {
        public string Text { get; set; } = "";
    }

    public class Panel : FrameworkElement { }
    public class Canvas : Panel { }
    public class Grid : Panel { }
    public class ColumnDefinition
    {
        public GridLength Width { get; set; }
    }
    public class StackPanel : Panel { }
    public class Decorator : FrameworkElement { }
    public class Border : Decorator { }
}

namespace System.Windows.Controls.Primitives
{
    public class ButtonBase : System.Windows.Controls.ContentControl { }
    public class ToggleButton : ButtonBase { public bool? IsChecked { get; set; } }
}

namespace System.Windows.Shapes
{
    public abstract class Shape : FrameworkElement
    {
        public Brush Fill { get; set; }
        public Brush Stroke { get; set; }
        public double StrokeThickness { get; set; }
    }
    public sealed class Ellipse : Shape { }
    public sealed class Rectangle : Shape { }
    public sealed class Path : Shape { }
}

namespace System.Windows.Data
{
    public interface IValueConverter
    {
        object Convert(object value, Type targetType, object parameter, Globalization.CultureInfo culture);
        object ConvertBack(object value, Type targetType, object parameter, Globalization.CultureInfo culture);
    }

    public class Binding
    {
        public static readonly object DoNothing = new();
        public Binding() { }
        public Binding(string path) { }
    }

    public class BindingExpression
    {
        public void UpdateSource() { }
        public void UpdateTarget() { }
    }
}
