using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using ICSharpCode.SharpDevelop.Designer.Devices;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// 16×16 device icons from the Visual Studio 2026 Image Library, drawn from the same path geometry
/// as <c>Phone.xaml</c> / <c>PhoneLandscape.xaml</c> / <c>Computer.xaml</c>. Copied as geometry
/// rather than referenced as resources because the addin ships on its own and cannot rely on
/// OpenDevelop's Resources/VS2026 tree being present. The fill follows the theme's
/// <c>Foreground</c> brush, like every OpenDevelop icon.
/// </summary>
public static class DeviceIcons
{
    // Images/Phone.xaml
    const string PhoneGeometry =
        "M7 12C6.72386 12 6.5 12.2239 6.5 12.5C6.5 12.7761 6.72386 13 7 13H9C9.27614 13 9.5 12.7761 9.5 12.5C9.5 12.2239 9.27614 12 9 12H7ZM5.75 1C4.7835 1 4 1.7835 4 2.75V13.25C4 14.2165 4.7835 15 5.75 15H10.25C11.2165 15 12 14.2165 12 13.25V2.75C12 1.7835 11.2165 1 10.25 1H5.75ZM5 2.75C5 2.33579 5.33579 2 5.75 2H10.25C10.6642 2 11 2.33579 11 2.75V13.25C11 13.6642 10.6642 14 10.25 14H5.75C5.33579 14 5 13.6642 5 13.25V2.75Z";

    // Images/PhoneLandscape.xaml
    const string PhoneLandscapeGeometry =
        "M12 9C12 9.27614 12.2239 9.5 12.5 9.5C12.7761 9.5 13 9.27614 13 9L13 7C13 6.72386 12.7761 6.5 12.5 6.5C12.2239 6.5 12 6.72386 12 7L12 9ZM1 10.25C1 11.2165 1.7835 12 2.75 12L13.25 12C14.2165 12 15 11.2165 15 10.25L15 5.75C15 4.7835 14.2165 4 13.25 4L2.75 4C1.7835 4 1 4.7835 1 5.75L1 10.25ZM2.75 11C2.33579 11 2 10.6642 2 10.25L2 5.75C2 5.33579 2.33579 5 2.75 5L13.25 5C13.6642 5 14 5.33579 14 5.75L14 10.25C14 10.6642 13.6642 11 13.25 11L2.75 11Z";

    // Images/Android.xaml (the Android robot; the library has no portrait/landscape variants)
    const string AndroidGeometry =
        """M15.954 15.648C15.949 15.614 15.945 15.582 15.939 15.55C15.906 15.349 15.865 15.151 15.817 14.955C15.733 14.613 15.628 14.279 15.502 13.956C15.395 13.682 15.274 13.416 15.14 13.158C14.967 12.826 14.772 12.508 14.556 12.205C14.292 11.834 13.996 11.487 13.674 11.166C13.539 11.031 13.399 10.902 13.254 10.777C12.941 10.507 12.607 10.261 12.254 10.041C12.257 10.036 12.26 10.03 12.263 10.025C12.424 9.74602 12.586 9.46802 12.748 9.18902C12.906 8.91702 13.064 8.64402 13.222 8.37202C13.336 8.17702 13.449 7.98102 13.562 7.78502C13.589 7.73902 13.61 7.69102 13.626 7.64102C13.67 7.50402 13.672 7.36002 13.637 7.22602C13.628 7.19302 13.617 7.16102 13.604 7.12902C13.591 7.09702 13.575 7.06702 13.558 7.03702C13.498 6.93602 13.411 6.84702 13.303 6.78302C13.207 6.72602 13.1 6.69202 12.99 6.68202C12.944 6.67802 12.898 6.67802 12.851 6.68302C12.813 6.68702 12.775 6.69402 12.738 6.70402C12.604 6.74002 12.48 6.81302 12.384 6.91902C12.349 6.95702 12.318 6.99902 12.291 7.04602C12.178 7.24102 12.064 7.43702 11.951 7.63302L11.477 8.45002C11.316 8.72902 11.154 9.00702 10.992 9.28602C10.974 9.31602 10.957 9.34702 10.939 9.37802C10.915 9.36802 10.89 9.35802 10.866 9.34902C9.976 9.01002 9.011 8.82402 8.001 8.82402C7.973 8.82402 7.946 8.82402 7.918 8.82402C7.021 8.83302 6.159 8.98902 5.354 9.26902C5.261 9.30102 5.169 9.33602 5.078 9.37202C5.062 9.34302 5.045 9.31502 5.029 9.28702C4.868 9.00802 4.706 8.73002 4.544 8.45102C4.386 8.17902 4.228 7.90602 4.07 7.63402C3.956 7.43902 3.843 7.24302 3.73 7.04702C3.703 7.00102 3.672 6.95802 3.637 6.92002C3.541 6.81402 3.416 6.74102 3.283 6.70502C3.245 6.69502 3.208 6.68802 3.169 6.68402C3.123 6.67902 3.077 6.67902 3.031 6.68302C2.921 6.69202 2.814 6.72602 2.718 6.78402C2.61 6.84802 2.524 6.93702 2.463 7.03802C2.445 7.06802 2.43 7.09802 2.417 7.13002C2.404 7.16202 2.392 7.19402 2.384 7.22702C2.349 7.36102 2.35 7.50502 2.394 7.64202C2.41 7.69102 2.431 7.73902 2.458 7.78602C2.572 7.98102 2.685 8.17702 2.798 8.37302C2.956 8.64502 3.114 8.91702 3.272 9.19002C3.433 9.46902 3.595 9.74702 3.756 10.026C3.757 10.028 3.759 10.031 3.76 10.033C3.434 10.235 3.125 10.46 2.832 10.705C2.657 10.852 2.488 11.006 2.326 11.167C2.004 11.487 1.709 11.835 1.444 12.206C1.228 12.509 1.033 12.827 0.86 13.159C0.725 13.417 0.604 13.684 0.498 13.957C0.372 14.28 0.266 14.614 0.183 14.956C0.135 15.152 0.095 15.35 0.061 15.551C0.056 15.583 0.051 15.616 0.046 15.649C0.028 15.766 0.013 15.883 0 16.001H16C15.987 15.883 15.972 15.765 15.954 15.649V15.648ZM4.907 13.459C4.644 13.854 4.172 14.002 3.852 13.788C3.532 13.575 3.485 13.082 3.748 12.687C4.011 12.292 4.483 12.144 4.803 12.358C5.123 12.571 5.17 13.064 4.907 13.459ZM12.162 13.787C11.842 14 11.37 13.853 11.107 13.458C10.844 13.063 10.891 12.57 11.211 12.357C11.531 12.144 12.004 12.291 12.266 12.686C12.529 13.081 12.482 13.574 12.162 13.787Z""";

    // Images/Computer.xaml (a monitor and a laptop, two paths)
    const string ComputerMonitorGeometry =
        "M2.5 1H6.5C7.33 1 8 1.67 8 2.5V3H7V2.5C7 2.22 6.78 2 6.5 2H2.5C2.22 2 2 2.22 2 2.5V12.5C2 12.78 2.22 13 2.5 13H4.59C4.53 13.16 4.5 13.32 4.5 13.5C4.5 13.68 4.53 13.84 4.59 14H2.5C1.67 14 1 13.33 1 12.5V2.5C1 1.67 1.67 1 2.5 1Z";

    const string ComputerLaptopGeometry =
        "M6 4H12C13.103 4 14 4.897 14 6V10C14 11.103 13.103 12 12 12H11V13H12C12.276 13 12.5 13.224 12.5 13.5C12.5 13.776 12.276 14 12 14H6C5.724 14 5.5 13.776 5.5 13.5C5.5 13.224 5.724 13 6 13H7V12H6C4.897 12 4 11.103 4 10V6C4 4.897 4.897 4 6 4ZM8 13H10V12H8V13ZM12 11C12.551 11 13 10.552 13 10V6C13 5.448 12.551 5 12 5H6C5.449 5 5 5.448 5 6V10C5 10.552 5.449 11 6 11H12Z";

    /// <summary>The icon for a target: a phone (portrait or landscape), the Android robot, or the
    /// macOS computer.</summary>
    public static FrameworkElement For(DevicePlatform platform, bool landscape) => platform switch
    {
        DevicePlatform.Host => Draw(ComputerMonitorGeometry, ComputerLaptopGeometry),
        DevicePlatform.AndroidEmulator => Draw(AndroidGeometry),
        _ => Draw(landscape ? PhoneLandscapeGeometry : PhoneGeometry),
    };

    static FrameworkElement Draw(params string[] geometries)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        foreach (var geometry in geometries)
        {
            var path = new Path
            {
                Data = Geometry.Parse(geometry),
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
            };
            // The code equivalent of {DynamicResource Foreground}: colours follow the IDE theme.
            if (System.Windows.Application.Current?.TryFindResource("Foreground") is Brush foreground)
                path.Fill = foreground;
            else
                path.Fill = SystemColors.ControlTextBrush;
            canvas.Children.Add(path);
        }

        return new Viewbox { Width = 16, Height = 16, Child = canvas };
    }
}
