using System.Runtime.CompilerServices;

namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        private static byte[] gmreset = [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7];
        private static byte[] rolandreset = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7];
        private static long totalFrames = 0, playedNotes, playedNotes2;
        public static int curr_tick = 0;
        public static double MIDIFps = 0, notespersec = 0;
        public static bool stopping = true;
        public static bool skipping = false;
        public static bool potato_mode = false;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void StartPlayback(bool singlethread)
        {
            if (!MIDILoader.midiLoaded) 
            {
                MIDILoader.Crash("no midi loaded!!!");
                return;
            }
            if (!Sound.issynthinitiated)
            { 
                MIDILoader.Crash("NO synth initiated. please load a synth first!!! (press q for ui)");
                return;
            }
            playedNotes = 0;
            playedNotes2 = 0;
            stopping = false;
            var midiev = SynthEvent.messages;
            uint24* msgptr = midiev.Pointer;
            uint24* msgcur = msgptr;
            uint24* buffer = Sound.ringbuffer;
            TickGroup* tickGroupArr = MIDIEvent.TickGroupArray.Pointer;
            TickGroup* currtg = tickGroupArr;
            Tempo[] tevs = MIDIEvent.TempoEventArray;
            SysEx[] sysExes = MIDIEvent.SysExArray;
            int clock = 0;
            uint sysexidx = 0, tempoidx = 0;
            Task.Run(UpdatePlaybackStats);
            var sendfn = Sound.sendTo;
            delegate* unmanaged[SuppressGCTransition]<IntPtr, uint, uint> sendfn2 = null;
            IntPtr handle = IntPtr.Zero;
            #if WINDOWS
            if(Sound.currsynth == "WinMM")
            {
                sendfn2 = WinMM._midiOutShortMsg;
                handle = (IntPtr)WinMM.handle;
            }
            #endif
            if(!singlethread) 
                Sound.StartAudioThread();
            MIDIClock.Start();
            while (!stopping)
            {
                clock = (int)MIDIClock.Update();
                totalFrames++;
                if(MIDIClock.paused || potato_mode) 
                    Thread.Sleep(1);
                if (curr_tick > clock)
                {
                    while (currtg->tick > clock && (currtg - tickGroupArr) > 0)
                    {
                        currtg--;
                        msgcur = msgptr + currtg->offset;
                        playedNotes -= currtg->notecount;
                    }
                    while (tevs[tempoidx].tick > clock && tempoidx > 0) 
                        tempoidx--;
                    while (sysExes[sysexidx].tick > clock && sysexidx > 0) 
                        sysexidx--;
                }
                while (currtg->tick <= clock)
                {
                    // accessing a field shouldnt be slow as hell mane js why
                    curr_tick = currtg->tick;
                    if (!skipping)
                    {
                        uint24* targetMsg = msgptr + currtg->offset;
                        if (!singlethread)
                        {
                            while (msgcur < targetMsg)
                                buffer[(ushort)msgcur] = *msgcur++;
                        }
                        else   
                        { 
                            if (sendfn2 != null)
                            {
                                while (msgcur < targetMsg)
                                    sendfn2(handle, (uint)msgcur++->Value);
                            }
                            else
                            {
                                while (msgcur < targetMsg)
                                    sendfn((uint)msgcur++->Value);
                            }
                        }
                    }
                    else
                        msgcur = msgptr + currtg->offset;
                    playedNotes += currtg->notecount;
                    currtg++;
                }
                while (tevs[tempoidx].tick <= clock)
                {
                    MIDIClock.SubmitBPM(tevs[tempoidx].tempo);
                    tempoidx++;
                }
                while (sysExes[sysexidx].tick <= clock)
                {
                    SubmitSysEx(sysExes[sysexidx].message);
                    sysexidx++;
                }
            }
            SubmitSysEx(gmreset);
            SubmitSysEx(rolandreset);
            MIDIClock.Reset();
            curr_tick = 0;
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
                    if (send != 0)
                        Console.WriteLine($"sysex send returned ({send})");
                #elif WINDOWS 
                    MIDIHDR header = new MIDIHDR 
                    {
                        lpData = messageptr,
                        dwBufferLength = (uint)message.Length,
                        dwBytesRecorded = (uint)message.Length,
                        dwFlags = 0
                    };
                    uint size = (uint)sizeof(MIDIHDR);
                    if (Sound.currsynth == "KDMAPI") 
                        KDMAPI.KDMAPI_SendSysEx_win(&header, size);
                    if (Sound.currsynth == "WinMM") 
                        WinMM.WinMM_SendSysEx(&header, size);
                #endif
            }
        }

        public static void UpdatePlaybackStats()
        {
            const double updateperiod = 0.1d;
            double last = 0d;
            bool kdmapi_hasvoice = Sound.currsynth == "KDMAPI" && KDMAPI.hasvoice;
            while(!stopping)
            {
                double delta = Timer.Seconds() - last;
                if (curr_tick >= MIDILoader.maxTick) stopping = true;
                if (delta > updateperiod)
                {
                    MIDIFps = totalFrames / delta;
                    notespersec = (playedNotes - playedNotes2) / delta;
                    playedNotes2 = playedNotes;
                    totalFrames = 0;
                    last = Timer.Seconds();
                }
                if (kdmapi_hasvoice)
                    Console.Write($"\rTick: {curr_tick:N0} / {MIDILoader.maxTick:N0} | Played Notes: {playedNotes:N0} / {MIDILoader.totalNotes:N0} ({notespersec:N0}/s) | MIDI Thread: @{MIDIFps:N0} fps | {KDMAPI._getActiveVoices()} voices         ");
                else
                    Console.Write($"\rTick: {curr_tick:N0} / {MIDILoader.maxTick:N0} | Played Notes: {playedNotes:N0} / {MIDILoader.totalNotes:N0} ({notespersec:N0}/s) | MIDI Thread: @{MIDIFps:N0} fps         ");
                Thread.Sleep(1000/60);
            }
        }
    }
}