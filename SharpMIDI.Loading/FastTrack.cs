#pragma warning disable 8625
namespace SharpMIDI
{
    public unsafe class FastTrack : IDisposable
    {
        public long eventAmount = 0;
        public long loadedNotes = 0;
        public long totalNotes = 0;
        public int trackMaxTick = 0;
        public List<int[]> skippedNotes = new List<int[]>();
        BufferByteReader stupid;
        public FastTrack(BufferByteReader reader)
        {
            stupid = reader;
        }
        byte prevEvent = 0;
        public void ParseTrackEvents(byte thres, SynthEvent* destination)
        {
            SynthEvent* outputPtr = destination;
            uint localtracktime = 0;
            for (int i = 0; i < 16; i++)
            {
                skippedNotes.Add(new int[256]);
            }
            while (true)
            {
                //this is huge zenith inspiration lol, if you can't beat 'em, join 'em
                int delta = ReadVariableLen();
                localtracktime += (uint)delta;
                byte readEvent = stupid.ReadFast();
                if (readEvent < 0x80)
                {
                    stupid.Pushback = readEvent;
                    readEvent = prevEvent;
                }
                byte status = (byte)(readEvent & 0xF0);
                byte channel = (byte)(readEvent & 0x0F);
                prevEvent = readEvent;
                // is this the right way in ordering sake? meta then midi events?
                switch (readEvent)
                {
                    case 0xF0:
                        // add sysex later?????
                        stupid.Skip(ReadVariableLen());
                        break;
                    case 0xF1:
                        stupid.Skip(1);
                        break;
                    case 0xF2:
                        stupid.Skip(2);
                        break;
                    case 0xF3:
                        stupid.Skip(1);
                        break;
                    case 0xFF:
                        {
                            readEvent = stupid.Read();
                            int metaLength = ReadVariableLen();
                            if (readEvent == 0x51)
                            {
                                uint tempo = 0;
                                for (int i = 0; i < 3; i++)
                                    tempo = (tempo << 8) | stupid.Read();
                                lock (MIDI.temppos)
                                {
                                    MIDI.temppos.Add(new Tempo 
                                    { 
                                        tick = localtracktime, 
                                        tempo = tempo
                                    });
                                }
                            }
                            else if (readEvent == 0x2F)
                            {
                                if (localtracktime > MIDILoader.maxTick)
                                {
                                    MIDILoader.maxTick = (int)localtracktime;
                                }
                                trackMaxTick = (int)localtracktime;
                                return;
                            }
                            else
                            {
                                stupid.Skip(metaLength);
                            }
                        }
                        break;
                    default:
                        break;
                }
                switch (status)
                {
                    case 0x80:
                        {
                            byte note = stupid.Read();
                            byte vel = stupid.Read();
                            if (skippedNotes[channel][note] == 0)
                            {
                                outputPtr[eventAmount++] = new SynthEvent
                                {
                                    tick = localtracktime, 
                                    message = (uint24)(readEvent | (note << 8) | (vel << 16))
                                };
                            }
                            else
                            {
                                skippedNotes[channel][note]--;
                            }
                        }
                        break;
                    case 0x90:
                        {
                            byte note = stupid.Read();
                            byte vel = stupid.Read();
                            if (vel != 0)
                            {
                                totalNotes++;
                                if (vel >= thres)
                                {
                                    loadedNotes++;
                                    outputPtr[eventAmount++] = new SynthEvent
                                    {
                                        tick = localtracktime,
                                        message = (uint24)(readEvent | (note << 8) | (vel << 16))   
                                    };
                                }
                                else
                                {
                                    skippedNotes[channel][note]++;
                                }
                            }
                            else
                            {
                                if (skippedNotes[channel][note] == 0)
                                {
                                    byte dummynoteoff = (byte)(0x80 | channel);
                                    outputPtr[eventAmount++] = new SynthEvent
                                    {
                                        tick = localtracktime,
                                        message =  (uint24)(dummynoteoff | (note << 8) | (64 << 16))
                                    };
                                }
                                else
                                {
                                    skippedNotes[channel][note]--;
                                }
                            }
                        }
                        break;
                    case 0xA0:
                        {
                            byte note = stupid.Read();
                            byte pressure = stupid.Read();
                            outputPtr[eventAmount++] = new SynthEvent 
                            {
                                tick = localtracktime,
                                message = (uint24)(readEvent | (note << 8) | (pressure << 16))
                            };
                        }
                        break;
                    case 0xB0:
                        {
                            byte controller = stupid.Read();
                            byte value = stupid.Read();
                            outputPtr[eventAmount++] = new SynthEvent
                            {
                                tick = localtracktime, 
                                message = (uint24)(readEvent | (controller << 8) | (value << 16))
                            };
                        }
                        break;
                    case 0xC0:
                        {
                            byte program = stupid.Read();
                            outputPtr[eventAmount++] = new SynthEvent 
                            {
                                tick = localtracktime, 
                                message  = (uint24)(readEvent | (program << 8))
                            };
                        }
                        break;
                    case 0xD0:
                        {
                            byte pressure = stupid.Read();
                            outputPtr[eventAmount++] = new SynthEvent 
                            {
                                tick = localtracktime,
                                message = (uint24)(readEvent | (pressure << 8))
                            };
                        }
                        break;
                    case 0xE0:
                        {
                            byte lsb = stupid.Read();
                            byte msb = stupid.Read();
                            outputPtr[eventAmount++] = new SynthEvent
                            {
                                tick = localtracktime, 
                                message = (uint24)(readEvent | (lsb << 8) | (msb << 16))
                            };
                        }
                        break;
                    default:
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