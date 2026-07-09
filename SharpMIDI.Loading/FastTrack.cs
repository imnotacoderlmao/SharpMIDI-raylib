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
                
                byte readEvent = *localPtr;
                if (readEvent >= 0x80)
                {
                    localPtr++;
                    if (readEvent < 0xF0) 
                        prevEvent = readEvent;
                }
                else
                    readEvent = prevEvent;

                if (readEvent < 0xF0)
                {
                    byte status = (byte)(readEvent & 0xF0);
                    // lowkey curious in the difference between else if and switch cases but theyre the same
                    // also switches in this case seems more readable imo.
                    switch (status)
                    {
                        case 0x90:
                        {
                            byte note = *localPtr++;
                            byte vel = *localPtr++;
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1;
                            if (vel != 0)
                            { 
                                notecount++; 
                                msgPtr[pos] = (uint24)(readEvent | (note << 8) | (vel << 16));
                            }
                            else 
                            { 
                                byte channel = (byte)(readEvent & 0x0F);
                                byte dummynoteoff = (byte)(0x80 | channel);  
                                msgPtr[pos] = (uint24)(dummynoteoff | (note << 8) | (64 << 16));
                            }
                            if(trackcolors) trackPtr[pos] = track;
                            continue;
                        }
                        case 0x80:
                        {
                            byte note = *localPtr++; 
                            byte vel = *localPtr++;
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1;
                            msgPtr[pos] = (uint24)(readEvent | (note << 8) | (vel << 16));
                            if(trackcolors) trackPtr[pos] = track;
                            continue;
                        }
                        case 0xA0:
                        { 
                            byte note = *localPtr++;
                            byte pressure = *localPtr++; 
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                            msgPtr[pos] = (uint24)(readEvent | (note << 8) | (pressure << 16));
                            if(trackcolors) trackPtr[pos] = track;
                            continue;
                        }
                        case 0xB0:
                        { 
                            byte controller = *localPtr++;
                            byte val = *localPtr++;      
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                            msgPtr[pos] = (uint24)(readEvent | (controller << 8) | (val << 16));
                            if(trackcolors) trackPtr[pos] = track;
                            continue;
                        }
                        case 0xC0:
                        { 
                            byte prog = *localPtr++;                            
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                            msgPtr[pos] = (uint24)(readEvent | (prog << 8));
                            if(trackcolors) trackPtr[pos] = track;
                            continue;
                        }
                        case 0xD0:
                        { 
                            byte pres = *localPtr++;                            
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                            msgPtr[pos] = (uint24)(readEvent | (pres << 8));
                            if(trackcolors) trackPtr[pos] = track;
                            continue;
                        }
                        case 0xE0:
                        { 
                            byte lsb = *localPtr++; 
                            byte msb = *localPtr++;      
                            long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                            msgPtr[pos] = (uint24)(readEvent | (lsb << 8) | (msb << 16));
                            if(trackcolors) trackPtr[pos] = track;
                            continue;
                        }
                    }
                }
                else
                {
                    switch (readEvent)
                    {
                        case 0xF0:
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
                            continue;
                        }
                        case 0xF1:
                        case 0xF3:
                        {
                            localPtr++; 
                            continue;
                        }
                        case 0xF2:
                        { 
                            localPtr += 2;
                            continue;
                        }
                        case 0xFF:
                        {
                            readEvent = *localPtr++;
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
            }
            finalize:
                totalNotes = notecount;
                lock(tempMIDIstorage.temppos) 
                    tempMIDIstorage.temppos.AddRange(localTempos);
                lock(tempMIDIstorage.SysEx)   
                    tempMIDIstorage.SysEx.AddRange(localSysEx);
        }

        public void ScanEvents(ref BigArray<TickGroup> tickCounts)
        {
            tickCounts = new BigArray<TickGroup>(2048);
            TickGroup* ticks = tickCounts.Pointer;
            byte* localPtr = ptr;
            byte* localEndPtr = endPtr;
            int absolutetime = 0;
            byte prevEvent = 0;
            long tick_idx = 0;
            long tick_len = tickCounts.Length;
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
                        if (tick_idx >= tick_len)
                        {
                            tick_len *= 2;
                            tickCounts.Resize(tick_len);
                            ticks = tickCounts.Pointer;
                        }
                        ticks[tick_idx++] = new TickGroup 
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

                byte readEvent = *localPtr;
                if (readEvent >= 0x80)
                {
                    localPtr++;
                    if (readEvent < 0xF0) 
                        prevEvent = readEvent;
                }
                else
                    readEvent = prevEvent;

                if (readEvent < 0xF0)
                {
                    byte status = (byte)(readEvent & 0xF0);
                    switch (status)
                    {
                        case 0x90:
                        {
                            localPtr++;
                            if (*localPtr++ != 0) 
                                notecount++;
                            count++;
                            continue;
                        }
                        case 0x80: 
                        case 0xA0: 
                        case 0xB0:
                        case 0xE0:
                        {
                            localPtr += 2;
                            count++;
                            continue;
                        }
                        case 0xC0: 
                        case 0xD0:
                        {
                            localPtr++;
                            count++;
                            continue;
                        }
                    }
                }
                else
                {
                    switch (readEvent)
                    {
                        case 0xF0:
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
                            continue;
                        }
                        case 0xF1:
                        case 0xF3:
                        {
                            localPtr++; 
                            continue;
                        }
                        case 0xF2:
                        { 
                            localPtr += 2;
                            continue;
                        }
                        case 0xFF:
                        {
                            readEvent = *localPtr++;
                            int len2 = 0;
                            while (true)
                            {
                                byte curByte = *localPtr++;
                                len2 = (len2 << 7) | (curByte & 0x7F);
                                if ((curByte & 0x80) == 0) 
                                    break;
                            }
                            if (readEvent == 0x2F)
                                goto finalize;
                            else 
                                localPtr += len2;
                            continue;
                        }
                    }
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
                    if (tick_idx >= tick_len)
                    {
                        tick_len *= 2;
                        tickCounts.Resize(tick_len);
                        ticks = tickCounts.Pointer;
                    }
                    ticks[tick_idx++] = new TickGroup 
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