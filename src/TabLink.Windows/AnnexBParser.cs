namespace TabLink.Windows;

internal sealed record AnnexBAccessUnit(byte[] Data, byte[] Sps, byte[] Pps, bool IsKeyFrame, bool ConfigurationChanged);

// FFmpeg is required to emit an Access Unit Delimiter for each picture. Parsing
// delimiters avoids treating an arbitrary stdout read (or individual slice) as
// a complete Android MediaCodec input frame.
internal sealed class AnnexBParser
{
    internal const int MaxAccessUnitBytes = 8 * 1024 * 1024 - 8;
    byte[] buffer = new byte[128 * 1024];
    int count, scan;
    bool sawAud, completed;
    byte[]? sps, pps;

    internal IReadOnlyList<AnnexBAccessUnit> Append(ReadOnlySpan<byte> bytes)
    {
        if (completed) throw new InvalidOperationException("H.264 parser has already completed");
        if (bytes.Length > MaxAccessUnitBytes || count > MaxAccessUnitBytes + 65536 - bytes.Length)
            throw new InvalidDataException("H.264 stream exceeded the bounded access unit buffer");
        if (buffer.Length < count + bytes.Length)
            Array.Resize(ref buffer, Math.Min(MaxAccessUnitBytes + 65536, Math.Max(buffer.Length * 2, count + bytes.Length)));
        bytes.CopyTo(buffer.AsSpan(count));
        count += bytes.Length;
        var output = new List<AnnexBAccessUnit>();
        while (TryFindStartCode(buffer.AsSpan(0, count), scan, out var position, out var prefix))
        {
            if (position + prefix >= count) { scan = position; return output; }
            var type = buffer[position + prefix] & 0x1f;
            if (type == 9)
            {
                if (sawAud)
                {
                    if (position == 0) throw new InvalidDataException("Invalid H.264 delimiter position");
                    var unit = ParseUnit(buffer.AsSpan(0, position));
                    if (unit is not null) output.Add(unit);
                    Buffer.BlockCopy(buffer, position, buffer, 0, count - position);
                    count -= position;
                    position = 0;
                }
                sawAud = true;
            }
            scan = position + prefix + 1;
        }
        scan = Math.Max(scan, count - 3);
        if (count > MaxAccessUnitBytes)
            throw new InvalidDataException("H.264 access unit is too large or missing delimiters");
        return output;
    }

    internal AnnexBAccessUnit? Complete()
    {
        if (completed) return null;
        completed = true;
        if (count == 0) return null;
        if (!sawAud) throw new InvalidDataException("H.264 encoder did not emit access unit delimiters");
        var result = ParseUnit(buffer.AsSpan(0, count));
        count = scan = 0;
        return result;
    }

    AnnexBAccessUnit? ParseUnit(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxAccessUnitBytes) throw new InvalidDataException("H.264 access unit exceeded the packet limit");
        var hasPicture = false;
        var keyFrame = false;
        var configurationChanged = false;
        if (!TryFindStartCode(bytes, 0, out var start, out var prefix))
            throw new InvalidDataException("H.264 access unit has no Annex B start code");
        for (var i = 0; i < start; i++)
            if (bytes[i] != 0) throw new InvalidDataException("Unexpected bytes before H.264 start code");
        while (true)
        {
            var payload = start + prefix;
            if (payload >= bytes.Length) throw new InvalidDataException("Truncated H.264 NAL header");
            if ((bytes[payload] & 0x80) != 0) throw new InvalidDataException("Invalid H.264 forbidden bit");
            var foundNext = TryFindStartCode(bytes, payload + 1, out var next, out var nextPrefix);
            var end = foundNext ? next : bytes.Length;
            var type = bytes[payload] & 0x1f;
            if (type is 7 or 8)
            {
                var nal = new byte[4 + end - payload];
                nal[3] = 1;
                bytes[payload..end].CopyTo(nal.AsSpan(4));
                var previous = type == 7 ? sps : pps;
                configurationChanged |= previous is null || !previous.AsSpan().SequenceEqual(nal);
                if (type == 7) sps = nal; else pps = nal;
            }
            hasPicture |= type is >= 1 and <= 5;
            keyFrame |= type == 5;
            if (!foundNext) break;
            start = next; prefix = nextPrefix;
        }
        if (!hasPicture) return null;
        if (sps is null || pps is null) throw new InvalidDataException("H.264 picture arrived before SPS/PPS configuration");
        return new(bytes.ToArray(), sps, pps, keyFrame, configurationChanged);
    }

    static bool TryFindStartCode(ReadOnlySpan<byte> bytes, int from, out int position, out int prefix)
    {
        for (var i = Math.Max(0, from); i + 2 < bytes.Length; i++)
        {
            if (bytes[i] != 0 || bytes[i + 1] != 0) continue;
            if (bytes[i + 2] == 1) { position = i; prefix = 3; return true; }
            if (i + 3 < bytes.Length && bytes[i + 2] == 0 && bytes[i + 3] == 1)
            { position = i; prefix = 4; return true; }
        }
        position = prefix = 0;
        return false;
    }
}
