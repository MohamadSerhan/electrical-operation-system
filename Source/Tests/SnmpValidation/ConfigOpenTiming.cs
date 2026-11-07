using System.Diagnostics;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

// Measures the complete Configuration workspace using isolated services and local data.
internal static class ConfigOpenTiming
{
    public static void Run(string temporary)
    {
        Directory.CreateDirectory(temporary);
        var session = new UserSession { Username = "isolated-qa", UserId = "isolated-qa", RoleName = "Administrator" };
        var store = new FireConnectionStore(Path.Combine(temporary, "fire.json"));
        using var fire = new FireConnectionService(store, false);
        var recording = new RecordingSettings { ExportFolderPath = temporary };
        recording.Normalize();
        using var historian = new MeasurementHistorian(Path.Combine(temporary, "historian.db"),
            recordingSettings: recording, migrateLegacyDatabase: false);
        var backupSettings = new BackupSettings();
        using var backup = new BackupRecoveryService(historian, backupSettings, recording, temporary);
        using var versions = new ConfigVersionService(temporary);
        for (var pass = 1; pass <= 3; pass++)
        {
            var watch = Stopwatch.StartNew();
            fire.ReadSettings(); var readSettingsMs = watch.ElapsedMilliseconds; watch.Restart();
            fire.ReadStatus(); var readStatusMs = watch.ElapsedMilliseconds; watch.Restart();
            using var alarm = new AlarmSettingsView(session);
            var alarmMs = watch.ElapsedMilliseconds; watch.Restart();
            using var fireView = new FireConnectionSettingsView(fire, session);
            var fireMs = watch.ElapsedMilliseconds; watch.Restart();
            using var system = new SystemDataView(new PlcSettings(), new PlcConnectionStatus(), () => { }, Array.Empty<MeterSnapshot>());
            var systemMs = watch.ElapsedMilliseconds; watch.Restart();
            using var config = new ConfigurationView(new PlcSettings(), 0, new PlcConnectionStatus(),
                Array.Empty<MeterSnapshot>(), session, fire, backupSettings, backup, versions,
                (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
                _ => Task.FromResult(new RestorePreparationResult()), () => { }, () => { });
            var configMs = watch.ElapsedMilliseconds;
            Console.WriteLine($"pass {pass}: ReadSettings={readSettingsMs} ms, ReadStatus={readStatusMs} ms, Alarm={alarmMs} ms, Fire={fireMs} ms, SystemData={systemMs} ms, ConfigurationView={configMs} ms");
        }
    }
}
