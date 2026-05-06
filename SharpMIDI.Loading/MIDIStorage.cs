using System.Runtime.InteropServices;
namespace SharpMIDI
{
    public static class SynthEvent
    {
        public static BigArray<uint24> messages;
        public static BigArray<ushort> track;

        public static void Alloc(long length)
        {
            messages = new BigArray<uint24>(length);
            track = new BigArray<ushort>(length);
        }

        public static void Dispose()
        {
            messages?.Dispose(); 
            messages = null;
            track?.Dispose();   
            track = null;
        }
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
        public uint notecount;
        public long offset;
    }
    
    static class MIDIEvent
    {
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
        public long Length;
        private T* ptr;
        public T* Pointer => ptr;
        
        public BigArray(long length)
        {
            Length = length;
            long bytes = Length * sizeof(T);
            ptr = (T*)NativeMemory.Alloc((nuint)bytes);
        }

        public void Resize(long newLength)
        {
            Length = newLength;
            long bytes = Length * sizeof(T);
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
