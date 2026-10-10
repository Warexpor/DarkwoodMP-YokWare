// RNNoise, ported to C# from the reference C code (https://github.com/xiph/rnnoise, v0.1.1).
//
// Copyright (c) 2017, Mozilla
// Copyright (c) 2007-2017, Jean-Marc Valin
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
    /// Noise suppression for the microphone by a trained model: RNNoise (Jean-Marc Valin,
    /// Mozilla / Xiph.Org), its reference C code and its trained weights brought to managed
    /// code, so there is no native library to ship and it is the same on every install.
    /// <para>
    /// It works on 48 kHz sound in 10 ms frames. Each frame is described by 42 numbers (the
    /// loudness of 22 bands on the ear's scale, how they changed, how strongly the frame
    /// repeats at the voice's pitch) and a small recurrent network, trained on hours of
    /// speech mixed with recorded noise, answers how much of each band is speech. Unlike a
    /// noise-floor tracker it knows what speech sounds like, so it also turns down noise
    /// that is not steady: keys, clicks, a fan changing speed, a room's clatter.
    /// </para>
    /// Samples are in 16-bit scale (-32768..32767), as the model was trained. The output is
    /// the input <see cref="FrameSize"/> samples later.
    /// </summary>
    internal sealed class RnNoise
    {
        internal const int SampleRate = 48000;
        internal const int FrameSize = 480;
        private const int WindowSize = 2 * FrameSize;
        private const int FreqSize = FrameSize + 1;

        private const int PitchMinPeriod = 60;
        private const int PitchMaxPeriod = 768;
        private const int PitchFrameSize = 960;
        private const int PitchBufSize = PitchMaxPeriod + PitchFrameSize;

        private const int NbBands = 22;
        private const int CepsMem = 8;
        private const int NbDeltaCeps = 6;
        private const int NbFeatures = NbBands + 3 * NbDeltaCeps + 2;

        private const int DenseSize = 24, VadSize = 24, NoiseSize = 48, DenoiseSize = 96;
        private const float WeightsScale = 1f / 256f;
        internal const string WeightsResource = "DWMPHorde.Resources.rnnoise_weights.bin";
        private const int WeightsLength = 87503;

        /// <summary>Band edges in units of 200 Hz (4 bins): 0 .. 20 kHz, close to the Bark scale.</summary>
        private static readonly int[] EBand =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100
        };
        private static readonly int[] SecondCheck = { 0, 0, 3, 2, 3, 2, 5, 2, 3, 2, 3, 2, 5, 2, 3, 2 };

        private static readonly float[] HalfWindow = BuildHalfWindow();
        private static readonly float[] DctTable = BuildDctTable();
        private static readonly float[] TansigTable = BuildTansigTable();
        private static readonly float[] TwRe = BuildTwiddles(false);
        private static readonly float[] TwIm = BuildTwiddles(true);
        /// <summary>960 = 2^6 * 3 * 5, biggest stage first.</summary>
        private static readonly int[] Factors = { 5, 3, 2, 2, 2, 2, 2, 2 };
        private static readonly Model Net = Model.Load();

        private readonly float[] _analysisMem = new float[FrameSize];
        private readonly float[] _synthesisMem = new float[FrameSize];
        private readonly float[][] _cepstralMem = new float[CepsMem][];
        private int _memId;
        private readonly float[] _pitchBuf = new float[PitchBufSize];
        private float _lastGain;
        private int _lastPeriod;
        private float _hp0, _hp1;
        private readonly float[] _lastG = new float[NbBands];
        private readonly float[] _vadState = new float[VadSize];
        private readonly float[] _noiseState = new float[NoiseSize];
        private readonly float[] _denoiseState = new float[DenoiseSize];

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
        private readonly float[] _pitchLp = new float[PitchBufSize >> 1];
        private readonly float[] _xLp4 = new float[PitchFrameSize >> 2];
        private readonly float[] _yLp4 = new float[(PitchFrameSize + PitchMaxPeriod) >> 2];
        private readonly float[] _xcorr = new float[PitchMaxPeriod >> 1];
        private readonly float[] _yyLookup = new float[(PitchMaxPeriod >> 1) + 1];
        private readonly float[] _denseOut = new float[DenseSize];
        private readonly float[] _noiseIn = new float[DenseSize + VadSize + NbFeatures];
        private readonly float[] _denoiseIn = new float[VadSize + NoiseSize + NbFeatures];
        private readonly float[] _gruZ = new float[DenoiseSize], _gruR = new float[DenoiseSize], _gruH = new float[DenoiseSize];
        private readonly float[] _vadOut = new float[1];
        private readonly float[] _ac = new float[5], _lpc = new float[4];
        private readonly int[] _best = new int[2];

        internal RnNoise()
        {
            for (int i = 0; i < CepsMem; i++)
                _cepstralMem[i] = new float[NbBands];
        }

        /// <summary>
        /// One frame of <see cref="FrameSize"/> samples in place, 16-bit scale. Returns how
        /// sure the model is that the frame holds speech (0..1).
        /// </summary>
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
                vad = ComputeRnn();
                PitchFilter();
                for (int i = 0; i < NbBands; i++)
                {
                    // A band's gain falls no faster than this from frame to frame (reverberation tails).
                    float g = Math.Max(_g[i], 0.6f * _lastG[i]);
                    _g[i] = g;
                    _lastG[i] = g;
                }
                InterpBandGain(_gf, _g);
                for (int i = 0; i < FreqSize; i++)
                {
                    _xRe[i] *= _gf[i];
                    _xIm[i] *= _gf[i];
                }
            }
            FrameSynthesis(buf, offset);
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
            for (int i = 0; i < NbDeltaCeps; i++)
                f[NbBands + 2 * NbDeltaCeps + i] = _bandTmp[i];
            f[NbBands + 2 * NbDeltaCeps] -= 1.3f;
            f[NbBands + 2 * NbDeltaCeps + 1] -= 0.9f;
            f[NbBands + 3 * NbDeltaCeps] = 0.01f * (pitchIndex - 300);

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
            float[] ceps0 = _cepstralMem[_memId];
            float[] ceps1 = _memId < 1 ? _cepstralMem[CepsMem + _memId - 1] : _cepstralMem[_memId - 1];
            float[] ceps2 = _memId < 2 ? _cepstralMem[CepsMem + _memId - 2] : _cepstralMem[_memId - 2];
            for (int i = 0; i < NbBands; i++)
                ceps0[i] = f[i];
            _memId++;
            for (int i = 0; i < NbDeltaCeps; i++)
            {
                f[i] = ceps0[i] + ceps1[i] + ceps2[i];
                f[NbBands + i] = ceps0[i] - ceps2[i];
                f[NbBands + NbDeltaCeps + i] = ceps0[i] - 2f * ceps1[i] + ceps2[i];
            }
            if (_memId == CepsMem)
                _memId = 0;
            // How much the spectrum has been changing over the last frames.
            float specVariability = 0f;
            for (int i = 0; i < CepsMem; i++)
            {
                float minDist = 1e15f;
                for (int j = 0; j < CepsMem; j++)
                {
                    if (j == i)
                        continue;
                    float dist = 0f;
                    float[] a = _cepstralMem[i], b = _cepstralMem[j];
                    for (int k = 0; k < NbBands; k++)
                    {
                        float t = a[k] - b[k];
                        dist += t * t;
                    }
                    minDist = Math.Min(minDist, dist);
                }
                specVariability += minDist;
            }
            f[NbBands + 3 * NbDeltaCeps + 1] = specVariability / CepsMem - 2.1f;
            return false;
        }

        private static void BandEnergy(float[] bandE, float[] re, float[] im)
        {
            Array.Clear(bandE, 0, NbBands);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int start = EBand[i] << 2;
                int size = (EBand[i + 1] - EBand[i]) << 2;
                for (int j = 0; j < size; j++)
                {
                    float frac = (float)j / size;
                    float t = re[start + j] * re[start + j] + im[start + j] * im[start + j];
                    bandE[i] += (1f - frac) * t;
                    bandE[i + 1] += frac * t;
                }
            }
            bandE[0] *= 2f;
            bandE[NbBands - 1] *= 2f;
        }

        private static void BandCorr(float[] bandE, float[] xRe, float[] xIm, float[] pRe, float[] pIm)
        {
            Array.Clear(bandE, 0, NbBands);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int start = EBand[i] << 2;
                int size = (EBand[i + 1] - EBand[i]) << 2;
                for (int j = 0; j < size; j++)
                {
                    float frac = (float)j / size;
                    float t = xRe[start + j] * pRe[start + j] + xIm[start + j] * pIm[start + j];
                    bandE[i] += (1f - frac) * t;
                    bandE[i + 1] += frac * t;
                }
            }
            bandE[0] *= 2f;
            bandE[NbBands - 1] *= 2f;
        }

        /// <summary>Band values spread back over the bins; nothing above the last band's centre (20 kHz).</summary>
        private static void InterpBandGain(float[] g, float[] bandE)
        {
            Array.Clear(g, 0, FreqSize);
            for (int i = 0; i < NbBands - 1; i++)
            {
                int start = EBand[i] << 2;
                int size = (EBand[i + 1] - EBand[i]) << 2;
                for (int j = 0; j < size; j++)
                {
                    float frac = (float)j / size;
                    g[start + j] = (1f - frac) * bandE[i] + frac * bandE[i + 1];
                }
            }
        }

        private static void Dct(float[] output, float[] input)
        {
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
        /// Comb at the voice's pitch: in bands where the model wants more cut than the frame
        /// is periodic, the period-old copy is mixed in, which lowers the noise between the
        /// voice's harmonics (the 22 bands alone are too coarse for that).
        /// </summary>
        private void PitchFilter()
        {
            float[] r = _bandTmp;
            for (int i = 0; i < NbBands; i++)
            {
                float v;
                if (_exp[i] > _g[i])
                    v = 1f;
                else
                {
                    float e2 = _exp[i] * _exp[i], g2 = _g[i] * _g[i];
                    v = e2 * (1f - g2) / (0.001f + g2 * (1f - e2));
                }
                v = (float)Math.Sqrt(Math.Min(1f, Math.Max(0f, v)));
                r[i] = v * (float)Math.Sqrt(_ex[i] / (1e-8 + _ep[i]));
            }
            InterpBandGain(_gf, r);
            for (int i = 0; i < FreqSize; i++)
            {
                _xRe[i] += _gf[i] * _pRe[i];
                _xIm[i] += _gf[i] * _pIm[i];
            }
            float[] newE = _bandTmp2;
            BandEnergy(newE, _xRe, _xIm);
            for (int i = 0; i < NbBands; i++)
                r[i] = (float)Math.Sqrt(_ex[i] / (1e-8 + newE[i]));
            InterpBandGain(_gf, r);
            for (int i = 0; i < FreqSize; i++)
            {
                _xRe[i] *= _gf[i];
                _xIm[i] *= _gf[i];
            }
        }

        private void FrameSynthesis(float[] output, int offset)
        {
            InverseTransform(_win, _xRe, _xIm);
            ApplyWindow(_win);
            for (int i = 0; i < FrameSize; i++)
                output[offset + i] = _win[i] + _synthesisMem[i];
            Array.Copy(_win, FrameSize, _synthesisMem, 0, FrameSize);
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
            Dense(m.InputDense, _denseOut, _features);
            Gru(m.VadGru, _vadState, _denseOut);
            Dense(m.VadOutput, _vadOut, _vadState);
            Array.Copy(_denseOut, 0, _noiseIn, 0, DenseSize);
            Array.Copy(_vadState, 0, _noiseIn, DenseSize, VadSize);
            Array.Copy(_features, 0, _noiseIn, DenseSize + VadSize, NbFeatures);
            Gru(m.NoiseGru, _noiseState, _noiseIn);
            Array.Copy(_vadState, 0, _denoiseIn, 0, VadSize);
            Array.Copy(_noiseState, 0, _denoiseIn, VadSize, NoiseSize);
            Array.Copy(_features, 0, _denoiseIn, VadSize + NoiseSize, NbFeatures);
            Gru(m.DenoiseGru, _denoiseState, _denoiseIn);
            Dense(m.DenoiseOutput, _g, _denoiseState);
            return _vadOut[0];
        }

        private static float Tansig(float x)
        {
            if (!(x < 8f))
                return 1f;
            if (!(x > -8f))
                return -1f;
            float sign = 1f;
            if (x < 0f)
            {
                x = -x;
                sign = -1f;
            }
            int i = (int)Math.Floor(0.5f + 25f * x);
            x -= 0.04f * i;
            float y = TansigTable[i];
            float dy = 1f - y * y;
            y = y + x * dy * (1f - y * x);
            return sign * y;
        }

        private static float Sigmoid(float x) => 0.5f + 0.5f * Tansig(0.5f * x);

        private static void Dense(Layer layer, float[] output, float[] input)
        {
            int n = layer.Neurons, m = layer.Inputs;
            float[] w = layer.Input;
            for (int i = 0, o = 0; i < n; i++, o += m)
            {
                float sum = (layer.Bias[i] + Dot(w, o, input, 0, m)) * WeightsScale;
                output[i] = layer.Sigmoid ? Sigmoid(sum) : Tansig(sum);
            }
        }

        private void Gru(Layer gru, float[] state, float[] input)
        {
            int n = gru.Neurons, m = gru.Inputs;
            float[] w = gru.Input, rw = gru.Recurrent, bias = gru.Bias;
            float[] z = _gruZ, r = _gruR, h = _gruH;
            // Update gate, then reset gate.
            for (int i = 0; i < 2 * n; i++)
            {
                float sum = bias[i] + Dot(w, i * m, input, 0, m) + Dot(rw, i * n, state, 0, n);
                float v = Sigmoid(WeightsScale * sum);
                if (i < n)
                    z[i] = v;
                else
                    r[i - n] = v;
            }
            for (int j = 0; j < n; j++)
                r[j] *= state[j];
            for (int i = 0; i < n; i++)
            {
                float sum = (bias[2 * n + i] + Dot(w, (2 * n + i) * m, input, 0, m) + Dot(rw, (2 * n + i) * n, r, 0, n)) * WeightsScale;
                if (sum < 0f)
                    sum = 0f;
                h[i] = z[i] * state[i] + (1f - z[i]) * sum;
            }
            Array.Copy(h, 0, state, 0, n);
        }

        private sealed class Layer
        {
            internal float[] Bias, Input, Recurrent;
            internal int Inputs, Neurons;
            internal bool Sigmoid;
        }

        /// <summary>The trained weights (8-bit, 1/256 steps), in the order the reference lists them.</summary>
        private sealed class Model
        {
            internal Layer InputDense, VadGru, NoiseGru, DenoiseGru, DenoiseOutput, VadOutput;

            internal static Model Load()
            {
                byte[] data;
                using (Stream stream = typeof(RnNoise).Assembly.GetManifestResourceStream(WeightsResource))
                {
                    if (stream == null)
                        throw new InvalidOperationException("RNNoise weights missing: " + WeightsResource);
                    data = new byte[WeightsLength];
                    int got = 0;
                    while (got < data.Length)
                    {
                        int n = stream.Read(data, got, data.Length - got);
                        if (n <= 0)
                            break;
                        got += n;
                    }
                    if (got != WeightsLength || stream.ReadByte() >= 0)
                        throw new InvalidOperationException("RNNoise weights have the wrong size");
                }
                int pos = 0;
                var m = new Model();
                m.InputDense = new Layer { Inputs = NbFeatures, Neurons = DenseSize };
                m.InputDense.Input = Take(data, ref pos, NbFeatures, DenseSize);
                m.InputDense.Bias = Take(data, ref pos, DenseSize);
                m.VadGru = TakeGru(data, ref pos, DenseSize, VadSize);
                m.NoiseGru = TakeGru(data, ref pos, DenseSize + VadSize + NbFeatures, NoiseSize);
                m.DenoiseGru = TakeGru(data, ref pos, VadSize + NoiseSize + NbFeatures, DenoiseSize);
                m.DenoiseOutput = new Layer { Inputs = DenoiseSize, Neurons = NbBands, Sigmoid = true };
                m.DenoiseOutput.Input = Take(data, ref pos, DenoiseSize, NbBands);
                m.DenoiseOutput.Bias = Take(data, ref pos, NbBands);
                m.VadOutput = new Layer { Inputs = VadSize, Neurons = 1, Sigmoid = true };
                m.VadOutput.Input = Take(data, ref pos, VadSize, 1);
                m.VadOutput.Bias = Take(data, ref pos, 1);
                return m;
            }

            private static Layer TakeGru(byte[] data, ref int pos, int inputs, int neurons)
            {
                var layer = new Layer { Inputs = inputs, Neurons = neurons };
                layer.Input = Take(data, ref pos, inputs, neurons * 3);
                layer.Recurrent = Take(data, ref pos, neurons, neurons * 3);
                layer.Bias = Take(data, ref pos, neurons * 3);
                return layer;
            }

            private static float[] Take(byte[] data, ref int pos, int count)
            {
                var a = new float[count];
                for (int i = 0; i < count; i++)
                    a[i] = (sbyte)data[pos + i];
                pos += count;
                return a;
            }

            /// <summary>A weight matrix, stored input by input, turned so each output's weights lie in a row.</summary>
            private static float[] Take(byte[] data, ref int pos, int inputs, int outputs)
            {
                var a = new float[inputs * outputs];
                for (int j = 0; j < inputs; j++)
                    for (int i = 0; i < outputs; i++)
                        a[i * inputs + j] = (sbyte)data[pos + j * outputs + i];
                pos += inputs * outputs;
                return a;
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

        private static float[] BuildTansigTable()
        {
            var t = new float[201];
            for (int i = 0; i < t.Length; i++)
                t[i] = (float)Math.Tanh(0.04 * i);
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
