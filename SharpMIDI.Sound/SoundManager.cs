using System.Runtime.InteropServices;
namespace SharpMIDI
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct MIDIHDR
    {
        public byte* lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public MIDIHDR* lpNext;
        public IntPtr reserved;
        public uint dwOffset;

        public IntPtr dwReserved0;
        public IntPtr dwReserved1;
        public IntPtr dwReserved2;
        public IntPtr dwReserved3;
        public IntPtr dwReserved4;
        public IntPtr dwReserved5;
        public IntPtr dwReserved6;
        public IntPtr dwReserved7;
    }

    static unsafe class Sound
    {
        public static uint24* ringbuffer;
        public const uint bufferSize = 1 << 23; // 8 million somethuings
        public const uint bufferMask = bufferSize - 1;
        public static uint readptr = 0, writeptr = 0;
        static bool running = false;
        public static bool issynthinitiated = false;
        public static string currsynth = "";
        public static int velocitythreshold = 0;
        static Thread? audthread;
        public static string[] synths = ["Empty", "KDMAPI", "WinMM"];
        public static delegate* unmanaged[SuppressGCTransition]<uint, void> sendTo;
        //public static bool InitSynth(int synth)
        public static bool InitSynth(string synth, string WinMMDevice)
        {
            Close();
            AllocateEvBuffer();
            switch (synth)
            {
                case "KDMAPI":
                    try
                    {
                        KDMAPI.Load();
                        KDMAPI._initializeKDMAPIStream();
                        sendTo = KDMAPI._sendDirectData;
                        currsynth = synth;
                        issynthinitiated = true;
                        return issynthinitiated;
                    }
                    catch (DllNotFoundException)
                    {
                        Console.WriteLine($"{synth} is not available.");
                        MIDILoader.loadstatus = $"{synth} is not available.";
                        issynthinitiated = false;
                        return issynthinitiated;
                    }
#if WINDOWS
                case "WinMM":
                    WinMM.InitializeFunctionPointer();
                    currsynth = synth;
                    (bool, string, string, IntPtr?, MidiOutCaps?) result = WinMM.Setup(WinMMDevice);
                    if (!result.Item1)
                    {
                        Console.WriteLine(result.Item3);
                        issynthinitiated = false;
                        return issynthinitiated;
                    }
                    else
                    {
                        Console.WriteLine("loading from winmm.dll");
                        WinMM.handle = result.Item4;
                        WinMM.lastWinMMDevice = WinMMDevice;
                        issynthinitiated = true;
                        return issynthinitiated;
                    }
#endif
                default:
                    Close();
                    return issynthinitiated;
            }
        }

        static void AllocateEvBuffer()
        {
            ringbuffer = (uint24*)NativeMemory.AllocZeroed((nuint)(bufferSize * sizeof(uint24)));
        }
        public static void StartAudioThread()
        {
            if (running) return;
            running = true;
            audthread = new Thread(AudioThread)
            {
                IsBackground = true
            };
            audthread.Start();
        }

        static void AudioThread()
        {
            uint24* buffer = ringbuffer;
            var sendfn = sendTo;
#if WINDOWS
            if (currsynth == "WinMM")
            {
                var sendfn2 = WinMM._midiOutShortMsg;
                IntPtr handle = (IntPtr)WinMM.handle;
                while (running)
                {
                    while(readptr != writeptr)
                    {
                        uint val = (uint)buffer[readptr].Value;
                        readptr = (readptr + 1) & bufferMask;
                        if ((msg & 0xF0) <= 0x90 && (msg >> 16) < velthreshlocal)
                            continue;
                        sendfn2(handle, val);
                    }
                }
                return;
            }
#endif
            while (running)
            {
                int velthreshlocal = velocitythreshold;
                while (readptr != writeptr)
                {
                    uint msg = (uint)buffer[readptr].Value;
                    readptr = (readptr + 1) & bufferMask;
                    if ((msg & 0xF0) == 0x90 && (msg >> 16) < velthreshlocal)
                        continue;
                    sendfn(msg);
                }
            }
        }

        public static void KillAudioThread()
        {
            running = false;
            audthread?.Join(100);
            NativeMemory.Clear(ringbuffer, (nuint)(bufferSize * sizeof(uint24)));
            readptr = 0;
            writeptr = 0;
        }

        public static void AllNotesOFF()
        {
            if (!issynthinitiated) return;

#if WINDOWS
            if (currsynth == "WinMM")
            {
                for (int channel = 0; channel < 16; channel++)
                    WinMM._midiOutShortMsg((IntPtr)WinMM.handle, (uint)(0xB0 | channel) | (0x7B << 8));
                return;
            }
#endif

            for (int channel = 0; channel < 16; channel++)
                sendTo((uint)(0xB0 | channel) | (0x7B << 8));
        }


        public static void Close()
        {
            if (issynthinitiated)
            {
                issynthinitiated = false;
                switch (currsynth)
                {
                    case "KDMAPI":
                        KDMAPI._terminateKDMAPIStream();
                        break;
#if WINDOWS
                    case "WinMM":
                        WinMM.midiOutClose((IntPtr)WinMM.handle);
                        break;
#endif
                }
                currsynth = "Empty";
            }
        }
    }
}