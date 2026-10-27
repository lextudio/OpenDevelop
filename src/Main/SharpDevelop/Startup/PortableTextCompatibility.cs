using System.Windows;
using System.Windows.Media;
using System.Windows.Media.ProGPU;
using ProGPU.Wpf.Interop;

namespace ICSharpCode.SharpDevelop.Startup;

/// <summary>Adapts legacy Display-mode themes to the installed portable formatter's capabilities.</summary>
internal static class PortableTextCompatibility
{
    static bool initialized;
    static readonly object gate = new();

    public static void Initialize()
    {
        lock (gate) {
            if (initialized)
                return;

            // Only the portable media backend uses the portable formatter. On Windows the SDK
            // bootstrap keeps Windows MIL and registers no portable window host, so selecting
            // portable media here would make every Window.Show throw PlatformNotSupportedException.
            if (PortableWpfRuntime.ConfiguredMediaBackend != PortableWpfMediaBackend.Portable) {
                initialized = true;
                return;
            }

            ProGpuWpfNativeMediaServices.Initialize();
            if (!PortableWpfServiceRegistry.TryGetTextFormatting(out var formatter)
                || formatter is not IPortableDisplayTextFormatting) {
                // The portable Display provider is not yet qualified. Its simple fallback cannot
                // shape CJK and can abort the entire layout pass. Make the application's choice
                // explicit until a provider implements Display, including for theme-local values.
                TextOptions.TextFormattingModeProperty.OverrideMetadata(typeof(FrameworkElement),
                    new FrameworkPropertyMetadata(TextFormattingMode.Ideal,
                        FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender
                            | FrameworkPropertyMetadataOptions.Inherits,
                        null, (_, _) => TextFormattingMode.Ideal));
            }
            initialized = true;
        }
    }
}
