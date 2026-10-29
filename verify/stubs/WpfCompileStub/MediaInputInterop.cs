// VERIFY ONLY: System.Windows.Media(.Animation/.Imaging), Input, Interop, Markup, System.IO.Packaging and the WPF
// file dialogs, as far as Mediator.Wpf uses them.
#nullable disable
using System.Xml;
using System.Xml.Linq;

namespace System.Windows.Media.Animation
{
    public abstract class Animatable : Freezable
    {
        public void BeginAnimation(DependencyProperty dp, AnimationTimeline animation) { }
    }

    public readonly struct Duration
    {
        public Duration(TimeSpan timeSpan) => TimeSpan = timeSpan;
        public TimeSpan TimeSpan { get; }
        public bool HasTimeSpan => true;
        public static Duration Automatic => default;
        public static Duration Forever => default;
        public static implicit operator Duration(TimeSpan timeSpan) => new(timeSpan);
    }

    public readonly struct RepeatBehavior
    {
        public RepeatBehavior(double count) => Count = count;
        public RepeatBehavior(TimeSpan duration) => Duration = duration;
        public double Count { get; }
        public TimeSpan Duration { get; }
        public static RepeatBehavior Forever => new(double.PositiveInfinity);
    }

    public enum FillBehavior { HoldEnd = 0, Stop = 1 }

    public abstract class Timeline : Animatable
    {
        public bool AutoReverse { get; set; }
        public RepeatBehavior RepeatBehavior { get; set; }
        public Duration Duration { get; set; }
        public TimeSpan? BeginTime { get; set; }
        public FillBehavior FillBehavior { get; set; }
    }

    public abstract class AnimationTimeline : Timeline { }
    public abstract class DoubleAnimationBase : AnimationTimeline { }

    public class DoubleAnimation : DoubleAnimationBase
    {
        public DoubleAnimation() { }
        public DoubleAnimation(double toValue, Duration duration) { To = toValue; Duration = duration; }
        public DoubleAnimation(double fromValue, double toValue, Duration duration) { From = fromValue; To = toValue; Duration = duration; }
        public double? From { get; set; }
        public double? To { get; set; }
        public IEasingFunction EasingFunction { get; set; }
    }

    public interface IEasingFunction
    {
        double Ease(double normalizedTime);
    }

    public enum EasingMode { EaseIn = 0, EaseOut = 1, EaseInOut = 2 }

    public abstract class EasingFunctionBase : Freezable, IEasingFunction
    {
        public EasingMode EasingMode { get; set; }
        public double Ease(double normalizedTime) => normalizedTime;
    }

    public class SineEase : EasingFunctionBase { }
    public class QuadraticEase : EasingFunctionBase { }
    public class CubicEase : EasingFunctionBase { }
}

namespace System.Windows.Media
{
    public abstract class Brush : Animation.Animatable { }

    public sealed class SolidColorBrush : Brush
    {
        public SolidColorBrush() { }
        internal SolidColorBrush(string name) => Name = name;
        internal string Name { get; } = "";
        public override string ToString() => Name;
    }

    public static class Brushes
    {
        public static SolidColorBrush Transparent { get; } = new("Transparent");
        public static SolidColorBrush Gray { get; } = new("Gray");
        public static SolidColorBrush Black { get; } = new("Black");
        public static SolidColorBrush White { get; } = new("White");
    }

    public abstract class GeneralTransform : Animation.Animatable { }
    public abstract class Transform : GeneralTransform { }

    public sealed class RotateTransform : Transform
    {
        public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle), typeof(double), typeof(RotateTransform), new PropertyMetadata(0.0));
        public RotateTransform() { }
        public RotateTransform(double angle) => Angle = angle;
        public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
    }

    public sealed class ScaleTransform : Transform
    {
        public static readonly DependencyProperty ScaleXProperty = DependencyProperty.Register(nameof(ScaleX), typeof(double), typeof(ScaleTransform), new PropertyMetadata(1.0));
        public static readonly DependencyProperty ScaleYProperty = DependencyProperty.Register(nameof(ScaleY), typeof(double), typeof(ScaleTransform), new PropertyMetadata(1.0));
        public ScaleTransform() { }
        public ScaleTransform(double scaleX, double scaleY) { ScaleX = scaleX; ScaleY = scaleY; }
        public double ScaleX { get => (double)GetValue(ScaleXProperty); set => SetValue(ScaleXProperty, value); }
        public double ScaleY { get => (double)GetValue(ScaleYProperty); set => SetValue(ScaleYProperty, value); }
    }

    public sealed class TranslateTransform : Transform
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    public abstract class ImageSource : Animation.Animatable { }
}

namespace System.Windows.Media.Imaging
{
    public abstract class BitmapSource : ImageSource
    {
        public int PixelWidth { get; }
        public int PixelHeight { get; }
    }

    public abstract class BitmapFrame : BitmapSource
    {
        public static BitmapFrame Create(BitmapSource source) => new StubFrame();
        private sealed class StubFrame : BitmapFrame { }
    }

    public abstract class BitmapEncoder : Threading.DispatcherObject
    {
        public IList<BitmapFrame> Frames { get; set; } = new List<BitmapFrame>();
        public virtual void Save(IO.Stream stream) { }
    }

    public sealed class PngBitmapEncoder : BitmapEncoder { }
}

namespace System.Windows.Input
{
    /// <summary>The members of WPF's Key enumeration that the overlay uses (values as in WPF).</summary>
    public enum Key
    {
        None = 0, Back = 2, Tab = 3, Return = 6, Enter = 6, Escape = 13, Space = 18,
        A = 44, B = 45, C = 46, D = 47, E = 48, F = 49, G = 50, H = 51, I = 52, J = 53, K = 54, L = 55, M = 56,
        N = 57, O = 58, P = 59, Q = 60, R = 61, S = 62, T = 63, U = 64, V = 65, W = 66, X = 67, Y = 68, Z = 69,
        Left = 23, Up = 24, Right = 25, Down = 26,
    }

    [Flags]
    public enum ModifierKeys { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }

    public static class Keyboard
    {
        public static ModifierKeys Modifiers => ModifierKeys.None;
        public static IInputElement Focus(IInputElement element) => element;
    }

    public class InputEventArgs : RoutedEventArgs { }
    public class KeyboardEventArgs : InputEventArgs { }

    public class KeyEventArgs : KeyboardEventArgs
    {
        public Key Key { get; }
        public Key SystemKey { get; }
        public bool IsRepeat { get; }
    }
    public delegate void KeyEventHandler(object sender, KeyEventArgs e);
}

namespace System.Windows.Interop
{
    public delegate IntPtr HwndSourceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled);

    public struct HwndSourceParameters
    {
        public HwndSourceParameters(string name) : this() => WindowName = name;
        public HwndSourceParameters(string name, int width, int height) : this() { WindowName = name; Width = width; Height = height; }
        public string WindowName { get; set; }
        public IntPtr ParentWindow { get; set; }
        public int WindowStyle { get; set; }
        public int ExtendedWindowStyle { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class HwndSource : PresentationSource, IDisposable
    {
        public HwndSource(HwndSourceParameters parameters) { }
        public static HwndSource FromHwnd(IntPtr hwnd) => null;
        public IntPtr Handle { get; }
        public void AddHook(HwndSourceHook hook) { }
        public void RemoveHook(HwndSourceHook hook) { }
        public void Dispose() { }
    }

    public sealed class WindowInteropHelper
    {
        public WindowInteropHelper(Window window) { }
        public IntPtr Handle { get; }
        public IntPtr Owner { get; set; }
        public IntPtr EnsureHandle() => IntPtr.Zero;
    }
}

namespace System.Windows.Markup
{
    public interface IComponentConnector
    {
        void Connect(int connectionId, object target);
        void InitializeComponent();
    }

    public class ParserContext
    {
        public Uri BaseUri { get; set; }
    }

    public class XamlParseException : SystemException
    {
        public XamlParseException() { }
        public XamlParseException(string message) : base(message) { }
        public XamlParseException(string message, Exception innerException) : base(message, innerException) { }
    }

    public static class XamlReader
    {
        public static object Load(IO.Stream stream) => Load(stream, null);

        /// <summary>Stub: parses the XML; a ResourceDictionary root becomes a dictionary with its x:Key entries. Malformed XML → XamlParseException.</summary>
        public static object Load(IO.Stream stream, ParserContext parserContext)
        {
            XDocument doc;
            try { doc = XDocument.Load(stream); }
            catch (XmlException ex) { throw new XamlParseException(ex.Message, ex); }
            if (doc.Root?.Name.LocalName != "ResourceDictionary") return doc.Root;
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var dictionary = new ResourceDictionary();
            foreach (var e in doc.Root.Elements())
                if (e.Attribute(x + "Key") is { } key) dictionary[key.Value] = e.Name.LocalName;
            return dictionary;
        }
    }
}

namespace System.IO.Packaging
{
    public static class PackUriHelper
    {
        static PackUriHelper()
        {
            // As in WPF: the pack scheme is registered on first use of PackUriHelper.
            if (!UriParser.IsKnownScheme("pack")) UriParser.Register(new GenericUriParser(GenericUriParserOptions.GenericAuthority), "pack", -1);
        }
        public static readonly string UriSchemePack = "pack";
    }
}

namespace Microsoft.Win32
{
    public abstract class CommonDialog
    {
        public bool? ShowDialog() => false;
        public bool? ShowDialog(System.Windows.Window owner) => false;
    }

    public abstract class FileDialog : CommonDialog
    {
        public string Title { get; set; } = "";
        public string Filter { get; set; } = "";
        public string FileName { get; set; } = "";
        public string[] FileNames { get; } = [];
        public bool CheckFileExists { get; set; } = true;
        public bool CheckPathExists { get; set; } = true;
        public bool DereferenceLinks { get; set; } = true;
        public string InitialDirectory { get; set; } = "";
    }

    public sealed class OpenFileDialog : FileDialog
    {
        public bool Multiselect { get; set; }
    }

    public sealed class OpenFolderDialog : CommonDialog
    {
        public string Title { get; set; } = "";
        public string FolderName { get; set; } = "";
        public string InitialDirectory { get; set; } = "";
    }
}

namespace Mediator.Verify.WpfStub
{
    /// <summary>Test hooks: what the stub does where real WPF would load a compiled resource.</summary>
    public static class StubHooks
    {
        /// <summary>Called for ResourceDictionary.Source. Default: WPF's error for a missing resource.</summary>
        public static Func<Uri, System.Windows.ResourceDictionary> ResourceLoader { get; set; } = DefaultLoader;

        public static System.Windows.ResourceDictionary DefaultLoader(Uri uri) =>
            throw new System.IO.IOException($"Cannot locate resource '{uri?.ToString().ToLowerInvariant()}'.");

        internal static System.Windows.ResourceDictionary LoadResource(Uri uri) => ResourceLoader(uri);
    }
}
