// Phase 1 core loop, no UI: HOTAS push-to-talk → Whistle (in process) → profile match → DCS-BIOS / keys.
//
//   dcsvoicecom-host                    run with %LOCALAPPDATA%\DcsVoiceCom\settings.json
//   dcsvoicecom-host --settings FILE    use another settings file
//   dcsvoicecom-host --devices          list game controllers
//   dcsvoicecom-host --bind             press the PTT button to save it to settings
//   dcsvoicecom-host --init             write a default settings file to edit
//   dcsvoicecom-host --fetch-model      download the Whistle model and engine now (the first run does it otherwise)
//
// While running, type a phrase to test it without speaking ("gear up"), or:
//   :dry PHRASE       show the steps without sending anything
//   :profile NAME     pin a profile; ":profile auto" follows the aircraft again
//   :reload           reload definitions and profile
//   :quit

using DcsVoiceCom.Config;
using DcsVoiceCom.Runtime;
using DcsVoiceCom.Speech;
using DcsVoiceCom.Windows;

var settingsPath = AppSettings.DefaultPath;
var mode = "run";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--settings": settingsPath = args[++i]; break;
        case "--devices": mode = "devices"; break;
        case "--bind": mode = "bind"; break;
        case "--init": mode = "init"; break;
        case "--fetch-model": mode = "fetch"; break;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}");
            return 1;
    }
}

var settings = AppSettings.Load(settingsPath);
switch (mode)
{
    case "devices":
        foreach (var c in DirectInputPushToTalk.ListControllers()) Console.WriteLine($"{c.InstanceGuid}  {c.Name}  ({c.Product})");
        return 0;

    case "init":
        if (File.Exists(settingsPath))
        {
            Console.WriteLine($"{settingsPath} already exists");
            return 1;
        }
        settings.Save(settingsPath);
        Console.WriteLine($"Wrote {settingsPath}");
        return 0;

    case "fetch":
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            var folder = string.IsNullOrWhiteSpace(settings.Speech.ModelDirectory) ? ModelLocator.DefaultDirectory : settings.Speech.ModelDirectory;
            var files = await ModelDownloader.DownloadAsync(folder, http, Console.WriteLine);
            Console.WriteLine($"Whistle model ready: {files.Weights}\nEngine: {files.Engine}");
        }
        return 0;

    case "bind":
        Console.WriteLine("Press the push-to-talk button (15 s)...");
        var detected = DirectInputPushToTalk.DetectButton(TimeSpan.FromSeconds(15));
        if (detected is not { } hit)
        {
            Console.WriteLine("No button pressed.");
            return 1;
        }
        settings = settings with { Ptt = new PttSettings { DeviceGuid = hit.Controller.InstanceGuid.ToString(), DeviceName = hit.Controller.Name, Button = hit.Button } };
        settings.Save(settingsPath);
        Console.WriteLine($"Bound {hit.Controller.Name} button {hit.Button + 1}; saved to {settingsPath}");
        return 0;
}

void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

var hasPtt = settings.Ptt.DeviceGuid != null || settings.Ptt.DeviceName != null;
if (!hasPtt) Log("No push-to-talk bound (run with --bind); typed phrases still work.");

await using var runtime = new VoiceComRuntime(
    settings,
    new SendInputKeySender(),
    new WavCuePlayer(settings.Cues),
    hasPtt ? new DirectInputPushToTalk(settings.Ptt, log: Log) : null,
    Log,
    new WasapiMicrophoneProvider());

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await runtime.StartAsync(cts.Token);
}
catch (OperationCanceledException)
{
    return 1;
}

Log("Ready. Type a phrase to test, :quit to exit.");
while (!cts.IsCancellationRequested && Console.ReadLine() is { } line)
{
    line = line.Trim();
    if (line.Length == 0) continue;
    try
    {
        if (line == ":quit") break;
        if (line == ":reload")
        {
            await runtime.ReloadProfileAsync();
        }
        else if (line.StartsWith(":profile", StringComparison.Ordinal))
        {
            var name = line[":profile".Length..].Trim();
            await runtime.SetProfileOverrideAsync(name.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : name);
        }
        else if (line.StartsWith(":dry ", StringComparison.Ordinal))
        {
            var outcome = await runtime.TestAsync(line[5..], dryRun: true);
            foreach (var step in outcome.Trace) Console.WriteLine($"    {step}");
        }
        else
        {
            await runtime.TestAsync(line, dryRun: false);
        }
    }
    catch (Exception ex)
    {
        Log($"Error: {ex.Message}");
    }
}
return 0;
