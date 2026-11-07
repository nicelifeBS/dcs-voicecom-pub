---
title: Troubleshooting
group: Speech
---
# Troubleshooting

Look in the **Log** first. It says what was heard and why a command did not run.

| Log says | Do this |
|---|---|
| No push-to-talk button bound | Settings → Rebind. |
| too short | Hold the button a little longer while you speak. |
| not heard | A word was missing. Say the whole phrase. |
| too close to call | Two commands sound alike. Make their phrases differ more. |
| Windows has no default microphone | Plug one in, or pick one in Settings. |
| no microphone matches | Pick the microphone again in Settings. |
| DCS-BIOS waiting for DCS | Start DCS and check that DCS-BIOS is installed. |
| No profile, wrong profile | The profile name must match the aircraft. Or pin a profile in the top bar. |

The app does not start? Read `%LOCALAPPDATA%\DcsVoiceCom\crash.log`. Also check the tray: a minimized window
hides there.
