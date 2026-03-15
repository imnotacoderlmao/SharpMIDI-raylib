namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static byte[] gmreset = {0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7};
        public static long totalFrames = 0;
        public static long playedEvents, playedevents2, eventspersec = 0;
        public static float MIDIFps = 0f;
        public static bool stopping = true;
        public static bool stalled = false;
        public static bool skipping = false;
        public static void StartPlayback()
        {
            if (!Sound.issynthinitiated)
            { 
                Console.WriteLine("NO synth initiated. trying OmniMIDI as a fallback");
                Sound.InitSynth("KDMAPI"); // fallback since i kept forgetting to do both and it crashes the whole program lmao
            }
            if (!MIDILoader.midiLoaded) 
            {
                Console.WriteLine("no midi loaded!!!");
                return;
            }
            playedEvents = 0;
            playedevents2 = 0;
            stopping = false;
            var synthev = MIDI.synthEvents;
            uint24* msgptr = MIDI.synthEvents.Pointer;
            uint24* msgcur = msgptr;
            TickGroup[] tickGroupArr = MIDI.tickGroupArr;
            Tempo[] tevs = MIDI.tempoEvents;
            SysEx[] sysExes = MIDI.SysExarr;
            uint maxTick = (uint)MIDILoader.maxTick;
            uint clock = 0;
            uint24* buffer = Sound.ringbuffer;
            ushort writeptr = 0;
            uint sysexidx = 0;
            fixed(TickGroup* tg0 = tickGroupArr)
            {
                fixed (Tempo* t0 = tevs)
                {
                    Tempo* currtev = t0;
                    TickGroup* currtg = tg0;
                    Sound.StartAudioThread();
                    Task.Run(PlaybackStats);
                    MIDIClock.Start();
                    while (!stopping)
                    {
                        clock = (uint)MIDIClock.Update();
                        if (!skipping)
                        {
                            while (currtg->tick <= clock)
                            {
                                uint24* groupEnd = msgcur + currtg->count;
                                while (msgcur < groupEnd)
                                    buffer[writeptr++] = *msgcur++;
                                playedEvents += currtg->count;
                                currtg++;
                            }
                        }
                        else
                        {
                            while (currtg->tick <= clock)
                            {
                                msgcur += currtg->count;
                                playedEvents += currtg->count;
                                currtg++;
                            }
                        }
                        while (currtev->tick <= clock)
                        {
                            MIDIClock.SubmitBPM(currtev->tick, currtev->tempo);
                            currtev++;
                        }
                        while (sysExes[sysexidx].tick <= clock)
                        {
                            SubmitSysEx(sysExes[sysexidx].message);
                            sysexidx++;
                        }
                        totalFrames++;
                        if (clock > maxTick) stopping = true;
                    }
                }
            }
            MIDIClock.Reset();
            Sound.KillAudioThread();
            SubmitSysEx(gmreset);
            Console.WriteLine("Playback finished...");
        }

        public static void SubmitSysEx(byte[] message)
        {
                fixed (byte* messageptr = message)
                {
                    MIDIHDR header = new MIDIHDR {
                        lpData = messageptr,
                        dwBufferLength = (uint)message.Length,
                        dwBytesRecorded = (uint)message.Length,
                        dwFlags = 0
                    };
                    #if WINDOWS 
                        uint size = (uint)sizeof(MIDIHDR);
                        uint prepare = KDMAPI._prepareLongData(&header, size); 
                        uint send = KDMAPI._sendDirectLongData(&header, size);
                        uint unprepare = KDMAPI._unprepareLongData(&header, size);
                        Console.WriteLine($"sysex prepare,send,unprepare returned ({prepare},{send},{unprepare})");
                    #elif LINUX
                        uint size = (uint)(message.Length * sizeof(byte));
                        uint send = KDMAPI._sendDirectLongDataLinux(messageptr, size);
                        Console.WriteLine($"sysex send returned ({send})");
                    #endif
                }
        }

        public static void PlaybackStats()
        {
            while (!stopping)
            {
                MIDIFps = totalFrames * 30;
                eventspersec = (playedEvents - playedevents2) * 30;
                if (stalled) Console.Write($"\rTick: {(int)MIDIClock.tick} / {MIDILoader.maxTick} | Played Events: {playedEvents} / {MIDILoader.eventCount} ({eventspersec}/s) | MIDI Thread: STALLED | Skip events?: {MIDIClock.skipevents}          ");
                else Console.Write($"\rTick: {(int)MIDIClock.tick} / {MIDILoader.maxTick} | Played Events: {playedEvents} / {MIDILoader.eventCount} ({eventspersec}/s) | MIDI Thread: @{MIDIFps} fps | Skip events?: {MIDIClock.skipevents}          ");
                playedevents2 = playedEvents;
                totalFrames = 0;
                Thread.Sleep(1000/30);
            }
        }
    }
}