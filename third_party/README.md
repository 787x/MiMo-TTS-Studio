NLayer 3.0.0 is a managed MP3 decoder used for complete frame validation on Linux.
Its NuGet package declares MIT but omits the license file. `NLayer.LICENSE` is
the upstream license from the exact release tag:
https://github.com/naudio/NLayer/blob/v3.0.0/LICENSE

The build copies this notice into the FPK alongside runtime/other dependency
licenses. No NAudio/Windows playback dependency or ffmpeg runtime is included.

## PR7 Agent dependencies

`ModelContextProtocol` / `ModelContextProtocol.Core` 2.2.0: exact NuGet repository commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`, upstream `LICENSE` and `THIRD-PARTY-NOTICES.txt` saved as ModelContextProtocol.LICENSE / .NOTICES (Apache-2.0 plus retained MIT contributions; full upstream notices preserved). No source copied from SeleVox.

`Microsoft.Extensions.AI.Abstractions` 10.8.3: exact NuGet repository commit `ccb356f31db9d894807c4fd0c97c2f41553d1524`, dotnet/extensions `LICENSE` saved as Microsoft.Extensions.AI.Abstractions.LICENSE (MIT). Other restored package/runtime license files are collected by scripts/notices.py from both exact project.assets.json files into FPK server/licenses. Build fails on any missing license.
