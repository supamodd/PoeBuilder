// tool: tree-art-decoder
//
// Расшифровывает BC7 (BPTC) атласы классов из репо PoE2 (TreeData/*.dds, zstd),
// вырезает полноразмерные слайсы и пишет PNG в Data/Tree/Art/class.
//
// Алгоритм и таблицы BC7/BPTC (режимы 0..7, s_bptcP2/P3, s_bptcA2/A3, s_bptcFactors)
// портированы из K0lb3/texture2ddecoder, MIT License:
// https://github.com/K0lb3/texture2ddecoder  (Copyright (c) 2020 K0lb3)
//
// Формат DdsAtlas zstd:
//   [zstd-поток] -> [DDS-заголовок] [DX10-заголовок] [MIP0 слайса 0] [MIP0 слайса 1] ...
//   DDS:  magic "DDS ", 124-байтный header; +128: DX10
//         (format u32, resourceDim u32, miscFlags u32, arraySize u32, reserved u32).
//   Mip0-слайс s: offset = 148 + s * stride, где stride = (len - 148) / arraySize;
//          размер блока = ceil(W/4) * ceil(H/4) * 16.
//
// CLI:
//   tree-art-decoder probe <atlas.zst>
//       печатает параметры DDS/DdsAtlas (W, H, arraySize, stride, размер mip0, format).
//   tree-art-decoder extract <atlas.zst> <outDir> <Name=sliceIdx>[;<Name=sliceIdx>...]
//       декодирует указанные слайсы (mip0) и пишет outDir/<Name>.png (RGBA8).

using System.IO.Compression;
using System.Text;
using ZstdSharp;

static class Bc7
{
    // Режимы BC7: numSubsets, partitionBits, rotationBits, indexSelectionBits,
    //             colorBits, alphaBits, endpointPBits, sharedPBits, indexBits[0..1]
    static readonly (byte ss, byte p, byte rot, byte sel, byte c, byte a, byte ep, byte sp, byte i0, byte i1)[] Modes =
    {
        (3, 4, 0, 0, 4, 0, 1, 0, 3, 0), // 0
        (2, 6, 0, 0, 6, 0, 0, 1, 3, 0), // 1
        (3, 6, 0, 0, 5, 0, 0, 0, 2, 0), // 2
        (2, 6, 0, 0, 7, 0, 1, 0, 2, 0), // 3
        (1, 0, 2, 1, 5, 6, 0, 0, 2, 3), // 4
        (1, 0, 2, 0, 7, 8, 0, 0, 2, 2), // 5
        (1, 0, 0, 0, 7, 7, 1, 0, 4, 0), // 6
        (2, 6, 0, 0, 5, 5, 1, 0, 2, 0), // 7
    };

    // 64 партицион-сета по 2 субсета (1 бит на текстел, LSB = текстел 0)
    static readonly ushort[] P2 =
    {
        0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80, 0xc800, 0xffec,
        0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000, 0xf710, 0x008e, 0x7100, 0x08ce,
        0x008c, 0x7310, 0x3100, 0x8cce, 0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0,
        0x718e, 0x399c, 0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a,
        0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660, 0x0272, 0x04e4,
        0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c, 0x9336, 0x9cc6, 0x817e, 0xe718,
        0xccf0, 0x0fcc, 0x7744, 0xee22,
    };

    // anchor-тексел в ненулевом субсете (15 = текстел 15)
    static readonly byte[] A2 =
    {
        15, 15, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15, 15, 15, 15, 15,
        15,  2,  8,  2,  2,  8,  8, 15,
         2,  8,  2,  2,  8,  8,  2,  2,
        15, 15,  6,  8,  2,  8, 15, 15,
         2,  8,  2,  2,  2, 15, 15,  6,
         6,  2,  6,  8, 15, 15,  2,  2,
        15, 15, 15, 15, 15,  2,  2, 15,
    };

    // 64 партицион-сета по 3 субсета (2 бита на текстел, первый = текстел 0)
    static readonly uint[] P3 =
    {
        0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0, 0x5a5a5050,
        0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4, 0xa9a59450, 0x2a0a4250,
        0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454, 0x6a6a4040, 0xa4a45000, 0x1a1a0500,
        0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400, 0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200,
        0xa9a58000, 0x5090a0a8, 0xa8a09050, 0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50,
        0x500aa550, 0xaaaa4444, 0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600,
        0xaa444444, 0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000,
        0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44, 0x2a4a5254,
    };

    // anchor-тексел для субсета 1 / субсета 2
    static readonly byte[] A3a =
    {
         3,  3, 15, 15,  8,  3, 15, 15,
         8,  8,  6,  6,  6,  5,  3,  3,
         3,  3,  8, 15,  3,  3,  6, 10,
         5,  8,  8,  6,  8,  5, 15, 15,
         8, 15,  3,  5,  6, 10,  8, 15,
        15,  3, 15,  5, 15, 15, 15, 15,
         3, 15,  5,  5,  5,  8,  5, 10,
         5, 10,  8, 13, 15, 12,  3,  3,
    };
    static readonly byte[] A3b =
    {
        15,  8,  8,  3, 15, 15,  3,  8,
        15, 15, 15, 15, 15, 15, 15,  8,
        15,  8, 15,  3, 15,  8, 15,  8,
         3, 15,  6, 10, 15, 15, 10,  8,
        15,  3, 15, 10, 10,  8,  9, 10,
         6, 15,  8, 15,  3,  6,  6,  8,
        15,  3, 15, 15, 15, 15, 15, 15,
        15, 15, 15, 15,  3, 15, 15,  8,
    };

    // веса [0..64] для 2/3/4-битных индексов
    static readonly byte[] F2 = { 0, 21, 43, 64, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly byte[] F3 = { 0, 9, 18, 27, 37, 46, 55, 64, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly byte[] F4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    static byte[] Factors(int bits) => bits == 2 ? F2 : bits == 3 ? F3 : F4;

    static byte ExpandQuantized(byte v, int bits)
    {
        int x = v << (8 - bits);
        return (byte)(x | (v >> bits));
    }

    // Читает 4..12 бит из блока (LSB-first, как в BPTC) начиная с bitPos (без сдвига курсора).
    static int Peek(ReadOnlySpan<byte> block, int bitPos, int numBits)
    {
        if (numBits <= 0)
            return 0;
        int pos = bitPos / 8;
        int shift = bitPos & 7;
        uint data = 0;
        int n = Math.Min(4, 16 - pos);
        for (int i = 0; i < n; i++)
            data |= (uint)block[pos + i] << (8 * i);
        return (int)((data >> shift) & ((1 << numBits) - 1));
    }

    // Декодирует один 16-байтный BC7-блок в 16 RGBA-текселей (dst.Length >= 64).
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        int bitPos = 0;
        int Read(ReadOnlySpan<byte> b, int n)
        {
            int v = Peek(b, bitPos, n);
            bitPos += n;
            return v;
        }

        int mode = 0;
        while (mode < 8 && Read(block, 1) == 0)
            mode++;

        if (mode == 8) // block == 0x00...00 -> прозрачные пиксели
        {
            dst.Clear();
            return;
        }

        var mi = Modes[mode];
        int pBits = mi.ep != 0 ? mi.ep : mi.sp;

        int partitionSet = Read(block, mi.p);
        int rotation = Read(block, mi.rot);
        int indexSel = Read(block, mi.sel);

        byte[] r = new byte[6];
        byte[] g = new byte[6];
        byte[] b = new byte[6];
        byte[] a = new byte[6];

        for (int ii = 0; ii < mi.ss; ii++)
        {
            r[ii * 2] = (byte)(Read(block, mi.c) << pBits);
            r[ii * 2 + 1] = (byte)(Read(block, mi.c) << pBits);
        }
        for (int ii = 0; ii < mi.ss; ii++)
        {
            g[ii * 2] = (byte)(Read(block, mi.c) << pBits);
            g[ii * 2 + 1] = (byte)(Read(block, mi.c) << pBits);
        }
        for (int ii = 0; ii < mi.ss; ii++)
        {
            b[ii * 2] = (byte)(Read(block, mi.c) << pBits);
            b[ii * 2 + 1] = (byte)(Read(block, mi.c) << pBits);
        }
        if (mi.a != 0)
        {
            for (int ii = 0; ii < mi.ss; ii++)
            {
                a[ii * 2] = (byte)(Read(block, mi.a) << pBits);
                a[ii * 2 + 1] = (byte)(Read(block, mi.a) << pBits);
            }
        }
        else
        {
            Array.Fill(a, (byte)0xff);
        }

        if (pBits != 0)
        {
            for (int ii = 0; ii < mi.ss; ii++)
            {
                int pda = Read(block, pBits);
                int pdb = mi.sp == 0 ? Read(block, pBits) : pda;
                r[ii * 2] |= (byte)pda; r[ii * 2 + 1] |= (byte)pdb;
                g[ii * 2] |= (byte)pda; g[ii * 2 + 1] |= (byte)pdb;
                b[ii * 2] |= (byte)pda; b[ii * 2 + 1] |= (byte)pdb;
                a[ii * 2] |= (byte)pda; a[ii * 2 + 1] |= (byte)pdb;
            }
        }

        int cExp = mi.c + pBits;
        for (int ii = 0; ii < mi.ss; ii++)
        {
            r[ii * 2] = ExpandQuantized(r[ii * 2], cExp);
            r[ii * 2 + 1] = ExpandQuantized(r[ii * 2 + 1], cExp);
            g[ii * 2] = ExpandQuantized(g[ii * 2], cExp);
            g[ii * 2 + 1] = ExpandQuantized(g[ii * 2 + 1], cExp);
            b[ii * 2] = ExpandQuantized(b[ii * 2], cExp);
            b[ii * 2 + 1] = ExpandQuantized(b[ii * 2 + 1], cExp);
        }
        if (mi.a != 0)
        {
            int aExp = mi.a + pBits;
            for (int ii = 0; ii < mi.ss; ii++)
            {
                a[ii * 2] = ExpandQuantized(a[ii * 2], aExp);
                a[ii * 2 + 1] = ExpandQuantized(a[ii * 2 + 1], aExp);
            }
        }

        byte[] f0 = Factors(mi.i0);
        byte[] f1 = mi.i1 != 0 ? Factors(mi.i1) : f0;
        bool hasI1 = mi.i1 != 0;

        int off0 = 0;
        int off1 = mi.ss * (16 * mi.i0 - 1);

        for (int idx = 0; idx < 16; idx++)
        {
            int subset = 0;
            int anchor = 0;
            switch (mi.ss)
            {
                case 2:
                    subset = (P2[partitionSet] >> idx) & 1;
                    anchor = subset != 0 ? A2[partitionSet] : 0;
                    break;
                case 3:
                    subset = (int)((P3[partitionSet] >> (2 * idx)) & 3u);
                    anchor = subset != 0 ? (subset == 1 ? A3a[partitionSet] : A3b[partitionSet]) : 0;
                    break;
            }

            bool isAnchor = idx == anchor;
            // BPTC: anchor хранит полный индекс, остальные текстелы - на 1 бит меньше.
            int n0 = mi.i0 - (isAnchor ? 0 : 1);
            int n1 = hasI1 ? mi.i1 - (isAnchor ? 0 : 1) : 0;

            int i0 = Peek(block, bitPos + off0, n0);
            int i1 = hasI1 ? Peek(block, bitPos + off1, n1) : i0;
            off0 += n0;
            off1 += n1;

            // indexSel: 0 -> i0 = индекс цвета; 1 -> i0 = индекс альфы.
            int fc = indexSel == 0 ? f0[i0] : f0[i1];
            int fa = indexSel == 0 ? f1[i1] : f1[i0];

            int s2 = subset * 2;
            int fca = 64 - fc, fcb = fc, faa = 64 - fa, fab = fa;
            byte rr = (byte)((r[s2] * fca + r[s2 + 1] * fcb + 32) >> 6);
            byte gg = (byte)((g[s2] * fca + g[s2 + 1] * fcb + 32) >> 6);
            byte bb = (byte)((b[s2] * fca + b[s2 + 1] * fcb + 32) >> 6);
            byte aa = (byte)((a[s2] * faa + a[s2 + 1] * fab + 32) >> 6);

            switch (rotation)
            {
                case 1: (aa, rr) = (rr, aa); break;
                case 2: (aa, gg) = (gg, aa); break;
                case 3: (aa, bb) = (bb, aa); break;
            }

            dst[idx * 4] = rr;
            dst[idx * 4 + 1] = gg;
            dst[idx * 4 + 2] = bb;
            dst[idx * 4 + 3] = aa;
        }
    }
}

sealed class DdsAtlas
{
    public int Width;
    public int Height;
    public int ArraySize;
    public uint DxgiFormat;
    public uint Caps1;
    public uint Caps2;
    public int Stride; // байты слайса (mip0 + мипы)
    public int DataLen;

    public int Mip0Size => ((Width + 3) / 4) * ((Height + 3) / 4) * 16;

    public static DdsAtlas FromDds(byte[] dds)
    {
        if (dds.Length < 148)
            throw new InvalidDataException("короткий DDS");
        if (dds[0] != 0x44 || dds[1] != 0x44 || dds[2] != 0x53 || dds[3] != 0x20)
            throw new InvalidDataException("нет magic 'DDS '");

        int height = BitConverter.ToInt32(dds, 12);
        int width = BitConverter.ToInt32(dds, 16);
        uint caps1 = BitConverter.ToUInt32(dds, 68);
        uint caps2 = BitConverter.ToUInt32(dds, 72);
        uint dxgiFormat = BitConverter.ToUInt32(dds, 128);
        uint resourceDim = BitConverter.ToUInt32(dds, 132);
        uint arraySize = BitConverter.ToUInt32(dds, 140);

        if (resourceDim != 3) // D3D11_RESOURCE_DIMENSION_TEXTURE2D
            throw new InvalidDataException($"resourceDim = {resourceDim} (ожидается 3)");

        int dataLen = dds.Length - 148;
        if (arraySize < 1 || dataLen % arraySize != 0)
            throw new InvalidDataException($"arraySize={arraySize}, dataLen={dataLen}");
        int stride = dataLen / (int)arraySize;

        int mip0 = ((width + 3) / 4) * ((height + 3) / 4) * 16;
        if (stride < mip0)
            throw new InvalidDataException($"stride {stride} < mip0 {mip0}");

        return new DdsAtlas
        {
            Width = width,
            Height = height,
            ArraySize = (int)arraySize,
            DxgiFormat = dxgiFormat,
            Caps1 = caps1,
            Caps2 = caps2,
            Stride = stride,
            DataLen = dataLen,
        };
    }

    // Декодирует mip0-слайса sliceIdx в RGBA8 (width * height * 4 байта).
    public static byte[] DecodeSlice(byte[] dds, int sliceIdx)
    {
        var a = FromDds(dds);
        if (sliceIdx < 0 || sliceIdx >= a.ArraySize)
            throw new ArgumentOutOfRangeException(nameof(sliceIdx));

        int start = 148 + sliceIdx * a.Stride;
        int blocksX = (a.Width + 3) / 4;
        int blocksY = (a.Height + 3) / 4;
        byte[] px = new byte[a.Width * a.Height * 4];
        byte[] block = new byte[16];
        Span<byte> dst = stackalloc byte[64];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                Array.Copy(dds, start + (by * blocksX + bx) * 16, block, 0, 16);
                Bc7.DecodeBlock(block, dst);

                for (int ty = 0; ty < 4; ty++)
                {
                    int py = by * 4 + ty;
                    if (py >= a.Height)
                        break;
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int px_ = bx * 4 + tx;
                        if (px_ >= a.Width)
                            continue;
                        int o = (py * a.Width + px_) * 4;
                        px[o] = dst[ty * 16 + tx * 4];
                        px[o + 1] = dst[ty * 16 + tx * 4 + 1];
                        px[o + 2] = dst[ty * 16 + tx * 4 + 2];
                        px[o + 3] = dst[ty * 16 + tx * 4 + 3];
                    }
                }
            }
        }
        return px;
    }
}

static class PngWriter
{
    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    // PNG хранит многобайтовые поля big-endian.
    static byte[] Be32(uint v) =>
        new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    static byte[] Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return new byte[] { (byte)(a >> 8), (byte)a, (byte)(b >> 8), (byte)b };
    }

    // RGBA8 -> PNG (color type 6, bit depth 8).
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        byte[] raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (1 + width * 4);
            raw[dst] = 0; // filter: none
            rgba.Slice(y * width * 4, width * 4).CopyTo(raw.AsSpan(dst + 1));
        }

        using var zlib = new MemoryStream();
        zlib.Write(new byte[] { 0x78, 0x9C });
        using (var deflate = new DeflateStream(zlib, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        zlib.Write(Adler32(raw), 0, 4);
        byte[] idat = zlib.ToArray();

        using var out_ = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            byte[] name = Encoding.ASCII.GetBytes(type);
            out_.Write(Be32((uint)data.Length), 0, 4);
            out_.Write(name, 0, 4);
            out_.Write(data, 0, data.Length);
            byte[] crcBuf = new byte[4 + data.Length];
            name.CopyTo(crcBuf, 0);
            data.CopyTo(crcBuf, 4);
            out_.Write(Be32(Crc32(crcBuf)), 0, 4);
        }

        out_.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        byte[] ihdr = new byte[13];
        Buffer.BlockCopy(Be32((uint)width), 0, ihdr, 0, 4);
        Buffer.BlockCopy(Be32((uint)height), 0, ihdr, 4, 4);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // color type RGBA
        Chunk("IHDR", ihdr);
        Chunk("IDAT", idat);
        Chunk("IEND", Array.Empty<byte>());
        return out_.ToArray();
    }
}

static class Program
{
    static byte[] LoadZstd(string path)
    {
        // ZstdStream ������� �� BCL � .NET 10 -> managed-���� ZstdSharp (MIT).
        byte[] compressed = File.ReadAllBytes(path);
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(compressed).ToArray();
    }

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: tree-art-decoder probe <atlas.zst>\n" +
                "       tree-art-decoder extract <atlas.zst> <outDir> <Name=sliceIdx>[;<Name=sliceIdx>...]");
            return 2;
        }

        byte[] dds = LoadZstd(args[1]);
        var a = DdsAtlas.FromDds(dds);

        if (args[0] == "probe")
        {
            Console.WriteLine($"width={a.Width} height={a.Height} arraySize={a.ArraySize}");
            Console.WriteLine($"dxgiFormat=0x{a.DxgiFormat:X8} caps1=0x{a.Caps1:X8} caps2=0x{a.Caps2:X8}");
            Console.WriteLine($"dataLen={a.DataLen} stride={a.Stride} mip0={a.Mip0Size}");
            Console.WriteLine($"slices beyond mip0 per slice={a.Stride - a.Mip0Size}");
            return 0;
        }

        if (args[0] == "extract" && args.Length >= 4)
        {
            string outDir = args[2];
            Directory.CreateDirectory(outDir);

            int exit = 0;
            foreach (string spec in args[3].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = spec.Split('=', 2);
                if (kv.Length != 2 || !int.TryParse(kv[1], out int sliceIdx))
                {
                    Console.Error.WriteLine($"плохой слайс '{spec}' (ожидается Name=sliceIdx)");
                    exit = 2;
                    continue;
                }
                try
                {
                    byte[] px = DdsAtlas.DecodeSlice(dds, sliceIdx);
                    byte[] png = PngWriter.Encode(a.Width, a.Height, px);
                    string outPath = Path.Combine(outDir, kv[0] + ".png");
                    File.WriteAllBytes(outPath, png);
                    Console.WriteLine($"{outPath} ({a.Width}x{a.Height}, {png.Length} bytes)");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"слайс {spec}: {ex.Message}");
                    exit = 1;
                }
            }
            return exit;
        }

        Console.Error.WriteLine($"неизвестная подкоманда: {args[0]}");
        return 2;
    }
}


