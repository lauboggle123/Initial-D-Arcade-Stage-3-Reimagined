using System;

// CPU decode of BC1 (DXT1) and BC3 (DXT5) for GPUs without BC texture support
// (many Mali). Course packs keep their authored DXT payloads; this only expands
// each stored mip level to RGBA32 at load, so nothing is re-encoded and the
// source mip chain and alpha masks stay exactly as authored. Depends on nothing
// from UnityEngine so Tests/Courses can compile it on its own.
public static class Idas3DxtDecoder
{
    // Total compressed bytes for a chain of mips (each level halves, minimum 1).
    public static int CompressedSize(int width, int height, int mips, bool bc3)
    {
        long total = 0;
        for (int level = 0; level < mips; ++level, width = Math.Max(1, width / 2), height = Math.Max(1, height / 2))
            total += (long)((width + 3) / 4) * ((height + 3) / 4) * (bc3 ? 16 : 8);
        return checked((int)total);
    }

    // RGBA32 bytes for every level, in the same order as the source chain.
    public static byte[] Decode(byte[] source, int offset, int width, int height, int mips, bool bc3)
    {
        if (width <= 0 || height <= 0 || mips <= 0) throw new ArgumentException("Invalid DXT dimensions.");
        if (source.Length - offset < CompressedSize(width, height, mips, bc3)) throw new ArgumentException("Truncated DXT mip chain.");
        long outputSize = 0;
        for (int level = 0, w = width, h = height; level < mips; ++level, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2)) outputSize += (long)w * h * 4;
        var output = new byte[checked((int)outputSize)];
        int read = offset, write = 0;
        var block = new byte[64];
        for (int level = 0, w = width, h = height; level < mips; ++level, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            for (int by = 0; by < (h + 3) / 4; ++by)
                for (int bx = 0; bx < (w + 3) / 4; ++bx)
                {
                    if (bc3) { DecodeAlpha(source, read, block); DecodeColor(source, read + 8, block, true); read += 16; }
                    else { DecodeColor(source, read, block, false); read += 8; }
                    for (int y = 0; y < 4; ++y)
                    {
                        int py = by * 4 + y; if (py >= h) break;
                        for (int x = 0; x < 4; ++x)
                        {
                            int px = bx * 4 + x; if (px >= w) break;
                            Buffer.BlockCopy(block, (y * 4 + x) * 4, output, write + (py * w + px) * 4, 4);
                        }
                    }
                }
            write += w * h * 4;
        }
        return output;
    }

    // BC1 color endpoints are R5G6B5, bit-replicated to 8 bits. BC3 always uses
    // the four-color mode; BC1 with color0 <= color1 has three colors plus
    // transparent black, matching D3D's BC1 (Unity TextureFormat.DXT1) alpha.
    static void DecodeColor(byte[] s, int at, byte[] block, bool bc3)
    {
        int c0 = s[at] | s[at + 1] << 8, c1 = s[at + 2] | s[at + 3] << 8;
        var palette = new int[16];
        Expand(c0, palette, 0); Expand(c1, palette, 4);
        bool four = bc3 || c0 > c1;
        for (int i = 0; i < 3; ++i)
        {
            int a = palette[i], b = palette[4 + i];
            if (four) { palette[8 + i] = (2 * a + b + 1) / 3; palette[12 + i] = (a + 2 * b + 1) / 3; }
            else { palette[8 + i] = (a + b + 1) / 2; palette[12 + i] = 0; }
        }
        palette[3] = palette[7] = palette[11] = 255; palette[15] = four ? 255 : 0;
        uint indices = (uint)(s[at + 4] | s[at + 5] << 8 | s[at + 6] << 16 | s[at + 7] << 24);
        for (int p = 0; p < 16; ++p)
        {
            int index = (int)(indices >> (p * 2)) & 3;
            block[p * 4] = (byte)palette[index * 4]; block[p * 4 + 1] = (byte)palette[index * 4 + 1]; block[p * 4 + 2] = (byte)palette[index * 4 + 2];
            if (!bc3) block[p * 4 + 3] = (byte)palette[index * 4 + 3];
        }
    }

    static void Expand(int c, int[] palette, int at)
    {
        int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31;
        palette[at] = r << 3 | r >> 2; palette[at + 1] = g << 2 | g >> 4; palette[at + 2] = b << 3 | b >> 2;
    }

    // BC3 alpha: two 8-bit endpoints and sixteen 3-bit indices.
    static void DecodeAlpha(byte[] s, int at, byte[] block)
    {
        int a0 = s[at], a1 = s[at + 1];
        var values = new int[8];
        values[0] = a0; values[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; ++i) values[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
        else { for (int i = 1; i < 5; ++i) values[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5; values[6] = 0; values[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 6; ++i) bits |= (ulong)s[at + 2 + i] << (8 * i);
        for (int p = 0; p < 16; ++p) block[p * 4 + 3] = (byte)values[(int)(bits >> (p * 3)) & 7];
    }
}
