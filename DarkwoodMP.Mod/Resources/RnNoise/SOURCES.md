# RNNoise

The microphone's noise suppression is RNNoise by Jean-Marc Valin (Mozilla / Xiph.Org),
from https://github.com/xiph/rnnoise at tag `v0.1.1`.

- `rnnoise_weights.bin`: the trained weights of that release (`src/rnn_data.c`), as the
  87503 signed bytes of its arrays in this order: `input_dense` weights and bias;
  `vad_gru` weights, recurrent weights, bias; `noise_gru` the same three; `denoise_gru`
  the same three; `denoise_output` weights and bias; `vad_output` weights and bias.
- `DarkwoodMP.Mod/Audio/RnNoise.cs`: its C code (`denoise.c`, `rnn.c`, `pitch.c`,
  `celt_lpc.c`, the transform of `kiss_fft.c`) ported to C#.
- `COPYING.txt`: its licence (BSD 3-clause), which covers both. It is also embedded in
  the mod's DLL next to the weights.
