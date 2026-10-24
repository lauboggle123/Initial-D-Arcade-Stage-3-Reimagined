using System;
using System.IO;

// Exercises Idas3DxtDecoder without Unity. Exit code 0 only when every check passes.
static class DxtDecoderDriver
{
    static int failures;
    static void Check(bool condition, string message) { if (!condition) { ++failures; Console.WriteLine("FAIL " + message); } }
    static void Pixel(byte[] rgba, int index, int r, int g, int b, int a, string name)
    {
        int at = index * 4;
        Check(rgba[at] == r && rgba[at + 1] == g && rgba[at + 2] == b && rgba[at + 3] == a,
            string.Format("{0}: pixel {1} = {2},{3},{4},{5}, expected {6},{7},{8},{9}", name, index, rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3], r, g, b, a));
    }

    static int Main(string[] args)
    {
        // BC1 four-color mode: red > blue, row 0 uses indices 0,1,2,3.
        var bc1 = new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0xE4, 0, 0, 0 };
        var rgba = Idas3DxtDecoder.Decode(bc1, 0, 4, 4, 1, false);
        Pixel(rgba, 0, 255, 0, 0, 255, "bc1 four-color");
        Pixel(rgba, 1, 0, 0, 255, 255, "bc1 four-color");
        Pixel(rgba, 2, 170, 0, 85, 255, "bc1 four-color");
        Pixel(rgba, 3, 85, 0, 170, 255, "bc1 four-color");
        Pixel(rgba, 4, 255, 0, 0, 255, "bc1 four-color");

        // BC1 three-color mode (color0 <= color1): midpoint and transparent black.
        var bc1Three = new byte[] { 0x1F, 0x00, 0x00, 0xF8, 0xE4, 0, 0, 0 };
        rgba = Idas3DxtDecoder.Decode(bc1Three, 0, 4, 4, 1, false);
        Pixel(rgba, 2, 128, 0, 128, 255, "bc1 three-color");
        Pixel(rgba, 3, 0, 0, 0, 0, "bc1 three-color");

        // BC3: eight-value alpha (a0 > a1); its color block ignores endpoint order.
        var bc3 = new byte[] { 255, 0, 0x88, 0xC6, 0xFA, 0, 0, 0, 0x1F, 0x00, 0x00, 0xF8, 0xE4, 0, 0, 0 };
        rgba = Idas3DxtDecoder.Decode(bc3, 0, 4, 4, 1, true);
        Pixel(rgba, 0, 0, 0, 255, 255, "bc3 eight-value");
        Pixel(rgba, 1, 255, 0, 0, 0, "bc3 eight-value");
        Pixel(rgba, 2, 85, 0, 170, 219, "bc3 eight-value");
        Pixel(rgba, 3, 170, 0, 85, 182, "bc3 eight-value");
        // Six-value alpha (a0 <= a1) adds explicit 0 and 255.
        var bc3Six = new byte[] { 0, 255, 0xC0, 0x7F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        rgba = Idas3DxtDecoder.Decode(bc3Six, 0, 4, 4, 1, true);
        Pixel(rgba, 0, 0, 0, 0, 0, "bc3 six-value");
        Pixel(rgba, 1, 0, 0, 0, 0, "bc3 six-value");
        Pixel(rgba, 2, 0, 0, 0, 255, "bc3 six-value");
        Pixel(rgba, 3, 0, 0, 0, 255, "bc3 six-value");
        Pixel(rgba, 4, 0, 0, 0, 255, "bc3 six-value");

        // Mip chain: 8x8 + 4x4 + 2x2 BC1 = 4 + 1 + 1 blocks, partial blocks cropped.
        Check(Idas3DxtDecoder.CompressedSize(8, 8, 3, false) == 48, "bc1 8x8 3-mip size");
        Check(Idas3DxtDecoder.CompressedSize(8, 8, 3, true) == 96, "bc3 8x8 3-mip size");
        var chain = new byte[48];
        for (int block = 0; block < 6; ++block) Buffer.BlockCopy(bc1, 0, chain, block * 8, 8);
        chain[40] = 0x1F; chain[41] = 0x00; chain[42] = 0x1F; chain[43] = 0x00; // last level: solid blue
        rgba = Idas3DxtDecoder.Decode(chain, 0, 8, 8, 3, false);
        Check(rgba.Length == (64 + 16 + 4) * 4, "mip chain output size " + rgba.Length);
        Pixel(rgba, 64 + 16, 0, 0, 255, 255, "2x2 level");
        Pixel(rgba, 64 + 16 + 3, 0, 0, 255, 255, "2x2 level");

        bool threw = false;
        try { Idas3DxtDecoder.Decode(new byte[47], 0, 8, 8, 3, false); } catch (ArgumentException) { threw = true; }
        Check(threw, "truncated chain must be rejected");

        // Every DXT texture in the course packs decodes with its own mip chain.
        int packs = 0, decoded = 0, trailing = 0;
        if (args.Length > 0 && Directory.Exists(args[0]))
            foreach (string file in Directory.EnumerateFiles(args[0], "*.dds", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length < 128 || BitConverter.ToUInt32(bytes, 0) != 0x20534444) continue;
                string fourcc = System.Text.Encoding.ASCII.GetString(bytes, 84, 4);
                if (fourcc != "DXT1" && fourcc != "DXT5") continue;
                ++packs;
                int height = BitConverter.ToInt32(bytes, 12), width = BitConverter.ToInt32(bytes, 16), mips = Math.Max(1, BitConverter.ToInt32(bytes, 28));
                bool alphaBlocks = fourcc == "DXT5";
                // The GPU path (LoadRawTextureData) reads the declared chain from the start;
                // some packs store more levels than the header declares.
                int needed = Idas3DxtDecoder.CompressedSize(width, height, mips, alphaBlocks);
                if (bytes.Length - 128 < needed) { Check(false, "truncated chain " + file); continue; }
                if (bytes.Length - 128 > needed) ++trailing;
                var output = Idas3DxtDecoder.Decode(bytes, 128, width, height, mips, alphaBlocks);
                long expected = 0; for (int l = 0, w = width, h = height; l < mips; ++l, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2)) expected += (long)w * h * 4;
                Check(output.Length == expected, "decoded size " + file);
                ++decoded;
            }
        Console.WriteLine("course DXT textures decoded: " + decoded + " of " + packs + " (" + trailing + " with data past the declared mips)");
        Console.WriteLine(failures == 0 ? "PASS" : failures + " failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
