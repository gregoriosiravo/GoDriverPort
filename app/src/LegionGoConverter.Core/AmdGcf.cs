using System.Buffers.Binary;
using System.Text;

namespace LegionGoConverter.Core;

public sealed record GcfRecord(int Device, int Revision);

public sealed class AmdGcfFormatException : Exception
{
    public AmdGcfFormatException(string message) : base(message) { }
}

/// <summary>
/// Reader/writer for AMD's amdgcf.dat: uint32 LE count, 8-byte big-endian MetroHash64 header
/// over (record bytes + ASCII release string), then 3-byte records (uint16 LE Device+position, byte revision).
/// </summary>
public static class AmdGcf
{
    const int HeaderSize = 12;
    const int RecordSize = 3;

    public static IReadOnlyList<GcfRecord> Read(byte[] dat, string release)
    {
        if (dat.Length < HeaderSize)
            throw new AmdGcfFormatException($"amdgcf.dat is too short ({dat.Length} bytes).");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(dat);
        if ((long)dat.Length != HeaderSize + (long)RecordSize * count)
            throw new AmdGcfFormatException($"amdgcf.dat length {dat.Length} does not match {count} records.");
        if (!ComputeHeader(dat, release).AsSpan().SequenceEqual(dat.AsSpan(4, 8)))
            throw new AmdGcfFormatException("amdgcf.dat header self-test failed: format or release string not understood.");
        var list = new List<GcfRecord>((int)count);
        for (int i = 0; i < count; i++)
        {
            int p = HeaderSize + RecordSize * i;
            int dev = (BinaryPrimitives.ReadUInt16LittleEndian(dat.AsSpan(p)) - p) & 0xFFFF;
            list.Add(new GcfRecord(dev, dat[p + 2]));
        }
        return list;
    }

    public static byte[] Write(IEnumerable<GcfRecord> records, string release)
    {
        var sorted = records.OrderBy(r => r.Device).ThenBy(r => r.Revision).ToList();
        var output = new byte[HeaderSize + RecordSize * sorted.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            int p = HeaderSize + RecordSize * i;
            var r = sorted[i];
            if (r.Device < 0 || r.Device > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(records), "Device out of range.");
            if (r.Revision < 0 || r.Revision > 0xFF) throw new ArgumentOutOfRangeException(nameof(records), "Revision out of range.");
            int stored = r.Device + p;
            if (stored > 0xFFFF) throw new InvalidOperationException("Stored offset overflow.");
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(p), (ushort)stored);
            output[p + 2] = (byte)r.Revision;
        }
        ComputeHeader(output, release).CopyTo(output, 4);
        return output;
    }

    public static byte[] AddRecords(byte[] dat, string release, IEnumerable<GcfRecord> add)
    {
        var existing = Read(dat, release);
        var set = new HashSet<GcfRecord>(existing);
        bool changed = false;
        foreach (var r in add) changed |= set.Add(r);
        return changed ? Write(set, release) : dat;
    }

    static byte[] ComputeHeader(byte[] dat, string release)
    {
        var rel = Encoding.ASCII.GetBytes(release);
        var input = new byte[dat.Length - HeaderSize + rel.Length];
        Buffer.BlockCopy(dat, HeaderSize, input, 0, dat.Length - HeaderSize);
        Buffer.BlockCopy(rel, 0, input, dat.Length - HeaderSize, rel.Length);
        var be = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(be, MetroHash64.Hash(input, 0));
        return be;
    }
}
