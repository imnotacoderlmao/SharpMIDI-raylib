#pragma warning disable 8618
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace SharpMIDI
{
    public static unsafe class GLNoteRenderer
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RenderNote
        {
            public int StartTick;
            public uint PackedData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct KeyHeader
        {
            public int Head;
            public int Tail;
        }

        private const string LineVertSrc =
@"#version 330 core
uniform vec3 uMetrics;
uniform int uViewStart;
uniform int uViewEnd;
uniform int uTboStart;
uniform int uTboMask;
uniform usamplerBuffer uNotesTbo;
uniform sampler2D uPalette;
flat out vec4 vColor;
void main() {
    int physIdx = (gl_InstanceID + uTboStart) & uTboMask;
    uvec2 notedata = texelFetch(uNotesTbo, physIdx).rg;
    int aStartTick = int(notedata.r & 0x7FFFFFFFu);
    bool isNoteOn = bool(notedata.r & 0x80000000u);
    uint aPackedData = notedata.g;
    float dur = isNoteOn? float(uViewEnd - aStartTick) : float(aPackedData & 0xFFFFu);
    uint isEnd = uint(gl_VertexID) & 1u;
    uint isTop = uint(gl_VertexID >> 1) & 1u;
    float startX = float(aStartTick - uViewStart) * uMetrics.x - 1.0;
    float endX = startX + dur * uMetrics.x;
    float x = bool(isEnd)? endX : startX;
    float y = uMetrics.y + float(((aPackedData >> 16) & 0xFFu) + isTop) * uMetrics.z;
    float z = dur / 65536.0;
    vColor = texelFetch(uPalette, ivec2(int(aPackedData >> 24), 0), 0);
    gl_Position = vec4(x, y, z, 1.0);
}";

        private const string LineFragSrc =
@"#version 330 core
flat in vec4 vColor;
out vec4 fragColor;
void main() {
   fragColor = vColor;
}";

        private static GL Gl;

        private static uint _lineShader;
        private static int _uMetrics, _uViewStart, _uViewEnd;
        private static int _uNotesTbo, _uTboStart, _uTboMask, _uPalette;
        private static uint _vao, _tboBuffer, _tboTex, _paletteTex;

        private static RenderNote* _ring;
        private static int* _nextPtrs;

        private static int _ringCap = 1 << 23;
        private static int _deferred_ringCap = -1;
        private static int _mask;
        
        private static int _head = 1;
        private static int _tail = 1;

        private static KeyHeader* _keyHeaders;
        private const int TOTAL_KEYS = 128 * 16;
        private const int COLOR_SIZE  = 256;

        private static int _lookaheadTicks = 4000;
        private static float _pixelsPerTick;
        private static int _lastWindowTicks = -1;
        private static int _lastSweepEnd = -1;
        private readonly static byte[] paletteData = new byte[COLOR_SIZE * 3];
        private static bool _paletteUploadPending = false;
        private static bool _isInitialized;

        public static int WindowTicks = 2000;
        public static int RingCap;
        public static int NotesDrawnLastFrame;

        public static void Initialize()
        {
            if (_isInitialized) return;
            
            Gl = GL.GetApi(NativeGLBindingsContext.GetProcAddress);

            _lineShader  = BuildShader(LineVertSrc, LineFragSrc);
            _uMetrics    = Gl.GetUniformLocation(_lineShader, "uMetrics");
            _uViewStart  = Gl.GetUniformLocation(_lineShader, "uViewStart");
            _uViewEnd    = Gl.GetUniformLocation(_lineShader, "uViewEnd");
            _uNotesTbo   = Gl.GetUniformLocation(_lineShader, "uNotesTbo");
            _uTboStart   = Gl.GetUniformLocation(_lineShader, "uTboStart");
            _uTboMask    = Gl.GetUniformLocation(_lineShader, "uTboMask");
            _uPalette    = Gl.GetUniformLocation(_lineShader, "uPalette");

            Gl.UseProgram(_lineShader);
            Gl.Uniform1(_uPalette, 0);
            Gl.Uniform1(_uNotesTbo, 1);
            Gl.UseProgram(0);

            _vao = Gl.GenVertexArray();
            _tboTex = Gl.GenTexture();
            _tboBuffer = Gl.GenBuffer();
            _paletteTex = Gl.GenTexture();

            _keyHeaders = (KeyHeader*)NativeMemory.AlignedAlloc(TOTAL_KEYS * (nuint)sizeof(KeyHeader), 64);
                        
            AllocTbo(_ringCap);
            _isInitialized = true;
        }
        
        private static void AllocTbo(int cap)
        {
            _mask = cap - 1;

            if (_ring != null)
            {
                Gl.BindBuffer(BufferTargetARB.TextureBuffer, _tboBuffer);
                Gl.UnmapBuffer(BufferTargetARB.TextureBuffer);
                _ring = null;
            }

            if (_nextPtrs != null) 
                NativeMemory.AlignedFree(_nextPtrs);
            _nextPtrs = (int*)NativeMemory.AlignedAlloc((nuint)(cap * sizeof(int)), 64);

            Gl.DeleteBuffer(_tboBuffer);
            _tboBuffer = Gl.GenBuffer();

            Gl.BindBuffer(BufferTargetARB.TextureBuffer, _tboBuffer);
            
            nuint totalBytes = (nuint)(cap * sizeof(RenderNote));
            BufferStorageMask storageFlags = BufferStorageMask.MapReadBit | BufferStorageMask.MapWriteBit | BufferStorageMask.MapPersistentBit | BufferStorageMask.MapCoherentBit;
            Gl.BufferStorage(GLEnum.TextureBuffer, totalBytes, null, (uint)storageFlags);

            MapBufferAccessMask accessFlags = MapBufferAccessMask.ReadBit | MapBufferAccessMask.WriteBit | MapBufferAccessMask.PersistentBit | MapBufferAccessMask.CoherentBit;
            _ring = (RenderNote*)Gl.MapBufferRange(BufferTargetARB.TextureBuffer, 0, totalBytes, (uint)accessFlags);

            Gl.BindTexture(TextureTarget.TextureBuffer, _tboTex);
            Gl.TexBuffer(GLEnum.TextureBuffer, GLEnum.RG32ui, _tboBuffer);
            Gl.BindTexture(TextureTarget.TextureBuffer, 0);
            Gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);

            _ringCap = cap;
        }

        public static void InitializeForMIDI()
        {
            _isInitialized = false;
            NativeMemory.Clear(_keyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
            _head = 1;
            _tail = 1;
            _lastSweepEnd = -1;
            _lastWindowTicks = -1;
            _paletteUploadPending = true;
            _isInitialized = true;
        }

        public static void ResetForUnload()
        {
            _isInitialized = false;
            _head = 1;
            _tail = 1;
            _lastSweepEnd = -1;
            _lastWindowTicks = -1;
            NativeMemory.Clear(_nextPtrs, (nuint)_ringCap * sizeof(int));
            if (_ringCap != 1 << 23) 
                _deferred_ringCap = 1 << 23;
        }

        public static void Dispose()
        {
            _isInitialized = false;
            if (_keyHeaders != null) { NativeMemory.AlignedFree(_keyHeaders); _keyHeaders = null; }
            if (_nextPtrs != null) { NativeMemory.AlignedFree(_nextPtrs); _nextPtrs = null; }

            if (_tboBuffer != 0)
            {
                Gl.BindBuffer(BufferTargetARB.TextureBuffer, _tboBuffer);
                if (_ring != null) { Gl.UnmapBuffer(BufferTargetARB.TextureBuffer); _ring = null; }
                Gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
                Gl.DeleteBuffer(_tboBuffer);
                _tboBuffer = 0;
            }
            if (_tboTex != 0) { Gl.DeleteTexture(_tboTex); _tboTex = 0; }
            if (_paletteTex != 0) { Gl.DeleteTexture(_paletteTex); _paletteTex = 0; }
            if (_vao != 0) { Gl.DeleteVertexArray(_vao); _vao = 0; }
            if (_lineShader != 0) { Gl.DeleteProgram(_lineShader); _lineShader = 0; }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int tick, int pad)
        {
            if (_paletteUploadPending)
            {
                for (int i = 0; i < COLOR_SIZE; i++)
                {
                    uint c = (uint)Random.Shared.Next(0x808080, 0x1000000);
                    paletteData[i * 3 + 0] = (byte)((c >> 16) & 0xFF);
                    paletteData[i * 3 + 1] = (byte)((c >>  8) & 0xFF);
                    paletteData[i * 3 + 2] = (byte)( c        & 0xFF);
                }
                Gl.BindTexture(TextureTarget.Texture2D, _paletteTex);
                fixed (byte* ptr = paletteData)
                {
                    Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8, COLOR_SIZE, 1, 0, PixelFormat.Rgb, PixelType.UnsignedByte, ptr);
                }
                Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                Gl.BindTexture(TextureTarget.Texture2D, 0);
                _paletteUploadPending = false;
            }

            if (_deferred_ringCap > 0) 
            { 
                ResizeRing(_deferred_ringCap); 
                _deferred_ringCap = -1; 
            }

            if (!MIDILoader.midiLoaded) 
                return;

            int maxtick = MIDILoader.maxTick - 1;
            int half = WindowTicks >> 1;
            int viewStart = Math.Clamp(tick - half, 0, maxtick);
            int viewEnd = Math.Clamp(tick + half, 0, maxtick);

            if (WindowTicks != _lastWindowTicks)
            {
                _pixelsPerTick = 2.0f / WindowTicks;
                _lastWindowTicks = WindowTicks;
                _lookaheadTicks = Math.Min(WindowTicks / 2, 2000);
            }

            int sweepEnd = Math.Clamp(viewEnd + _lookaheadTicks, 0, maxtick);
            bool incremental = _lastSweepEnd >= 0 && sweepEnd >= _lastSweepEnd && sweepEnd - _lastSweepEnd < WindowTicks;

            if (!incremental)
            {
                _head = 1;
                _tail = 1;
                NativeMemory.Clear(_keyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
                SweepRange(Math.Max(0, viewStart - WindowTicks), sweepEnd);
            }
            else
            {
                SweepRange(_lastSweepEnd + 1, sweepEnd);
            }

            _lastSweepEnd = sweepEnd;
            AdvanceTail(viewStart);

            Raylib_cs.Rlgl.DrawRenderBatchActive();

            int count = _head - _tail;
            NotesDrawnLastFrame = count;
            RingCap = _ringCap;

            if (count > 0)
            {
                float yBottom = -1.0f + 2.0f * pad / screenHeight;
                float yTop = 1.0f - 2.0f * pad / screenHeight;
                float yStep = (yTop - yBottom) / 128.0f;

                Gl.Viewport(0, 0, (uint)screenWidth, (uint)screenHeight);
                Gl.Enable(EnableCap.DepthTest);
                Gl.DepthFunc(DepthFunction.Less);
                Gl.UseProgram(_lineShader);

                Gl.Uniform3(_uMetrics, _pixelsPerTick, yBottom, yStep);
                Gl.Uniform1(_uViewStart, viewStart);
                Gl.Uniform1(_uViewEnd, viewEnd);
                
                Gl.Uniform1(_uTboStart,  _tail & _mask);
                Gl.Uniform1(_uTboMask,   _mask);

                Gl.ActiveTexture(TextureUnit.Texture0);
                Gl.BindTexture(TextureTarget.Texture2D, _paletteTex);
                Gl.ActiveTexture(TextureUnit.Texture1);
                Gl.BindTexture(TextureTarget.TextureBuffer, _tboTex);

                Gl.BindVertexArray(_vao);
                Gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)count);
                Gl.BindVertexArray(0);

                Gl.ActiveTexture(TextureUnit.Texture1);
                Gl.BindTexture(TextureTarget.TextureBuffer, 0);
                Gl.ActiveTexture(TextureUnit.Texture0);
                Gl.BindTexture(TextureTarget.Texture2D, 0);
                Gl.UseProgram(0);
                Gl.Disable(EnableCap.DepthTest);
            }
            int linepos = (int)Math.Min(tick * _pixelsPerTick * screenWidth / 2, screenWidth / 2);
            Raylib_cs.Raylib.DrawLine(linepos, 0, linepos, screenHeight, Raylib_cs.Color.Red);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SweepRange(int fromTick, int toTick)
        {
            RenderNote* ringLocal = _ring;
            int* nextPtrsLocal = _nextPtrs;
            KeyHeader* keyheader = _keyHeaders;

            BigArray<TickGroup> groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = SynthEvent.track != null ? SynthEvent.track.Pointer : null;
            bool useTrack = tracks != null;

            int limit = Math.Min(toTick, (int)groups.Length - 2);
            TickGroup* tickgroups = groups.Pointer;
            int headLocal = _head;
            int maskLocal = _mask;
            int tailLocal = _tail;
            int cap = _ringCap;

            long currentOffset = tickgroups[fromTick].offset;
            int tick = fromTick;

            while (tick <= limit)
            {
                long nextOffset = tickgroups[tick + 1].offset;
                byte* ev = messages + currentOffset * 3;
                byte* evEnd = messages + nextOffset * 3;
                long totalEventsInTick = nextOffset - currentOffset;
                
                if (totalEventsInTick > 0)
                {
                    while (headLocal - tailLocal + totalEventsInTick >= cap)
                    {
                        _tail = tailLocal;
                        _head = headLocal;
                        ResizeRing(cap * 2);
                        cap = _ringCap;
                        maskLocal = _mask;
                        ringLocal = _ring;
                        nextPtrsLocal = _nextPtrs;
                    }
                }

                while (ev < evEnd)
                {
                    byte status = ev[0];
                    if ((status & 0xE0) != 0x80)
                    {
                        ev += 3;
                        continue;
                    }

                    byte channel = (byte)(status & 0x0F);
                    byte key = ev[1];
                    KeyHeader* header = keyheader + ((channel << 7) | key);

                    if ((status & 0x10) == 0) // NoteOff
                    {
                        int oldest = header->Head;
                        if (oldest != 0)
                        {
                            if (oldest >= headLocal - cap)
                            {
                                int physIdx = oldest & maskLocal;

                                header->Head = nextPtrsLocal[physIdx];
                                if (header->Head == 0) 
                                    header->Tail = 0;

                                RenderNote note = ringLocal[physIdx];
                                int startTick = note.StartTick & 0x7FFFFFFF;
                                uint packedDur = Math.Min((uint)(tick - startTick), ushort.MaxValue);
                                ringLocal[physIdx] = new RenderNote
                                {
                                    StartTick  = startTick,
                                    PackedData = (note.PackedData & 0xFFFF0000u) | packedDur
                                };
                            }
                            else
                            {
                                // reset head/tail if a note has been force culled to prevent reading garbage
                                header->Head = 0;
                                header->Tail = 0;
                            }
                        }
                    }
                    else
                    {
                        int absId = headLocal++;
                        int physIdx = absId & maskLocal;
                        uint colorIdx = useTrack ? (uint)(tracks[(ev - messages) / 3] + channel) & 0xFFu : channel;

                        // terminate new node
                        nextPtrsLocal[physIdx] = 0;
                        
                        if (header->Tail == 0)
                            header->Head = absId;
                        else
                            nextPtrsLocal[header->Tail & maskLocal] = absId;
                        
                        header->Tail = absId;

                        // i was gonna wish that using the sign bit as a noteon/off flag was gonna save memory by removing duration 
                        // but then i realized that you also need to store the end tick if you do that. oh well, simpler shader logic i guess
                        // even then i dont have to use a dummy value for duration! (probably slower this way though lmao)
                        ringLocal[physIdx] = new RenderNote
                        {
                            StartTick = (int)(tick | 0x80000000),
                            PackedData = ((uint)key << 16) | (colorIdx << 24)
                        };
                    }
                    ev += 3;
                }
                currentOffset = nextOffset;
                tick++;
            }
            _tail = tailLocal;
            _head = headLocal;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AdvanceTail(int viewStart)
        {
            int safeTail = _head - _ringCap;
            if (_tail < safeTail) 
                _tail = safeTail;

            int forcecullthresh = Math.Min(_lookaheadTicks * 2, ushort.MaxValue);
            bool forceCull = NotesDrawnLastFrame > 262144;
            int forceCullBefore = viewStart - forcecullthresh;
            
            RenderNote* ring = _ring;
            int maskLocal = _mask;
            int headLocal = _head;
            int tailLocal = _tail;

            while (tailLocal < headLocal)
            {
                int physIdx = tailLocal & maskLocal;
                RenderNote note = ring[physIdx];
                bool isopen = note.StartTick < 0;
                int startTick = (int)(note.StartTick & 0x7FFFFFFFu);
                int endTick = (int)(startTick + (note.PackedData & 0xFFFFu));
                
                if ((!isopen && endTick < viewStart) || (forceCull && startTick < forceCullBefore))
                    tailLocal++;
                else
                    break;
            }
            _tail = tailLocal;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ResizeRing(int newCap)
        {
            if (newCap < 0) 
                return;
            int newMask = newCap - 1;
            
            nuint totalBytes = (nuint)newCap * (nuint)sizeof(RenderNote);
            int* newNext = (int*)NativeMemory.AlignedAlloc((nuint)newCap * sizeof(int), 64);

            uint newBuffer = Gl.GenBuffer();
            Gl.BindBuffer(BufferTargetARB.TextureBuffer, newBuffer);
            
            BufferStorageMask storageFlags = BufferStorageMask.MapReadBit | BufferStorageMask.MapWriteBit | BufferStorageMask.MapPersistentBit | BufferStorageMask.MapCoherentBit;
            Gl.BufferStorage(GLEnum.TextureBuffer, totalBytes, null, (uint)storageFlags);

            MapBufferAccessMask accessFlags = MapBufferAccessMask.ReadBit | MapBufferAccessMask.WriteBit | MapBufferAccessMask.PersistentBit | MapBufferAccessMask.CoherentBit;
            RenderNote* newRing = (RenderNote*)Gl.MapBufferRange(BufferTargetARB.TextureBuffer, 0, totalBytes, (uint)accessFlags);

            for (int absId = _tail; absId < _head; absId++)
            {
                int oldIdx = absId & _mask;
                int newIdx = absId & newMask;
                newRing[newIdx] = _ring[oldIdx];
                newNext[newIdx] = _nextPtrs[oldIdx];
            }

            if (_nextPtrs != null) NativeMemory.AlignedFree(_nextPtrs);
            _nextPtrs = newNext;

            if (_ring != null)
            {
                Gl.BindBuffer(BufferTargetARB.TextureBuffer, _tboBuffer);
                Gl.UnmapBuffer(BufferTargetARB.TextureBuffer);
            }
            Gl.DeleteBuffer(_tboBuffer);

            _tboBuffer = newBuffer;
            _ring = newRing;
            _mask = newMask;
            _ringCap = newCap;

            Gl.BindTexture(TextureTarget.TextureBuffer, _tboTex);
            Gl.TexBuffer(TextureTarget.TextureBuffer, GLEnum.RG32ui, _tboBuffer);
            Gl.BindTexture(TextureTarget.TextureBuffer, 0);
            Gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
        }

        private static uint BuildShader(string vert, string frag)
        {
            uint vertex = CompileStage(ShaderType.VertexShader, vert);
            uint fragment = CompileStage(ShaderType.FragmentShader, frag);
            uint program = Gl.CreateProgram();
            Gl.AttachShader(program, vertex);
            Gl.AttachShader(program, fragment);
            Gl.LinkProgram(program);
            Gl.GetProgram(program, GLEnum.LinkStatus, out int ok);
            if (ok == 0) 
                throw new Exception("Shader link:\n" + Gl.GetProgramInfoLog(program));
            Gl.DeleteShader(vertex);
            Gl.DeleteShader(fragment);
            return program;
        }

        private static uint CompileStage(ShaderType type, string src)
        {
            uint shader = Gl.CreateShader(type);
            Gl.ShaderSource(shader, src);
            Gl.CompileShader(shader);
            Gl.GetShader(shader, GLEnum.CompileStatus, out int ok);
            if (ok == 0) 
                throw new Exception($"{type}:\n" + Gl.GetShaderInfoLog(shader));
            return shader;
        }

        public class NativeGLBindingsContext
        {
            private static readonly nint s_glLibrary = LoadGL();
            private static nint LoadGL()
            {
                if (OperatingSystem.IsWindows()) 
                    return NativeLibrary.Load("opengl32.dll");
                if (OperatingSystem.IsLinux())
                    return NativeLibrary.TryLoad("libGL.so.1", out nint h) ? h : NativeLibrary.Load("libGL.so");
                if (OperatingSystem.IsMacOS())
                    return NativeLibrary.Load("/System/Library/Frameworks/OpenGL.framework/OpenGL");
                throw new PlatformNotSupportedException();
            }
            [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", ExactSpelling = true)]
            private static extern nint WglGetProcAddress(string name);
            public static nint GetProcAddress(string procName)
            {
                if (OperatingSystem.IsWindows())
                {
                    nint addr = WglGetProcAddress(procName);
                    if (addr is 0 or 1 or 2 or 3 or -1)
                        NativeLibrary.TryGetExport(s_glLibrary, procName, out addr);
                    return addr;
                }
                NativeLibrary.TryGetExport(s_glLibrary, procName, out nint unixAddr);
                return unixAddr;
            }
        }
    }
}