#pragma warning disable 8625

namespace SharpMIDI
{
    public unsafe class FastTrack : IDisposable
    {
        public long eventCount = 0;
        public long totalNotes = 0;
        public int trackMaxTick = 0;
        
        private byte* ptr;
        private byte* endPtr;

        public FastTrack(byte* trackData, uint len)
        {
            this.ptr = trackData;
            this.endPtr = trackData + len;
        }

        public void ParseTrackEvents(uint24* msgPtr, ushort* trackPtr, long* writeCursors, ushort track)
        {
            byte* localPtr = this.ptr;
            int absolutetime = 0;
            long notecount = 0;
            byte prevEvent = 0;
            bool trackcolors = trackPtr != null;

            while (localPtr < endPtr)
            {
                // inline varlen decode
                int delta = 0;
                while (true)
                {
                    byte curByte = *localPtr++;
                    delta = (delta << 7) | (curByte & 0x7F);
                    if ((curByte & 0x80) == 0) 
                        break;
                }
                absolutetime += delta;

                byte readEvent = *localPtr++;
                if (readEvent < 0x80) 
                { 
                    localPtr--;
                    readEvent = prevEvent; 
                }
                
                byte status = (byte)(readEvent & 0xF0);
                byte channel = (byte)(readEvent & 0x0F);
                prevEvent = readEvent;

                switch (readEvent)
                {
                    case 0xF0:
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
                        lock(tempMIDIstorage.SysEx)
                        {
                            tempMIDIstorage.SysEx.Add(new SysEx { tick = absolutetime, message = [.. data] });
                        }
                        break;
                    case 0xF1: 
                        localPtr += 1; 
                        break;
                    case 0xF2: 
                        localPtr += 2; 
                        break;
                    case 0xF3: 
                        localPtr += 1; 
                        break;
                    case 0xFF:
                        readEvent = *localPtr++;
                        if (readEvent == 0x51)
                        {
                            int len = 0;
                            while (true)
                            {
                                byte curByte = *localPtr++;
                                len = (len << 7) | (curByte & 0x7F);
                                if ((curByte & 0x80) == 0) break;
                            }
                            int tempo = 0;
                            for (int i = 0; i < len; i++) 
                                tempo = (tempo << 8) | *localPtr++;
                            lock(tempMIDIstorage.temppos)
                            {
                                tempMIDIstorage.temppos.Add(new Tempo { tick = absolutetime, tempo = (uint24)tempo });
                            }
                        }
                        else if (readEvent == 0x2F)
                        {
                            totalNotes = notecount;
                            this.ptr = localPtr;
                            return;
                        }
                        else 
                        {
                            int len = 0;
                            while (true)
                            {
                                byte curByte = *localPtr++;
                                len = (len << 7) | (curByte & 0x7F);
                                if ((curByte & 0x80) == 0) break;
                            }
                            localPtr += len;
                        }
                        break;
                }

                switch (status)
                {
                    case 0x80:
                    {
                        byte note = *localPtr++; 
                        byte vel = *localPtr++;
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1;
                        msgPtr[pos] = (uint24)(readEvent | (note << 8) | (vel << 16));
                        if(trackcolors) trackPtr[pos] = track;
                        break;
                    }
                    case 0x90:
                    {
                        byte note = *localPtr++;
                        byte vel = *localPtr++;
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1;
                        if (vel != 0)
                        { 
                            notecount++; 
                            msgPtr[pos] = (uint24)(readEvent | (note << 8) | (vel << 16));
                            if(trackcolors) trackPtr[pos] = track;
                        }
                        else 
                        { 
                            byte dummynoteoff = (byte)(0x80 | channel);  
                            msgPtr[pos] = (uint24)(dummynoteoff | (note << 8) | (64 << 16));
                            if(trackcolors) trackPtr[pos] = track;
                        }
                        break;
                    }
                    case 0xA0: 
                    { 
                        byte note = *localPtr++;
                        byte pressure = *localPtr++; 
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (note << 8) | (pressure << 16));
                        if(trackcolors) trackPtr[pos] = track;
                        break; 
                    }
                    case 0xB0: 
                    { 
                        byte controller = *localPtr++;
                        byte val = *localPtr++;      
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (controller << 8) | (val << 16));
                        if(trackcolors) trackPtr[pos] = track;
                        break; 
                    }
                    case 0xC0: 
                    { 
                        byte prog = *localPtr++;                            
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (prog << 8));
                        if(trackcolors) trackPtr[pos] = track;
                        break; 
                    }
                    case 0xD0: 
                    { 
                        byte pres = *localPtr++;                            
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (pres << 8));
                        if(trackcolors) trackPtr[pos] = track;
                        break; 
                    }
                    case 0xE0: 
                    { 
                        byte lsb  = *localPtr++; 
                        byte msb = *localPtr++;      
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | (lsb << 8) | (msb << 16));
                        if(trackcolors) trackPtr[pos] = track;
                        break; 
                    }
                }
            }
            this.ptr = localPtr;
        }

        public void ScanEvents(List<TickGroup> tickCounts)
        {
            byte* localPtr = this.ptr;
            int absolutetime = 0;
            int lastTick = 0;
            byte prevEvent = 0;
            uint count = 0;
            uint notecount = 0;

            while (localPtr < endPtr)
            {
                int delta = 0;
                // also inline varlen decode
                while (true)
                {
                    byte curByte = *localPtr++;
                    delta = (delta << 7) | (curByte & 0x7F);
                    if ((curByte & 0x80) == 0) 
                        break;
                }
                absolutetime += delta;

                byte readEvent = *localPtr++;
                if (readEvent < 0x80)
                {
                    localPtr--;
                    readEvent = prevEvent; 
                }
                
                byte status = (byte)(readEvent & 0xF0);
                prevEvent = readEvent;

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
                        }
                        continue;
                    case 0xF1: 
                        localPtr += 1; 
                        continue;
                    case 0xF2: 
                        localPtr += 2; 
                        continue;
                    case 0xF3: 
                        localPtr += 1; 
                        continue;
                    case 0xFF:
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
                        {
                            trackMaxTick = absolutetime;
                            if (absolutetime > 1 << 28)
                                MIDILoader.Crash($"dear lord what is wrong with your midi file's varlen. current tick = {absolutetime}");
                            if (trackMaxTick > MIDILoader.maxTick)
                                Interlocked.Exchange(ref MIDILoader.maxTick, trackMaxTick);
                            if (count > 0)
                            {
                                tickCounts.Add(new TickGroup { tick = lastTick, notecount = notecount, offset = count });
                                eventCount += count;
                            }
                            this.ptr = localPtr;
                            return;
                        }
                        else 
                        {
                            localPtr += len2;
                        }
                        continue;
                }

                bool isChannelEvent = false;
                switch (status)
                {
                    case 0x90:
                        localPtr += 1;
                        if (*localPtr++ != 0) 
                            notecount++;
                        isChannelEvent = true;
                        break;
                    case 0x80:
                    case 0xA0:
                    case 0xB0:
                    case 0xE0:
                        localPtr += 2;
                        isChannelEvent = true;
                        break;
                    case 0xC0:
                    case 0xD0:
                        localPtr += 1;
                        isChannelEvent = true;
                        break;
                }

                if (isChannelEvent)
                {
                    if (delta > 0 && count > 0)
                    {
                        tickCounts.Add(new TickGroup { tick = lastTick, notecount = notecount, offset = count });
                        eventCount += count;
                        totalNotes += notecount;
                        notecount = 0;
                        count = 0;
                    }

                    lastTick = absolutetime;
                    count++;
                }
            }
            this.ptr = localPtr;
        }

        public void Dispose()
        {
            ptr = null;
            endPtr = null;
        }
    }
}