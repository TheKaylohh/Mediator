// Offline stand-in for System.IO.Hashing (verify builds and offline ship builds, MediatorUseShims=true): streaming XXH64 with the same members as System.IO.Hashing.XxHash64 that Mediator uses.
using System.Buffers.Binary;
using System.Numerics;

namespace System.IO.Hashing;

public sealed class XxHash64
{
    private const ulong P1 = 0x9E3779B185EBCA87UL, P2 = 0xC2B2AE3D27D4EB4FUL, P3 = 0x165667B19E3779F9UL,
                        P4 = 0x85EBCA77C2B2AE63UL, P5 = 0x27D4EB2F165667C5UL;

    private readonly ulong _seed;
    private ulong _v1, _v2, _v3, _v4;
    private readonly byte[] _buffer = new byte[32];
    private int _buffered;
    private ulong _total;

    public XxHash64() : this(0) { }

    public XxHash64(long seed)
    {
        _seed = unchecked((ulong)seed);
        Reset();
    }

    public void Reset()
    {
        unchecked
        {
            _v1 = _seed + P1 + P2;
            _v2 = _seed + P2;
            _v3 = _seed;
            _v4 = _seed - P1;
        }
        _buffered = 0;
        _total = 0;
    }

    private static ulong Round(ulong acc, ulong input)
    {
        unchecked
        {
            acc += input * P2;
            acc = BitOperations.RotateLeft(acc, 31);
            return acc * P1;
        }
    }

    private static ulong Merge(ulong acc, ulong val)
    {
        unchecked
        {
            acc ^= Round(0, val);
            return acc * P1 + P4;
        }
    }

    private void Stripe(ReadOnlySpan<byte> s)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(s));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(s[8..]));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(s[16..]));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(s[24..]));
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;
        if (_buffered > 0)
        {
            int take = Math.Min(32 - _buffered, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
            if (_buffered < 32) return;
            Stripe(_buffer);
            _buffered = 0;
        }
        while (data.Length >= 32)
        {
            Stripe(data);
            data = data[32..];
        }
        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    public ulong GetCurrentHashAsUInt64()
    {
        unchecked
        {
            ulong h;
            if (_total >= 32)
            {
                h = BitOperations.RotateLeft(_v1, 1) + BitOperations.RotateLeft(_v2, 7)
                  + BitOperations.RotateLeft(_v3, 12) + BitOperations.RotateLeft(_v4, 18);
                h = Merge(h, _v1); h = Merge(h, _v2); h = Merge(h, _v3); h = Merge(h, _v4);
            }
            else
            {
                h = _seed + P5;
            }
            h += _total;

            ReadOnlySpan<byte> rem = _buffer.AsSpan(0, _buffered);
            while (rem.Length >= 8)
            {
                h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(rem));
                h = BitOperations.RotateLeft(h, 27) * P1 + P4;
                rem = rem[8..];
            }
            if (rem.Length >= 4)
            {
                h ^= BinaryPrimitives.ReadUInt32LittleEndian(rem) * P1;
                h = BitOperations.RotateLeft(h, 23) * P2 + P3;
                rem = rem[4..];
            }
            foreach (byte b in rem)
            {
                h ^= b * P5;
                h = BitOperations.RotateLeft(h, 11) * P1;
            }
            h ^= h >> 33; h *= P2;
            h ^= h >> 29; h *= P3;
            h ^= h >> 32;
            return h;
        }
    }

    public static ulong HashToUInt64(ReadOnlySpan<byte> source, long seed = 0)
    {
        var x = new XxHash64(seed);
        x.Append(source);
        return x.GetCurrentHashAsUInt64();
    }
}
