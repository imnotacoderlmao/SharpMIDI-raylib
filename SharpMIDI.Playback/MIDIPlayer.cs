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
            var synthev = MIDI.synthEvents;
            SynthEvent* evptr = synthev.Pointer;
            SynthEvent* currev = evptr;
            SynthEvent* evend = evptr + synthev.Length;
            Tempo[] tevs = MIDI.tempoEvents;
            uint maxTick = (uint)MIDILoader.maxTick;
            uint clock = 0;
            //uint localbuffermask = Sound.bufferMask;
            uint24* buffer = Sound.ringbuffer;
            ushort writeptr = Sound.write;
            fixed (Tempo* t0 = tevs)
            {
                Tempo* currtev = t0;
                MIDIClock.Start();
                while (!stopping)
                {
                    clock = (uint)MIDIClock.Update();
                    if (!MIDIClock.skipping) 
                    {
                        while (currev->tick <= clock)
                        {
                            buffer[writeptr] = currev++->message;
                            writeptr++;
                            Sound.write = writeptr;
                        }
                    }
                    else 
                    {
                        SynthEvent* left = currev;
                        SynthEvent* right = evend;
                        while (right - left > 16)
                        {
                            SynthEvent* mid = left + ((right - left) >> 1);
                            if (mid->tick <= clock)
                                left = mid + 1;
                            else
                                right = mid;
                        }
                        currev = left;
                    }
                    while (currtev->tick <= clock)
                    {
                        MIDIClock.SubmitBPM(currtev->tick, currtev->tempo);
                        currtev++;
                    }
                    playedEvents = currev - evptr;
                    totalFrames++;
                    if (clock > maxTick) stopping = true;
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