using System.Runtime.CompilerServices;
namespace SharpMIDI
{
    public unsafe class FastTrack(byte* trackData, uint len) : IDisposable
    {
        public long eventCount = 0;
        public long totalNotes = 0;
        public int trackMaxTick = 0;
        private byte* ptr = trackData;
        private byte* endPtr = trackData + len;

        public void ParseTrackEvents(uint24* msgPtr, byte* trackPtr, long* writeCursors, byte track)
        {
            byte* localPtr = ptr;
            byte* localEndPtr = endPtr;
            int absolutetime = 0;
            long notecount = 0;
            byte prevEvent = 0;
            var localTempos = new List<Tempo>();
            var localSysEx = new List<SysEx>();
            bool trackcolors = trackPtr != null;

            while (localPtr < localEndPtr)
            {
                int delta = *localPtr++;
                if (delta >= 0x80)
                {
                    delta &= 0x7F;
                    byte b;
                    do 
                    {
                        b = *localPtr++;
                        delta = (delta << 7) | (b & 0x7F);
                    } 
                    while (b >= 0x80);
                }
                absolutetime += delta;
                uint eventPayload = Unsafe.ReadUnaligned<uint>(localPtr);
                byte readEvent = (byte)eventPayload;
                if (readEvent >= 0x80)
                {
                    localPtr++;
                    eventPayload >>= 8;
                    if (readEvent < 0xF0)
                        prevEvent = readEvent;
                }
                else
                    readEvent = prevEvent;

                if (readEvent < 0xF0)
                {
                    byte status = (byte)(readEvent & 0xF0);
                    byte data1 = (byte)eventPayload;
                    byte data2 = (byte)(eventPayload >> 8);
                    localPtr += ((status & 0xE0) == 0xC0) ? 1 : 2;
                    if (status == 0x90)
                    {
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1;
                        if (data2 != 0)
                        { 
                            notecount++; 
                            msgPtr[pos] = (uint24)(readEvent | (data1 << 8) | (data2 << 16));
                        }
                        else 
                        { 
                            byte dummynoteoff = (byte)(0x80 | readEvent & 0x0F);  
                            msgPtr[pos] = (uint24)(dummynoteoff | (data1 << 8) | (64 << 16));
                        }
                        if(trackcolors) trackPtr[pos] = track;
                    }
                    if (status == 0x80 || status == 0xA0 || status == 0xB0 || status == 0xE0)
                    {
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1;
                        msgPtr[pos] = (uint24)(readEvent | (data1 << 8) | (data2 << 16));
                        if(trackcolors) trackPtr[pos] = track;
                    }
                    if (status == 0xC0 || status == 0xD0)
                    {                    
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (data1 << 8));
                        if(trackcolors) trackPtr[pos] = track;
                    }
                }
                else
                {
                    if (readEvent == 0xF0)
                    {
                        List<byte> data = [ readEvent ];
                        int size = 0;
                        while (true)
                        {
                            byte curByte = *localPtr++;
                            size = (size << 7) | (curByte & 0x7F);
                            if ((curByte & 0x80) == 0) 
                                break;
                        }
                        for(uint i = 0; i < size; i++)
                            data.Add(*localPtr++);
                        
                        localSysEx.Add(new SysEx { tick = absolutetime, message = [.. data] });
                    }
                    if (readEvent == 0xF1 || readEvent == 0xF3)
                        localPtr++;
                    if (readEvent == 0xF2)
                        localPtr += 2;
                    if (readEvent == 0xFF)
                    {
                        readEvent = (byte)eventPayload;
                        localPtr++;
                        if (readEvent == 0x51)
                        {
                            int len = 0;
                            while (true)
                            {
                                byte curByte = *localPtr++;
                                len = (len << 7) | (curByte & 0x7F);
                                if ((curByte & 0x80) == 0) 
                                    break;
                            }
                            int tempo = 0;
                            for (int i = 0; i < len; i++) 
                                tempo = (tempo << 8) | *localPtr++;
                            
                            localTempos.Add(new Tempo { tick = absolutetime, tempo = (uint24)tempo });
                        }
                        else if (readEvent == 0x2F)
                            goto finalize;
                        else 
                        {
                            int len = 0;
                            while (true)
                            {
                                byte curByte = *localPtr++;
                                len = (len << 7) | (curByte & 0x7F);
                                if ((curByte & 0x80) == 0) 
                                    break;
                            }
                            localPtr += len;
                        }
                        continue;
                    }
                }
            }
            finalize:
                totalNotes = notecount;
                lock(tempMIDIstorage.temppos) 
                    tempMIDIstorage.temppos.AddRange(localTempos);
                lock(tempMIDIstorage.SysEx)   
                    tempMIDIstorage.SysEx.AddRange(localSysEx);
        }
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ResizeTickCounts(ref BigArray<TickGroup> tickCounts) => tickCounts.Resize(tickCounts.Length * 2);
        
        public void ScanEvents(ref BigArray<TickGroup> tickCounts)
        {
            tickCounts = new BigArray<TickGroup>(2048);
            byte* localPtr = ptr;
            byte* localEndPtr = endPtr;
            byte prevEvent = 0;
            int absolutetime = 0;
            int tick_idx = 0;
            uint count = 0;
            uint notecount = 0;

            while (localPtr < localEndPtr)
            {
                int delta = *localPtr++;
                if (delta >= 0x80)
                {
                    delta &= 0x7F;
                    byte b;
                    do 
                    {
                        b = *localPtr++;
                        delta = (delta << 7) | (b & 0x7F);
                    }
                    while (b >= 0x80);
                }

                if (delta > 0)
                {
                    if (count > 0)
                    {
                        tick_idx++;
                        if (tick_idx >= tickCounts.Length)
                            ResizeTickCounts(ref tickCounts);
                        tickCounts.Pointer[tick_idx] = new TickGroup 
                        { 
                            tick = absolutetime, 
                            notecount = notecount, 
                            offset = count 
                        };
                        eventCount += count;
                        totalNotes += notecount;
                        notecount = 0;
                        count = 0;
                    }
                    absolutetime += delta;
                }
                uint eventPayload = Unsafe.ReadUnaligned<uint>(localPtr);
                byte readEvent = (byte)eventPayload;
                if (readEvent >= 0x80)
                {
                    eventPayload >>= 8;
                    localPtr++;
                    if (readEvent < 0xF0)
                    {
                        prevEvent = readEvent;
                        int status = readEvent & 0xF0;
                        if (status == 0x90 && eventPayload >> 8 != 0)
                            notecount++;
                        localPtr += ((status & 0xE0) == 0xC0) ? 1 : 2;
                        count++;
                    }
                    else
                    {
                        if (readEvent == 0xF0)
                        {
                            int len = 0;
                            while (true)
                            {
                                byte curByte = *localPtr++;
                                len = (len << 7) | (curByte & 0x7F);
                                if ((curByte & 0x80) == 0) 
                                    break;
                            }
                            localPtr += len;
                        }
                        if (readEvent == 0xF3 && readEvent == 0xF1)
                            localPtr++;
                        if (readEvent == 0xF2)
                            localPtr += 2;
                        if (readEvent == 0xFF)
                        {
                            readEvent = (byte)eventPayload;
                            localPtr++;
                            if (readEvent == 0x2F)
                                goto finalize;
                            else
                            {
                                int len2 = 0;
                                while (true)
                                {
                                    byte curByte = *localPtr++;
                                    len2 = (len2 << 7) | (curByte & 0x7F);
                                    if ((curByte & 0x80) == 0) 
                                        break;
                                }
                                localPtr += len2;
                            }
                            continue;
                        }
                    }
                }
                else
                {
                    int status = prevEvent & 0xF0;
                    if (status == 0x90 && eventPayload >> 8 != 0)
                        notecount++;
                    localPtr += ((status & 0xE0) == 0xC0) ? 1 : 2;
                    count++;
                }
            }

            MIDILoader.Crash("does this midi not have an end of track byte? this message isnt supposed to appear otherwise");
            finalize:
                trackMaxTick = absolutetime;
                if (absolutetime > 1 << 28)
                    MIDILoader.Crash($"dear lord what is wrong with your midi file's varlen. current tick = {absolutetime}");
                if (trackMaxTick > MIDILoader.maxTick)
                    Interlocked.Exchange(ref MIDILoader.maxTick, trackMaxTick);
                if (count > 0)
                {
                    tick_idx++;
                    if (tick_idx >= tickCounts.Length)
                        ResizeTickCounts(ref tickCounts);
                    tickCounts.Pointer[tick_idx] = new TickGroup 
                    { 
                        tick = absolutetime, 
                        notecount = notecount, 
                        offset = count 
                    };
                    eventCount += count;
                    totalNotes += notecount;
                }
                tickCounts.Count = tick_idx;
        }

        public void Dispose()
        {
            ptr = null;
            endPtr = null;
        }
    }
}