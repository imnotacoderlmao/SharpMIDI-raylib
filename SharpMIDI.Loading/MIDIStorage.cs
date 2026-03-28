using System.Runtime.InteropServices;
namespace SharpMIDI
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MIDIEvent
    {
        public uint tick;
        public uint24 message;
    }
    
    public struct Tempo
    {
        public uint tick;
        public uint tempo;
    }
    
    public struct SysEx
    {
        public uint tick;
        public byte[] message;
    }

    public struct TickGroup
    {
        public uint tick;
        public uint count;
    }
    
    static class MIDI
    {
        // is there a better way to do this without bloating the midi class too much
        public static BigArray<uint24> MIDIEvents;
        public static List<TickGroup> tickGroups;
        public static TickGroup[] tickGroupArr = Array.Empty<TickGroup>();
        public static List<Tempo> temppos = new List<Tempo>();
        public static Tempo[] tempoEvents = Array.Empty<Tempo>();
        public static List<SysEx> SysEx = new List<SysEx>();
        public static SysEx[] SysExarr = Array.Empty<SysEx>();
    }
    
    public unsafe class BigArray<T> : IDisposable
    {
        public ulong Length;
        private T* ptr;
        public T* Pointer => ptr;
        
        public BigArray(ulong length)
        {
            Length = length;
            ulong bytes = Length * (ulong)sizeof(T);
            ptr = (T*)NativeMemory.Alloc((nuint)bytes);
        }

        public void Resize(ulong newLength)
        {
            Length = newLength;
            ulong bytes = Length * (ulong)sizeof(T);
            ptr = (T*)NativeMemory.Realloc(ptr, (nuint)bytes);
        }

        public void Dispose()
        {
            if (ptr != null)
            {
                NativeMemory.Free(ptr);
                ptr = null;
            }
        }
    }
    
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct uint24
    {
        private byte b0;
        private byte b1;
        private byte b2;

        public uint24(int value)
        {
            b0 = (byte)(value & 0xFF);
            b1 = (byte)((value >> 8) & 0xFF);
            b2 = (byte)((value >> 16) & 0xFF);
        }

        public int Value
        {
            readonly get => b0 | (b1 << 8) | (b2 << 16);
            set
            {
                b0 = (byte)(value & 0xFF);
                b1 = (byte)((value >> 8) & 0xFF);
                b2 = (byte)((value >> 16) & 0xFF);
            }
        }

        public static implicit operator uint24(int value) => new uint24(value);
        public static implicit operator int(uint24 value) => value.Value;
    }
}