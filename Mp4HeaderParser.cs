using System.Buffers.Binary;
using System.Text;

namespace Mp4ls;

public class Mp4HeaderParser
{
    private static readonly string[] ContainerBoxes = ["moov", "trak", "mdia", "minf", "stbl", "stsd", "sinf"];

    public static void Parse(string filePath, bool isVerbose, byte[] peekBuffer)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 786432, FileOptions.RandomAccess);
        if (fs.Length < 8) return;

        fs.ReadExactly(peekBuffer, 0, 8);

        // MKV Check
        if (peekBuffer[0] == 0x1A && peekBuffer[1] == 0x45 && peekBuffer[2] == 0xDF && peekBuffer[3] == 0xA3)
        {
            Console.WriteLine($"\n--- {Path.GetFileName(filePath)} ---");
            Console.WriteLine($"  {"Format:",-21} Matroska / WebM (MKV)");
            return;
        }

        // M2TS Check (Sync byte 0x47)
        if (peekBuffer[0] == 0x47 || (peekBuffer[0] == 0x00 && peekBuffer[4] == 0x47))
        {
            Console.WriteLine($"\n--- {Path.GetFileName(filePath)} ---");
            Console.WriteLine($"  {"Format:",-21} Blu-ray Video (M2TS)");
            return;
        }

        string firstBoxType = Encoding.ASCII.GetString(peekBuffer, 4, 4);
        string[] validStarts = ["ftyp", "moov", "mdat", "free", "skip", "wide"];
        if (!validStarts.Contains(firstBoxType)) return;

        var context = new ParseContext { TotalFileSize = fs.Length };
        Console.WriteLine($"\n--- {Path.GetFileName(filePath)} ---");

        if (firstBoxType == "ftyp")
        {
            byte[] brandBuf = new byte[4];
            fs.Seek(8, SeekOrigin.Begin);
            fs.ReadExactly(brandBuf, 0, 4);
            context.MajorBrand = Encoding.ASCII.GetString(brandBuf);
            context.BrandExplanation = context.MajorBrand == "iso6" ? " (MP4 Base Media version 6)" : "";
        }

        long moovOffset = FindMoovOffset(fs);
        if (moovOffset != -1)
        {
            fs.Seek(moovOffset, SeekOrigin.Begin);
            ParseBoxes(fs, fs.Length, 0, isVerbose, context);
            PrintConsolidatedSummary(context);
        }
    }

    private static void PrintConsolidatedSummary(ParseContext ctx)
    {
        Console.WriteLine($"  {"Format:",-21} {ctx.MajorBrand}{ctx.BrandExplanation}");
        if (ctx.DurationSeconds > 0)
        {
            TimeSpan t = TimeSpan.FromSeconds(ctx.DurationSeconds);
            Console.WriteLine($"  {"Duration:",-21} {(int)t.TotalMinutes} min {t.Seconds} sec");
            Console.WriteLine($"  {"Bitrate:",-21} {ctx.BitrateMbps:F2} Mbps");
        }
        Console.WriteLine("  Tracks:");
        foreach (var track in ctx.Tracks)
        {
            Console.ForegroundColor = track.HandlerCode == "vide" ? ConsoleColor.Cyan : ConsoleColor.Green;

            // Only add the colon if we actually have data (codec or res) to display
            bool hasData = !string.IsNullOrEmpty(track.Codec) || !string.IsNullOrEmpty(track.Resolution);
            string typeLabel = track.Type + (hasData ? ":" : "");

            string resPart = (track.HandlerCode == "vide" && !string.IsNullOrEmpty(track.Resolution)) ? $" [{track.Resolution}]" : "";
            string codecPart = track.Codec;
            string drm = track.IsDrm ? " [DRM-protected]" : "";

            Console.WriteLine($"    -> {typeLabel,-16} {codecPart}{resPart}{drm}");
            Console.ResetColor();
        }
    }

    private static long FindMoovOffset(FileStream fs)
    {
        fs.Seek(0, SeekOrigin.Begin);
        byte[] header = new byte[8];
        while (fs.Position + 8 <= fs.Length)
        {
            fs.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);
            if (type == "moov") return fs.Position - 8;
            if (size == 1) { byte[] ext = new byte[8]; fs.ReadExactly(ext, 0, 8); size = BinaryPrimitives.ReadInt64BigEndian(ext); fs.Seek(size - 16, SeekOrigin.Current); }
            else if (size == 0) break;
            else fs.Seek(size - 8, SeekOrigin.Current);
        }
        return -1;
    }

    private static void ParseBoxes(FileStream fs, long endPosition, int depth, bool isVerbose, ParseContext ctx)
    {
        byte[] header = new byte[8];
        string indent = new(' ', depth * 2);

        while (fs.Position + 8 <= endPosition)
        {
            fs.ReadExactly(header, 0, 8);
            long boxSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string boxType = Encoding.ASCII.GetString(header, 4, 4);
            long dataSize = boxSize - 8;

            if (boxSize == 1) { byte[] ext = new byte[8]; fs.ReadExactly(ext, 0, 8); boxSize = BinaryPrimitives.ReadInt64BigEndian(ext); dataSize = boxSize - 16; }
            if (boxType == "moof" || boxType == "mdat") { fs.Seek(dataSize, SeekOrigin.Current); continue; }

            if (isVerbose)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {indent}[{boxType}] Size: {boxSize:N0}");
                Console.ResetColor();
            }

            if (boxType == "trak") { ctx.CurrentTrack = new TrackInfo(); ctx.Tracks.Add(ctx.CurrentTrack); }

            if (boxType == "mvhd") ParseMvhd(fs, dataSize, ctx);
            else if (boxType is "encv" or "avc1" or "hev1" or "hvc1" or "dvh1" or "enca" or "mp4a")
            {
                if (ctx.CurrentTrack != null) { ctx.CurrentTrack.Codec = boxType; ctx.CurrentTrack.IsDrm = boxType is "encv" or "hev1" or "enca"; }
                fs.Seek(dataSize, SeekOrigin.Current);
            }
            else if (Array.Exists(ContainerBoxes, c => c == boxType))
            {
                long skip = (boxType == "stsd") ? 8 : 0;
                if (skip > 0) fs.Seek(skip, SeekOrigin.Current);
                ParseBoxes(fs, fs.Position + dataSize - skip, depth + 1, isVerbose, ctx);
            }
            else if (boxType == "tkhd") ParseTrackHeader(fs, (int)dataSize, ctx);
            else if (boxType == "hdlr") ParseHandler(fs, (int)dataSize, ctx);
            else fs.Seek(dataSize, SeekOrigin.Current);
        }
    }

    private static void ParseMvhd(FileStream fs, long dataSize, ParseContext ctx)
    {
        byte[] buf = new byte[32];
        fs.ReadExactly(buf, 0, 32);
        byte v = buf[0];
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(v == 1 ? 20 : 12, 4));
        ulong dur = v == 1 ? BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(28, 8)) : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16, 4));
        if (ts > 0) ctx.DurationSeconds = (double)dur / ts;
        if (ctx.DurationSeconds > 0) ctx.BitrateMbps = (ctx.TotalFileSize * 8.0) / ctx.DurationSeconds / 1_000_000;
        fs.Seek(dataSize - 32, SeekOrigin.Current);
    }

    private static void ParseTrackHeader(FileStream fs, int dataSize, ParseContext ctx)
    {
        byte[] b = new byte[dataSize]; fs.ReadExactly(b, 0, dataSize);
        int off = 4 + (b[0] == 1 ? 32 : 20) + 16 + 36;
        if (off + 8 <= dataSize && ctx.CurrentTrack != null)
        {
            uint w = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off, 4));
            uint h = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 4, 4));

            int width = (int)(w >> 16);
            int height = (int)(h >> 16);
            if (width > 0 && height > 0)
            {
                ctx.CurrentTrack.Resolution = $"{width}x{height}";
            }
        }
    }

    private static void ParseHandler(FileStream fs, int dataSize, ParseContext ctx)
    {
        byte[] b = new byte[dataSize]; fs.ReadExactly(b, 0, dataSize);
        if (dataSize >= 12 && ctx.CurrentTrack != null)
        {
            string h = Encoding.ASCII.GetString(b, 8, 4);
            ctx.CurrentTrack.HandlerCode = h;
            ctx.CurrentTrack.Type = h == "vide" ? "Video Track" :
                                    h == "soun" ? "Audio Track" :
                                    (h == "text" || h == "subt" || h == "sbtl") ? "Subtitle Track" :
                                    $"Track ({h})";
        }
    }
}