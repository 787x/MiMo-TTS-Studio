NLayer 3.0.0 is a managed MP3 decoder used for complete frame validation on Linux.
Its NuGet package declares MIT but omits the license file. `NLayer.LICENSE` is
the upstream license from the exact release tag:
https://github.com/naudio/NLayer/blob/v3.0.0/LICENSE

The build copies this notice into the FPK alongside runtime/other dependency
licenses. No NAudio/Windows playback dependency or ffmpeg runtime is included.
