using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Pengu.FontGen
{
    class Program
    {
        static IEnumerable<char> Range(char from, char to)
        {
            while (from <= to)
            {
                yield return from;
                ++from;
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: fontgen \"font-name\" font-size");
                return;
            }

            var chars = new List<char>();
            chars.AddRange(Range('a', 'z'));
            chars.AddRange(Range('A', 'Z'));
            chars.AddRange(Range('0', '9'));
            chars.AddRange(" `~!@#$%^&*()_+-=[]\\{}|;':\",./<>?");
            chars.AddRange("─│┌┐└┘├┤┬┴┼═║╒╓╔╕╖╗╘╙╚╛╜╝╞╟╠╡╢╣╤╥╦╧╨╩╪╫╬");

            Dictionary<char, Size> charSizes;

            using var font = new Font(args[0], Convert.ToSingle(args[1]));

            using (var tmpbmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(tmpbmp))
                charSizes = chars.Select(ch => (ch, size: TextRenderer.MeasureText(g, ch.ToString(), font, Size.Empty,
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding)))
                    .ToDictionary(w => w.ch, w => w.size);

            var area = charSizes.Values.Sum(s => s.Width * s.Height);
            var texWidth = (int)(Math.Sqrt(area) * 1.07);

            var fn = args[0].Replace(' ', '_');
            using var binoutput = new BinaryWriter(File.OpenWrite($"{fn}.bin"));

            using var bmp = new Bitmap(texWidth, texWidth, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                int x = 0, y = 0;
                foreach (var ch in chars)
                {
                    var size = charSizes[ch];
                    if (x + size.Width >= texWidth)
                    {
                        x = 0;
                        y += size.Height;
                    }

                    TextRenderer.DrawText(g, ch.ToString(), font, new Point(x - 15, y), Color.White, TextFormatFlags.NoPrefix);
                    binoutput.Write(ch);
                    binoutput.Write((float)x / texWidth); binoutput.Write((float)y / texWidth);

                    x += size.Width;

                    binoutput.Write((float)x / texWidth); binoutput.Write((float)(y + size.Height) / texWidth);
                }
            }

            bmp.Save($"{fn}.png");
        }
    }
}
