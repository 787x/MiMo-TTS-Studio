`tone.mp3` is an original synthetic 440 Hz sine wave (one second, 44.1 kHz mono),
created for format validation. It contains no human voice or copied media.
`tone-vbr.mp3` is the same synthetic tone at 24 kHz stereo with MPEG2 VBR,
ID3 and the encoder's Xing header, used to exercise another common encoding.

Test-fixture regeneration only; ffmpeg is not a runtime/build dependency of the FPK:

```bash
ffmpeg -v error -f lavfi -i 'sine=frequency=440:sample_rate=44100:duration=1' -codec:a libmp3lame -b:a 96k -write_xing 0 -id3v2_version 0 -y tests/fixtures/tone.mp3
ffmpeg -v error -f lavfi -i 'sine=frequency=440:sample_rate=24000:duration=1' -ac 2 -codec:a libmp3lame -q:a 4 -y tests/fixtures/tone-vbr.mp3
```
