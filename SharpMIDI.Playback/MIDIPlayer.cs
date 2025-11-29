namespace SharpMIDI
{
    static unsafe class MIDIPlayer
    {
        public static int ppq = 0, totalFrames = 0, clock = 0;
        public static bool stopping = false , paused = false;
        public static void StartPlayback()
        {
            stopping = false;
            var ev = MIDI.synthEvents;
            var tev = MIDI.tempos;
            int localclock = 0, tempoProgress = 0, eventProgress = 0, evcount = ev.Length, tevcount = tev.Count, maxTick = MIDILoader.maxTick;
            MIDIClock.Start();
            fixed (SynthEvent* p0 = ev)
            {
                SynthEvent* evs = p0 + eventProgress;
                SynthEvent* end = p0 + evcount;
                while (!stopping)
                {
                    ++totalFrames;
                    localclock = (int)MIDIClock.GetTick();
                    clock = localclock;
                    while (evs < end)
                    {
                        int pos = evs->pos;
                        if (pos > localclock) break;
                        uint val = (uint)evs->val;
                        Sound.Submit(val);
                        ++evs;
                        ++eventProgress;
                    }
                    while (tempoProgress < tevcount)
                    {
                        int pos = tev[tempoProgress].pos;
                        if (pos > localclock) break;
                        int tempo = tev[tempoProgress].tempo;
                        MIDIClock.SubmitBPM(pos, tempo);
                        ++tempoProgress;
                    }
                    Sound.playedEvents = eventProgress;
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