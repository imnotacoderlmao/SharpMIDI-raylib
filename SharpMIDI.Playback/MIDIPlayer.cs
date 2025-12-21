namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static int totalFrames = 0;
        public static long playedEvents = 0;
        public static bool stopping;
        public static void StartPlayback()
        {
            stopping = false;
            // this could be done better but whatever
            var synthev = MIDI.synthEvents;
            long* evptr = synthev.Pointer;
            long* evend = evptr + synthev.Length;
            long* currev = evptr;
            
            long[] tevs = MIDI.tempoEvents;
            int maxTick = MIDILoader.maxTick;
            MIDIClock.Start();
            fixed (long* t0 = tevs)
            {
                long* currtev = t0;
                long* tevend = t0 + tevs.Length;
                
                int evPos = (int)(*currev >> 32);
                int tevPos = (int)(*currtev >> 32);
                while (!stopping)
                {
                    int localclock = (int)MIDIClock.Update();
                    while (true)
                    {
                        if(evPos > localclock && tevPos > localclock) 
                            break;
                        if (evPos <= tevPos)
                        {
                            Sound.Submit((uint)*currev);
                            ++currev;
                            evPos = (int)(*currev >> 32);
                            if (currev >= evend) 
                            {
                                evPos = int.MaxValue;
                            }
                        }
                        else
                        {
                            // playack kills itself when a tempo event happens cause it was also reading the upper 32 bits lmao
                            MIDIClock.SubmitBPM(tevPos, *currtev & 0xFFFFFF);
                            ++currtev;
                            tevPos = (int)(*currtev >> 32);
                            if (currtev >= tevend) 
                            {
                                tevPos = int.MaxValue;
                            }
                        }
                    }
                    playedEvents = currev - evptr;
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