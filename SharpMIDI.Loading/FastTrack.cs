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
                    ushort data = (ushort)eventPayload;
                    localPtr += ((readEvent & 0xE0) == 0xC0) ? 1 : 2;
                    if ((readEvent & 0xE0) != 0xC0)
                    {
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1;
                        if ((readEvent & 0xF0) == 0x90)
                        {
                            if (data >> 8 != 0)
                                totalNotes++;
                            else
                            {
                                msgPtr[pos] = (uint24)(0x80 | (readEvent & 0x0F) | ((byte)data << 8) | (64 << 16));
                                if(trackcolors) trackPtr[pos] = track;
                                continue;
                            }
                        }
                        msgPtr[pos] = (uint24)(readEvent | (data << 8));
                        if(trackcolors) trackPtr[pos] = track;
                    }
                    else
                    {                    
                        long pos = Interlocked.Increment(ref writeCursors[absolutetime]) - 1; 
                        msgPtr[pos] = (uint24)(readEvent | ((byte)data << 8));
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
                    else if (readEvent == 0xF1 || readEvent == 0xF3)
                        localPtr++;
                    else if (readEvent == 0xF2)
                        localPtr += 2;
                    else if (readEvent == 0xFF)
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
                    }
                }
            }
            finalize:
                lock(tempMIDIstorage.temppos) 
                    tempMIDIstorage.temppos.AddRange(localTempos);
                lock(tempMIDIstorage.SysEx)   
                    tempMIDIstorage.SysEx.AddRange(localSysEx);
        }
        
        public BigArray<TickGroup> ScanEvents()
        {
            BigArray<TickGroup> tickCounts = new BigArray<TickGroup>(2048);
            byte* localPtr = ptr;
            byte* localEndPtr = endPtr;
            byte prevEvent = 0;
            int absolutetime = 0;
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
                        tickCounts.Add(new TickGroup 
                        { 
                            tick = absolutetime, 
                            notecount = notecount, 
                            offset = count 
                        });
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
                    localPtr++;
                    if (readEvent < 0xF0)
                    {
                        prevEvent = readEvent;
                        if ((readEvent & 0xF0) == 0x90 && (byte)(eventPayload >> 16) != 0)
                            notecount++;
                        localPtr += ((readEvent & 0xE0) == 0xC0)? 1 : 2;
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
                        else if (readEvent == 0xF2)
                            localPtr += 2;
                        else if (readEvent == 0xF3 || readEvent == 0xF1)
                            localPtr++;
                        else if (readEvent == 0xFF)
                        {
                            readEvent = (byte)(eventPayload >> 8);
                            localPtr++;
                            if (readEvent != 0x2F)
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
                            else
                                goto finalize;
                        }
                    }
                }
                else
                {
                    if ((prevEvent & 0xF0) == 0x90 && eventPayload >> 8 != 0)
                        notecount++;
                    localPtr += ((prevEvent & 0xE0) == 0xC0) ? 1 : 2;
                    count++;
                }
            }

            MIDILoader.Crash("does this midi not have an end of track byte? this message isnt supposed to appear otherwise", choices: false);
            finalize:
                trackMaxTick = absolutetime;
                if (absolutetime > 1 << 28)
                    MIDILoader.Crash($"dear lord what is wrong with your midi file's varlen. current tick = {absolutetime}", choices: false);
                if (trackMaxTick > MIDILoader.maxTick)
                    Interlocked.Exchange(ref MIDILoader.maxTick, trackMaxTick);
                if (count > 0)
                {
                    tickCounts.Add(new TickGroup 
                    { 
                        tick = absolutetime, 
                        notecount = notecount, 
                        offset = count 
                    });
                    eventCount += count;
                    totalNotes += notecount;
                }
                return tickCounts;
        }

        public void Dispose()
        {
            ptr = null;
            endPtr = null;
        }
    }
}