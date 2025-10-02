using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Threading;
namespace SharpMIDI
{
    unsafe class Sound
    {
        private static int engine = 0;
        public static long totalEvents = 0;
        static string lastWinMMDevice = "";
        public static bool isrunning = false;
        private const int BufferSize = 131072;
        private static readonly uint[] buffer = new uint[BufferSize];
        private static int head = 0, tail = 0; // r/w index
        private static Thread? worker;
        private static IntPtr? handle;
        //static System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        public static delegate*<uint,uint> sendTo;
        static uint stWinMM(uint ev) => WinMM.midiOutShortMsg((IntPtr)handle, ev);
        public static bool Init(int synth, string winMMdev)
        {
            Close();
            switch (synth)
            {
                case 1:
                    bool KDMAPIAvailable = false;
                    try { KDMAPIAvailable = KDMAPI.IsKDMAPIAvailable(); } catch (DllNotFoundException) { }
                    if (KDMAPIAvailable)
                    {
                        int loaded = KDMAPI.InitializeKDMAPIStream();
                        if (loaded == 1)
                        {
                            engine = 1;
                            sendTo = &KDMAPI.SendDirectData;
                            StartAudioThread();
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
                        sendTo = &stWinMM;
                        handle = result.Item4;
                        lastWinMMDevice = winMMdev;
                        StartAudioThread();
                        return true;
                    }
                case 3:
                    bool XSynthAvailable = false;
                    try { XSynthAvailable = XSynth.IsKDMAPIAvailable(); } catch (DllNotFoundException) { }
                    if (XSynthAvailable)
                    {
                        int loaded = XSynth.InitializeKDMAPIStream();
                        if (loaded == 1)
                        {
                            engine = 3;
                            sendTo = &XSynth.SendDirectData;
                            StartAudioThread();
                            return true;
                        }
                        else { MessageBox.Show("KDMAPI is not available."); return false; }
                    }
                    else { return false; }
                default:
                    return false;
            }
        }
        
        private static void StartAudioThread()
        {
            isrunning = true;
            worker = new Thread(AudioThread)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            worker.Start();
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AudioThread()
        {
            int localTail = tail;
            while (isrunning)
            {
                if (localTail != head)
                {
                    sendTo(buffer[localTail & (BufferSize - 1)]);
                    localTail++;
                    tail = localTail;
                }
            }
        }

        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Submit(uint ev)
        {
            int currentHead = head;
            // Check if buffer is full (simplified)
            if (((currentHead + 1) & (BufferSize - 1)) == (tail & (BufferSize - 1))) return;
            buffer[currentHead & (BufferSize - 1)] = ev;
            head = currentHead + 1; // Single write
        }

        public static void Close()
        {
            isrunning = false;
            worker?.Join();
            worker = null;
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
