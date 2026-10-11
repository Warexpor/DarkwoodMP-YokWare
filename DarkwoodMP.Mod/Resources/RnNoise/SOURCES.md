# RNNoise

The microphone's noise suppression is RNNoise by Jean-Marc Valin (Mozilla / Xiph.Org),
from https://github.com/xiph/rnnoise at tag `v0.2`, with the model that release
downloads: `https://media.xiph.org/rnnoise/models/rnnoise_data-0b50c45.tar.gz`
(its `src/rnnoise_data.c`, the full model, not the "little" one).

- `rnnoise_weights.bin`: that model's weights, the 8-bit ones where it has both 8-bit
  and float (the reference's own choice without AVX). Little-endian; each array is a
  32-bit count followed by its values, in this order:
  - `conv1_weights_float` (float), `conv1_bias` (float)
  - `conv2_weights_int8` (8-bit), `conv2_scale` (float), `conv2_bias` (float)
  - for `gru1`, `gru2`, `gru3`: `input_weights_idx` (16-bit), `input_weights_int8`
    (8-bit), `input_scale`, `input_bias` (float), then `recurrent_weights_idx`,
    `recurrent_weights_int8`, `recurrent_scale`, `recurrent_bias`,
    `recurrent_weights_diag` (float)
  - `dense_out_weights_float`, `dense_out_bias`, `vad_dense_weights_float`,
    `vad_dense_bias` (float)
- `DarkwoodMP.Mod/Audio/RnNoise.cs`: its C code (`denoise.c`, `rnn.c`, `nnet.c`,
  `nnet_arch.h`, the plain-C parts of `vec.h`, `pitch.c`, `celt_lpc.c`, the transform of
  `kiss_fft.c`) ported to C#.
- `COPYING.txt`: its licence (BSD 3-clause), which covers both. It is also embedded in
  the mod's DLL next to the weights.
