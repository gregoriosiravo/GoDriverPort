using System.Buffers.Binary;

namespace LegionGoConverter.Core;

/// <summary>MetroHash64 variant used by AMD's amdgcf.dat header (ported from tools/spike/AmdGcf.ps1).</summary>
public static class MetroHash64
{
    const ulong K0 = 0xD6D018F5UL, K1 = 0xA2AA033BUL, K2 = 0x62992FC1UL, K3 = 0x30BC5B29UL;

    static ulong R(ulong v, int b) => (v >> b) | (v << (64 - b));
    static ulong U64(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d[o..]);
    static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
    static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    public static ulong Hash(ReadOnlySpan<byte> d, ulong seed)
    {
        unchecked
        {
            int len = d.Length, o = 0;
            ulong h = (seed + K2) * K0;
            if (len >= 32)
            {
                ulong v0 = h, v1 = h, v2 = h, v3 = h;
                int end = len - 32;
                while (o <= end)
                {
                    v0 += U64(d, o) * K0; o += 8; v0 = R(v0, 29) + v2;
                    v1 += U64(d, o) * K1; o += 8; v1 = R(v1, 29) + v3;
                    v2 += U64(d, o) * K2; o += 8; v2 = R(v2, 29) + v0;
                    v3 += U64(d, o) * K3; o += 8; v3 = R(v3, 29) + v1;
                }
                v2 ^= R(((v0 + v3) * K0) + v1, 37) * K1;
                v3 ^= R(((v1 + v2) * K1) + v0, 37) * K0;
                v0 ^= R(((v0 + v2) * K0) + v3, 37) * K1;
                v1 ^= R(((v1 + v3) * K1) + v2, 37) * K0;
                h += v0 ^ v1;
            }
            int rem = len - o;
            if (rem >= 16)
            {
                ulong v0 = h + (U64(d, o) * K2); o += 8; v0 = R(v0, 29) * K3;
                ulong v1 = h + (U64(d, o) * K2); o += 8; v1 = R(v1, 29) * K3;
                v0 ^= R(v0 * K0, 21) + v1;
                v1 ^= R(v1 * K3, 21) + v0;
                h += v1;
                rem = len - o;
            }
            if (rem >= 8) { h += U64(d, o) * K3; o += 8; h ^= R(h, 55) * K1; rem = len - o; }
            if (rem >= 4) { h += (ulong)U32(d, o) * K3; o += 4; h ^= R(h, 26) * K1; rem = len - o; }
            if (rem >= 2) { h += (ulong)U16(d, o) * K3; o += 2; h ^= R(h, 48) * K1; rem = len - o; }
            if (rem >= 1) { h += (ulong)d[o] * K3; h ^= R(h, 37) * K1; }
            h ^= R(h, 28); h *= K0; h ^= R(h, 29);
            return h;
        }
    }
}
