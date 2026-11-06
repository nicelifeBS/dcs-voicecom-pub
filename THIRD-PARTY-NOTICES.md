# Third-party notices

DCS VoiceCom uses the following components. Their licence texts must ship with any release.

| Component | Licence | Use |
|---|---|---|
| Whistle model (`whistle.cact`, Cactus-Compute/whistle on Hugging Face) | Apache-2.0 | Speech recognition. Downloaded on first start, SHA-256 pinned. Ship the licence text. No NOTICE file is published. |
| Cactus needle engine (`libneedle.dll`, Cactus-Compute/needle3 on Hugging Face) | Apache-2.0 | Runs the Whistle model in process. Downloaded on first start from the cactus-needle 3.3.3 wheel, SHA-256 pinned. Ship the licence text. No NOTICE file is published. |
| NAudio (NAudio.Wasapi, NAudio.Core) | MIT | Microphone capture (WASAPI) and resampling to 16 kHz. Ship the copyright notice. |
| Vortice.DirectInput | MIT | Push-to-talk from HOTAS buttons |

## Not bundled

**DCS-BIOS (Skunkworks)** is GPL-3.0 and is a separate program the user installs. DCS VoiceCom talks to it only
over UDP (export multicast 239.255.50.10:5010, input 127.0.0.1:7778), never embeds or links its code, never modifies
its Lua, and reads the control reference JSON from the user's own install at runtime.
