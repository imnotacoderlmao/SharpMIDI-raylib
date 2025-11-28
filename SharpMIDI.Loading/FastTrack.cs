#pragma warning disable 8625
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
namespace SharpMIDI
{
    // genuinely do not know why its taking up more than 8 bytes/note
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SynthEvent
    {
        public int pos;
        public int val;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Tempo
    {
        public int pos;
        public int tempo;
    }
    class MIDI
    {
        public static SynthEvent[] synthEvents = new SynthEvent[1024];
        public static List<Tempo> tempos = new List<Tempo>();
    }

    public unsafe class FastTrack : IDisposable
    {
        public long eventAmount = 0;
        public long loadedNotes = 0;
        public long totalNotes = 0;
        public List<SynthEvent> localEvents = new List<SynthEvent>(4096);
        public List<int[]> skippedNotes = new List<int[]>();
        public int trackTime = 0;
        BufferByteReader stupid;
        public FastTrack(BufferByteReader reader)
        {
            stupid = reader;
        }
        byte prevEvent = 0;
        public void ParseTrackEvents(byte thres)
        {
            int localtracktime = 0;
            for (int i = 0; i < 16; i++)
            {
                skippedNotes.Add(new int[256]);
            }
            while (true)
            {
                try
                {
                    //this is huge zenith inspiration lol, if you can't beat 'em, join 'em
                    int test = ReadVariableLen();
                    localtracktime += test;
                    byte readEvent = stupid.ReadFast();
                    if (readEvent < 0x80)
                    {
                        stupid.Pushback = readEvent;
                        readEvent = prevEvent;
                    }
                    if (localtracktime > MIDILoader.maxTick)
                    {
                        MIDILoader.maxTick = localtracktime;
                    }
                    prevEvent = readEvent;
                    switch ((byte)(readEvent & 0b11110000))
                    {
                        case 0b10010000:
                            {
                                byte ch = (byte)(readEvent & 0b00001111);
                                byte note = stupid.Read();
                                byte vel = stupid.ReadFast();
                                if (vel != 0)
                                {
                                    totalNotes++;
                                    if (vel >= thres)
                                    {
                                        loadedNotes++;
                                        eventAmount++;
                                        localEvents.Add(new SynthEvent(){
                                            pos = localtracktime,
                                            val = readEvent | (note << 8) | (vel << 16)
                                        });
                                    }
                                    else
                                    {
                                        skippedNotes[ch][note]++;
                                    }
                                }
                                else
                                {
                                    if (skippedNotes[ch][note] == 0)
                                    {
                                        byte customEvent = (byte)(readEvent - 0b00010000);
                                        eventAmount++;
                                        localEvents.Add(new SynthEvent(){
                                            pos = localtracktime,
                                            val = customEvent | (note << 8) | (vel << 16)
                                        });
                                    }
                                    else
                                    {
                                        skippedNotes[ch][note]--;
                                    }
                                }
                            }
                            break;
                        case 0b10000000:
                            {
                                int ch = readEvent & 0b00001111;
                                byte note = stupid.Read();
                                byte vel = stupid.ReadFast();
                                if (skippedNotes[ch][note] == 0)
                                {
                                    eventAmount++;
                                    localEvents.Add(new SynthEvent(){
                                        pos = localtracktime,
                                        val = readEvent | (note << 8) | (vel << 16)
                                    });
                                }
                                else
                                {
                                    skippedNotes[ch][note]--;
                                }
                            }
                            break;
                        case 0b10100000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte note = stupid.Read();
                                byte vel = stupid.Read();
                                eventAmount++;
                                localEvents.Add(new SynthEvent(){
                                    pos = localtracktime,
                                    val = readEvent | (note << 8) | (vel << 16)
                                });
                            }
                            break;
                        case 0b11000000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte program = stupid.Read();
                                eventAmount++;
                                localEvents.Add(new SynthEvent(){
                                    pos = localtracktime,
                                    val = readEvent | (program << 8)
                                });
                            }
                            break;
                        case 0b11010000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte pressure = stupid.Read();
                                eventAmount++;
                                localEvents.Add(new SynthEvent(){
                                    pos = localtracktime,
                                    val = readEvent | (pressure << 8)
                                });
                            }
                            break;
                        case 0b11100000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte l = stupid.Read();
                                byte m = stupid.Read();
                                eventAmount++;
                                localEvents.Add(new SynthEvent(){
                                    pos = localtracktime,
                                    val = readEvent | (l << 8) | (m << 16)
                                });
                            }
                            break;
                        case 0b10110000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte cc = stupid.Read();
                                byte vv = stupid.Read();
                                eventAmount++;
                                localEvents.Add(new SynthEvent(){
                                    pos = localtracktime,
                                    val = readEvent | (cc << 8) | (vv << 16)
                                });
                            }
                            break;
                        default:
                            switch (readEvent)
                            {
                                case 0b11110000:
                                    while (stupid.Read() != 0b11110111);
                                    break;
                                case 0b11110010:
                                    stupid.Skip(2);
                                    break;
                                case 0b11110011:
                                    stupid.Skip(1);
                                    break;
                                case 0xFF:
                                    {
                                        readEvent = stupid.Read();
                                        if (readEvent == 0x51)
                                        {
                                            stupid.Skip(1);
                                            int tempo = 0;
                                            for (int i = 0; i != 3; i++)
                                                tempo = (tempo << 8) | stupid.Read();
                                            MIDI.tempos.Add(new Tempo()
                                            { 
                                                pos = localtracktime,
                                                tempo = tempo
                                            });
                                        }
                                        else if (readEvent == 0x2F)
                                        {
                                            break;
                                        }
                                        else
                                        {
                                            stupid.Skip(stupid.Read());
                                        }
                                    }
                                    break;
                                default:
                                    break;
                            }
                            break;
                    }
                }
                catch (IndexOutOfRangeException)
                {
                    break;
                }
            }
            localEvents.TrimExcess();
            //MIDI.AddEvents(events, eventCount);
        }
        
        int ReadVariableLen()
        {
            byte c;
            int val = 0;
            for (int i = 0; i < 4; i++)
            {
                c = stupid.ReadFast();
                if (c > 0x7F)
                {
                    val = (val << 7) | (c & 0x7F);
                }
                else
                {
                    val = val << 7 | c;
                    return val;
                }
            }
            return val;
        }
        public void Dispose()
        {
            stupid.Dispose();
            stupid = null;
        }
    }
}