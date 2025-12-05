using System.Runtime.CompilerServices;

namespace SharpMIDI
{
    static unsafe class Sound
    {
        public static uint[] ring;      // buffer
        public static int head;         // write index
        public static int tail;         // read index
        public static int mask;         // bufferSize - 1
        static bool running = false;
        static Thread? audthread;
        
        private static int engine = 0;
        static string lastWinMMDevice = "";
        private static IntPtr? handle;
        public static delegate* unmanaged[SuppressGCTransition]<uint,uint> sendTo;
        public static bool Init(int synth, string winMMdev)
        {
            Close();
            AllocateEvBuffer();
            StartAudioThread();
            switch (synth)
            {
                case 1:
                    bool KDMAPIAvailable = false;
                    try { KDMAPIAvailable = KDMAPI.IsKDMAPIAvailable(); } catch (DllNotFoundException) { }
                    if (KDMAPIAvailable)
                    {
                        KDMAPI.InitializeFunctionPointer();
                        int loaded = KDMAPI.InitializeKDMAPIStream();
                        if (loaded == 1)
                        {
                            engine = 1;
                            sendTo = KDMAPI._sendDirectData;
                            return true;
                        }
                        else { return false; }
                    }
                    else { MessageBox.Show("KDMAPI is not available."); return false; }
                case 2:
                    (bool, string, string, IntPtr?, MidiOutCaps?) result = WinMM.Setup(winMMdev);
                    if (!result.Item1)
                    {
                        MessageBox.Show(result.Item3);
                        return false;
                    }
                    else
                    {
                        engine = 2;
                        sendTo = WinMM._midiOutShortMsg;
                        handle = result.Item4;
                        lastWinMMDevice = winMMdev;
                        return true;
                    }
                case 3:
                    bool XSynthAvailable = false;
                    try { XSynthAvailable = XSynth.IsKDMAPIAvailable(); } catch (DllNotFoundException) { }
                    if (XSynthAvailable)
                    {
                        XSynth.InitializeFunctionPointer();
                        int loaded = XSynth.InitializeKDMAPIStream();
                        if (loaded == 1)
                        {
                            engine = 3;
                            sendTo = XSynth._sendDirectData;
                            return true;
                        }
                        else { MessageBox.Show("KDMAPI is not available."); return false; }
                    }
                    else { return false; }
                default:
                    return false;
            }
        }

        static void AllocateEvBuffer()
        {    
            ring = new uint[65536];
            mask = 65536 - 1;
            head = 0;
            tail = 0;
        }
        
        public static void Submit(uint ev)
        {
            int h = head;
            ring[h] = ev;
            head = (h + 1) & mask; // wrap
        }

        static void StartAudioThread()
        {
            if (running) return;
            running = true;
            audthread = new Thread(AudioThread)
            {
                IsBackground = true,
            };
            audthread.Start();
        }

        static void AudioThread()
        {
            var buffer = ring;
            ref uint start = ref buffer[0];
            int m = mask;
            int t = tail;
            int hLocal = head;     
            SpinWait sw = new SpinWait();
        
            while (running)
            {
                if (t == hLocal)
                {
                    sw.SpinOnce();
                    hLocal = head;
                    continue;
                }
                _ = Unsafe.Add(ref start, (t + 16) & m);
                uint ev = buffer[t];
                sendTo(ev);
                t = (t + 1) & m;
                tail = t;
            }
        }
        
        public static void Close()
        {
            running = false;
            audthread?.Join(100);
            switch(engine){
                case 1:
                    KDMAPI.TerminateKDMAPIStream();
                    return;
                case 2:
                    if(handle!=null){
                        WinMM.midiOutClose((IntPtr)handle);
                    }
                    return;
                case 3:
                    XSynth.TerminateKDMAPIStream();
                    return;
            }
        }
    }
}
