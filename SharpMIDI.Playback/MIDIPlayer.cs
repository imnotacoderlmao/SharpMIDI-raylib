namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static int clock = 0, totalFrames = 0;
        public static long playedEvents = 0;
        public static bool stopping;
        public static void StartPlayback()
        {
            stopping = false;
            // this could be done better but whatever
            var synthev = MIDI.synthEvents;
            long* evend = synthev.Pointer + synthev.Length;
            long* evs = synthev.Pointer;
            long* currev = evs;
            
            long[] tevs = MIDI.tempoEvents;
            int maxTick = MIDILoader.maxTick;
            MIDIClock.Start();
            fixed (long* t0 = tevs)
            {
                long* currtev = t0;
                long* tevend = t0 + tevs.Length;
                
                int evPos = (int)(*currev >> 32);
                int tevPos = (int)(*currev >> 32);
                while (!stopping)
                {
                    int localclock = (int)MIDIClock.GetTick();
                    while (true)
                    {
                        if(evPos > localclock && tevPos > localclock) 
                            break;
                        if (evPos <= tevPos)
                        {
                            Sound.Submit((uint)*currev);
                            ++currev;
                            if (currev < evend) 
                            {
                                evPos = (int)(*currev >> 32);
                            }
                            else
                            {
                                evPos = int.MaxValue;
                            } 
                        }
                        else
                        {
                            // playack kills itself when a tempo event happens cause it was also reading the upper 32 bits lmao
                            MIDIClock.SubmitBPM(tevPos, *currtev & 0xFFFFFF);
                            ++currtev;
                            if (currtev < tevend) 
                            {
                                tevPos = (int)(*currtev >> 32);
                            }
                            else 
                            {
                                tevPos = int.MaxValue;
                            }  
                        }
                    }
                    // for the sake of less writes to ui stuff
                    if (localclock != clock)
                    {
                        playedEvents = currev - evs;
                        clock = localclock;
                    }
                    ++totalFrames;
                    if (localclock > maxTick) stopping = true;
                }
            }
            MIDIClock.Reset();
            Console.WriteLine("Playback finished...");
            Starter.form.button4.Enabled = true;
            Starter.form.button5.Enabled = false;
            Starter.form.button6.Enabled = false;
        }
    }
}