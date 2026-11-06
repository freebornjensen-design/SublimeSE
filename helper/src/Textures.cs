using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace SeHelper
{
    // Текстуры LCD-спрайтов: имя спрайта -> файл из Content -> PNG для браузера.
    // Игра хранит их в DDS (в основном BC7), поэтому здесь свой декодер BC7/BC3/BC1.
    public class Textures
    {
        readonly string _content;
        readonly Definitions _defs;
        Dictionary<string, string> _paths;
        readonly Dictionary<string, byte[]> _png = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        readonly object _lock = new object();

        public Textures(string contentDir, Definitions defs) { _content = contentDir; _defs = defs; }

        void Index()
        {
            if (_paths != null) return;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var data = Path.Combine(_content, "Data");
            foreach (var f in Directory.GetFiles(data, "LCDTextures*.sbc"))
            {
                try
                {
                    foreach (var def in XDocument.Load(f).Descendants("LCDTextureDefinition"))
                    {
                        var id = def.Element("Id");
                        if (id == null) continue;
                        var name = (string)id.Element("SubtypeId") ?? (string)id.Attribute("Subtype");
                        var path = (string)def.Element("SpritePath") ?? (string)def.Element("TexturePath");
                        if (name != null && path != null) d[name] = path;
                    }
                }
                catch { }
            }
            _paths = d;
        }

        public List<string> Names()
        {
            lock (_lock) { Index(); return _paths.Keys.OrderBy(k => k).ToList(); }
        }

        string Resolve(string name)
        {
            Index();
            string rel;
            if (!_paths.TryGetValue(name, out rel))
            {
                // иконки предметов: "MyObjectBuilder_Ore/Iron"
                int slash = name.IndexOf('/');
                if (slash <= 0) return null;
                var item = _defs.Item(name.Substring(0, slash), name.Substring(slash + 1));
                if (item == null || item.Icon == null) return null;
                rel = item.Icon;
            }
            var full = Path.Combine(_content, rel);
            if (File.Exists(full)) return full;
            foreach (var ext in new[] { ".dds", ".png" })
            {
                var alt = Path.ChangeExtension(full, ext);
                if (File.Exists(alt)) return alt;
            }
            return null;
        }

        public byte[] Png(string name)
        {
            lock (_lock)
            {
                byte[] cached;
                if (_png.TryGetValue(name, out cached)) return cached;
                byte[] res = null;
                try
                {
                    var file = Resolve(name);
                    if (file != null)
                    {
                        if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) res = File.ReadAllBytes(file);
                        else
                        {
                            int w, h;
                            var rgba = Dds.Decode(File.ReadAllBytes(file), out w, out h);
                            if (rgba != null) res = ToPng(rgba, w, h);
                        }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("Текстура " + name + ": " + ex.Message); }
                _png[name] = res;
                return res;
            }
        }

        static byte[] ToPng(byte[] rgba, int w, int h)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                var bgra = new byte[w * h * 4];
                for (int i = 0; i < w * h; i++)
                {
                    bgra[i * 4] = rgba[i * 4 + 2];
                    bgra[i * 4 + 1] = rgba[i * 4 + 1];
                    bgra[i * 4 + 2] = rgba[i * 4];
                    bgra[i * 4 + 3] = rgba[i * 4 + 3];
                }
                for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
                bmp.UnlockBits(bd);
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }

    // Чтение верхнего mip-уровня DDS: BC1 (DXT1), BC3 (DXT5), BC7.
    public static class Dds
    {
        public static byte[] Decode(byte[] f, out int w, out int h)
        {
            w = h = 0;
            if (f.Length < 128 || BitConverter.ToUInt32(f, 0) != 0x20534444) return null;   // "DDS "
            h = BitConverter.ToInt32(f, 12);
            w = BitConverter.ToInt32(f, 16);
            string fourcc = System.Text.Encoding.ASCII.GetString(f, 84, 4);
            int offset = 128, fmt;
            if (fourcc == "DX10")
            {
                uint dxgi = BitConverter.ToUInt32(f, 128);
                offset = 148;
                fmt = dxgi == 98 || dxgi == 99 ? 7 : dxgi == 77 || dxgi == 78 ? 3 : dxgi == 71 || dxgi == 72 ? 1 : 0;
            }
            else fmt = fourcc == "DXT5" ? 3 : fourcc == "DXT1" ? 1 : 0;
            if (fmt == 0) return null;
            var outp = new byte[w * h * 4];
            int bw = (w + 3) / 4, bh = (h + 3) / 4, bsize = fmt == 1 ? 8 : 16;
            var px = new byte[64];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int p = offset + (by * bw + bx) * bsize;
                    if (p + bsize > f.Length) return outp;
                    if (fmt == 7) Bc7.DecodeBlock(f, p, px);
                    else if (fmt == 3) { DecodeBc1(f, p + 8, px, false); DecodeBc4Alpha(f, p, px); }
                    else DecodeBc1(f, p, px, true);
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int X = bx * 4 + x, Y = by * 4 + y;
                            if (X >= w || Y >= h) continue;
                            Buffer.BlockCopy(px, (y * 4 + x) * 4, outp, (Y * w + X) * 4, 4);
                        }
                }
            return outp;
        }

        static void Rgb565(int c, byte[] o, int i)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            o[i] = (byte)((r << 3) | (r >> 2)); o[i + 1] = (byte)((g << 2) | (g >> 4)); o[i + 2] = (byte)((b << 3) | (b >> 2)); o[i + 3] = 255;
        }

        static void DecodeBc1(byte[] f, int p, byte[] px, bool alpha1bit)
        {
            int c0 = BitConverter.ToUInt16(f, p), c1 = BitConverter.ToUInt16(f, p + 2);
            var pal = new byte[16];
            Rgb565(c0, pal, 0); Rgb565(c1, pal, 4);
            bool four = c0 > c1 || !alpha1bit;
            for (int k = 0; k < 3; k++)
            {
                if (four)
                {
                    pal[8 + k] = (byte)((2 * pal[k] + pal[4 + k]) / 3);
                    pal[12 + k] = (byte)((pal[k] + 2 * pal[4 + k]) / 3);
                }
                else
                {
                    pal[8 + k] = (byte)((pal[k] + pal[4 + k]) / 2);
                    pal[12 + k] = 0;
                }
            }
            pal[11] = 255; pal[15] = (byte)(four ? 255 : 0);
            uint idx = BitConverter.ToUInt32(f, p + 4);
            for (int i = 0; i < 16; i++)
            {
                int s = (int)((idx >> (2 * i)) & 3);
                px[i * 4] = pal[s * 4]; px[i * 4 + 1] = pal[s * 4 + 1]; px[i * 4 + 2] = pal[s * 4 + 2];
                if (alpha1bit) px[i * 4 + 3] = pal[s * 4 + 3];
            }
        }

        static void DecodeBc4Alpha(byte[] f, int p, byte[] px)
        {
            int a0 = f[p], a1 = f[p + 1];
            var a = new int[8];
            a[0] = a0; a[1] = a1;
            if (a0 > a1) for (int i = 1; i < 7; i++) a[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            else { for (int i = 1; i < 5; i++) a[i + 1] = ((5 - i) * a0 + i * a1) / 5; a[6] = 0; a[7] = 255; }
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)f[p + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++) px[i * 4 + 3] = (byte)a[(int)((bits >> (3 * i)) & 7)];
        }
    }

    // Декодер BC7 по спецификации D3D11 (8 режимов, таблицы разбиения на 2 и 3 подмножества).
    public static class Bc7
    {
        // NS, PB, RB, ISB, CB, AB, EPB, SPB, IB, IB2
        static readonly int[,] Modes =
        {
            { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
            { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
            { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
            { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
            { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
            { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
            { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
            { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
        };

        static readonly ushort[] Part2 =
        {
            0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
            0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
            0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
            0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
        };

        static readonly string[] Part3 =
        {
            "0011001102210222", "0001001122112221", "0000200122112211", "0222002200110111", "0000000011221122", "0011001100220022",
            "0022002211111111", "0011001122112211", "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012",
            "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220", "0001001101121122", "0111001120012200",
            "0000112211221122", "0022002200221111", "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
            "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221", "0022110211020022", "0110011020022222",
            "0011012201220011", "0000200022112221", "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120",
            "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011", "0011112222000011", "0101010122222222",
            "0000000021212121", "0022112200221122", "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
            "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112", "0222011101110222", "0002111211120002",
            "0110011001102222", "0000000021122112", "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112",
            "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
        };

        static readonly byte[] Anchor2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
        };

        static readonly byte[] Anchor3a =
        {
            3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
            8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
        };

        static readonly byte[] Anchor3b =
        {
            15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
            15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
        };

        static readonly int[] W2 = { 0, 21, 43, 64 };
        static readonly int[] W3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        static readonly int[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        static int[] Weights(int bits) { return bits == 2 ? W2 : bits == 3 ? W3 : W4; }

        class Bits
        {
            readonly byte[] _d; readonly int _p; int _pos;
            public Bits(byte[] d, int p) { _d = d; _p = p; }
            public int Read(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++, _pos++)
                    v |= ((_d[_p + (_pos >> 3)] >> (_pos & 7)) & 1) << i;
                return v;
            }
        }

        static int Subset(int ns, int part, int i)
        {
            if (ns == 2) return (Part2[part] >> i) & 1;
            if (ns == 3) return Part3[part][i] - '0';
            return 0;
        }

        static bool IsAnchor(int ns, int part, int i)
        {
            if (i == 0) return true;
            if (ns == 2) return i == Anchor2[part];
            if (ns == 3) return i == Anchor3a[part] || i == Anchor3b[part];
            return false;
        }

        public static void DecodeBlock(byte[] d, int p, byte[] px)
        {
            int mode = 0;
            while (mode < 8 && ((d[p] >> mode) & 1) == 0) mode++;
            if (mode == 8) { Array.Clear(px, 0, 64); return; }
            var r = new Bits(d, p);
            r.Read(mode + 1);
            int ns = Modes[mode, 0], pb = Modes[mode, 1], rb = Modes[mode, 2], isb = Modes[mode, 3], cb = Modes[mode, 4],
                ab = Modes[mode, 5], epb = Modes[mode, 6], spb = Modes[mode, 7], ib = Modes[mode, 8], ib2 = Modes[mode, 9];
            int part = r.Read(pb), rot = r.Read(rb), idxMode = r.Read(isb);

            int ne = ns * 2;
            var ep = new int[ne, 4];
            for (int c = 0; c < 3; c++) for (int e = 0; e < ne; e++) ep[e, c] = r.Read(cb);
            for (int e = 0; e < ne; e++) ep[e, 3] = ab > 0 ? r.Read(ab) : 255;

            int cbits = cb, abits = ab;
            if (epb > 0)
            {
                for (int e = 0; e < ne; e++)
                {
                    int pbit = r.Read(1);
                    for (int c = 0; c < 3; c++) ep[e, c] = (ep[e, c] << 1) | pbit;
                    if (ab > 0) ep[e, 3] = (ep[e, 3] << 1) | pbit;
                }
                cbits++; if (ab > 0) abits++;
            }
            else if (spb > 0)
            {
                for (int s = 0; s < ns; s++)
                {
                    int pbit = r.Read(1);
                    for (int e = s * 2; e < s * 2 + 2; e++)
                    {
                        for (int c = 0; c < 3; c++) ep[e, c] = (ep[e, c] << 1) | pbit;
                        if (ab > 0) ep[e, 3] = (ep[e, 3] << 1) | pbit;
                    }
                }
                cbits++; if (ab > 0) abits++;
            }
            for (int e = 0; e < ne; e++)
            {
                for (int c = 0; c < 3; c++) ep[e, c] = Expand(ep[e, c], cbits);
                if (ab > 0) ep[e, 3] = Expand(ep[e, 3], abits);
            }

            var idx1 = new int[16];
            var idx2 = new int[16];
            for (int i = 0; i < 16; i++) idx1[i] = r.Read(IsAnchor(ns, part, i) ? ib - 1 : ib);
            if (ib2 > 0) for (int i = 0; i < 16; i++) idx2[i] = r.Read(i == 0 ? ib2 - 1 : ib2);

            for (int i = 0; i < 16; i++)
            {
                int s = Subset(ns, part, i);
                int e0 = s * 2, e1 = s * 2 + 1;
                int ci, ai, cbw, abw;
                if (ib2 == 0) { ci = ai = idx1[i]; cbw = abw = ib; }
                else if (idxMode == 0) { ci = idx1[i]; cbw = ib; ai = idx2[i]; abw = ib2; }
                else { ci = idx2[i]; cbw = ib2; ai = idx1[i]; abw = ib; }
                var wc = Weights(cbw)[ci];
                var wa = Weights(abw)[ai];
                int o = i * 4;
                for (int c = 0; c < 3; c++) px[o + c] = (byte)(((64 - wc) * ep[e0, c] + wc * ep[e1, c] + 32) >> 6);
                px[o + 3] = (byte)(((64 - wa) * ep[e0, 3] + wa * ep[e1, 3] + 32) >> 6);
                if (rot > 0)
                {
                    byte t = px[o + 3]; px[o + 3] = px[o + rot - 1]; px[o + rot - 1] = t;
                }
            }
        }

        static int Expand(int v, int bits)
        {
            v <<= 8 - bits;
            return v | (v >> bits);
        }
    }
}
