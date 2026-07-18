using System.Windows;
using System.Windows.Media;

namespace CodexUsageTray;

internal static class Theme
{
    public const string FontFamily = "Segoe UI Variable Text, Segoe UI";
    public static readonly FontFamily FontFamilyValue = new(FontFamily);

    public static readonly Color Background = Color.FromRgb(0x0d, 0x0d, 0x0d);
    public static readonly Color Surface = Color.FromRgb(0x14, 0x14, 0x14);
    public static readonly Color Elevated = Color.FromRgb(0x1c, 0x1c, 0x1c);
    public static readonly Color Header = Color.FromRgb(0x22, 0x22, 0x22);
    public static readonly Color Border = Color.FromRgb(0x2d, 0x2d, 0x2d);
    public static readonly Color BorderStrong = Color.FromRgb(0x40, 0x40, 0x40);

    public static readonly Color TextPrimary = Color.FromRgb(0xe8, 0xe8, 0xe8);
    public static readonly Color TextSecondary = Color.FromRgb(0xad, 0xad, 0xad);
    public static readonly Color TextMuted = Color.FromRgb(0x6e, 0x6e, 0x6e);

    public static readonly Color Accent = Color.FromRgb(0x56, 0x9c, 0xd6);
    public static readonly Color AccentHover = Color.FromRgb(0x6b, 0xb5, 0xe8);
    public static readonly Color AccentPressed = Color.FromRgb(0x45, 0x89, 0xc2);

    public static readonly Color Success = Color.FromRgb(0x48, 0xd4, 0x9b);
    public static readonly Color Error = Color.FromRgb(0xff, 0x7b, 0x86);
    public static readonly Color Warning = Color.FromRgb(0xf1, 0xb8, 0x5b);

    public static readonly Color ButtonNormal = Color.FromRgb(0x1e, 0x1e, 0x1e);
    public static readonly Color ButtonHover = Color.FromRgb(0x2e, 0x2e, 0x2e);
    public static readonly Color ButtonPressed = Color.FromRgb(0x17, 0x17, 0x17);
    public static readonly Color ButtonBorder = Color.FromRgb(0x33, 0x33, 0x33);
    public static readonly Color ButtonBorderHover = Color.FromRgb(0x50, 0x50, 0x50);

    public static readonly FontWeight FontWeightNormal = FontWeights.Normal;
    public static readonly FontWeight FontWeightSemibold = FontWeights.SemiBold;
    public static readonly FontWeight FontWeightBold = FontWeights.Bold;

    public const int FontSizeH1 = 28;
    public const int FontSizeH2 = 16;
    public const int FontSizeBody = 13;
    public const int FontSizeSmall = 11;
    public const int FontSizeCaption = 10;
    public const int FontSizeMicro = 9;

    public const double SpacingXs = 4;
    public const double SpacingSm = 8;
    public const double SpacingMd = 12;
    public const double SpacingLg = 16;
    public const double SpacingXl = 24;
    public const double SpacingXxl = 32;

    public const double ControlHeight = 36;
    public const double ControlHeightCompact = 30;

    public static readonly CornerRadius RadiusSmall = new(4);
    public static readonly CornerRadius RadiusMedium = new(6);
    public static readonly CornerRadius RadiusLarge = new(8);
    // Kept for the settings panel changes already present in the working tree.
    public static readonly CornerRadius RadiusCard = new(10);
    public static readonly CornerRadius RadiusXLarge = new(12);

    public const double BorderThickness = 1;

    public static System.Windows.Media.Effects.DropShadowEffect PanelShadow()
    {
        return new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 30,
            ShadowDepth = 10,
            Opacity = 0.55,
            Color = Colors.Black,
        };
    }

    public static readonly System.Windows.Media.Effects.DropShadowEffect WidgetShadow = new()
    {
        BlurRadius = 2,
        ShadowDepth = 1,
        Opacity = 0.7,
        Color = Colors.Black,
    };

    public static SolidColorBrush BgBrush => Brush(Background);
    public static SolidColorBrush SurfaceBrush => Brush(Surface);
    public static SolidColorBrush ElevatedBrush => Brush(Elevated);
    public static SolidColorBrush HeaderBrush => Brush(Header);
    public static SolidColorBrush BorderBrush => Brush(Border);
    public static SolidColorBrush BorderStrongBrush => Brush(BorderStrong);
    public static SolidColorBrush AccentBrush => Brush(Accent);
    public static SolidColorBrush AccentHoverBrush => Brush(AccentHover);
    public static SolidColorBrush AccentPressedBrush => Brush(AccentPressed);
    public static SolidColorBrush TextPrimaryBrush => Brush(TextPrimary);
    public static SolidColorBrush TextSecondaryBrush => Brush(TextSecondary);
    public static SolidColorBrush TextMutedBrush => Brush(TextMuted);
    public static SolidColorBrush SuccessBrush => Brush(Success);
    public static SolidColorBrush ErrorBrush => Brush(Error);
    public static SolidColorBrush WarningBrush => Brush(Warning);
    public static SolidColorBrush ButtonNormalBrush => Brush(ButtonNormal);
    public static SolidColorBrush ButtonHoverBrush => Brush(ButtonHover);
    public static SolidColorBrush ButtonPressedBrush => Brush(ButtonPressed);
    public static SolidColorBrush ButtonBorderBrush => Brush(ButtonBorder);
    public static SolidColorBrush ButtonBorderHoverBrush => Brush(ButtonBorderHover);

    public static readonly Color ScrollThumb = Color.FromRgb(0x45, 0x45, 0x45);
    public static readonly Color ScrollTrack = Background;
    public static SolidColorBrush ScrollThumbBrush => Brush(ScrollThumb);

    private static SolidColorBrush Brush(Color color) => new(color);
}
