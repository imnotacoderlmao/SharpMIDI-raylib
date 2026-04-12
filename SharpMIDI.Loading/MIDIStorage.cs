using System.Runtime.InteropServices;
namespace SharpMIDI
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MIDIEvent
    {
        public uint24 message;
        public ushort track;
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
    
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TickGroup
    {
        public uint tick;
        public uint count;
        public long offset;
    }
    
    static class MIDI
    {
        // is there a better way to do this without bloating the midi class too much
        public static BigArray<MIDIEvent> MIDIEventArray;
        public static TickGroup[] TickGroupArray = Array.Empty<TickGroup>();
        public static Tempo[] TempoEventArray = Array.Empty<Tempo>();
        public static SysEx[] SysExArray = Array.Empty<SysEx>();
    }

    static class tempMIDIstorage
    {
        public static List<Tempo> temppos = new List<Tempo>();
        public static List<SysEx> SysEx = new List<SysEx>();
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
        private ushort val1;
        private byte val2;

        public uint24(int value)
        {
            val1 = (ushort)(value & 0xFFFF);
            val2 = (byte)((value >> 16) & 0xFF);
        }

        public int Value
        {
            readonly get => val1 | (val2 << 16);
            set
            {
                val1 = (ushort)(value & 0xFFFF);
                val2 = (byte)((value >> 16) & 0xFF);
            }
        }

        public static implicit operator uint24(int value) => new uint24(value);
    }
}
