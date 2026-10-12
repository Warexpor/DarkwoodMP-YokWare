# Concentus (Opus)

The voice codec is Opus, in the managed C# port Concentus by Logan Stromberg:
https://github.com/lostromb/concentus at commit `b14383a` (2024-03-21), the last one
before the library moved to `Span<T>`, which is the code of its NuGet package 1.1.7.

- `Celt/`, `Common/`, `Opus/`, `Silk/`: that commit's `CSharp/Concentus/` folders, unchanged
  (its `Readme.txt` and project file left out).
- `LICENSE.txt`: its licence (the Opus BSD licence), also embedded in the mod DLL.

It is compiled into `DarkwoodMP.Mod.dll` rather than shipped as `Concentus.dll`: the
package's DLLs are built against `System.Runtime` facades the game does not ship. The mod
project defines `PARITY`, as the package's own build does (integer maths bit-exact with
the C reference). `DarkwoodMP.Mod/Audio/VoiceCodec.cs` is the only user.
