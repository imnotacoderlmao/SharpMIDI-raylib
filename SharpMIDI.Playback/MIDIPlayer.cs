using System.Runtime.CompilerServices;

namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        private static byte[] gmreset = [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7];
        private static byte[] rolandreset = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7];
        private static long playedNotes, playedNotes2;
        private readonly static long[] npshistory = new long[60];
        private static double notespersec = 0, laststatsupdate = 0;
        public static int curr_tick = 0, npshistoryidx = 0;
        public static string fpsStr = string.Empty;
        public static bool stopping = true, skipping = false, potato_mode = false;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void StartPlayback(bool singlethread)
        {
            if (!MIDILoader.midiLoaded)
            {
                MIDILoader.Crash("no midi loaded!!!", choices: false);
                return;
            }
            if (!Sound.issynthinitiated && MIDILoader.Crash("NO synth initiated. please load a synth first for audio!!! (press q for ui)", choices: true) == 0)
                return;
            stopping = false;
            playedNotes = 0;
            playedNotes2 = 0;
            bool synthExists = Sound.currsynth != "Empty";
            if (!singlethread && synthExists)
                Sound.StartAudioThread();
            uint24* msgptr = SynthEvent.messages.Pointer;
            uint24* buffer = Sound.ringbuffer;
            TickGroup* currtg = MIDIEvent.TickGroupArray.Pointer;
            Tempo[] tevs = MIDIEvent.TempoEventArray;
            SysEx[] sysExes = MIDIEvent.SysExArray;
            long played = 0;
            uint sysexidx = 0, tempoidx = 0;
            var sendfn = Sound.sendTo;
#if WINDOWS
            var sendfn2 = WinMM._midiOutShortMsg;
            IntPtr handle = IntPtr.Zero;
            if(Sound.currsynth == "WinMM")
            {
                sendfn2 = WinMM._midiOutShortMsg;
                handle = (IntPtr)WinMM.handle;
            }
#endif
            MIDIClock.Start();
            while (!stopping)
            {
                int clock = (int)MIDIClock.Update();
                if (MIDIClock.paused || potato_mode)
                    Thread.Sleep(1);
                if (curr_tick > clock)
                {
                    while (currtg->tick > clock)
                    {
                        currtg--;
                        playedNotes -= currtg->notecount;
                    }
                    played = currtg->offset;
                    while (tevs[tempoidx].tick > clock && tempoidx > 0)
                        tempoidx--;
                    while (sysExes[sysexidx].tick > clock && sysexidx > 0)
                        sysexidx--;
                    Sound.readptr = (uint)(played & Sound.bufferMask);
                }
                while (currtg->tick <= clock)
                {
                    // accessing a field shouldnt be slow as hell mane js why
                    curr_tick = currtg->tick;
                    if (!skipping)
                    {
                        long offset = currtg->offset;
                        if (!singlethread && synthExists)
                        {
                            while (played < offset)
                            {
                                uint write = (uint)(played & Sound.bufferMask);
                                uint chunk = (uint)Math.Min(offset - played, Sound.bufferSize - write);
                                uint bytes = chunk * (uint)sizeof(uint24);
                                Unsafe.CopyBlockUnaligned(buffer + write, msgptr + played, bytes);
                                played += chunk;
                            }
                            Sound.writeptr = (uint)(played & Sound.bufferMask);
                        }
                        else
                        {
                            int velthreshlocal = Sound.velocitythreshold;
                            uint msg;
                            if (sendfn != null)
                            {
                                while (played < offset)
                                {
                                    msg = (uint)msgptr[played++].Value;
                                    if ((msg & 0xF0) == 0x90 && (msg >> 16) < velthreshlocal)
                                        continue;
                                    sendfn(msg);
                                }
                            }
#if WINDOWS
                            else if (sendfn2 != null)
                            {
                                while (played < offset)
                                {
                                    msg = (uint)msgptr[played++].Value;
                                    if ((msg & 0xF0) == 0x90 && (msg >> 16) < velthreshlocal)
                                        continue;
                                    sendfn2(handle, msg);
                                }
                            }
#endif
                        }
                    }
                    else
                        played = currtg->offset;
                    playedNotes += currtg->notecount;
                    UpdatePlaybackStats();
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
            //SubmitSysEx(gmreset);
            SubmitSysEx(rolandreset);
            MIDIClock.Reset();
            curr_tick = 0;
            laststatsupdate = Timer.Seconds();
            Sound.AllNotesOFF();
            Sound.KillAudioThread();
            Console.WriteLine("Playback finished...");
        }

        public static void SubmitSysEx(byte[] message)
        {
            if (Sound.currsynth == "Empty") return;
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

        // going from a loop to a frequently called function hopefully fishes out a bit more clock cycles for the synth since the async task stuff is gone 
        // call overhead should be minimal unless youre running super nut midis or something. this is literally just printing to console 60x per sec
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void UpdatePlaybackStats()
        {
            if (curr_tick >= MIDILoader.maxTick)
                stopping = true;

            if ((Timer.Seconds() - laststatsupdate) < 0.01666666d)
                return;

            double MIDIFps = 1.0d / MIDIClock.delta;
            npshistoryidx = (npshistoryidx + 1) % 60;
            notespersec -= npshistory[npshistoryidx];
            npshistory[npshistoryidx] = playedNotes - playedNotes2;
            notespersec += npshistory[npshistoryidx];
            playedNotes2 = playedNotes;
#if WINDOWS
                fpsStr = (MIDIFps > Double.MaxValue)? ">10,000,000" : $"{MIDIFps,10}:N0";
#elif LINUX
                fpsStr = $"{MIDIFps,10:N0}";
#endif
            // fps too volatile, idk what the word is for rapidly changing but you have to pad to make the stats string actually readable
            if (KDMAPI.hasvoice)
                Console.Write($"\rTick: {curr_tick:N0} / {MIDILoader.maxTick:N0} | Played Notes: {playedNotes:N0} / {MIDILoader.totalNotes:N0} ({notespersec:N0}/s) | MIDI Thread: @{fpsStr} fps | {KDMAPI._getActiveVoices()} voices    ");
            else
                Console.Write($"\rTick: {curr_tick:N0} / {MIDILoader.maxTick:N0} | Played Notes: {playedNotes:N0} / {MIDILoader.totalNotes:N0} ({notespersec:N0}/s) | MIDI Thread: @{fpsStr} fps    ");
            laststatsupdate = Timer.Seconds();
        }
    }
}