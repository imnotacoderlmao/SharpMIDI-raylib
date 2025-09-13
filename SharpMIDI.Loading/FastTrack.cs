#pragma warning disable 8625

namespace SharpMIDI
{
    public struct SynthEvent
    {
        public int pos;
        public int val;
    }
    public struct Tempo
    {
        public int pos;
        public int tempo;
    }

    public class MIDITrack
    {
        public List<SynthEvent> synthEvents = new List<SynthEvent>();
        public static List<Tempo> tempos = new List<Tempo>();
        public long eventAmount = 0;
        public long tempoAmount = 0;
        public long loadedNotes = 0;
        public long totalNotes = 0;
        public int maxTick = 0;
        public static bool finished = false;
    }

    public unsafe class FastTrack : IDisposable
    {
        public MIDITrack track = new MIDITrack();
        public List<int[]> skippedNotes = new List<int[]>();
        public long trackTime = 0;
        BufferByteReader stupid;
        public FastTrack(BufferByteReader reader)
        {
            stupid = reader;
        }
        byte prevEvent = 0;
        public void ParseTrackEvents(byte thres)
        {
            for (int i = 0; i < 16; i++)
            {
                skippedNotes.Add(new int[256]);
            }
            int trackTime = 0;
            while (true)
            {
                try
                {
                    //this is huge zenith inspiration lol, if you can't beat 'em, join 'em
                    int test = ReadVariableLen();
                    trackTime += test;
                    byte readEvent = stupid.ReadFast();
                    if (readEvent < 0x80)
                    {
                        stupid.Pushback = readEvent;
                        readEvent = prevEvent;
                    }
                    prevEvent = readEvent;
                    byte trackEvent = (byte)(readEvent & 0b11110000);
                    switch (trackEvent)
                    {
                        case 0b10010000:
                            {
                                byte ch = (byte)(readEvent & 0b00001111);
                                byte note = stupid.Read();
                                byte vel = stupid.ReadFast();
                                if (vel != 0)
                                {
                                    track.totalNotes++;
                                    if (vel >= thres)
                                    {
                                        track.loadedNotes++;
                                        track.eventAmount++;
                                        track.synthEvents.Add(new SynthEvent()
                                        {
                                            pos = trackTime,
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
                                        track.eventAmount++;
                                        track.synthEvents.Add(new SynthEvent()
                                        {
                                            pos = trackTime,
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
                                    track.eventAmount++;
                                    track.synthEvents.Add(new SynthEvent()
                                    {
                                        pos = trackTime,
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
                                track.eventAmount++;
                                track.synthEvents.Add(new SynthEvent()
                                {
                                    pos = trackTime,
                                    val = readEvent | (note << 8) | (vel << 16)
                                });
                            }
                            break;
                        case 0b11000000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte program = stupid.Read();
                                track.eventAmount++;
                                track.synthEvents.Add(new SynthEvent()
                                {
                                    pos = trackTime,
                                    val = readEvent | (program << 8)
                                });
                            }
                            break;
                        case 0b11010000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte pressure = stupid.Read();
                                track.eventAmount++;
                                track.synthEvents.Add(new SynthEvent()
                                {
                                    pos = trackTime,
                                    val = readEvent | (pressure << 8)
                                });
                            }
                            break;
                        case 0b11100000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte l = stupid.Read();
                                byte m = stupid.Read();
                                track.eventAmount++;
                                track.synthEvents.Add(new SynthEvent()
                                {
                                    pos = trackTime,
                                    val = readEvent | (l << 8) | (m << 16)
                                });
                            }
                            break;
                        case 0b10110000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte cc = stupid.Read();
                                byte vv = stupid.Read();
                                track.eventAmount++;
                                track.synthEvents.Add(new SynthEvent()
                                {
                                    pos = trackTime,
                                    val = readEvent | (cc << 8) | (vv << 16)
                                });
                            }
                            break;
                        default:
                            switch (readEvent)
                            {
                                case 0b11110000:
                                    while (stupid.Read() != 0b11110111) ;
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
                                            //track.tempoAmount++;
                                            MIDITrack.tempos.Add(new Tempo()
                                            { 
                                                pos = trackTime,
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
                            track.synthEvents.TrimExcess();
                            break;
                    }
                }
                catch (IndexOutOfRangeException)
                {
                    break;
                }
            }
            track.maxTick = trackTime;
            MIDITrack.finished = true;
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