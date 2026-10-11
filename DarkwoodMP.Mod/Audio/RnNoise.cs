// RNNoise, ported to C# from the reference C code (https://github.com/xiph/rnnoise, v0.2).
//
// Copyright (c) 2007-2017, 2024 Jean-Marc Valin
// Copyright (c) 2023 Amazon
// Copyright (c) 2017, Mozilla
// Copyright (c) 2005-2017, Xiph.Org Foundation
// Copyright (c) 2003-2004, Mark Borgerding
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
// - Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//
// - Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
//
// - Neither the name of the Xiph.Org Foundation nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
// OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

using System;
using System.IO;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Noise suppression for the microphone by a trained model: RNNoise 0.2 (Jean-Marc Valin,
    /// Mozilla / Xiph.Org), its reference C code and its trained weights brought to managed
    /// code, so there is no native library to ship and it is the same on every install.
    /// <para>
    /// It works on 48 kHz sound in 10 ms frames. Each frame is described by 65 numbers (the
    /// loudness of 32 bands on the ear's scale, how strongly each repeats at the voice's
    /// pitch, and the pitch) and a recurrent network (two convolutions over the last frames,
    /// three layers of 384 units), trained on hours of speech mixed with recorded noise,
    /// answers how much of each band is speech. Unlike a noise-floor tracker it knows what
    /// speech sounds like, so it also takes out noise that is not steady: keys, clicks, a
    /// fan changing speed, a room's clatter. The answer for a frame is made with the next
    /// frame already seen.
    /// </para>
    /// Samples are in 16-bit scale (-32768..32767), as the model was trained. The output is
    /// the input <see cref="DelaySamples"/> samples later. About a million multiply-adds a
    /// frame: it belongs on a thread of its own (<see cref="VoiceMicChain"/> runs there).
    /// </summary>
    internal sealed class RnNoise
    {
        internal const int SampleRate = 48000;
        internal const int FrameSize = 480;
        /// <summary>The overlap of the windows, and the frame the network looks ahead.</summary>
        internal const int DelaySamples = 2 * FrameSize;
        private const int WindowSize = 2 * FrameSize;
        private const int FreqSize = FrameSize + 1;

        private const int PitchMinPeriod = 60;
        private const int PitchMaxPeriod = 768;
        private const int PitchFrameSize = 960;
        private const int PitchBufSize = PitchMaxPeriod + PitchFrameSize;

        private const int NbBands = 32;
        private const int NbFeatures = 2 * NbBands + 1;

        private const int Conv1Out = 128, Conv2Out = 384, GruSize = 384;
        internal const string WeightsResource = "DWMPHorde.Resources.rnnoise_weights.bin";

        /// <summary>Band edges in bins of 50 Hz: 0 .. 20 kHz in steps of the ear's own resolution (ERB).</summary>
        private static readonly int[] EBand =
        {
            0, 2, 4, 6, 8, 10, 12, 15, 18, 21, 24, 28, 32, 36, 41, 47, 53, 60, 68, 77, 87, 98, 110, 124, 140, 157, 176, 198, 223,
            251, 282, 317, 356, 400
        };
        private static readonly int[] SecondCheck = { 0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2 };

        private static readonly float[] HalfWindow = BuildHalfWindow();
        private static readonly float[] DctTable = BuildDctTable();
        private static readonly float[] TwRe = BuildTwiddles(false);
        private static readonly float[] TwIm = BuildTwiddles(true);
        /// <summary>960 = 2^6 * 3 * 5, biggest stage first.</summary>
        private static readonly int[] Factors = { 5, 3, 2, 2, 2, 2, 2, 2 };
        private static readonly Model Net = Model.Load();

        private readonly float[] _analysisMem = new float[FrameSize];
        private readonly float[] _synthesisMem = new float[FrameSize];
        private readonly float[] _pitchBuf = new float[PitchBufSize];
        private float _lastGain;
        private int _lastPeriod;
        private float _hp0, _hp1;
        private readonly float[] _lastG = new float[NbBands];
        // The frame before this one, waiting for the gains the network gives it now.
        private readonly float[] _dxRe = new float[FreqSize], _dxIm = new float[FreqSize];
        private readonly float[] _dpRe = new float[FreqSize], _dpIm = new float[FreqSize];
        private readonly float[] _dEx = new float[NbBands], _dEp = new float[NbBands], _dExp = new float[NbBands];
        private readonly float[] _conv1In = new float[3 * NbFeatures];
        private readonly float[] _conv2In = new float[3 * Conv1Out];
        private readonly float[] _gru1 = new float[GruSize], _gru2 = new float[GruSize], _gru3 = new float[GruSize];

        // Scratch, one instance runs on one thread.
        private readonly float[] _x = new float[FrameSize];
        private readonly float[] _win = new float[WindowSize], _winP = new float[WindowSize];
        private readonly float[] _xRe = new float[FreqSize], _xIm = new float[FreqSize];
        private readonly float[] _pRe = new float[FreqSize], _pIm = new float[FreqSize];
        private readonly float[] _fftInRe = new float[WindowSize], _fftInIm = new float[WindowSize];
        private readonly float[] _fftOutRe = new float[WindowSize], _fftOutIm = new float[WindowSize];
        private readonly float[] _scrRe = new float[5], _scrIm = new float[5];
        private readonly float[] _ex = new float[NbBands], _ep = new float[NbBands], _exp = new float[NbBands];
        private readonly float[] _features = new float[NbFeatures];
        private readonly float[] _g = new float[NbBands];
        private readonly float[] _gf = new float[FreqSize];
        private readonly float[] _bandTmp = new float[NbBands], _bandTmp2 = new float[NbBands];
        private readonly float[] _bandSum = new float[NbBands + 2];
        private readonly float[] _pitchLp = new float[PitchBufSize >> 1];
        private readonly float[] _xLp4 = new float[PitchFrameSize >> 2];
        private readonly float[] _yLp4 = new float[(PitchFrameSize + PitchMaxPeriod) >> 2];
        private readonly float[] _xcorr = new float[PitchMaxPeriod >> 1];
        private readonly float[] _yyLookup = new float[(PitchMaxPeriod >> 1) + 1];
        private readonly float[] _ac = new float[5], _lpc = new float[4];
        private readonly int[] _best = new int[2];
        private readonly float[] _conv1Out = new float[Conv1Out], _conv2Out = new float[Conv2Out];
        private readonly float[] _zrh = new float[3 * GruSize], _recur = new float[3 * GruSize];
        private readonly int[] _quant = new int[3 * Conv1Out];
        private readonly int[] _gather = new int[GruSize];
        private readonly float[] _vadOut = new float[1];

        /// <summary>
        /// One frame of <see cref="FrameSize"/> samples in place, 16-bit scale. Returns how
        /// sure the model is that the frame holds speech (0..1).
        /// </summary>
        /// <summary>
        /// How hard the model's verdict is applied: the power each band's gain is raised to,
        /// so a band's cut in dB is this many times the model's. 1 is the model as trained;
        /// above it noise goes down further and the quiet edges of speech go with it.
        /// </summary>
        internal float Strength = 1f;
        internal const float MinStrength = 0.25f, MaxStrength = 3f;

        internal float Process(float[] buf, int offset)
        {
            // High-pass: the mic's rumble and any offset.
            const double a0 = -1.99599, a1 = 0.99600, b0 = -2, b1 = 1;
            for (int i = 0; i < FrameSize; i++)
            {
                float xi = buf[offset + i];
                float yi = xi + _hp0;
                _hp0 = (float)(_hp1 + (b0 * xi - a0 * yi));
                _hp1 = (float)(b1 * xi - a1 * yi);
                _x[i] = yi;
            }
            float vad = 0f;
            bool silence = ComputeFrameFeatures(_x);
            if (!silence)
            {
                // The gains are for the frame before: the network has now seen what follows it.
                vad = ComputeRnn();
                PitchFilter();
                for (int i = 0; i < NbBands; i++)
                {
                    // A band's gain falls no faster than this from frame to frame (reverberation tails).
                    float g = Math.Max(_g[i], 0.6f * _lastG[i]);
                    _g[i] = g;
                    _lastG[i] = g;
                }
                if (Strength != 1f)
                {
                    // After the smoothing, whose memory stays the model's own gains.
                    for (int i = 0; i < NbBands; i++)
                        _g[i] = (float)Math.Pow(_g[i], Strength);
                }
                InterpBandGain(_gf, _g);
                for (int i = 0; i < FreqSize; i++)
                {
                    _dxRe[i] *= _gf[i];
                    _dxIm[i] *= _gf[i];
                }
            }
            InverseTransform(_win, _dxRe, _dxIm);
            ApplyWindow(_win);
            for (int i = 0; i < FrameSize; i++)
                buf[offset + i] = _win[i] + _synthesisMem[i];
            Array.Copy(_win, FrameSize, _synthesisMem, 0, FrameSize);

            Array.Copy(_xRe, _dxRe, FreqSize);
            Array.Copy(_xIm, _dxIm, FreqSize);
            Array.Copy(_pRe, _dpRe, FreqSize);
            Array.Copy(_pIm, _dpIm, FreqSize);
            Array.Copy(_ex, _dEx, NbBands);
            Array.Copy(_ep, _dEp, NbBands);
            Array.Copy(_exp, _dExp, NbBands);
            return vad;
        }

        // ---- Features ----

        private bool ComputeFrameFeatures(float[] input)
        {
            Array.Copy(_analysisMem, 0, _win, 0, FrameSize);
            Array.Copy(input, 0, _win, FrameSize, FrameSize);
            Array.Copy(input, 0, _analysisMem, 0, FrameSize);
            ApplyWindow(_win);

            Array.Copy(_pitchBuf, FrameSize, _pitchBuf, 0, PitchBufSize - FrameSize);
            Array.Copy(input, 0, _pitchBuf, PitchBufSize - FrameSize, FrameSize);
            PitchDownsample(_pitchBuf, _pitchLp, PitchBufSize);
            int pitchIndex = PitchSearch(_pitchLp, PitchMaxPeriod >> 1, _pitchLp, PitchFrameSize, PitchMaxPeriod - 3 * PitchMinPeriod);
            pitchIndex = PitchMaxPeriod - pitchIndex;
            float gain = RemoveDoubling(_pitchLp, PitchMaxPeriod, PitchMinPeriod, PitchFrameSize, ref pitchIndex, _lastPeriod, _lastGain);
            _lastPeriod = pitchIndex;
            _lastGain = gain;

            // The same window one pitch period back: what in this frame repeats is voiced speech.
            Array.Copy(_pitchBuf, PitchBufSize - WindowSize - pitchIndex, _winP, 0, WindowSize);
            ApplyWindow(_winP);
            ForwardTransformPair(_win, _winP);
            BandEnergy(_ex, _xRe, _xIm);
            BandEnergy(_ep, _pRe, _pIm);
            BandCorr(_exp, _xRe, _xIm, _pRe, _pIm);
            for (int i = 0; i < NbBands; i++)
                _exp[i] = _exp[i] / (float)Math.Sqrt(0.001 + _ex[i] * _ep[i]);
            float[] f = _features;
            Dct(_bandTmp, _exp);
            Array.Copy(_bandTmp, 0, f, NbBands, NbBands);
            f[2 * NbBands] = 0.01f * (pitchIndex - 300);

            float logMax = -2f, follow = -2f, energy = 0f;
            for (int i = 0; i < NbBands; i++)
            {
                float ly = (float)Math.Log10(1e-2 + _ex[i]);
                ly = Math.Max(logMax - 7f, Math.Max(follow - 1.5f, ly));
                _bandTmp2[i] = ly;
                logMax = Math.Max(logMax, ly);
                follow = Math.Max(follow - 1.5f, ly);
                energy += _ex[i];
            }
            if (energy < 0.04f)
            {
                // No sound at all: leave the model's memory as it is.
                Array.Clear(f, 0, NbFeatures);
                return true;
            }
            Dct(_bandTmp, _bandTmp2);
            Array.Copy(_bandTmp, 0, f, 0, NbBands);
            f[0] -= 12f;
            f[1] -= 4f;
            return false;
        }

        private void BandEnergy(float[] bandE, float[] re, float[] im)
        {
            float[] sum = _bandSum;
            Array.Clear(sum, 0, NbBands + 2);
            for (int i = 0; i < NbBands + 1; i++)
            {
                int start = EBand[i];
                int size = EBand[i + 1] - start;
                for (int j = 0; j < size; j++)
                {
                    float frac = (float)j / size;
                    float t = re[start + j] * re[start + j] + im[start + j] * im[start + j];
                    sum[i] += (1f - frac) * t;
                    sum[i + 1] += frac * t;
                }
            }
            sum[1] = (sum[0] + sum[1]) * 2f / 3f;
            sum[NbBands] = (sum[NbBands] + sum[NbBands + 1]) * 2f / 3f;
            Array.Copy(sum, 1, bandE, 0, NbBands);
        }

        private void BandCorr(float[] bandE, float[] xRe, float[] xIm, float[] pRe, float[] pIm)
        {
            float[] sum = _bandSum;
            Array.Clear(sum, 0, NbBands + 2);
            for (int i = 0; i < NbBands + 1; i++)
            {
                int start = EBand[i];
                int size = EBand[i + 1] - start;
                for (int j = 0; j < size; j++)
                {
                    float frac = (float)j / size;
                    float t = xRe[start + j] * pRe[start + j] + xIm[start + j] * pIm[start + j];
                    sum[i] += (1f - frac) * t;
                    sum[i + 1] += frac * t;
                }
            }
            sum[1] = (sum[0] + sum[1]) * 2f / 3f;
            sum[NbBands] = (sum[NbBands] + sum[NbBands + 1]) * 2f / 3f;
            Array.Copy(sum, 1, bandE, 0, NbBands);
        }

        /// <summary>Band values spread back over the bins; nothing above 20 kHz.</summary>
        private static void InterpBandGain(float[] g, float[] bandE)
        {
            Array.Clear(g, 0, FreqSize);
            for (int i = 1; i < NbBands; i++)
            {
                int start = EBand[i];
                int size = EBand[i + 1] - start;
                for (int j = 0; j < size; j++)
                {
                    float frac = (float)j / size;
                    g[start + j] = (1f - frac) * bandE[i - 1] + frac * bandE[i];
                }
            }
            for (int j = 0; j < EBand[1]; j++)
                g[j] = bandE[0];
            for (int j = EBand[NbBands]; j < EBand[NbBands + 1]; j++)
                g[j] = bandE[NbBands - 1];
        }

        private static void Dct(float[] output, float[] input)
        {
            // The reference keeps this factor from its 22-band days; the model was trained with it.
            float scale = (float)Math.Sqrt(2.0 / 22);
            for (int i = 0; i < NbBands; i++)
            {
                float sum = 0f;
                for (int j = 0; j < NbBands; j++)
                    sum += input[j] * DctTable[j * NbBands + i];
                output[i] = sum * scale;
            }
        }

        private static void ApplyWindow(float[] x)
        {
            for (int i = 0; i < FrameSize; i++)
            {
                x[i] *= HalfWindow[i];
                x[WindowSize - 1 - i] *= HalfWindow[i];
            }
        }

        /// <summary>
        /// Comb at the voice's pitch, on the waiting frame: in bands where the model wants more
        /// cut than the frame is periodic, the period-old copy is mixed in, which lowers the
        /// noise between the voice's harmonics (the bands alone are too coarse for that).
        /// </summary>
        private void PitchFilter()
        {
            float[] r = _bandTmp;
            for (int i = 0; i < NbBands; i++)
            {
                float v;
                if (_dExp[i] > _g[i])
                    v = 1f;
                else
                {
                    float e2 = _dExp[i] * _dExp[i], g2 = _g[i] * _g[i];
                    v = e2 * (1f - g2) / (0.001f + g2 * (1f - e2));
                }
                v = (float)Math.Sqrt(Math.Min(1f, Math.Max(0f, v)));
                r[i] = v * (float)Math.Sqrt(_dEx[i] / (1e-8 + _dEp[i]));
            }
            InterpBandGain(_gf, r);
            for (int i = 0; i < FreqSize; i++)
            {
                _dxRe[i] += _gf[i] * _dpRe[i];
                _dxIm[i] += _gf[i] * _dpIm[i];
            }
            float[] newE = _bandTmp2;
            BandEnergy(newE, _dxRe, _dxIm);
            for (int i = 0; i < NbBands; i++)
                r[i] = (float)Math.Sqrt(_dEx[i] / (1e-8 + newE[i]));
            InterpBandGain(_gf, r);
            for (int i = 0; i < FreqSize; i++)
            {
                _dxRe[i] *= _gf[i];
                _dxIm[i] *= _gf[i];
            }
        }

        // ---- Transform (960 points, forward scaled by 1/960 as the reference's) ----

        /// <summary>
        /// The frame's spectrum and the pitch window's in one pass: two real signals as the
        /// real and imaginary part of one, taken apart again by their symmetry.
        /// </summary>
        private void ForwardTransformPair(float[] x, float[] p)
        {
            Array.Copy(x, 0, _fftInRe, 0, WindowSize);
            Array.Copy(p, 0, _fftInIm, 0, WindowSize);
            Fft(0, 0, 1, 0);
            const float scale = 0.5f / WindowSize;
            float[] zRe = _fftOutRe, zIm = _fftOutIm;
            for (int k = 0; k < FreqSize; k++)
            {
                int m = k == 0 ? 0 : WindowSize - k;
                _xRe[k] = (zRe[k] + zRe[m]) * scale;
                _xIm[k] = (zIm[k] - zIm[m]) * scale;
                _pRe[k] = (zIm[k] + zIm[m]) * scale;
                _pIm[k] = (zRe[m] - zRe[k]) * scale;
            }
        }

        private void InverseTransform(float[] output, float[] inRe, float[] inIm)
        {
            for (int i = 0; i < FreqSize; i++)
            {
                _fftInRe[i] = inRe[i];
                _fftInIm[i] = inIm[i];
            }
            for (int i = FreqSize; i < WindowSize; i++)
            {
                _fftInRe[i] = inRe[WindowSize - i];
                _fftInIm[i] = -inIm[WindowSize - i];
            }
            Fft(0, 0, 1, 0);
            // The forward transform of a spectrum, read backwards, is the inverse.
            output[0] = _fftOutRe[0];
            for (int i = 1; i < WindowSize; i++)
                output[i] = _fftOutRe[WindowSize - i];
        }

        /// <summary>Mixed radix, decimation in time, from the scratch input into the scratch output.</summary>
        private void Fft(int fo, int fi, int stride, int level)
        {
            int p = Factors[level];
            int m = WindowSize / (stride * p);
            if (m == 1)
            {
                for (int q = 0; q < p; q++)
                {
                    _fftOutRe[fo + q] = _fftInRe[fi + q * stride];
                    _fftOutIm[fo + q] = _fftInIm[fi + q * stride];
                }
            }
            else
            {
                for (int q = 0; q < p; q++)
                    Fft(fo + q * m, fi + q * stride, stride * p, level + 1);
            }
            if (p == 2)
            {
                float[] oRe = _fftOutRe, oIm = _fftOutIm;
                for (int u = 0, a = fo, b = fo + m, tw = 0; u < m; u++, a++, b++, tw += stride)
                {
                    float tRe = oRe[b] * TwRe[tw] - oIm[b] * TwIm[tw];
                    float tIm = oRe[b] * TwIm[tw] + oIm[b] * TwRe[tw];
                    oRe[b] = oRe[a] - tRe;
                    oIm[b] = oIm[a] - tIm;
                    oRe[a] += tRe;
                    oIm[a] += tIm;
                }
                return;
            }
            for (int u = 0; u < m; u++)
            {
                for (int q = 0; q < p; q++)
                {
                    _scrRe[q] = _fftOutRe[fo + u + q * m];
                    _scrIm[q] = _fftOutIm[fo + u + q * m];
                }
                int k = u;
                for (int q1 = 0; q1 < p; q1++)
                {
                    float re = _scrRe[0], im = _scrIm[0];
                    int tw = 0;
                    for (int q = 1; q < p; q++)
                    {
                        tw += stride * k;
                        if (tw >= WindowSize)
                            tw %= WindowSize;
                        re += _scrRe[q] * TwRe[tw] - _scrIm[q] * TwIm[tw];
                        im += _scrRe[q] * TwIm[tw] + _scrIm[q] * TwRe[tw];
                    }
                    _fftOutRe[fo + k] = re;
                    _fftOutIm[fo + k] = im;
                    k += m;
                }
            }
        }

        // ---- Pitch ----

        /// <summary>The pitch buffer at half rate, whitened (its formants flattened) so the pitch stands out.</summary>
        private void PitchDownsample(float[] x, float[] xLp, int len)
        {
            int half = len >> 1;
            for (int i = 1; i < half; i++)
                xLp[i] = 0.5f * (0.5f * (x[2 * i - 1] + x[2 * i + 1]) + x[2 * i]);
            xLp[0] = 0.5f * (0.5f * x[1] + x[0]);

            const int order = 4;
            float[] ac = _ac;
            for (int k = 0; k <= order; k++)
            {
                ac[k] = Dot(xLp, 0, xLp, k, half - k);
            }
            ac[0] *= 1.0001f;
            for (int i = 1; i <= order; i++)
                ac[i] -= ac[i] * (0.008f * i) * (0.008f * i);

            float[] lpc = _lpc;
            Array.Clear(lpc, 0, order);
            float error = ac[0];
            if (ac[0] != 0f)
            {
                for (int i = 0; i < order; i++)
                {
                    float rr = 0f;
                    for (int j = 0; j < i; j++)
                        rr += lpc[j] * ac[i - j];
                    rr += ac[i + 1];
                    float r = -rr / error;
                    lpc[i] = r;
                    for (int j = 0; j < (i + 1) >> 1; j++)
                    {
                        float t1 = lpc[j], t2 = lpc[i - 1 - j];
                        lpc[j] = t1 + r * t2;
                        lpc[i - 1 - j] = t2 + r * t1;
                    }
                    error -= r * r * error;
                    if (error < 0.001f * ac[0])
                        break;
                }
            }
            float tmp = 1f;
            for (int i = 0; i < order; i++)
            {
                tmp *= 0.9f;
                lpc[i] *= tmp;
            }
            const float c1 = 0.8f;
            float n0 = lpc[0] + 0.8f;
            float n1 = lpc[1] + c1 * lpc[0];
            float n2 = lpc[2] + c1 * lpc[1];
            float n3 = lpc[3] + c1 * lpc[2];
            float n4 = c1 * lpc[3];
            float m0 = 0f, m1 = 0f, m2 = 0f, m3 = 0f, m4 = 0f;
            for (int i = 0; i < half; i++)
            {
                float xi = xLp[i];
                float sum = xi + n0 * m0 + n1 * m1 + n2 * m2 + n3 * m3 + n4 * m4;
                m4 = m3;
                m3 = m2;
                m2 = m1;
                m1 = m0;
                m0 = xi;
                xLp[i] = sum;
            }
        }

        private static void FindBestPitch(float[] xcorr, float[] y, int len, int maxPitch, int[] best)
        {
            float syy = 1f;
            float num0 = -1f, num1 = -1f, den0 = 0f, den1 = 0f;
            best[0] = 0;
            best[1] = 1;
            for (int j = 0; j < len; j++)
                syy += y[j] * y[j];
            for (int i = 0; i < maxPitch; i++)
            {
                if (xcorr[i] > 0f)
                {
                    float x16 = xcorr[i] * 1e-12f;
                    float num = x16 * x16;
                    if (num * den1 > num1 * syy)
                    {
                        if (num * den0 > num0 * syy)
                        {
                            num1 = num0;
                            den1 = den0;
                            best[1] = best[0];
                            num0 = num;
                            den0 = syy;
                            best[0] = i;
                        }
                        else
                        {
                            num1 = num;
                            den1 = syy;
                            best[1] = i;
                        }
                    }
                }
                syy += y[i + len] * y[i + len] - y[i] * y[i];
                syy = Math.Max(1f, syy);
            }
        }

        /// <summary>The lag at which <paramref name="buf"/> from <paramref name="xStart"/> best matches the buffer's start: coarse at a quarter rate, then fine.</summary>
        private int PitchSearch(float[] buf, int xStart, float[] y, int len, int maxPitch)
        {
            int lag = len + maxPitch;
            int[] best = _best;
            for (int j = 0; j < len >> 2; j++)
                _xLp4[j] = buf[xStart + 2 * j];
            for (int j = 0; j < lag >> 2; j++)
                _yLp4[j] = y[2 * j];
            for (int i = 0; i < maxPitch >> 2; i++)
            {
                _xcorr[i] = Dot(_xLp4, 0, _yLp4, i, len >> 2);
            }
            FindBestPitch(_xcorr, _yLp4, len >> 2, maxPitch >> 2, best);

            for (int i = 0; i < maxPitch >> 1; i++)
            {
                _xcorr[i] = 0f;
                if (Math.Abs(i - 2 * best[0]) > 2 && Math.Abs(i - 2 * best[1]) > 2)
                    continue;
                _xcorr[i] = Math.Max(-1f, Dot(buf, xStart, y, i, len >> 1));
            }
            FindBestPitch(_xcorr, y, len >> 1, maxPitch >> 1, best);

            int offset = 0;
            if (best[0] > 0 && best[0] < (maxPitch >> 1) - 1)
            {
                float a = _xcorr[best[0] - 1], b = _xcorr[best[0]], c = _xcorr[best[0] + 1];
                if (c - a > 0.7f * (b - a))
                    offset = 1;
                else if (a - c > 0.7f * (b - c))
                    offset = -1;
            }
            return 2 * best[0] - offset;
        }

        private static float PitchGain(float xy, float xx, float yy) => xy / (float)Math.Sqrt(1f + xx * yy);

        /// <summary>The search tends to land on twice or three times the real period: check the shorter ones. Returns how periodic the frame is.</summary>
        private float RemoveDoubling(float[] buf, int maxPeriod, int minPeriod, int n, ref int t0Ref, int prevPeriod, float prevGain)
        {
            int minPeriod0 = minPeriod;
            maxPeriod /= 2;
            minPeriod /= 2;
            t0Ref /= 2;
            prevPeriod /= 2;
            n /= 2;
            int x = maxPeriod;
            if (t0Ref >= maxPeriod)
                t0Ref = maxPeriod - 1;
            int t0 = t0Ref, t = t0Ref;
            float xx = Dot(buf, x, buf, x, n), xy = Dot(buf, x, buf, x - t0, n);
            float[] yyLookup = _yyLookup;
            yyLookup[0] = xx;
            float yy = xx;
            for (int i = 1; i <= maxPeriod; i++)
            {
                yy = yy + buf[x - i] * buf[x - i] - buf[x + n - i] * buf[x + n - i];
                yyLookup[i] = Math.Max(0f, yy);
            }
            yy = yyLookup[t0];
            float bestXy = xy, bestYy = yy;
            float g0 = PitchGain(xy, xx, yy);
            float g = g0;
            for (int k = 2; k <= 15; k++)
            {
                int t1 = (2 * t0 + k) / (2 * k);
                if (t1 < minPeriod)
                    break;
                int t1b;
                if (k == 2)
                    t1b = t1 + t0 > maxPeriod ? t0 : t0 + t1;
                else
                    t1b = (2 * SecondCheck[k] * t0 + k) / (2 * k);
                xy = 0.5f * (Dot(buf, x, buf, x - t1, n) + Dot(buf, x, buf, x - t1b, n));
                yy = 0.5f * (yyLookup[t1] + yyLookup[t1b]);
                float g1 = PitchGain(xy, xx, yy);
                float cont;
                if (Math.Abs(t1 - prevPeriod) <= 1)
                    cont = prevGain;
                else if (Math.Abs(t1 - prevPeriod) <= 2 && 5 * k * k < t0)
                    cont = 0.5f * prevGain;
                else
                    cont = 0f;
                float thresh = Math.Max(0.3f, 0.7f * g0 - cont);
                // Against very short periods, which short-term correlation fakes.
                if (t1 < 3 * minPeriod)
                    thresh = Math.Max(0.4f, 0.85f * g0 - cont);
                if (g1 > thresh)
                {
                    bestXy = xy;
                    bestYy = yy;
                    t = t1;
                    g = g1;
                }
            }
            bestXy = Math.Max(0f, bestXy);
            float pg = bestYy <= bestXy ? 1f : bestXy / (bestYy + 1f);
            float c0 = Dot(buf, x, buf, x - (t - 1), n), c1 = Dot(buf, x, buf, x - t, n), c2 = Dot(buf, x, buf, x - (t + 1), n);
            int offset = 0;
            if (c2 - c0 > 0.7f * (c1 - c0))
                offset = 1;
            else if (c0 - c2 > 0.7f * (c1 - c2))
                offset = -1;
            if (pg > g)
                pg = g;
            t0Ref = 2 * t + offset;
            if (t0Ref < minPeriod0)
                t0Ref = minPeriod0;
            return pg;
        }

        /// <summary>Four running sums: the additions do not wait on each other.</summary>
        private static float Dot(float[] a, int ao, float[] b, int bo, int n)
        {
            float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f;
            int i = 0;
            for (; i + 3 < n; i += 4)
            {
                s0 += a[ao + i] * b[bo + i];
                s1 += a[ao + i + 1] * b[bo + i + 1];
                s2 += a[ao + i + 2] * b[bo + i + 2];
                s3 += a[ao + i + 3] * b[bo + i + 3];
            }
            for (; i < n; i++)
                s0 += a[ao + i] * b[bo + i];
            return (s0 + s1) + (s2 + s3);
        }

        // ---- Network ----

        private float ComputeRnn()
        {
            Model m = Net;
            // Each convolution sees its last three inputs.
            Array.Copy(_conv1In, NbFeatures, _conv1In, 0, 2 * NbFeatures);
            Array.Copy(_features, 0, _conv1In, 2 * NbFeatures, NbFeatures);
            for (int i = 0, o = 0; i < Conv1Out; i++, o += 3 * NbFeatures)
                _conv1Out[i] = Tanh(Dot(m.Conv1, o, _conv1In, 0, 3 * NbFeatures) + m.Conv1Bias[i]);

            Array.Copy(_conv2In, Conv1Out, _conv2In, 0, 2 * Conv1Out);
            Array.Copy(_conv1Out, 0, _conv2In, 2 * Conv1Out, Conv1Out);
            Linear(m.Conv2, _conv2Out, _conv2In);
            for (int i = 0; i < Conv2Out; i++)
                _conv2Out[i] = Tanh(_conv2Out[i]);

            Gru(m.Gru1In, m.Gru1Rec, _gru1, _conv2Out);
            Gru(m.Gru2In, m.Gru2Rec, _gru2, _gru1);
            Gru(m.Gru3In, m.Gru3Rec, _gru3, _gru2);

            for (int i = 0, o = 0; i < NbBands; i++, o += GruSize)
                _g[i] = Sigmoid(Dot(m.DenseOut, o, _gru3, 0, GruSize) + m.DenseOutBias[i]);
            return Sigmoid(Dot(m.VadDense, 0, _gru3, 0, GruSize) + m.VadDenseBias[0]);
        }

        private void Gru(Quantized input, Quantized recurrent, float[] state, float[] x)
        {
            const int n = GruSize;
            float[] zrh = _zrh, recur = _recur;
            Linear(input, zrh, x);
            Linear(recurrent, recur, state);
            // The recurrent matrix's own diagonal is kept apart, unquantized.
            float[] diag = recurrent.Diag;
            for (int i = 0; i < n; i++)
            {
                float s = state[i];
                recur[i] += diag[i] * s;
                recur[i + n] += diag[i + n] * s;
                recur[i + 2 * n] += diag[i + 2 * n] * s;
            }
            for (int i = 0; i < n; i++)
            {
                float z = Sigmoid(zrh[i] + recur[i]);
                float r = Sigmoid(zrh[i + n] + recur[i + n]);
                float h = Tanh(zrh[i + 2 * n] + recur[i + 2 * n] * r);
                state[i] = z * state[i] + (1f - z) * h;
            }
        }

        /// <summary>
        /// A layer with 8-bit weights: its input rounded to 127 steps, whole-number sums (exact,
        /// they stay far under what a float holds exactly), then each output's scale and bias.
        /// </summary>
        private void Linear(Quantized layer, float[] output, float[] x)
        {
            int[] q = _quant;
            int cols = layer.Inputs;
            for (int i = 0; i < cols; i++)
                q[i] = (sbyte)(int)Math.Floor(0.5 + 127.0 * x[i]);
            sbyte[] w = layer.Weights;
            float[] scale = layer.Scale, bias = layer.Bias;
            int rows = layer.Outputs;
            if (layer.BlockStart == null)
            {
                for (int i = 0, o = 0; i < rows; i++, o += cols)
                    output[i] = IntDot(w, o, q, cols) * scale[i] + bias[i];
                return;
            }
            // Sparse: each group of 8 outputs reads the same few runs of 4 inputs.
            int[] gather = _gather;
            ushort[] pos = layer.Positions;
            int[] blockStart = layer.BlockStart;
            int wo = 0;
            for (int b = 0; b < rows / 8; b++)
            {
                int from = blockStart[b], to = blockStart[b + 1];
                int n = 0;
                for (int k = from; k < to; k++)
                {
                    int p = pos[k];
                    gather[n] = q[p];
                    gather[n + 1] = q[p + 1];
                    gather[n + 2] = q[p + 2];
                    gather[n + 3] = q[p + 3];
                    n += 4;
                }
                for (int r = 0; r < 8; r++, wo += n)
                {
                    int i = b * 8 + r;
                    output[i] = IntDot(w, wo, gather, n) * scale[i] + bias[i];
                }
            }
        }

        private static int IntDot(sbyte[] w, int wo, int[] x, int n)
        {
            int s0 = 0, s1 = 0;
            int i = 0;
            for (; i + 3 < n; i += 4)
            {
                s0 += w[wo + i] * x[i] + w[wo + i + 1] * x[i + 1];
                s1 += w[wo + i + 2] * x[i + 2] + w[wo + i + 3] * x[i + 3];
            }
            for (; i < n; i++)
                s0 += w[wo + i] * x[i];
            return s0 + s1;
        }

        /// <summary>The reference's own tanh (a ratio of two short polynomials); the model was tuned with it.</summary>
        private static float Tanh(float x)
        {
            const float n0 = 952.52801514f, n1 = 96.39235687f, n2 = 0.60863042f;
            const float d0 = 952.72399902f, d1 = 413.36801147f, d2 = 11.88600922f;
            float x2 = x * x;
            float num = (n2 * x2 + n1) * x2 + n0;
            float den = (d2 * x2 + d1) * x2 + d0;
            num = num * x / den;
            return num < -1f ? -1f : num > 1f ? 1f : num;
        }

        private static float Sigmoid(float x) => 0.5f + 0.5f * Tanh(0.5f * x);

        /// <summary>An 8-bit layer, its weights laid out an output to a row (sparse: only the inputs that output's group reads).</summary>
        private sealed class Quantized
        {
            internal int Inputs, Outputs;
            internal sbyte[] Weights;
            internal float[] Scale, Bias, Diag;
            /// <summary>Sparse only: where each group of 8 outputs' input runs begin in <see cref="Positions"/>.</summary>
            internal int[] BlockStart;
            internal ushort[] Positions;
        }

        /// <summary>The trained weights, in the order <c>Resources/RnNoise/SOURCES.md</c> lists them.</summary>
        private sealed class Model
        {
            internal float[] Conv1, Conv1Bias, DenseOut, DenseOutBias, VadDense, VadDenseBias;
            internal Quantized Conv2, Gru1In, Gru1Rec, Gru2In, Gru2Rec, Gru3In, Gru3Rec;

            internal static Model Load()
            {
                using (Stream stream = typeof(RnNoise).Assembly.GetManifestResourceStream(WeightsResource))
                {
                    if (stream == null)
                        throw new InvalidOperationException("RNNoise weights missing: " + WeightsResource);
                    using (var r = new BinaryReader(stream))
                    {
                        var m = new Model();
                        m.Conv1 = Rows(Floats(r, 3 * NbFeatures * Conv1Out), 3 * NbFeatures, Conv1Out);
                        m.Conv1Bias = Floats(r, Conv1Out);
                        m.Conv2 = new Quantized { Inputs = 3 * Conv1Out, Outputs = Conv2Out };
                        m.Conv2.Weights = DenseRows(Bytes(r, 3 * Conv1Out * Conv2Out), 3 * Conv1Out, Conv2Out);
                        m.Conv2.Scale = Floats(r, Conv2Out);
                        m.Conv2.Bias = Floats(r, Conv2Out);
                        m.Gru1In = Sparse(r, false);
                        m.Gru1Rec = Sparse(r, true);
                        m.Gru2In = Sparse(r, false);
                        m.Gru2Rec = Sparse(r, true);
                        m.Gru3In = Sparse(r, false);
                        m.Gru3Rec = Sparse(r, true);
                        m.DenseOut = Rows(Floats(r, GruSize * NbBands), GruSize, NbBands);
                        m.DenseOutBias = Floats(r, NbBands);
                        m.VadDense = Floats(r, GruSize);
                        m.VadDenseBias = Floats(r, 1);
                        if (stream.Position != stream.Length)
                            throw new InvalidOperationException("RNNoise weights have the wrong size");
                        return m;
                    }
                }
            }

            private static int Count(BinaryReader r, int expected)
            {
                int n = r.ReadInt32();
                if (expected >= 0 && n != expected)
                    throw new InvalidOperationException("RNNoise weights: " + n + " values where " + expected + " belong");
                return n;
            }

            private static float[] Floats(BinaryReader r, int expected)
            {
                int n = Count(r, expected);
                byte[] raw = r.ReadBytes(n * 4);
                if (raw.Length != n * 4)
                    throw new InvalidOperationException("RNNoise weights cut short");
                var a = new float[n];
                Buffer.BlockCopy(raw, 0, a, 0, raw.Length);
                return a;
            }

            private static sbyte[] Bytes(BinaryReader r, int expected)
            {
                int n = Count(r, expected);
                byte[] raw = r.ReadBytes(n);
                if (raw.Length != n)
                    throw new InvalidOperationException("RNNoise weights cut short");
                var a = new sbyte[n];
                Buffer.BlockCopy(raw, 0, a, 0, n);
                return a;
            }

            /// <summary>A float matrix stored input by input, turned so each output's weights lie in a row.</summary>
            private static float[] Rows(float[] w, int inputs, int outputs)
            {
                var a = new float[w.Length];
                for (int j = 0; j < inputs; j++)
                    for (int i = 0; i < outputs; i++)
                        a[i * inputs + j] = w[j * outputs + i];
                return a;
            }

            /// <summary>An 8-bit matrix stored in blocks of 8 outputs by 4 inputs, turned so each output's weights lie in a row.</summary>
            private static sbyte[] DenseRows(sbyte[] w, int inputs, int outputs)
            {
                var a = new sbyte[w.Length];
                int at = 0;
                for (int i = 0; i < outputs; i += 8)
                    for (int j = 0; j < inputs; j += 4)
                        for (int r = 0; r < 8; r++)
                            for (int c = 0; c < 4; c++)
                                a[(i + r) * inputs + j + c] = w[at++];
                return a;
            }

            private static Quantized Sparse(BinaryReader r, bool recurrent)
            {
                const int outputs = 3 * GruSize;
                var q = new Quantized { Inputs = GruSize, Outputs = outputs };
                // For each group of 8 outputs: how many runs of 4 inputs it reads, then where each begins.
                int idxCount = Count(r, -1);
                var idx = new ushort[idxCount];
                for (int i = 0; i < idxCount; i++)
                    idx[i] = r.ReadUInt16();
                q.BlockStart = new int[outputs / 8 + 1];
                var positions = new System.Collections.Generic.List<ushort>(idxCount);
                int at = 0;
                for (int b = 0; b < outputs / 8; b++)
                {
                    if (at >= idxCount)
                        throw new InvalidOperationException("RNNoise weights: sparse index cut short");
                    int n = idx[at++];
                    q.BlockStart[b] = positions.Count;
                    for (int k = 0; k < n; k++)
                    {
                        if (at >= idxCount || idx[at] + 4 > GruSize)
                            throw new InvalidOperationException("RNNoise weights: bad sparse index");
                        positions.Add(idx[at++]);
                    }
                }
                q.BlockStart[outputs / 8] = positions.Count;
                if (at != idxCount)
                    throw new InvalidOperationException("RNNoise weights: sparse index has " + idxCount + " entries, " + at + " used");
                q.Positions = positions.ToArray();
                sbyte[] w = Bytes(r, positions.Count * 32);
                // Stored run by run (8 outputs by 4 inputs); laid out output by output over the group's runs.
                var rows = new sbyte[w.Length];
                int src = 0, dst = 0;
                for (int b = 0; b < outputs / 8; b++)
                {
                    int n = q.BlockStart[b + 1] - q.BlockStart[b];
                    for (int k = 0; k < n; k++)
                        for (int row = 0; row < 8; row++)
                            for (int c = 0; c < 4; c++)
                                rows[dst + row * n * 4 + k * 4 + c] = w[src++];
                    dst += 8 * n * 4;
                }
                q.Weights = rows;
                q.Scale = Floats(r, outputs);
                q.Bias = Floats(r, outputs);
                if (recurrent)
                    q.Diag = Floats(r, outputs);
                return q;
            }
        }

        // ---- Tables ----

        private static float[] BuildHalfWindow()
        {
            var w = new float[FrameSize];
            for (int i = 0; i < FrameSize; i++)
            {
                double s = Math.Sin(0.5 * Math.PI * (i + 0.5) / FrameSize);
                w[i] = (float)Math.Sin(0.5 * Math.PI * s * s);
            }
            return w;
        }

        private static float[] BuildDctTable()
        {
            var t = new float[NbBands * NbBands];
            for (int i = 0; i < NbBands; i++)
            {
                for (int j = 0; j < NbBands; j++)
                {
                    double v = Math.Cos((i + 0.5) * j * Math.PI / NbBands);
                    if (j == 0)
                        v *= Math.Sqrt(0.5);
                    t[i * NbBands + j] = (float)v;
                }
            }
            return t;
        }

        private static float[] BuildTwiddles(bool imaginary)
        {
            var t = new float[WindowSize];
            for (int i = 0; i < WindowSize; i++)
            {
                double phase = -2.0 * Math.PI * i / WindowSize;
                t[i] = (float)(imaginary ? Math.Sin(phase) : Math.Cos(phase));
            }
            return t;
        }
    }
}
