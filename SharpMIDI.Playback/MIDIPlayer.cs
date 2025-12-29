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
            long* currev = evptr;
            long[] tevs = MIDI.tempoEvents;
            int maxTick = MIDILoader.maxTick;
            uint localwrite = Sound.write;
            uint localbuffermask = Sound.bufferMask;
            uint* buffer = Sound.ringbuffer;
            fixed (long* t0 = tevs)
            {
                long* currtev = t0;
                MIDIClock.Start();
                while (!stopping)
                {
                    int localclock = (int)MIDIClock.Update();
                    bool skipping = MIDIClock.skipping;
                    while ((int)(*currev >> 32) <= localclock)
                    {
                        long ev = *currev++;
                        if (skipping) continue;
                        buffer[localwrite] = (uint)ev;
                        localwrite = (localwrite + 1) & localbuffermask;
                    }
                    while ((int)(*currtev >> 32) <= localclock)
                    {
                        long tev = *currtev++;
                        MIDIClock.SubmitBPM((int)(tev >> 32), tev & 0xFFFFFF);
                    }
                    Sound.write = localwrite;
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