namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static byte[] gmreset = [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7];
        public static byte[] rolandreset = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7];
        public static long totalFrames = 0;
        public static long playedNotes, playedNotes2, eventspersec = 0;
        public static long MIDIFps = 0;
        public static double last = 0d;
        public static bool stopping = true;
        public static bool stalled = false;
        public static bool skipping = false;
        public static bool potato_mode = false;
        public static void StartPlayback(bool singlethread)
        {
            if (!Sound.issynthinitiated)
            { 
                Console.WriteLine("NO synth initiated. please load a synth first!!! (press q for ui)");
                return;
            }
            if (!MIDILoader.midiLoaded) 
            {
                Console.WriteLine("no midi loaded!!!");
                return;
            }
            playedNotes = 0;
            playedNotes2 = 0;
            stopping = false;
            var midiev = SynthEvent.messages;
            uint24* msgptr = midiev.Pointer;
            uint24* msgcur = msgptr;
            uint24* buffer = Sound.ringbuffer;
            TickGroup[] tickGroupArr = MIDIEvent.TickGroupArray;
            Tempo[] tevs = MIDIEvent.TempoEventArray;
            SysEx[] sysExes = MIDIEvent.SysExArray;
            uint clock = 0, lastclock = 0;
            uint sysexidx = 0;
            Task.Run(UpdatePlaybackStats);
            var sendfn = Sound.sendTo;
            #if WINDOWS
            var sendfn2 = WinMM._midiOutShortMsg;
            IntPtr handle = (IntPtr)WinMM.handle;
            #endif
            if(!singlethread) Sound.StartAudioThread();
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
                        if(MIDIClock.paused || potato_mode) 
                        {
                            Thread.Sleep(1);
                        }
                        if (skipping)
                        {
                            while (currtg->tick <= clock)
                            {
                                msgcur = msgptr + currtg->offset;
                                playedNotes += currtg->notecount;
                                currtg++;
                            }
                            continue;
                        }
                        if(lastclock > clock)
                        {
                            while (currtg->tick > clock)
                            {
                                currtg--;
                                msgcur = msgptr + currtg->offset;
                                playedNotes -= currtg->notecount;
                            }
                            if(tevs.Length > 1) 
                                while (currtev->tick > clock) currtev--;
                            if(sysExes.Length > 1)
                                while (sysExes[sysexidx].tick > clock) sysexidx--;
                        }
                        while (currtg->tick <= clock)
                        {
                            uint24* targetMsg = msgptr + currtg->offset;
                            if (!singlethread)
                            {
                                while (msgcur < targetMsg)
                                    buffer[(ushort)msgcur] = *msgcur++;
                            }
                            else   
                            { 
                                #if WINDOWS
                                if (Sound.currsynth == "WinMM")
                                    while (msgcur < targetMsg)
                                        sendfn2(handle, (uint)msgcur++->Value);
                                #endif
                                if (Sound.currsynth == "KDMAPI")
                                    while (msgcur < targetMsg)
                                        sendfn((uint)msgcur++->Value);
                            }
                            playedNotes += currtg->notecount;
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
                        lastclock = clock;
                    }
                }
            }
            SubmitSysEx(gmreset);
            SubmitSysEx(rolandreset);
            MIDIClock.Reset();
            Sound.AllNotesOFF();
            Sound.KillAudioThread();
            Console.WriteLine("Playback finished...");
        }

        public static void SubmitSysEx(byte[] message)
        {
            fixed (byte* messageptr = message)
            {
                Console.WriteLine($"\nSending SysEx message: {BitConverter.ToString(message)}");
                #if LINUX
                    uint send = KDMAPI._sendDirectLongDataLinux(messageptr, (uint)(sizeof(byte) * message.Length));
                    Console.WriteLine($"sysex send returned ({send})");
                #elif WINDOWS 
                    uint prepare = 255, send = 255, unprepare = 255; // fallback values
                    MIDIHDR header = new MIDIHDR 
                    {
                        lpData = messageptr,
                        dwBufferLength = (uint)message.Length,
                        dwBytesRecorded = (uint)message.Length,
                        dwFlags = 0
                    };
                    uint size = (uint)sizeof(MIDIHDR);
                    Console.WriteLine($"\nSending SysEx message: {BitConverter.ToString(message)}");
                    if (Sound.currsynth == "KDMAPI") KDMAPI.KDMAPI_SendSysEx_win(&header, size);
                    if (Sound.currsynth == "WinMM") WinMM.WinMM_SendSysEx(&header, size);
                #endif
            }
        }

        public static void UpdatePlaybackStats()
        {
            const double updateperiod = 0.1d;
            while(!stopping)
            {
                double delta = Timer.Seconds() - last;
                if (MIDIClock.tick > MIDILoader.maxTick) stopping = true;
                if (delta > updateperiod)
                {
                    MIDIFps = (long)(totalFrames / delta);
                    eventspersec = (long)((playedNotes - playedNotes2) / delta);
                    playedNotes2 = playedNotes;
                    totalFrames = 0;
                    last = Timer.Seconds();
                }
                Console.Write($"\rTick: {(int)MIDIClock.tick} / {MIDILoader.maxTick} | Played Notes: {playedNotes} / {MIDILoader.totalNotes} ({eventspersec}/s) | MIDI Thread: @{MIDIFps} fps         ");
                Thread.Sleep(1000/60);
            }
        }
    }
}
