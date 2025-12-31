using System.Runtime.InteropServices;
namespace SharpMIDI
{
    static class MIDI
    {
        public static BigArray<long> synthEvents;
        public static List<long> temppos = new List<long>();
        public static long[] tempoEvents = Array.Empty<long>();
    }
    public unsafe class BigArray<T> : IDisposable
    {
        public readonly ulong Length;
        private long* ptr;

        public BigArray(ulong length)
        {
            Length = length;
            ulong bytes = length * (uint)sizeof(T);
            ptr = (long*)NativeMemory.Alloc((nuint)bytes);
        }

        public long this[ulong index]
        {
            get => ptr[index];
            set => ptr[index] = value;
        }

        public long* Pointer => ptr;

        public void Dispose()
        {
            if (ptr != null)
            {
                NativeMemory.Free(ptr);
                ptr = null;
            }
        }
    }
}