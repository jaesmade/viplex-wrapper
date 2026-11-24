using System.Buffers.Binary;

namespace ViPlex.Core;

public sealed record MediaDetails(MediaKind Kind, int? Width, int? Height, int? DurationMilliseconds);

public static class MediaInspector
{
    public static MediaDetails Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".mp4"))
            throw new InvalidDataException("Choose a JPEG, PNG, or H.264 MP4 file.");

        using var file = File.OpenRead(path);
        try
        {
            return extension switch
            {
                ".jpg" or ".jpeg" => ReadJpeg(file),
                ".png" => ReadPng(file),
                _ => ReadMp4(file)
            };
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("The media file ended before its structure was complete.", error);
        }
    }

    private static MediaDetails ReadJpeg(Stream file)
    {
        if (file.ReadByte() != 0xFF || file.ReadByte() != 0xD8)
            throw new InvalidDataException("The file does not have a JPEG signature.");

        int width = 0;
        int height = 0;
        int frameComponents = 0;
        bool hasScan = false;
        bool insideScan = false;
        Span<byte> header = stackalloc byte[6];

        while (true)
        {
            int marker = ReadJpegMarker(file, insideScan);
            insideScan = false;
            if (marker == 0xD9 && hasScan)
                return new MediaDetails(MediaKind.Image, width, height, null);
            if (marker < 0xC0 || marker is >= 0xD0 and <= 0xD9)
                throw new InvalidDataException("The JPEG contains an unexpected marker.");

            file.ReadExactly(header[..2]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(header[..2]) - 2;
            if (length < 0 || length > file.Length - file.Position)
                throw new InvalidDataException("The JPEG segment length is invalid.");
            long segmentEnd = file.Position + length;

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (frameComponents > 0 || length < header.Length)
                    throw new InvalidDataException("The JPEG frame header is invalid.");
                file.ReadExactly(header);
                height = BinaryPrimitives.ReadUInt16BigEndian(header[1..3]);
                width = BinaryPrimitives.ReadUInt16BigEndian(header[3..5]);
                frameComponents = header[5];
                if (header[0] != 8 || width == 0 || height == 0 ||
                    frameComponents is < 1 or > 4 || length != 6 + 3 * frameComponents)
                    throw new InvalidDataException("The JPEG frame has unsupported dimensions or color information.");
            }
            else if (marker is >= 0xC3 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                throw new InvalidDataException("Choose an 8-bit sequential or progressive JPEG.");
            }
            else if (marker == 0xDA)
            {
                if (length < 1)
                    throw new InvalidDataException("The JPEG scan header is missing.");
                int scanComponents = file.ReadByte();
                if (scanComponents < 1 || scanComponents > frameComponents || length != 4 + 2 * scanComponents)
                    throw new InvalidDataException("The JPEG scan header is invalid.");
                hasScan = true;
                insideScan = true;
            }

            file.Position = segmentEnd;
        }
    }

    private static int ReadJpegMarker(Stream file, bool insideScan)
    {
        bool hasScanData = false;
        while (true)
        {
            int value = file.ReadByte();
            if (value < 0)
                throw new EndOfStreamException();
            if (value != 0xFF)
            {
                if (!insideScan)
                    throw new InvalidDataException("The JPEG segment is missing its marker.");
                hasScanData = true;
                continue;
            }

            do
            {
                value = file.ReadByte();
            } while (value == 0xFF);

            if (value < 0)
                throw new EndOfStreamException();
            if (insideScan && value == 0)
            {
                hasScanData = true;
                continue;
            }
            if (insideScan && value is >= 0xD0 and <= 0xD7)
                continue;
            if (insideScan && !hasScanData)
                throw new InvalidDataException("The JPEG scan contains no image data.");

            return value;
        }
    }

    private static MediaDetails ReadPng(Stream file)
    {
        Span<byte> signature = stackalloc byte[8];
        file.ReadExactly(signature);
        if (BinaryPrimitives.ReadUInt64BigEndian(signature) != 0x89504E470D0A1A0A)
            throw new InvalidDataException("The file does not have a PNG signature.");

        Span<byte> header = stackalloc byte[25];
        file.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 13 || !header[4..8].SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(header[21..25]) != PngCrc(header[4..21]))
            throw new InvalidDataException("The PNG image header is invalid.");

        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[12..16]);
        byte depth = header[16];
        byte colorType = header[17];
        bool validDepth = colorType switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => depth is 8 or 16,
            _ => false
        };
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue ||
            !validDepth || header[18] != 0 || header[19] != 0 || header[20] > 1)
            throw new InvalidDataException("The PNG image header has unsupported dimensions or color information.");

        bool hasPalette = false;
        bool hasDataChunk = false;
        bool hasImageData = false;
        bool pastData = false;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (file.Position < file.Length)
        {
            file.ReadExactly(chunkHeader);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader);
            ReadOnlySpan<byte> type = chunkHeader[4..];
            if (length > int.MaxValue || length + 4L > file.Length - file.Position)
                throw new InvalidDataException("The PNG chunk length is invalid.");

            if (type.SequenceEqual("IDAT"u8))
            {
                if (pastData || (colorType == 3 && !hasPalette))
                    throw new InvalidDataException("The PNG image data is out of order.");
                hasDataChunk = true;
                hasImageData |= length > 0;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                file.ReadExactly(chunkHeader[..4]);
                if (length != 0 || !hasImageData || file.Position != file.Length ||
                    BinaryPrimitives.ReadUInt32BigEndian(chunkHeader) != 0xAE426082)
                    throw new InvalidDataException("The PNG end marker is invalid.");
                return new MediaDetails(MediaKind.Image, (int)width, (int)height, null);
            }
            else
            {
                if (type.SequenceEqual("PLTE"u8))
                {
                    if (hasPalette || hasDataChunk || colorType is 0 or 4 ||
                        length is 0 or > 768 || length % 3 != 0 || (colorType == 3 && length / 3 > 1 << depth))
                        throw new InvalidDataException("The PNG palette header is invalid.");
                    hasPalette = true;
                }
                else if (type[0] is < (byte)'a' or > (byte)'z')
                {
                    throw new InvalidDataException("The PNG contains an unsupported critical chunk.");
                }
                pastData = hasDataChunk;
            }

            file.Position += length + 4L;
        }

        throw new InvalidDataException("The PNG is missing its end marker.");
    }

    private static uint PngCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320U);
        }
        return ~crc;
    }

    private static MediaDetails ReadMp4(Stream file)
    {
        bool hasFileType = false;
        bool hasMediaData = false;
        Mp4Box? movie = null;
        foreach (Mp4Box box in ReadBoxes(file, 0, file.Length))
        {
            if (box.Is("ftyp"u8))
            {
                if (hasFileType || !IsMp4FileType(file, box))
                    throw new InvalidDataException("The file does not identify itself as MP4.");
                hasFileType = true;
            }
            else if (box.Is("moov"u8))
            {
                if (movie.HasValue)
                    throw new InvalidDataException("The MP4 has more than one movie box.");
                movie = box;
            }
            else if (box.Is("mdat"u8))
            {
                hasMediaData |= box.PayloadLength > 0;
            }
        }

        if (!hasFileType || movie is not { } movieBox || !hasMediaData)
            throw new InvalidDataException("The MP4 is missing its file type, movie metadata, or media data.");

        Mp4Box movieHeader = RequireBox(file, movieBox, "mvhd"u8);
        int duration = ReadMp4Duration(file, movieHeader);
        Span<byte> handler = stackalloc byte[12];
        (int Width, int Height)? dimensions = null;
        foreach (Mp4Box track in ReadBoxes(file, movieBox.Payload, movieBox.End))
        {
            if (!track.Is("trak"u8))
                continue;

            Mp4Box media = RequireBox(file, track, "mdia"u8);
            Mp4Box handlerBox = RequireBox(file, media, "hdlr"u8);
            if (handlerBox.PayloadLength < 24)
                throw new InvalidDataException("The MP4 track handler is incomplete.");
            file.Position = handlerBox.Payload;
            file.ReadExactly(handler);
            if (!handler[8..12].SequenceEqual("vide"u8))
                continue;

            Mp4Box mediaInfo = RequireBox(file, media, "minf"u8);
            Mp4Box sampleTable = RequireBox(file, mediaInfo, "stbl"u8);
            Mp4Box sampleDescriptions = RequireBox(file, sampleTable, "stsd"u8);
            var trackDimensions = ReadAvcSampleDescriptions(file, sampleDescriptions);
            dimensions ??= trackDimensions;
        }

        if (dimensions is not { } videoSize)
            throw new InvalidDataException("The MP4 has no H.264 video track.");
        return new MediaDetails(MediaKind.Video, videoSize.Width, videoSize.Height, duration);
    }

    private static bool IsMp4FileType(Stream file, Mp4Box box)
    {
        if (box.PayloadLength is < 8 or > 4096 || box.PayloadLength % 4 != 0)
            return false;

        bool compatible = false;
        Span<byte> brand = stackalloc byte[4];
        file.Position = box.Payload;
        while (file.Position < box.End)
        {
            if (file.Position == box.Payload + 4)
            {
                file.Position += 4; // Minor version.
                continue;
            }
            file.ReadExactly(brand);
            compatible |= brand.SequenceEqual("isom"u8) || brand.SequenceEqual("mp41"u8) ||
                brand.SequenceEqual("mp42"u8) || brand.SequenceEqual("avc1"u8) ||
                (brand[..3].SequenceEqual("iso"u8) && brand[3] is >= (byte)'2' and <= (byte)'9');
        }
        return compatible;
    }

    private static int ReadMp4Duration(Stream file, Mp4Box header)
    {
        if (header.PayloadLength < 100)
            throw new InvalidDataException("The MP4 movie header is incomplete.");

        Span<byte> fields = stackalloc byte[32];
        file.Position = header.Payload;
        file.ReadExactly(fields);
        byte version = fields[0];
        if (version > 1 || (version == 1 && header.PayloadLength < 112))
            throw new InvalidDataException("The MP4 movie header is invalid or unsupported.");

        int scaleOffset = version == 0 ? 12 : 20;
        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(fields[scaleOffset..]);
        ulong ticks = version == 0
            ? BinaryPrimitives.ReadUInt32BigEndian(fields[16..])
            : BinaryPrimitives.ReadUInt64BigEndian(fields[24..]);
        if (timescale == 0 || ticks == 0 || ticks == (version == 0 ? uint.MaxValue : ulong.MaxValue))
            throw new InvalidDataException("The MP4 does not declare a valid playback duration.");

        UInt128 milliseconds = ((UInt128)ticks * 1000 + timescale / 2) / timescale;
        if (milliseconds == 0 || milliseconds > int.MaxValue)
            throw new InvalidDataException("The MP4 playback duration is outside the supported range.");

        return (int)milliseconds;
    }

    private static (int Width, int Height) ReadAvcSampleDescriptions(Stream file, Mp4Box descriptions)
    {
        if (descriptions.PayloadLength < 8)
            throw new InvalidDataException("The MP4 sample description header is incomplete.");

        Span<byte> fields = stackalloc byte[28];
        file.Position = descriptions.Payload;
        file.ReadExactly(fields[..8]);
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(fields[4..]);
        if (BinaryPrimitives.ReadUInt32BigEndian(fields) != 0 ||
            entryCount == 0 || entryCount > (descriptions.PayloadLength - 8) / 8)
            throw new InvalidDataException("The MP4 sample description header is invalid.");

        (int Width, int Height)? dimensions = null;
        uint entriesRead = 0;
        foreach (Mp4Box entry in ReadBoxes(file, descriptions.Payload + 8, descriptions.End))
        {
            if (++entriesRead > entryCount || entry.PayloadLength < 78)
                throw new InvalidDataException("The MP4 video sample description is invalid.");
            if (!entry.Is("avc1"u8) && !entry.Is("avc3"u8))
                throw new InvalidDataException("The MP4 video track must use H.264/AVC.");

            file.Position = entry.Payload;
            file.ReadExactly(fields);
            int width = BinaryPrimitives.ReadUInt16BigEndian(fields[24..]);
            int height = BinaryPrimitives.ReadUInt16BigEndian(fields[26..]);
            if (width == 0 || height == 0)
                throw new InvalidDataException("The MP4 video dimensions are invalid.");

            var extensions = new Mp4Box(entry.Type, entry.Payload + 78, entry.End);
            Mp4Box configuration = RequireBox(file, extensions, "avcC"u8);
            file.Position = configuration.Payload;
            if (configuration.PayloadLength < 7 || file.ReadByte() != 1)
                throw new InvalidDataException("The MP4 H.264 decoder configuration is invalid.");
            dimensions ??= (width, height);
        }

        if (entriesRead != entryCount || dimensions is not { } videoSize)
            throw new InvalidDataException("The MP4 is missing a declared video sample description.");
        return videoSize;
    }

    private static Mp4Box RequireBox(Stream file, Mp4Box parent, ReadOnlySpan<byte> type)
    {
        Mp4Box? match = null;
        foreach (Mp4Box child in ReadBoxes(file, parent.Payload, parent.End))
        {
            if (!child.Is(type))
                continue;
            if (match.HasValue)
                throw new InvalidDataException($"The MP4 repeats its {System.Text.Encoding.ASCII.GetString(type)} box.");
            match = child;
        }

        return match ?? throw new InvalidDataException(
            $"The MP4 is missing its {System.Text.Encoding.ASCII.GetString(type)} box.");
    }

    private static IEnumerable<Mp4Box> ReadBoxes(Stream file, long start, long end)
    {
        while (start < end)
        {
            Mp4Box box = ReadBox(file, start, end);
            yield return box;
            start = box.End;
        }
    }

    private static Mp4Box ReadBox(Stream file, long start, long end)
    {
        long remaining = end - start;
        if (remaining < 8)
            throw new InvalidDataException("The MP4 box header is incomplete.");

        Span<byte> fields = stackalloc byte[8];
        file.Position = start;
        file.ReadExactly(fields);
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(fields);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(fields[4..]);
        int headerLength = 8;
        if (size == 1)
        {
            if (remaining < 16)
                throw new InvalidDataException("The MP4 extended box header is incomplete.");
            file.ReadExactly(fields);
            size = BinaryPrimitives.ReadUInt64BigEndian(fields);
            headerLength = 16;
        }
        else if (size == 0)
        {
            size = (ulong)remaining;
        }

        if (type == 0x75756964) // uuid adds a 16-byte user type.
            headerLength += 16;
        if (size < (ulong)headerLength || size > (ulong)remaining)
            throw new InvalidDataException("The MP4 box length exceeds the enclosing box or file.");

        return new Mp4Box(type, start + headerLength, start + (long)size);
    }

    private readonly record struct Mp4Box(uint Type, long Payload, long End)
    {
        public long PayloadLength => End - Payload;

        public bool Is(ReadOnlySpan<byte> type) => Type == BinaryPrimitives.ReadUInt32BigEndian(type);
    }
}
