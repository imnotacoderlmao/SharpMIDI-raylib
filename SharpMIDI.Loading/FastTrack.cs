#pragma warning disable 8625
using MIDIModificationFramework;
namespace SharpMIDI
{
    public unsafe class FastTrack (BufferByteReader reader) : IDisposable
    {
        public long eventCount = 0;
        public long totalNotes = 0;
        public int trackMaxTick = 0;
        BufferByteReader stupid = reader;
        byte prevEvent = 0;
        public void ParseTrackEvents(SynthEvent* destination)
        {
            SynthEvent* outputPtr = destination;
            uint absolutetime = 0;
            long eventAmount = 0;
            long notecount = 0;
            while (true)
            {
                //this is huge zenith inspiration lol, if you can't beat 'em, join 'em
                uint delta = ReadVariableLen();
                absolutetime += delta;
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
                        // will not add this for now, i dont know how to prepare and send these properly.
                        /*uint len = ReadVariableLen();
                        List<byte> data = new List<byte>() { readEvent };
                        for (uint i = 0; i < len; i++)
                            data.Add(stupid.Read());
                        MIDI.SysEx.Add(new SysEx
                        {
                            tick = absolutetime, 
                            message = [.. data]
                        });*/
                        stupid.Skip((int)ReadVariableLen());
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
                    case 0xF7:
                        //MIDI.SysEx.Add(new SysEx
                        //{
                        //    tick = absolutetime,
                        //    message = [readEvent]
                        //});
                        stupid.Skip((int)ReadVariableLen());
                        break;
                    case 0xFF:
                        {
                            readEvent = stupid.Read();
                            int metaLength = (int)ReadVariableLen();
                            if (readEvent == 0x51)
                            {
                                uint tempo = 0;
                                for (int i = 0; i < 3; i++)
                                    tempo = (tempo << 8) | stupid.Read();
                                    MIDI.temppos.Add(new Tempo 
                                    { 
                                        tick = absolutetime, 
                                        tempo = tempo
                                    });
                            }
                            else if (readEvent == 0x2F)
                            {
                                if (absolutetime > MIDILoader.maxTick)
                                {
                                    MIDILoader.maxTick = (int)absolutetime;
                                }
                                trackMaxTick = (int)absolutetime;
                                eventCount = eventAmount;
                                totalNotes = notecount;
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
                            outputPtr[eventAmount++] = new SynthEvent
                            {
                                tick = absolutetime, 
                                message = (uint24)(readEvent | (note << 8) | (vel << 16))
                            };
                        }
                        break;
                    case 0x90:
                        {
                            byte note = stupid.Read();
                            byte vel = stupid.Read();
                            if (vel != 0)
                            {
                                notecount++;
                                outputPtr[eventAmount++] = new SynthEvent
                                {
                                    tick = absolutetime,
                                    message = (uint24)(readEvent | (note << 8) | (vel << 16))   
                                };
                            }
                            else
                            {
                                byte dummynoteoff = (byte)(0x80 | channel);
                                outputPtr[eventAmount++] = new SynthEvent
                                {
                                    tick = absolutetime,
                                    message =  (uint24)(dummynoteoff | (note << 8) | (64 << 16))
                                };
                            }
                        }
                        break;
                    case 0xA0:
                        {
                            byte note = stupid.Read();
                            byte pressure = stupid.Read();
                            outputPtr[eventAmount++] = new SynthEvent 
                            {
                                tick = absolutetime,
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
                                tick = absolutetime, 
                                message = (uint24)(readEvent | (controller << 8) | (value << 16))
                            };
                        }
                        break;
                    case 0xC0:
                        {
                            byte program = stupid.Read();
                            outputPtr[eventAmount++] = new SynthEvent 
                            {
                                tick = absolutetime, 
                                message  = (uint24)(readEvent | (program << 8))
                            };
                        }
                        break;
                    case 0xD0:
                        {
                            byte pressure = stupid.Read();
                            outputPtr[eventAmount++] = new SynthEvent 
                            {
                                tick = absolutetime,
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
                                tick = absolutetime, 
                                message = (uint24)(readEvent | (lsb << 8) | (msb << 16))
                            };
                        }
                        break;
                    default:
                        break;
                }   
            }
        }

        uint ReadVariableLen()
        {
            int n = 0;
            while (true)
            {
                byte curByte = stupid.Read();
                n = (n << 7) | (byte)(curByte & 0x7F);
                if ((curByte & 0x80) == 0)
                {
                    break;
                }
            }
            return (uint)n;
        }
        
        public void Dispose()
        {
            stupid.Dispose();
            stupid = null;
        }
    }
}