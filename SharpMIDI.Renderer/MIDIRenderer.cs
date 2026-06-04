using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Core.Native;
using OpenTK;

namespace SharpMIDI
{
    public static unsafe class GLNoteRenderer
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GpuNote
        {
            public int StartTick;
            // bits 0-15 = duration, 16-23 = pitch, 24-31 = color index
            public uint PackedData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct KeyHeader
        {
            public fixed int NoteIdx[4];
            public byte Count;
        }

        private const string LineVertSrc =
            "#version 330 core\n"
          + "uniform vec4 uMetrics;\n"
          + "uniform int uViewStart;\n"
          + "uniform int uViewEnd;\n"
          + "uniform int uTboStart;\n"
          + "uniform int uTboMask;\n"
          + "uniform usamplerBuffer uNotesTbo;\n"
          + "uniform sampler2D uPalette;\n"
          + "flat out vec4 vColor;\n"
          + "void main() {\n"
          + "    int physIdx = (gl_InstanceID + uTboStart) & uTboMask;\n"
          + "    uvec2 nd = texelFetch(uNotesTbo, physIdx).rg;\n"
          + "    int aStartTick = int(nd.r);\n"
          + "    uint aPackedData = nd.g;\n"
          + "    uint rawDur = aPackedData & 0xFFFFu;\n"
          + "    float dur = mix(float(rawDur), float(max(uViewEnd - aStartTick, 0)), float(rawDur == 65535u));\n"
          + "    uint isEnd = uint(gl_VertexID) & 1u;\n"
          + "    uint isTop = (uint(gl_VertexID) >> 1) & 1u;\n"
          + "    float startX = float(aStartTick - uViewStart) * uMetrics.x - 1.0;\n"
          + "    float endX = startX + max(dur * uMetrics.x, uMetrics.y);\n"
          + "    float x = mix(startX, endX, float(isEnd));\n"
          + "    float y = uMetrics.z + float(((aPackedData >> 16) & 0xFFu) + isTop) * uMetrics.w;\n"
          + "    float z = min(float(rawDur), 65534.0) / 65535.0;\n"
          + "    vColor = texelFetch(uPalette, ivec2(int(aPackedData >> 24), 0), 0);\n"
          + "    gl_Position = vec4(x, y, z, 1.0);\n"
          + "}\n";

        private const string LineFragSrc =
            "#version 330 core\n"
          + "flat in vec4 vColor;\n"
          + "out vec4 fragColor;\n"
          + "void main() {\n"
          + "   fragColor = vColor;\n" 
          + "}\n";

        private static int _lineShader;
        private static int _uMetrics, _uViewStart, _uViewEnd;
        private static int _uNotesTbo, _uTboStart, _uTboMask, _uPalette;
        private static int _vao, _tboBuffer, _tboTex, _paletteTex;

        // no more cpu side array. memory usage cut in half (yay)
        private static GpuNote* _ring;

        private static int _ringCap = 1 << 23;
        private static int _deferred_ringCap = -1;
        private static int _mask;
        private static int _head;
        private static int _tail;

        private static KeyHeader* _keyHeaders;
        private const int TOTAL_KEYS = 128 * 16;
        private const int COLOR_SIZE  = 256;

        private static int _lookaheadTicks = 4000;
        private static float _pixelsPerTick;
        private static int _lastWindowTicks = -1;
        private static int _lastSweepEnd = -1;
        private static bool _paletteUploadPending = false;
        private static bool _isInitialized;

        public static int WindowTicks = 2000;
        public static int RingCap;
        public static int NotesDrawnLastFrame;

        public static void Initialize()
        {
            if (_isInitialized) return;
            GL.LoadBindings(new NativeGLBindingsContext());

            _lineShader  = BuildShader(LineVertSrc, LineFragSrc);
            _uMetrics    = GL.GetUniformLocation(_lineShader, "uMetrics");
            _uViewStart  = GL.GetUniformLocation(_lineShader, "uViewStart");
            _uViewEnd    = GL.GetUniformLocation(_lineShader, "uViewEnd");
            _uNotesTbo   = GL.GetUniformLocation(_lineShader, "uNotesTbo");
            _uTboStart   = GL.GetUniformLocation(_lineShader, "uTboStart");
            _uTboMask    = GL.GetUniformLocation(_lineShader, "uTboMask");
            _uPalette    = GL.GetUniformLocation(_lineShader, "uPalette");

            GL.UseProgram(_lineShader);
            GL.Uniform1(_uPalette, 0);
            GL.Uniform1(_uNotesTbo, 1);
            GL.UseProgram(0);

            _vao = GL.GenVertexArray();
            _tboTex = GL.GenTexture();
            _tboBuffer = GL.GenBuffer();
            _paletteTex = GL.GenTexture();

            _keyHeaders = (KeyHeader*)NativeMemory.AlignedAlloc(TOTAL_KEYS * (nuint)sizeof(KeyHeader), 64);
                        
            AllocTbo(_ringCap);
            _isInitialized = true;
        }
        
        private static void AllocTbo(int cap)
        {
            _mask = cap - 1;

            if (_ring != null)
            {
                GL.BindBuffer(BufferTarget.TextureBuffer, _tboBuffer);
                GL.UnmapBuffer(BufferTarget.TextureBuffer);
                _ring = null;
            }

            GL.DeleteBuffer(_tboBuffer);
            _tboBuffer = GL.GenBuffer();

            GL.BindBuffer(BufferTarget.TextureBuffer, _tboBuffer);
            
            // MapReadBit added so cpu can actually read stuff from it
            GL.BufferStorage(BufferTarget.TextureBuffer, (nint)(cap * sizeof(GpuNote)), IntPtr.Zero,
                BufferStorageFlags.MapWriteBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit);

            _ring = (GpuNote*)GL.MapBufferRange(BufferTarget.TextureBuffer, IntPtr.Zero, (nint)(cap * sizeof(GpuNote)),
                MapBufferAccessMask.MapWriteBit | MapBufferAccessMask.MapReadBit | MapBufferAccessMask.MapPersistentBit | MapBufferAccessMask.MapCoherentBit);

            GL.BindTexture(TextureTarget.TextureBuffer, _tboTex);
            GL.TexBuffer(TextureBufferTarget.TextureBuffer, SizedInternalFormat.Rg32ui, _tboBuffer);
            GL.BindTexture(TextureTarget.TextureBuffer, 0);
            GL.BindBuffer(BufferTarget.TextureBuffer, 0);

            _ringCap = cap;
        }

        public static void InitializeForMIDI()
        {
            _isInitialized = false;
            NativeMemory.Clear(_keyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
            _head = 0;
            _tail = 0;
            _lastSweepEnd = -1;
            _lastWindowTicks = -1;
            _paletteUploadPending = true;
            _isInitialized = true;
        }

        public static void ResetForUnload()
        {
            _isInitialized = false;
            _head = 0;
            _tail = 0;
            _lastSweepEnd = -1;
            _lastWindowTicks = -1;
            if (_ringCap != 1 << 23) 
                _deferred_ringCap = 1 << 23;
        }

        public static void Dispose()
        {
            _isInitialized = false;
            if (_keyHeaders != null) { NativeMemory.AlignedFree(_keyHeaders); _keyHeaders = null; }

            if (_tboBuffer != 0)
            {
                GL.BindBuffer(BufferTarget.TextureBuffer, _tboBuffer);
                if (_ring != null) { GL.UnmapBuffer(BufferTarget.TextureBuffer); _ring = null; }
                GL.BindBuffer(BufferTarget.TextureBuffer, 0);
                GL.DeleteBuffer(_tboBuffer);
                _tboBuffer = 0;
            }
            if (_tboTex != 0) { GL.DeleteTexture(_tboTex); _tboTex = 0; }
            if (_paletteTex != 0) { GL.DeleteTexture(_paletteTex); _paletteTex = 0; }
            if (_vao != 0) { GL.DeleteVertexArray(_vao); _vao = 0; }
            if (_lineShader != 0) { GL.DeleteProgram(_lineShader); _lineShader = 0; }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int tick, int pad)
        {
            if (_paletteUploadPending)
            {
                byte[] paletteData = new byte[COLOR_SIZE * 3];
                for (int i = 0; i < COLOR_SIZE; i++)
                {
                    uint c = (uint)Random.Shared.Next(0x808080, 0x1000000);
                    paletteData[i * 3 + 0] = (byte)((c >> 16) & 0xFF);
                    paletteData[i * 3 + 1] = (byte)((c >>  8) & 0xFF);
                    paletteData[i * 3 + 2] = (byte)( c        & 0xFF);
                }
                GL.BindTexture(TextureTarget.Texture2D, _paletteTex);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, COLOR_SIZE, 1, 0, PixelFormat.Rgb, PixelType.UnsignedByte, paletteData);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.BindTexture(TextureTarget.Texture2D, 0);
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
                _head = 0;
                _tail = 0;
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
                float minW = 2.0f / screenWidth;

                GL.Viewport(0, 0, screenWidth, screenHeight);
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Less);
                GL.UseProgram(_lineShader);

                GL.Uniform4(_uMetrics, _pixelsPerTick, minW, yBottom, yStep);
                GL.Uniform1(_uViewStart, viewStart);
                GL.Uniform1(_uViewEnd, viewEnd);
                
                // shader resolves physIdx
                // uTboMask handles ring wrap with no extra draw call
                GL.Uniform1(_uTboStart,  _tail & _mask);
                GL.Uniform1(_uTboMask,   _mask);

                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _paletteTex);
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.TextureBuffer, _tboTex);

                GL.BindVertexArray(_vao);
                GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, count);
                GL.BindVertexArray(0);

                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.TextureBuffer, 0);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.UseProgram(0);
                GL.Disable(EnableCap.DepthTest);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SweepRange(int fromTick, int toTick)
        {
            GpuNote* ringLocal = _ring;
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
            for (int tick = fromTick; tick <= limit; tick++)
            {
                long nextOffset = tickgroups[tick + 1].offset;
                if (currentOffset == nextOffset) 
                    continue;

                byte* ev = messages + currentOffset * 3;
                long idx = currentOffset;
                byte* evEnd = messages + nextOffset * 3;

                // for loop that was done looked too scary so i just went with another way to do a loop
                while (ev < evEnd)
                {
                    byte status = ev[0];
                    if ((status & 0xE0) != 0x80)
                    {
                        ev += 3;
                        idx++;
                        continue;
                    }

                    byte channel = (byte)(status & 0x0F);
                    byte note = ev[1];

                    KeyHeader* header = keyheader + ((channel << 7) | note);

                    if ((status & 0x10) == 0) // NoteOff
                    {
                        byte count = header->Count;
                        if (count > 0)
                        {
                            // shifting could infact be done better
                            int noteIdx = header->NoteIdx[0];
                            Unsafe.CopyBlockUnaligned(&header->NoteIdx[0], &header->NoteIdx[1], (uint)((count - 1) * 4));
                            header->Count = (byte)(count - 1);

                            if (noteIdx >= headLocal - cap)
                            {
                                int physIdx = noteIdx & maskLocal;

                                // for the sake of not reading write combined memory several times that probably caused stutters or something
                                GpuNote noteData = ringLocal[physIdx]; 
                                int duration = tick - noteData.StartTick;
                                noteData.PackedData = (noteData.PackedData & 0xFFFF0000u) | (uint)duration;

                                ringLocal[physIdx] = noteData; 
                            }
                        }
                    }
                    else
                    {
                        int count = header->Count;
                        if (count < 4) 
                        {
                            if (headLocal - tailLocal >= cap)
                            {
                                // flush first to prevent striping
                                _tail = tailLocal;
                                _head = headLocal;
                                ResizeRing(cap * 2);
                                cap = _ringCap;
                                maskLocal = _mask;
                                ringLocal = _ring;
                            }

                            int absId = headLocal++;
                            int physIdx = absId & maskLocal;
                            uint colorIdx = (useTrack ? (uint)(tracks[idx] + channel) : channel) & 0xFFu;

                            ringLocal[physIdx] = new GpuNote
                            {
                                StartTick = tick,
                                PackedData = 0xFFFFu | ((uint)note << 16) | (colorIdx << 24)
                            };

                            header->NoteIdx[count] = absId;
                            header->Count = (byte)(count + 1);
                        }
                    }
                    ev += 3;
                    idx++;
                }
                currentOffset = nextOffset;
            }
            _tail = tailLocal;
            _head = headLocal;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AdvanceTail(int viewStart)
        {
            int safeTail = _head - _ringCap;
            if (_tail < safeTail) _tail = safeTail;

            int forcecullthresh = Math.Clamp(_lookaheadTicks * 2, 8000, ushort.MaxValue);
            bool forceCull = NotesDrawnLastFrame > 262144;
            int forceCullBefore = viewStart - forcecullthresh;
            
            GpuNote* ringLocal = _ring;
            int maskLocal = _mask;
            int headLocal = _head;
            int tailLocal = _tail;

            while (tailLocal < headLocal)
            {
                int physIdx = tailLocal & maskLocal;
                GpuNote note = ringLocal[physIdx];

                uint duration = note.PackedData & 0xFFFFu;
                int endTick = note.StartTick + (int)duration;
                
                if (endTick < viewStart || (forceCull && note.StartTick < forceCullBefore))
                    tailLocal++;
                else
                    break;
            }
            _tail = tailLocal;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ResizeRing(int newCap)
        {
            if (newCap > int.MaxValue || newCap < 0) 
                return;
            int newMask = newCap - 1;
            
            int newBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.TextureBuffer, newBuffer);
            GL.BufferStorage(BufferTarget.TextureBuffer, (nint)(newCap * sizeof(GpuNote)), IntPtr.Zero,
                BufferStorageFlags.MapWriteBit | BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit);

            GpuNote* newRing = (GpuNote*)GL.MapBufferRange(BufferTarget.TextureBuffer, IntPtr.Zero, (nint)(newCap * sizeof(GpuNote)),
                MapBufferAccessMask.MapWriteBit | MapBufferAccessMask.MapReadBit | MapBufferAccessMask.MapPersistentBit | MapBufferAccessMask.MapCoherentBit);

            for (int absId = _tail; absId < _head; absId++)
                newRing[absId & newMask] = _ring[absId & _mask];

            if (_ring != null)
            {
                GL.BindBuffer(BufferTarget.TextureBuffer, _tboBuffer);
                GL.UnmapBuffer(BufferTarget.TextureBuffer);
            }
            GL.DeleteBuffer(_tboBuffer);

            _tboBuffer = newBuffer;
            _ring = newRing;
            _mask = newMask;
            _ringCap = newCap;

            GL.BindTexture(TextureTarget.TextureBuffer, _tboTex);
            GL.TexBuffer(TextureBufferTarget.TextureBuffer, SizedInternalFormat.Rg32ui, _tboBuffer);
            GL.BindTexture(TextureTarget.TextureBuffer, 0);
            GL.BindBuffer(BufferTarget.TextureBuffer, 0);
        }

        private static int BuildShader(string vert, string frag)
        {
            int vertex = CompileStage(ShaderType.VertexShader, vert);
            int fragment = CompileStage(ShaderType.FragmentShader, frag);
            int program = GL.CreateProgram();
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) 
                throw new Exception("Shader link:\n" + GL.GetProgramInfoLog(program));
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            return program;
        }

        private static int CompileStage(ShaderType type, string src)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, src);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) 
                throw new Exception($"{type}:\n" + GL.GetShaderInfoLog(shader));
            return shader;
        }

        private sealed class NativeGLBindingsContext : IBindingsContext
        {
            private static readonly nint s_gl = LoadGL();
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
            public nint GetProcAddress(string procName)
            {
                nint addr;
                if (OperatingSystem.IsWindows())
                {
                    addr = WglGetProcAddress(procName);
                    if (addr is 0 or 1 or 2 or 3 or -1) 
                        NativeLibrary.TryGetExport(s_gl, procName, out addr);
                }
                else 
                    NativeLibrary.TryGetExport(s_gl, procName, out addr);
                return addr;
            }
        }
    }
}