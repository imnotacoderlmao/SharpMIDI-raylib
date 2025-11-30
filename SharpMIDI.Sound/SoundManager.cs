using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SharpMIDI
{
    static unsafe class Sound
    {
        const int BufferBits = 16;
        const int BufferSize = 1 << BufferBits;
        const int BufferMask = BufferSize - 1;
        static uint* buf = null;
        static int head = 0, tail = 0;
        static bool running = false;
        static Thread? audthread;
        
        private static int engine = 0;
        public static long playedEvents = 0;
        static string lastWinMMDevice = "";
        private static IntPtr? handle;
        public static delegate*<uint,uint> sendTo;
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
                            sendTo = (delegate*<uint,uint>)KDMAPI._sendDirectData;
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
                        sendTo = (delegate*<uint,uint>)WinMM._midiOutShortMsg;
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
                            sendTo = (delegate*<uint,uint>)XSynth._sendDirectData;
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
            if (buf != null) return;
            buf = (uint*)Marshal.AllocHGlobal(BufferSize * sizeof(uint));
            for (int i = 0; i < BufferSize; i++) buf[i] = 0;
        }
        
        public static void Submit(uint ev)
        {
            int h = head;
            int next = (h + 1) & BufferMask;
            if (next == tail) return;
            buf[h] = ev;
            head = next;
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
            SpinWait spinWait = new SpinWait();
            while (running)
            {
                int t = tail;
                int h = head;
                if (t == h)
                {
                    spinWait.SpinOnce();
                    continue;
                }
                uint ev = buf[t];
                sendTo(ev);
                tail = (t + 1) & BufferMask;
            }
        }


        public static void Reload()
        {
            Close();
            switch (engine)
            {
                case 1:
                    KDMAPI.InitializeKDMAPIStream();
                    return;
                case 2:
                    (bool, string, string, IntPtr?, MidiOutCaps?) result = WinMM.Setup(lastWinMMDevice);
                    handle = result.Item4;
                    return;
                case 3:
                    XSynth.InitializeKDMAPIStream();
                    return;
            }
        }
        
        public static void Close()
        {
            running = false;
            if (audthread != null)
            {
                audthread.Join(100);
                audthread = null;
            }
            if (buf != null)
            {
                Marshal.FreeHGlobal((IntPtr)buf);
                buf = null;
            }
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
