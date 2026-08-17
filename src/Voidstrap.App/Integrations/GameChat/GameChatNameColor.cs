using System.Windows.Media;

namespace Voidstrap.Integrations.GameChat
{
    public static class GameChatNameColor
    {
        private static readonly Color[] NAME_COLORS =
        {
            Color.FromRgb(253, 41, 67),
            Color.FromRgb(1, 162, 255),
            Color.FromRgb(2, 184, 87),
            Color.FromRgb(107, 50, 124),
            Color.FromRgb(218, 133, 65),
            Color.FromRgb(245, 205, 48),
            Color.FromRgb(232, 186, 200),
            Color.FromRgb(215, 197, 154)
        };

        private static readonly SolidColorBrush[] NAME_BRUSHES = CreateBrushes();

        private static SolidColorBrush[] CreateBrushes()
        {
            var brushes = new SolidColorBrush[NAME_COLORS.Length];
            for (int i = 0; i < NAME_COLORS.Length; i++)
            {
                brushes[i] = new SolidColorBrush(NAME_COLORS[i]);
                brushes[i].Freeze();
            }
            return brushes;
        }

        private static int GetNameIndex(string name)
        {
            int value = 0;
            for (int i = 0; i < name.Length; i++)
            {
                int cValue = name[i];
                int reverseIndex = name.Length - i;
                if (name.Length % 2 == 1)
                    reverseIndex--;
                if (reverseIndex % 4 >= 2)
                    cValue = -cValue;
                value += cValue;
            }
            int index = value % NAME_COLORS.Length;
            if (index < 0)
                index += NAME_COLORS.Length;
            return index;
        }

        public static Color GetNameColor(string name)
        {
            return NAME_COLORS[GetNameIndex(name)];
        }

        public static SolidColorBrush GetNameBrush(string name)
        {
            return NAME_BRUSHES[GetNameIndex(name)];
        }
    }
}
