using UnityEngine;

namespace GSCapture.UI
{
    /// <summary>One place for colours, type and spacing. Dark "glass" over the camera feed, teal accent.</summary>
    public static class Theme
    {
        public static readonly Color Background = Hex(0x0B0F14);
        public static readonly Color Glass = new Color(0.07f, 0.09f, 0.12f, 0.78f);
        public static readonly Color GlassLight = new Color(0.13f, 0.16f, 0.21f, 0.85f);
        public static readonly Color Card = Hex(0x151A22);
        public static readonly Color CardHover = Hex(0x1C2330);
        public static readonly Color Stroke = new Color(1f, 1f, 1f, 0.08f);

        public static readonly Color Accent = Hex(0x2DD4BF);
        public static readonly Color AccentDim = Hex(0x14B8A6);
        public static readonly Color OnAccent = Hex(0x04211D);

        public static readonly Color Text = Hex(0xF3F5F8);
        public static readonly Color TextDim = Hex(0x9AA4B2);
        public static readonly Color TextFaint = Hex(0x5F6B7A);

        public static readonly Color Success = Hex(0x34D399);
        public static readonly Color Warning = Hex(0xFBBF24);
        public static readonly Color Danger = Hex(0xF87171);
        public static readonly Color Info = Hex(0x60A5FA);

        public const int TitleSize = 60;
        public const int HeadingSize = 40;
        public const int BodySize = 32;
        public const int CaptionSize = 26;

        public const float Radius = 28f;
        public const float Gap = 24f;
        public const float Pad = 40f;

        public static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

        static Font regular, semibold, bold;
        public static Font Regular => regular ? regular : regular = LoadFont("Fonts/Inter-Regular");
        public static Font SemiBold => semibold ? semibold : semibold = LoadFont("Fonts/Inter-SemiBold");
        public static Font Bold => bold ? bold : bold = LoadFont("Fonts/Inter-Bold");

        static Font LoadFont(string path)
        {
            var f = Resources.Load<Font>(path);
            return f != null ? f : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
    }
}
