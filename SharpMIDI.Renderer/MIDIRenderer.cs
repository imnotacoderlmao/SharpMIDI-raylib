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
            public int EndTick;
            public uint ColorPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyHeader
        {
            public int Head;
            public int Count;
            public fixed int NoteIdx[4]; 
        }

        // i wonder if this could be represented better lmao
        private const string LineVertSrc =
            "#version 330 core\n"                                                                           +
            "layout(location = 0) in ivec2 aTicks;\n"                                                       +
            "layout(location = 1) in vec3 aColor;\n"                                                        +
            "layout(location = 2) in uint aPitch;\n"                                                        +
            "uniform int uViewStart;\n"                                                                     +
            "uniform int uViewEnd;\n"                                                                       +
            "uniform float uPpt;\n"                                                                         +
            "flat out vec4 vColor;\n"                                                                       +
            "void main() {\n"                                                                               +
            "    const float kMaxDur = 65535.0;\n"                                                          +
            "    int tick = gl_VertexID == 0 ? aTicks.x : aTicks.y;\n"                                      +
            "    float x = float(tick - uViewStart) * uPpt - 1.0;\n"                                        +
            "    float y = float(aPitch) * (1.0 / 64.0) + (1.0 / 128.0) - 1.0;\n"                           +
            "    float dur = min(float(aTicks.y - aTicks.x), kMaxDur);\n"                                   +
            "    vColor = vec4(aColor, 1.0);\n"                                                             +
            "    gl_Position = vec4(x, y, dur * (1.0 / kMaxDur), 1.0);\n"                                   +
            "}\n";

        private const string LineFragSrc =
            "#version 330 core\n"                   +
            "flat in vec4 vColor;\n"                +
            "out vec4 fragColor;\n"                 +
            "void main() {\n"                       + 
            "fragColor = vColor;\n"                 + 
            "}\n";

        private const string BlitVertSrc =
            "#version 330 core\n"                                               +
            "uniform float uYBottom, uYTop;\n"                                  +
            "out vec2 vUV;\n"                                                   +
            "void main() {\n"                                                   +
            "    float x = (gl_VertexID & 1) == 0 ? -1.0 : 1.0;\n"              +
            "    float t = (gl_VertexID >> 1) == 0 ?  0.0 : 1.0;\n"             +
            "    gl_Position = vec4(x, mix(uYBottom, uYTop, t), 0.0, 1.0);\n"   +
            "    vUV = vec2((x + 1.0) * 0.5, t);\n"                             +
            "}\n";

        private const string BlitFragSrc =
            "#version 330 core\n"                               +
            "uniform sampler2D uTex;\n"                         +
            "in vec2 vUV;\n"                                    +
            "out vec4 fragColor;\n"                             +
            "void main() {\n"                                   + 
            "   fragColor = texture(uTex, vUV);\n"              + 
            "}\n";

        private static int _lineShader, _blitShader;
        private static int _uViewStart, _uViewEnd, _uPpt;
        private static int _uYBottom, _uYTop, _uTex;
        private static int _vao, _blitVao, _vbo;
        private static int _fbo, _fboTex, _fboDepth, _fboWidth;

        private static GpuNote* _ring;
        private static GpuNote* _cpuNotes; // cpu side shadow array
        
        public  static int _ringCap = 1 << 23; 
        private static int _mask;               

        private static int _head; 
        private static int _tail; 
        
        // bulk transfer trackers
        private static int _appendMin = int.MaxValue;
        private static int _appendMax = -1;

        private static KeyHeader* _keyHeaders;
        private const int TOTAL_KEYS = 128 * 16;
        private const int COLOR_SIZE = byte.MaxValue + 1;
        private const int LOOKAHEAD_TICKS = 4000;

        private static readonly uint[] colorTable = new uint[COLOR_SIZE];

        private static float pixelsPerTick; 
        private static int lastWindowTicks = -1;
        private static int lastSweepEnd = -1;
        private static int lastTick = -1;
        private static bool IsInitialized;

        public static int WindowTicks = 2000;
        public static int NotesDrawnLastFrame;

        public static void Initialize()
        {
            if (IsInitialized) return;

            GL.LoadBindings(new NativeGLBindingsContext());

            _lineShader = BuildShader(LineVertSrc, LineFragSrc);
            _uViewStart = GL.GetUniformLocation(_lineShader, "uViewStart");
            _uViewEnd = GL.GetUniformLocation(_lineShader, "uViewEnd");
            _uPpt = GL.GetUniformLocation(_lineShader, "uPpt");
 
            _blitShader = BuildShader(BlitVertSrc, BlitFragSrc);
            _uYBottom = GL.GetUniformLocation(_blitShader, "uYBottom");
            _uYTop = GL.GetUniformLocation(_blitShader, "uYTop");
            _uTex = GL.GetUniformLocation(_blitShader, "uTex");

            GL.UseProgram(_blitShader);
            GL.Uniform1(_uTex, 0);
            GL.UseProgram(0);
            GL.ActiveTexture(TextureUnit.Texture0);

            _blitVao = GL.GenVertexArray();
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();

            _keyHeaders = (KeyHeader*)NativeMemory.AlignedAlloc(TOTAL_KEYS * (nuint)sizeof(KeyHeader), 64);
            _cpuNotes = (GpuNote*)NativeMemory.AlignedAlloc((nuint)(_ringCap * sizeof(GpuNote)), 64);

            AllocRingVbo(_ringCap);
            IsInitialized = true;
        }

        private static void AllocRingVbo(int cap)
        {
            _mask = cap - 1;

            if (_ring != null)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
                GL.UnmapBuffer(BufferTarget.ArrayBuffer);
                _ring = null;
            }

            GL.DeleteBuffer(_vbo);
            _vbo = GL.GenBuffer();

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferStorage(BufferTarget.ArrayBuffer, (nint)(cap * sizeof(GpuNote)), IntPtr.Zero,
                BufferStorageFlags.MapWriteBit |
                BufferStorageFlags.MapPersistentBit |
                BufferStorageFlags.MapCoherentBit);
            
            _ring = (GpuNote*)GL.MapBufferRange(BufferTarget.ArrayBuffer, IntPtr.Zero, (nint)(cap * sizeof(GpuNote)),
                MapBufferAccessMask.MapWriteBit |
                MapBufferAccessMask.MapPersistentBit |
                MapBufferAccessMask.MapCoherentBit);

            GL.BindVertexArray(_vao);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribIPointer(0, 2, VertexAttribIntegerType.Int, 12, 0);
            GL.VertexAttribDivisor(0, 1);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.UnsignedByte, true, 12, 8);
            GL.VertexAttribDivisor(1, 1);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribIPointer(2, 1, VertexAttribIntegerType.UnsignedByte, 12, 11);
            GL.VertexAttribDivisor(2, 1);
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

            _ringCap = cap;
        }

        public static void InitializeForMIDI()
        {
            IsInitialized  = false;
            NativeMemory.Clear(_keyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
            _head = 0;
            _tail = 0;
            _appendMin = int.MaxValue;
            _appendMax = -1;
            lastSweepEnd = -1;
            lastTick = -1;
            lastWindowTicks = -1;

            for (int i = 0; i < COLOR_SIZE; i++)
            {
                uint c = (uint)Random.Shared.Next(0x808080, 0x1000000);
                colorTable[i] = ((c >> 16) & 0xFF) | (((c >> 8) & 0xFF) << 8) | ((c & 0xFF) << 16);
            }

            IsInitialized = true;
        }

        public static void ResetForUnload()
        {
            IsInitialized = false;
            _head = 0;
            _tail = 0;
            _appendMin = int.MaxValue;
            _appendMax = -1;
            lastSweepEnd = -1;
            lastTick = -1;
            lastWindowTicks = -1;
        }

        public static void Dispose()
        {
            IsInitialized = false;
            if (_keyHeaders != null) { NativeMemory.AlignedFree(_keyHeaders); _keyHeaders = null; }
            if (_cpuNotes != null) { NativeMemory.AlignedFree(_cpuNotes); _cpuNotes = null; }

            if (_vbo != 0)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
                if (_ring != null) { GL.UnmapBuffer(BufferTarget.ArrayBuffer); _ring = null; }
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.DeleteBuffer(_vbo);
                _vbo = 0;
            }
            DestroyFbo();
            if (_vao != 0) { GL.DeleteVertexArray(_vao); _vao = 0; }
            if (_blitVao != 0) { GL.DeleteVertexArray(_blitVao); _blitVao = 0; }
            if (_lineShader != 0) { GL.DeleteProgram(_lineShader); _lineShader = 0; }
            if (_blitShader != 0) { GL.DeleteProgram(_blitShader); _blitShader = 0; }
        }

        private static void DestroyFbo()
        {
            if (_fboTex != 0) { GL.DeleteTexture(_fboTex); _fboTex = 0; }
            if (_fboDepth != 0) { GL.DeleteRenderbuffer(_fboDepth); _fboDepth = 0; }
            if (_fbo != 0) { GL.DeleteFramebuffer(_fbo); _fbo = 0; }
            _fboWidth = 0;
        }

        private static void EnsureFbo(int width)
        {
            if (_fboWidth == width) return;
            DestroyFbo();

            _fboTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _fboTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, 128, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            _fboDepth = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _fboDepth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent32f, width, 128);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

            _fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _fboTex, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _fboDepth);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            _fboWidth = width;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Render(int screenWidth, int screenHeight, int tick, int pad)
        {
            if (!MIDILoader.midiLoaded) return;

            int maxtick = MIDILoader.maxTick - 1;
            int half = WindowTicks >> 1;
            int viewStart = Math.Clamp(tick - half, 0, maxtick);
            int viewEnd = Math.Clamp(tick + half, 0, maxtick);

            if (WindowTicks != lastWindowTicks)
            {
                pixelsPerTick = 2.0f / WindowTicks;
                lastWindowTicks = WindowTicks;
            }

            if (tick == lastTick && screenWidth == _fboWidth)
            {
                BlitToScreen(screenWidth, screenHeight, pad);
                return;
            }
            lastTick = tick;

            TickGroup[] groups = MIDIEvent.TickGroupArray;
            byte* messages = (byte*)SynthEvent.messages.Pointer;
            ushort* tracks = SynthEvent.track != null ? SynthEvent.track.Pointer : null;

            int sweepEnd = Math.Clamp(viewEnd + LOOKAHEAD_TICKS, 0, maxtick);
            bool incremental = lastSweepEnd >= 0 && sweepEnd >= lastSweepEnd && sweepEnd - lastSweepEnd < WindowTicks;

            if (!incremental)
            {
                _head = 0;
                _tail = 0;
                NativeMemory.Clear(_keyHeaders, TOTAL_KEYS * (nuint)sizeof(KeyHeader));
                SweepRange(Math.Max(0, viewStart - WindowTicks), sweepEnd, groups, messages, tracks);
            }
            else
            {
                SweepRange(lastSweepEnd + 1, sweepEnd, groups, messages, tracks);
            }
            lastSweepEnd = sweepEnd;

            AdvanceTail(viewStart);
            
            // send (all new) note ons to the gpu
            SyncAppendedBlocksToGpu();

            Raylib_cs.Rlgl.DrawRenderBatchActive();
            EnsureFbo(screenWidth);

            int count = _head - _tail;

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.Viewport(0, 0, screenWidth, 128);
            GL.ClearColor(0f, 0f, 0f, 0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (count > 0)
            {
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Lequal);
                GL.UseProgram(_lineShader);
                GL.Uniform1(_uViewStart, viewStart);
                GL.Uniform1(_uViewEnd, viewEnd);
                GL.Uniform1(_uPpt, pixelsPerTick);
                GL.BindVertexArray(_vao);
                
                DrawRingSlice(_tail, count);
                
                GL.BindVertexArray(0);
                GL.UseProgram(0);
                GL.Disable(EnableCap.DepthTest);
            }

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            BlitToScreen(screenWidth, screenHeight, pad);
            NotesDrawnLastFrame = count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawRingSlice(int tail, int count)
        {
            int startIdx = tail & _mask;
            int wrap = _ringCap - startIdx; 

            if (count <= wrap)
            {
                GL.DrawArraysInstancedBaseInstance(PrimitiveType.Lines, 0, 2, count, startIdx);
            }
            else
            {
                GL.DrawArraysInstancedBaseInstance(PrimitiveType.Lines, 0, 2, wrap, startIdx);
                GL.DrawArraysInstancedBaseInstance(PrimitiveType.Lines, 0, 2, count - wrap, 0);
            }
        }

        private static void BlitToScreen(int screenWidth, int screenHeight, int pad)
        {
            float yBottom = -1.0f + 2.0f * pad / screenHeight;
            float yTop =  1.0f - 2.0f * pad / screenHeight;

            GL.Viewport(0, 0, screenWidth, screenHeight);
            GL.UseProgram(_blitShader);
            GL.Uniform1(_uYBottom, yBottom);
            GL.Uniform1(_uYTop, yTop);
            GL.BindTexture(TextureTarget.Texture2D, _fboTex);
            GL.BindVertexArray(_blitVao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            GL.BindVertexArray(0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.UseProgram(0);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SweepRange(int fromTick, int toTick, TickGroup[] groups, byte* messages, ushort* tracks)
        {
            GpuNote* cpunotesLocal = _cpuNotes;
            GpuNote* ringbufferLocal = _ring;        
            bool useTrack = tracks != null;
            KeyHeader* keyheader = _keyHeaders;
            int limit = Math.Min(toTick, groups.Length - 2);

            fixed (TickGroup* tickgroups = groups)
            fixed (uint* palette = colorTable)
            {
                for (int tick = fromTick; tick <= limit; tick++)
                {
                    long offset = tickgroups[tick].offset;
                    long next = tickgroups[tick + 1].offset;
                    
                    if (offset == next) 
                        continue;

                    byte* ev = messages + offset * 3;
                    byte* evEnd = messages + next * 3;

                    for (long idx = offset; ev < evEnd; ev += 3, idx++)
                    {
                        byte status = ev[0];
                        uint diff = (uint)(status & 0xF0) - 0x80u;
                        if (diff > 0x10u) 
                            continue;

                        byte channel = (byte)(status & 0xF);
                        byte note = ev[1];
                        KeyHeader* header = keyheader + (channel << 7 | note);

                        if (diff == 0) // NoteOff
                        {
                            if (header->Count > 0)
                            {
                                int noteIdx = header->NoteIdx[header->Head];
                                header->Head = (header->Head + 1) & 3;
                                header->Count--;

                                if (noteIdx >= _head - _ringCap)
                                {
                                    int phys = noteIdx & _mask;
                                    _cpuNotes[phys].EndTick = tick;
                                    if (ringbufferLocal != null) 
                                        ringbufferLocal[phys].EndTick = tick; 
                                }
                            }
                        }
                        else
                        {
                            if (header->Count >= 4) 
                                continue;
                            if (_head - _tail >= _ringCap) 
                            {
                                GrowRing();
                                // Refresh cached pointers after a reallocation!
                                cpunotesLocal = _cpuNotes;
                                ringbufferLocal = _ring;
                            }

                            int ringTail = (header->Head + header->Count) & 3;
                            int absId = _head++;
                            int physIdx = absId & _mask;
                            byte colorIdx = useTrack ? (byte)(tracks[idx] + channel) : channel;

                            cpunotesLocal[physIdx] = new GpuNote 
                            {
                                StartTick = tick,
                                EndTick = int.MaxValue,
                                ColorPitch = palette[colorIdx] | ((uint)note << 24)
                            };

                            header->NoteIdx[ringTail] = absId;
                            header->Count++;
                            
                            // expand upload chunk bounds
                            if (absId < _appendMin) 
                                _appendMin = absId;
                            if (absId > _appendMax) 
                                _appendMax = absId;
                        }
                    }
                }
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void SyncAppendedBlocksToGpu()
        {
            if (_appendMin <= _appendMax)
            {
                int startAbs = _appendMin;
                int endAbs = _appendMax;
                
                // prevent over copying if we appended more than a full ring capacity in one frame
                if (endAbs - startAbs >= _ringCap) 
                    startAbs = endAbs - _ringCap + 1;

                int startIdx = startAbs & _mask;
                int endIdx = endAbs & _mask;

                if (startIdx <= endIdx)
                {
                    uint bytes = (uint)((endIdx - startIdx + 1) * sizeof(GpuNote));
                    Unsafe.CopyBlockUnaligned(_ring + startIdx, _cpuNotes + startIdx, bytes);
                }
                else
                {
                    uint bytesPart1 = (uint)((_ringCap - startIdx) * sizeof(GpuNote));
                    uint bytesPart2 = (uint)((endIdx + 1) * sizeof(GpuNote));
                    
                    Unsafe.CopyBlockUnaligned(_ring + startIdx, _cpuNotes + startIdx, bytesPart1);
                    Unsafe.CopyBlockUnaligned(_ring, _cpuNotes, bytesPart2);
                }

                _appendMin = int.MaxValue;
                _appendMax = -1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void AdvanceTail(int viewStart)
        {
            int safeTail = _head - _ringCap;
            if (_tail < safeTail) 
                _tail = safeTail;

            while (_tail < _head)
            {
                int endTick = _cpuNotes[_tail & _mask].EndTick;
                if (endTick >= 0 && endTick < viewStart)
                    _tail++;
                else
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void GrowRing()
        {
            int newCap = _ringCap * 2;
            int newMask = newCap - 1;
            
            GpuNote* newCpu = (GpuNote*)NativeMemory.AlignedAlloc((nuint)(newCap * sizeof(GpuNote)), 64);

            for (int absId = _tail; absId < _head; absId++)
                newCpu[absId & newMask] = _cpuNotes[absId & _mask];
                
            NativeMemory.AlignedFree(_cpuNotes);
            _cpuNotes = newCpu;

            AllocRingVbo(newCap);

            // flag the entire ring to send to the gpu on the next frame sync
            _appendMin = _tail;
            _appendMax = _head - 1;
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
    }
}