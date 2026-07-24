using System.IO;
using Newtonsoft.Json;

namespace LiraSlabZones.Core
{
    public static class AnalysisSettingsStore
    {
        public static AnalysisSettings LoadOrDefault(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new AnalysisSettings();

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<AnalysisSettings>(json) ?? new AnalysisSettings();
        }

        public static void Save(string path, AnalysisSettings settings)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
    }
}
