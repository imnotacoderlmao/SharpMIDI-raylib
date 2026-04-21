namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static byte[] gmreset = [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7];
        public static long totalFrames = 0;
        public static long playedEvents, playedevents2, eventspersec = 0;
        public static long MIDIFps = 0;
        public static double last = 0d;
        public static bool stopping = true;
        public static bool stalled = false;
        public static bool skipping = false;
        public static void StartPlayback()
        {
            if (!Sound.issynthinitiated)
            { 
                Console.WriteLine("NO synth initiated. please load a synth first!!!");
                return;
            }
            if (!MIDILoader.midiLoaded) 
            {
                Console.WriteLine("no midi loaded!!!");
                return;
            }
            playedEvents = 0;
            playedevents2 = 0;
            stopping = false;
            var midiev = SynthEvent.messages;
            uint24* msgptr = midiev.Pointer;
            uint24* msgcur = msgptr;
            uint24* buffer = Sound.ringbuffer;
            TickGroup[] tickGroupArr = MIDIEvent.TickGroupArray;
            Tempo[] tevs = MIDIEvent.TempoEventArray;
            SysEx[] sysExes = MIDIEvent.SysExArray;
            uint clock = 0;
            uint sysexidx = 0;
            Sound.StartAudioThread();
            MIDIClock.Start();
            fixed(TickGroup* tg0 = tickGroupArr)
            {
                fixed (Tempo* t0 = tevs)
                {
                    Tempo* currtev = t0;
                    TickGroup* currtg = tg0;
                    while (!stopping)
                    {
                        clock = (uint)MIDIClock.Update();
                        totalFrames++;
                        if (skipping)
                        {
                            while (currtg->tick <= clock)
                            {
                                msgcur += currtg->count;
                                playedEvents += currtg->count;
                                currtg++;
                            }
                            continue;
                        }
                        while (currtg->tick <= clock)
                        {
                            uint24* groupEnd = msgcur + currtg->count;
                            while (msgcur < groupEnd)
                                buffer[(ushort)msgcur] = *msgcur++;
                            playedEvents += currtg->count;
                            currtg++;
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
                    }
                }
            }
            SubmitSysEx(gmreset);
            MIDIClock.Reset();
            Sound.AllNotesOFF();
            Sound.KillAudioThread();
            Console.WriteLine("Playback finished...");
        }

        public static void SubmitSysEx(byte[] message)
        {
            fixed (byte* messageptr = message)
            {
                MIDIHDR header = new MIDIHDR 
                {
                    lpData = messageptr,
                    dwBufferLength = (uint)message.Length,
                    dwBytesRecorded = (uint)message.Length,
                    dwFlags = 0
                };
                uint size = (uint)sizeof(MIDIHDR);
                Console.WriteLine($"\nSending SysEx message: {BitConverter.ToString(message)}");
                #if LINUX
                    uint send = KDMAPI._sendDirectLongDataLinux(messageptr, (uint)(sizeof(byte) * message.Length));
                    Console.WriteLine($"sysex send returned ({send})");
                #elif WINDOWS 
                    uint prepare = 255, send = 255, unprepare = 255; // fallback values
                    prepare = KDMAPI._prepareLongData(&header, size);
                    if (prepare == 0)
                    {
                        send = KDMAPI._sendDirectLongData(&header, size);
                        if (send == 0)
                        {
                            while (KDMAPI._unprepareLongData(&header, size) == 65) // MIDIERR_STILLPLAYING
                                Thread.Sleep(1);
                            unprepare = 0;
                        }
                    }
                    if (prepare != 0 || send != 0 || unprepare != 0)
                    {
                        Console.WriteLine($"sysex prepare,send,unprepare returned ({prepare},{send},{unprepare})");
                    }
                #endif
            }
        }

        public static void UpdatePlaybackStats(int tick)
        {
            const double updateperiod = 0.1d;
            double delta = Timer.Seconds() - last;
            if (MIDIClock.tick > MIDILoader.maxTick) stopping = true;
            if (delta > updateperiod)
            {
                MIDIFps = (long)(totalFrames / delta);
                eventspersec = (long)((playedEvents - playedevents2) / delta);
                playedevents2 = playedEvents;
                totalFrames = 0;
                last = Timer.Seconds();
            }
            Console.Write($"\rTick: {tick} / {MIDILoader.maxTick} | Played Events: {playedEvents} / {MIDILoader.eventCount} ({eventspersec}/s) | MIDI Thread: @{MIDIFps} fps | Skip events?: {MIDIClock.skipevents}         ");
        }
    }
}
