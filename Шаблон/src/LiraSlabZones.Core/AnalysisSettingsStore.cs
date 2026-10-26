using System.IO;
using Newtonsoft.Json;

namespace LiraSlabZones.Core
{
    /// <summary>Совместимость: предпочтительно AppConfig.LoadEffectiveSettings / SaveUserSettings.</summary>
    public static class AnalysisSettingsStore
    {
        public static AnalysisSettings LoadOrDefault(string? path = null)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var json = File.ReadAllText(path!);
                var s = JsonConvert.DeserializeObject<AnalysisSettings>(json) ?? AppConfig.BuiltInDefaults();
                if (string.IsNullOrWhiteSpace(s.FamilyStraight) && !string.IsNullOrWhiteSpace(s.FamilyName))
                    s.FamilyStraight = AppConfig.StripRfa(s.FamilyName);
                s.FamilyName = s.FamilyStraight;
                s.SyncBackgroundAsFromBars();
                return s;
            }

            return AppConfig.LoadEffectiveSettings();
        }

        public static void Save(string path, AnalysisSettings settings)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            settings.FamilyStraight = string.IsNullOrWhiteSpace(settings.FamilyStraight)
                ? AppConfig.StripRfa(settings.FamilyName)
                : settings.FamilyStraight;
            settings.FamilyName = settings.FamilyStraight;
            settings.FamilyFileName = settings.FamilyStraight + ".rfa";
            File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }

        public static void SaveUser(AnalysisSettings settings) => AppConfig.SaveUserSettings(settings);
    }
}
