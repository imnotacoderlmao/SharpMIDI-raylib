#pragma warning disable 8625
namespace SharpMIDI
{
    public unsafe class FastTrack : IDisposable
    {
        public long eventAmount = 0;
        public long loadedNotes = 0;
        public long totalNotes = 0;
        public List<long> localEvents = new List<long>();
        public List<int[]> skippedNotes = new List<int[]>();
        public int trackTime = 0;
        BufferByteReader stupid;
        public FastTrack(BufferByteReader reader)
        {
            stupid = reader;
        }
        byte prevEvent = 0;
        public void ParseTrackEvents(byte thres, List<long> eventsList)
        {
            localEvents = eventsList;
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
                                        long data = ((long)localtracktime << 32) | (uint)(readEvent | (note << 8) | (vel << 16));
                                        localEvents.Add(data);
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
                                        long data = ((long)localtracktime << 32) | (uint)(customEvent | (note << 8) | (vel << 16));
                                        localEvents.Add(data);
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
                                    long data = ((long)localtracktime << 32) | (uint)(readEvent | (note << 8) | (vel << 16));
                                    localEvents.Add(data);
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
                                long data = ((long)localtracktime << 32) | (uint)(readEvent | (note << 8) | (vel << 16));
                                localEvents.Add(data);
                            }
                            break;
                        case 0b11000000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte program = stupid.Read();
                                eventAmount++;
                                long data = ((long)localtracktime << 32) | (uint)(readEvent | (program << 8));
                                localEvents.Add(data);
                            }
                            break;
                        case 0b11010000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte pressure = stupid.Read();
                                eventAmount++;
                                long data = ((long)localtracktime << 32) | (uint)(readEvent | (pressure << 8));
                                localEvents.Add(data);
                            }
                            break;
                        case 0b11100000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte l = stupid.Read();
                                byte m = stupid.Read();
                                eventAmount++;
                                long data = ((long)localtracktime << 32) | (uint)(readEvent | (l << 8) | (m << 16));
                                localEvents.Add(data);
                            }
                            break;
                        case 0b10110000:
                            {
                                int channel = readEvent & 0b00001111;
                                byte cc = stupid.Read();
                                byte vv = stupid.Read();
                                eventAmount++;
                                long data = ((long)localtracktime << 32) | (uint)(readEvent | (cc << 8) | (vv << 16));
                                localEvents.Add(data);
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
                                            uint tempo = 0;
                                            for (int i = 0; i != 3; i++)
                                                tempo = (tempo << 8) | stupid.Read();
                                            long data = ((long)localtracktime << 32) | tempo;
                                            MIDI.temppos.Add(data);
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
                            }
                        break;
                    }
                }
                catch (IndexOutOfRangeException)
                {
                    localEvents.TrimExcess();
                    break;
                }
            }
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