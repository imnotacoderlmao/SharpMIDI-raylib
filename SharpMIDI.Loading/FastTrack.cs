#pragma warning disable 8625
using System.Runtime.CompilerServices;
using MIDIModificationFramework;
namespace SharpMIDI
{
    public unsafe class FastTrack (BufferByteReader bbr) : IDisposable
    {
        public long eventCount = 0;
        public long totalNotes = 0;
        public int trackMaxTick = 0;
        BufferByteReader reader = bbr;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ParseTrackEvents(uint24* msgPtr, ushort* trackPtr, long* writeCursors, ushort track)
        {
            BufferByteReader stupid = reader;
            uint absolutetime = 0;
            long notecount = 0;
            byte prevEvent = 0;
            while (true)
            {
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
                switch (readEvent)
                {
                    case 0xF0:
                        List<byte> data = new List<byte>() { readEvent };
                        uint size = ReadVariableLen();
                        for(uint i = 0; i < size; i++)
                            data.Add(stupid.Read());
                        lock(tempMIDIstorage.SysEx)
                        {
                            tempMIDIstorage.SysEx.Add(new SysEx
                            {
                                tick = absolutetime, 
                                message = [.. data]
                            });
                        }
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
                        readEvent = stupid.Read();
                        if (readEvent == 0x51)
                        {
                            uint len = ReadVariableLen();
                            uint tempo = 0;
                            for (int i = 0; i < len; i++) 
                                tempo = (tempo << 8) | stupid.Read();
                            lock(tempMIDIstorage.temppos)
                            {
                                tempMIDIstorage.temppos.Add(new Tempo 
                                { 
                                    tick = absolutetime, 
                                    tempo = tempo 
                                });
                            }
                        }
                        else if (readEvent == 0x2F)
                        {
                            totalNotes = notecount;
                            return;
                        }
                        else stupid.Skip((int)ReadVariableLen());
                        break;
                }
                switch (status)
                {
                    case 0x80:
                    {
                        byte note = stupid.Read(); 
                        byte vel = stupid.Read();
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1;
                        msgPtr[pos] = (uint24)(readEvent | (note << 8) | (vel << 16));
                        trackPtr[pos] = track;
                        break;
                    }
                    case 0x90:
                    {
                        byte note = stupid.Read();
                        byte vel = stupid.Read();
                        if (vel != 0)
                        { 
                            notecount++; 
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                            msgPtr[pos] = (uint24)(readEvent | (note << 8) | (vel << 16));
                            trackPtr[pos] = track;
                        }
                        else 
                        { 
                            byte dummynoteoff = (byte)(0x80 | channel); 
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                            msgPtr[pos] = (uint24)(dummynoteoff | (note << 8) | (64 << 16));
                            trackPtr[pos] = track;
                        }
                        break;
                    }
                    case 0xA0: 
                    { 
                        byte note = stupid.Read();
                        byte pressure = stupid.Read(); 
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (note << 8) | (pressure << 16));
                        trackPtr[pos] = track;
                        break; 
                    }
                    case 0xB0: 
                    { 
                        byte controller = stupid.Read();
                        byte val = stupid.Read();      
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (controller << 8) | (val << 16));
                        trackPtr[pos] = track;
                        break; 
                    }
                    case 0xC0: 
                    { 
                        byte prog = stupid.Read();                            
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (prog << 8));
                        trackPtr[pos] = track;
                        break; 
                    }
                    case 0xD0: 
                    { 
                        byte pres = stupid.Read();                            
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (pres << 8));
                        trackPtr[pos] = track;
                        break; 
                    }
                    case 0xE0: 
                    { 
                        byte lsb  = stupid.Read(); 
                        byte msb = stupid.Read();      
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (lsb << 8) | (msb << 16));
                        trackPtr[pos] = track;
                        break; 
                    }
                }
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ScanEvents(List<TickGroup> tickCounts)
        {
            BufferByteReader stupid = reader;
            uint absolutetime = 0;
            uint lastTick = 0;
            byte prevEvent = 0;
            uint count = 0;

            while (true)
            {
                uint delta = ReadVariableLen();
                absolutetime += delta;
                byte readEvent = stupid.ReadFast();

                if (readEvent < 0x80) 
                { 
                    stupid.Pushback = readEvent; 
                    readEvent = prevEvent; 
                }

                byte status = (byte)(readEvent & 0xF0);
                prevEvent = readEvent;
                switch (readEvent)
                {
                    case 0xF0:
                        byte b;
                        do { b = stupid.Read(); } while (b != 0xF7);
                        continue;
                    case 0xF1: 
                        stupid.Skip(1); 
                        continue;
                    case 0xF2: 
                        stupid.Skip(2); 
                        continue;
                    case 0xF3: 
                        stupid.Skip(1); 
                        continue;
                    case 0xFF:
                        readEvent = stupid.Read();
                        int len = (int)ReadVariableLen();

                        if (readEvent == 0x2F)
                        {
                            trackMaxTick = (int)absolutetime;
                            if (trackMaxTick > MIDILoader.maxTick)
                                MIDILoader.maxTick = trackMaxTick;
                            if (count > 0)
                            {
                                lock(tickCounts)
                                {
                                    tickCounts.Add(new TickGroup { tick = lastTick, count = count });
                                    eventCount += count;
                                }
                            }
                            return;
                        }
                        else 
                        {
                            stupid.Skip(len);
                        }
                        continue;
                }
                
                bool isChannelEvent = false;
                switch (status)
                {
                    case 0x80:
                    case 0x90:
                    case 0xA0:
                    case 0xB0:
                    case 0xE0:
                        stupid.Skip(2);
                        isChannelEvent = true;
                        break;

                    case 0xC0:
                    case 0xD0:
                        stupid.Skip(1);
                        isChannelEvent = true;
                        break;
                }

                if (isChannelEvent)
                {
                    if (delta > 0 && count > 0)
                    {
                        lock(tickCounts)
                        {
                            tickCounts.Add(new TickGroup { tick = lastTick, count = count });
                            eventCount += count;
                            count = 0;
                        }
                    }

                    lastTick = absolutetime;
                    count++;
                }
            }
        }

        uint ReadVariableLen()
        {
            uint n = 0;
            while (true)
            {
                byte curByte = reader.Read();
                n = (n << 7) | (byte)(curByte & 0x7F);
                if ((curByte & 0x80) == 0)
                {
                    break;
                }
            }
            return n;
        }
        
        public void Dispose()
        {
            reader.Dispose();
            reader = null;
        }
    }
}
