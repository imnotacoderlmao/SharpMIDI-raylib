using System.Runtime.InteropServices;
namespace SharpMIDI
{
    public struct SynthEvent
    {
        public uint tick;
        public uint message;
    }
    public struct Tempo
    {
        public uint tick;
        public uint tempo;
    }
    
    static class MIDI
    {
        public static BigArray<SynthEvent> synthEvents;
        public static List<Tempo> temppos = new List<Tempo>();
        public static Tempo[] tempoEvents = Array.Empty<Tempo>();
    }
    public unsafe class BigArray<T> : IDisposable
    {
        public ulong Length;
        private T* ptr;
        public T* Pointer => ptr;
        
        public BigArray(ulong length)
        {
            Length = length;
            ulong bytes = length * (uint)sizeof(T);
            ptr = (T*)NativeMemory.Alloc((nuint)bytes);
        }

        public void Resize(ulong newLength)
        {
            Length = newLength;
            ulong bytes = Length * (uint)sizeof(T);
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
}