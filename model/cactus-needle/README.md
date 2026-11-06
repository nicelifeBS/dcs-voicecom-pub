# Whistle model and needle engine

Bundled so builds and installs need no download. Same layout as the cactus-needle cache, which `ModelLocator` reads.

| File | From | SHA-256 |
|---|---|---|
| `whistle/2.0.0/whistle.cact` | [Cactus-Compute/whistle](https://huggingface.co/Cactus-Compute/whistle) `whistle.cact` | `b6e02f048568ac5d01a2042556c658061e699acbc0aa2a1439f52f3d461dffeb` |
| `v3/3.3.3/libneedle.dll` | [Cactus-Compute/needle3](https://huggingface.co/Cactus-Compute/needle3) `python/cactus_needle-3.3.3-py3-none-win_amd64.whl`, `needle/libneedle3.dll` | `1263c6003dd88b9fcb1904a55985be66858639849309280b42f7764ee5d874d0` |
| `v3/3.3.3/libneedle.so` | the same repo, `python/cactus_needle-3.3.3-py3-none-manylinux2014_x86_64.whl`, `needle/libneedle3.so` (Linux x64: tests and CI) | `87476ff70e80334d7c766dbf236b72f42876072df22014c6ec8a9b087b82b260` |

Both are Apache-2.0 (`LICENSE`, the text published with both models; no NOTICE file). The hashes are the ones
`ModelDownloader` pins; update both together when moving to a new version.
