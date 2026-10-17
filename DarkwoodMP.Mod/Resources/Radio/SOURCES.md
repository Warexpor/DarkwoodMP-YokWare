# Radio sound sources

Recordings embedded in the DLL. Each was trimmed, mixed to mono and converted to
16-bit PCM: 44.1 kHz for the one-shot sounds, 16 kHz (the voice codec's rate) for
what is mixed into a received transmission (the two statics, made to loop and
levelled, and a second copy of the beep and the squelch tail).

`key16.wav` and `release16.wav` are pitched 3 semitones under the 44.1 kHz copies
(rubberband R3, length unchanged, average level matched to the unshifted take): a
far radio heard through this one is lower than the talker's own key sounds.

All of them have their bass cut (nothing under 160 to 200 Hz, a zero-phase slope up
to 320 to 380 Hz) and raised-cosine fades at both ends. The knob takes had almost all
of their energy in a thump under 120 Hz; with it gone they are the click itself, and
the recording's room noise between the knob's sounds is pulled down.

The one-shot sounds (beep, squelch tail, knob takes, both rates) end in a short
decaying tail, about 0.3 s, made from the recording itself through a noise-shaped
decay kept to the radio's band (300 Hz to 3.4 kHz): the originals stopped within
10 ms, which sounded cut off. `static_far.wav` had its crackle levelled (its loudness
over 12 ms windows made even) so it is a steady hiss; its spectrum and average level
are unchanged and it still loops.

| File | Used for | Source | Author | Licence |
|------|----------|--------|--------|---------|
| `key.wav`, `key16.wav` | Talk key pressed; the caller's beep on the receiving radio | [Walkie Talkie famous beep](https://freesound.org/people/SEF7/sounds/701326/) | SEF7 | CC0 1.0 |
| `release.wav`, `release16.wav` | Talk key released; the squelch tail on the receiving radio | [Radio Sign Off / Squelch](https://freesound.org/people/JovianSounds/sounds/524205/) | JovianSounds | CC0 1.0 |
| `knob1.wav` .. `knob4.wav` | Radio switched on or off (four takes) | [Press radio knob.wav](https://freesound.org/people/greatsoundstube/sounds/629301/) | greatsoundstube | CC0 1.0 |
| `static_near.wav` | Static under a clear transmission | [Walkie Talkie radio static](https://freesound.org/people/SEF7/sounds/701330/) | SEF7 | CC0 1.0 |
| `static_far.wav` | Static of a weak signal and of dropouts | [Radio static](https://freesound.org/people/LukaCafuka/sounds/760335/) | LukaCafuka | CC0 1.0 |
