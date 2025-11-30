using System.Runtime.InteropServices;
namespace SharpMIDI
{
    // genuinely do not know why its taking up more than 8 bytes/note
    /*[StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SynthEvent
    {
        public int pos;
        public int val;
    }*/
    [StructLayout(LayoutKind.Sequential)]
    public struct Tempo
    {
        public int pos;
        public int tempo;
    }
    static class MIDI
    {
        //public static SynthEvent[] synthEvents = Array.Empty<SynthEvent>();
        public static long[] synthEvents = Array.Empty<long>();
        public static List<long> tempos = new List<long>();
    }
}